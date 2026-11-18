using System.Text.Json;
using System.Text.Json.Serialization;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Core;

// Path frecency only: every open keeps a path's count and score, independent of the query association layer
// that this fork no longer uses.
public static class SearchHistoryStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, List<StoredEntry>>? _buckets;
    private static Dictionary<string, int>? _priorityCache;
    private static Dictionary<string, int>? _countCache;

    private static readonly IReadOnlyDictionary<string, int> EmptyPriorities =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal readonly record struct StoredEntry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("type")] HistoryEntryKind Kind,
        [property: JsonPropertyName("time")] long Time,
        [property: JsonPropertyName("count")] int Count = 1,
        [property: JsonPropertyName("score")] double Score = 0);

    public static string HistoryPath => Path.Combine(Logger.UserDataDir, "search-history.json");
    private static string BackupPath => HistoryPath + ".bak";

    /// <summary>Raised after the store changes, so the settings list can refresh its counts live.</summary>
    public static event Action? Changed;

    public static void Record(string keyword, string path, HistoryEntryKind kind)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("__", StringComparison.Ordinal))
            return;
        if (string.IsNullOrWhiteSpace(keyword))
            return;

        var enabled = UserSettings.Load().EnableHistory;
        Task.Run(() => RecordCore(keyword.Trim(), path, kind, enabled));
    }

    private static void RecordCore(string keyword, string path, HistoryEntryKind kind, bool enabled)
    {
        var isApp = kind == HistoryEntryKind.Application;
        var normalizedPath = isApp ? path.Trim() : NormalizePath(path);
        if (!ExistsForKind(normalizedPath, kind, File.Exists, Directory.Exists))
            return;

        lock (Gate)
        {
            EnsureCacheNoLock();
            var buckets = _buckets!;

            var entryExists = buckets.Values.Any(list => list.Any(e =>
                e.Path.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)));
            if (!HistoryRecordingPolicy.ShouldRecord(enabled, entryExists))
                return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var previous = RemovePathFromAllBuckets(buckets, normalizedPath);
            var previousScore = previous?.Score ?? 0d;
            var score = Math.Min(SearchHistoryBucketSupport.ScoreCap, previousScore + 1d);

            if (!buckets.TryGetValue(keyword, out var list))
                buckets[keyword] = list = new List<StoredEntry>();

            list.Insert(0, new StoredEntry(normalizedPath, kind, now, (previous?.Count ?? 0) + 1, score));
            if (list.Count > SearchHistoryBucketSupport.MaxEntriesPerKeyword)
                list.RemoveRange(SearchHistoryBucketSupport.MaxEntriesPerKeyword, list.Count - SearchHistoryBucketSupport.MaxEntriesPerKeyword);

            PersistNoLock();
            _priorityCache = BuildPriorityCache(buckets);
            _countCache = BuildCountCache(buckets);
        }

        Changed?.Invoke();
    }

    private static StoredEntry? RemovePathFromAllBuckets(Dictionary<string, List<StoredEntry>> buckets, string path)
    {
        StoredEntry? previous = null;
        List<string>? emptied = null;
        foreach (var (keyword, list) in buckets)
        {
            var removed = list.Find(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (removed != default)
                previous = removed;
            list.RemoveAll(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0)
                (emptied ??= new List<string>()).Add(keyword);
        }

        if (emptied != null)
            foreach (var keyword in emptied)
                buckets.Remove(keyword);
        return previous;
    }

    /// <summary>Ranking boost lookup -- keyed by the bare target path.</summary>
    public static int GetPriority(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return int.MaxValue;

        var normalized = NormalizePath(path);
        lock (Gate)
        {
            EnsureCacheNoLock();
            return _priorityCache != null && _priorityCache.TryGetValue(normalized, out var priority)
                ? priority
                : int.MaxValue;
        }
    }

    /// <summary>Every entry across every keyword, most-recently-opened first.</summary>
    public static IReadOnlyList<HistoryEntry> GetEntries()
    {
        lock (Gate)
        {
            EnsureCacheNoLock();
            return Flatten(_buckets!);
        }
    }

    /// <summary>Replaces the whole path-frecency store with these entries.</summary>
    public static void SaveEntries(IEnumerable<HistoryEntry> entries)
    {
        var validated = new List<(string Keyword, StoredEntry Entry)>();
        foreach (var entry in entries.OrderByDescending(e => e.Time))
        {
            var isApp = entry.Kind == HistoryEntryKind.Application;
            var normalizedPath = isApp ? entry.Path.Trim() : NormalizePath(entry.Path);
            if (!ExistsForKind(normalizedPath, entry.Kind, File.Exists, Directory.Exists))
                continue;

            validated.Add((entry.Keyword?.Trim() ?? string.Empty,
                new StoredEntry(normalizedPath, entry.Kind, entry.Time, entry.Count)));
        }

        lock (Gate)
        {
            EnsureCacheNoLock();
            var storedScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in _buckets!.Values)
                foreach (var entry in list)
                    if (entry.Score > 0d)
                        storedScores[entry.Path] = entry.Score;

            var scored = new List<(string Keyword, StoredEntry Entry)>(validated.Count);
            foreach (var (keyword, entry) in validated)
                scored.Add((keyword, entry with
                {
                    Score = storedScores.TryGetValue(entry.Path, out var stored)
                        ? stored
                        : Math.Min(SearchHistoryBucketSupport.ScoreCap, entry.Count),
                }));

            _buckets = BuildBuckets(scored);
            PersistNoLock();
            _priorityCache = BuildPriorityCache(_buckets);
            _countCache = BuildCountCache(_buckets);
        }

        Changed?.Invoke();
    }

    public static IReadOnlyDictionary<string, int> Snapshot()
    {
        lock (Gate)
        {
            EnsureCacheNoLock();
            return _priorityCache ?? EmptyPriorities;
        }
    }

    public static IReadOnlyDictionary<string, int> UsageCounts()
    {
        lock (Gate)
        {
            EnsureCacheNoLock();
            return _countCache ?? EmptyPriorities;
        }
    }

    public static IReadOnlyDictionary<string, int> ScoreSnapshot(DateTimeOffset now)
    {
        lock (Gate)
        {
            EnsureCacheNoLock();
            return SearchHistoryBucketSupport.BuildScoreCache(_buckets!, now.ToUnixTimeSeconds());
        }
    }

    private static Dictionary<string, int> BuildCountCache(Dictionary<string, List<StoredEntry>> buckets)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in buckets.Values)
            foreach (var entry in list)
                counts[entry.Path] = entry.Count;
        return counts;
    }

    internal static bool ExistsForKind(
        string path,
        HistoryEntryKind kind,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists) => kind switch
        {
            HistoryEntryKind.Application => true,
            HistoryEntryKind.Folder => directoryExists(path),
            _ => fileExists(path)
        };

    private static void EnsureCacheNoLock()
    {
        if (_buckets != null)
            return;

        _buckets = LoadNoLock();
        _priorityCache = BuildPriorityCache(_buckets);
        _countCache = BuildCountCache(_buckets);
    }

    private static Dictionary<string, List<StoredEntry>> LoadNoLock() => LoadFromFiles(HistoryPath, BackupPath);

    internal static Dictionary<string, List<StoredEntry>> LoadFromFiles(string mainPath, string backupPath)
    {
        if (!File.Exists(mainPath))
            return new Dictionary<string, List<StoredEntry>>(StringComparer.OrdinalIgnoreCase);

        return TryLoadFile(mainPath)
            ?? TryLoadFile(backupPath)
            ?? new Dictionary<string, List<StoredEntry>>(StringComparer.OrdinalIgnoreCase);
    }

    internal static Dictionary<string, List<StoredEntry>>? TryLoadFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<StoredEntry>>>(File.ReadAllText(path), JsonOptions);
            if (raw == null)
                return null;

            var flat = raw.SelectMany(kv => kv.Value.Select(e => (Keyword: kv.Key, Entry: e)))
                .OrderByDescending(x => x.Entry.Time);
            return BuildBuckets(flat);
        }
        catch (Exception ex)
        {
            Logger.Log($"[SearchHistoryStore] Failed to read history from '{path}': {ex.Message}", LogLevel.Error);
            return null;
        }
    }

    private static Dictionary<string, List<StoredEntry>> BuildBuckets(
        IEnumerable<(string Keyword, StoredEntry Entry)> mostRecentFirst) =>
        SearchHistoryBucketSupport.BuildBuckets(mostRecentFirst);

    private static void PersistNoLock()
    {
        try
        {
            Directory.CreateDirectory(Logger.UserDataDir);
            AtomicFileStore.Write(HistoryPath, JsonSerializer.Serialize(_buckets, JsonOptions), BackupPath);
        }
        catch (Exception ex)
        {
            Logger.Log($"[SearchHistoryStore] Failed to write history: {ex.Message}", LogLevel.Error);
        }
    }

    private static List<HistoryEntry> Flatten(Dictionary<string, List<StoredEntry>> buckets) =>
        SearchHistoryBucketSupport.Flatten(buckets);

    private static Dictionary<string, int> BuildPriorityCache(Dictionary<string, List<StoredEntry>> buckets) =>
        SearchHistoryBucketSupport.BuildPriorityCache(buckets);

    internal static string NormalizePath(string path)
    {
        var normalized = path.Trim().Trim('"')
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        try
        {
            if (!WslPath.IsPath(normalized))
                normalized = Path.GetFullPath(normalized);
        }
        catch
        {
        }

        return normalized.TrimEnd(Path.DirectorySeparatorChar);
    }
}
