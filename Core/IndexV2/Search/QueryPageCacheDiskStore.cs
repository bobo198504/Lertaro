using System.Text;
using Lertaro.PluginSdk.Abstractions;

namespace Lertaro.Core.IndexV2.Search;

// Persists one drive's search pages so the first query after a restart does not pay for a full index scan.
//
// Without this the cache only ever helped within a single service run: every restart -- an update, a reboot,
// a crash -- threw away every page, and the first search of each session was a cold scan again. That is the
// latency the user actually notices, because it lands right after they open the app.
//
// The file is stamped with the drive key and the index revision it was built from. A page is only a valid
// answer for the snapshot it was scanned from, so a file whose revision no longer matches is discarded at
// load rather than served: a stale page would show rows that the index has since moved or deleted. Rebuild
// cost in that case is exactly what it was before this file existed.
internal static class QueryPageCacheDiskStore
{
    private const int Magic = 0x4347504C;   // "LPGC"
    // Bumped when the row layout changes. An older file is rejected at load (and simply rebuilt), never
    // decoded with the newer reader.
    private const int Version = 1;

    private const int MaxPages = 100_000;
    private const int MaxRowsPerPage = 100_000;

    internal static string PathFor(string directory, string sourceKey)
    {
        var safe = sourceKey.Replace(':', '_').Replace('\\', '_').Replace('/', '_');
        return System.IO.Path.Combine(directory, safe + ".pagecache");
    }

    /// <summary>
    /// Writes the pages through a temporary file so a crash mid-write leaves the previous cache intact
    /// instead of a half-written one that would fail to decode forever.
    /// </summary>
    internal static void Save(string path, string sourceKey, long revision, IReadOnlyList<QueryPageCache.SavedPage> pages)
    {
        if (pages.Count == 0)
            return;

        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(sourceKey);
                writer.Write(revision);
                writer.Write(pages.Count);
                foreach (var page in pages)
                {
                    writer.Write(page.Key);
                    writer.Write(page.Exhausted);
                    writer.Write(page.Rows.Count);
                    foreach (var row in page.Rows)
                        WriteRow(writer, row);
                }
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // A cache that cannot be written is not an error: the next search just scans.
        }
    }

    /// <summary>Loads the pages for this drive and revision, or null when the file is absent, from another
    /// version, or describes a different index. Any failure is treated as "no cache".</summary>
    internal static List<QueryPageCache.SavedPage>? Load(string path, string sourceKey, long revision)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                return null;
            if (!string.Equals(reader.ReadString(), sourceKey, StringComparison.Ordinal))
                return null;
            if (reader.ReadInt64() != revision)
                return null;

            var pageCount = reader.ReadInt32();
            if (pageCount <= 0 || pageCount > MaxPages)
                return null;

            var pages = new List<QueryPageCache.SavedPage>(pageCount);
            for (var p = 0; p < pageCount; p++)
            {
                var key = reader.ReadString();
                var exhausted = reader.ReadBoolean();
                var rowCount = reader.ReadInt32();
                if (rowCount < 0 || rowCount > MaxRowsPerPage)
                    return null;

                var rows = new List<SearchResult>(rowCount);
                for (var r = 0; r < rowCount; r++)
                    rows.Add(ReadRow(reader));
                pages.Add(new QueryPageCache.SavedPage(key, rows, exhausted));
            }

            return pages;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteRow(BinaryWriter writer, SearchResult row)
    {
        writer.Write(row.Name);
        writer.Write(row.Path);
        writer.Write(row.IsDir);
        writer.Write(row.Drive);
        writer.Write((int)row.Attributes);
        writer.Write(row.RankSortKey);
        writer.Write(row.RankScore);
        writer.Write(row.RankFlags);
        writer.Write(row.HistoryScoreX100);
        writer.Write(row.Metadata.Size);
        writer.Write(row.Metadata.Created.Ticks);
        writer.Write(row.Metadata.Modified.Ticks);
        writer.Write(row.Metadata.Accessed.Ticks);
    }

    private static SearchResult ReadRow(BinaryReader reader) => new()
    {
        Name = reader.ReadString(),
        Path = reader.ReadString(),
        IsDir = reader.ReadBoolean(),
        Drive = reader.ReadString(),
        Attributes = (FileAttributes)reader.ReadInt32(),
        RankSortKey = reader.ReadUInt64(),
        RankScore = reader.ReadInt32(),
        RankFlags = reader.ReadByte(),
        HistoryScoreX100 = reader.ReadInt32(),
        Metadata = new FileMetadata(
            reader.ReadInt64(),
            new DateTime(reader.ReadInt64()),
            new DateTime(reader.ReadInt64()),
            new DateTime(reader.ReadInt64())),
    };
}
