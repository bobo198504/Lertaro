using Lertaro.Core.SearchIndex.Fzf;
using Lertaro.Core.IndexV2.Delta;
using Lertaro.Core.IndexV2.Persistence;
using Lertaro.Core.Services.Plugin.DirectoryIndex;
namespace Lertaro.Core.IndexV2.Search;

// Split out of NameSearch purely to keep that file under the repo's per-file line limit: this is the
// candidate-collection half of name search (base-row fanout, delta rows, file-scope filtering and the
// empty-pattern sort key). It has no state of its own and always operates on the snapshot/delta pair
// its caller hands it.
internal static class NameSearchRankCollector
{
    private static readonly FzfPatternResult EmptyPatternMatch = new(0, int.MaxValue, int.MaxValue, 0, false);
    internal static void CollectRanks(Snapshot snapshot, DeltaOverlay delta, FzfPattern pattern, bool matchAll, NameSearch.DirectoryContext ctx, FzfTopN topN, CancellationToken token, string[]? fileNamePatterns)
    {
        var membership = ctx.FilterLower != null ? new Dictionary<int, bool>() : null;

        // Hoisted out of the per-row loops below. Snapshot.Flags builds a span over mapped memory on
        // every access and IsSuperseded is three hash lookups, both of which the fanout was paying for
        // every one of the tens of thousands of rows a broad query reaches.
        var flags = snapshot.Flags;
        const ushort deletedFlag = (ushort)FileRecordFlags.Deleted;
        var mayBeSuperseded = !delta.HasNoBaseChanges;

        if (matchAll)
        {
            // Unique-first like the pattern path below: the empty-pattern sort key depends only on the
            // name, so it's computed once per unique instead of materializing a string per row.
            // Superseded rows are skipped in the fanout, so no override name can be needed here.
            var worker = SearchMatcher.RentWorker();
            for (var uid = 0; uid < snapshot.UniqueCount; uid++)
            {
                if ((uid & 0xFFF) == 0)
                    token.ThrowIfCancellationRequested();
                var utf8 = snapshot.UniqueNameUtf8(uid);
                if (utf8.Length == 0)
                    continue;
                if (fileNamePatterns != null
                    && !FilterPatternHelper.Matches(snapshot.GetUniqueName(uid), fileNamePatterns)
                    && !SearchMatcher.HasDirectoryRow(snapshot, uid))
                    continue;
                var sortKey = MatchAllSortKey(snapshot, uid, worker, utf8);
                foreach (var row in snapshot.RowsForUid(uid))
                {
                    if ((flags[row] & deletedFlag) != 0 || (mayBeSuperseded && delta.IsSuperseded(row)))
                        continue;
                    if (membership != null && !NameSearch.RowMatchesFilter(snapshot, delta, row, ctx, membership))
                        continue;
                    if (!MatchesFileScope(snapshot, delta, row, fileNamePatterns))
                        continue;
                    topN.Add(new FzfRank(row, 0, sortKey));
                }
            }
            SearchMatcher.ReturnWorker(worker);
        }
        else
        {
            var hits = SearchMatcher.RentHitList();
            SearchMatcher.MatchUniques(snapshot, pattern, hits, token, fileNamePatterns);
            foreach (var m in hits)
            {
                foreach (var row in snapshot.RowsForUid(m.Uid))
                {
                    if ((flags[row] & deletedFlag) != 0 || (mayBeSuperseded && delta.IsSuperseded(row)))
                        continue;
                    if (membership != null && !NameSearch.RowMatchesFilter(snapshot, delta, row, ctx, membership))
                        continue;
                    if (!MatchesFileScope(snapshot, delta, row, fileNamePatterns))
                        continue;
                    // The per-unique sort key applies verbatim to every row of that unique --
                    // EntryIndex isn't packed into the key, so nothing is recomputed per row.
                    topN.Add(new FzfRank(row, m.Match.Score, m.SortKey));
                }
            }
            SearchMatcher.ReturnHitList(hits);
        }

        MatchDeltaRows(snapshot, delta, pattern, matchAll, ctx, topN, fileNamePatterns);
    }

    private static bool MatchesFileScope(Snapshot snapshot, DeltaOverlay delta, int row, string[]? patterns)
        => patterns == null || IsDirectory(snapshot, delta, row) || FilterPatternHelper.Matches(delta.NameOf(row), patterns);
    private static bool IsDirectory(Snapshot snapshot, DeltaOverlay delta, int row)
        => delta.BaseOverrides.TryGetValue(row, out var record)
            ? (record.Flags & (ushort)FileRecordFlags.Directory) != 0
            : snapshot.IsDirectory(row);

    private static bool MatchesFileScope(string name, ushort flags, string[]? patterns)
        => patterns == null || (flags & (ushort)FileRecordFlags.Directory) != 0 || FilterPatternHelper.Matches(name, patterns);

    private static ulong MatchAllSortKey(Snapshot snapshot, int uid, SearchMatcher.Worker worker, ReadOnlySpan<byte> utf8)
    {
        if (snapshot.IsUniqueAscii(uid))
            return FzfBytePattern.ForDefaultScheme(0, utf8, EmptyPatternMatch).SortKey;
        if (worker.Scratch.Length < utf8.Length)
            worker.Scratch = new char[Math.Max(utf8.Length, worker.Scratch.Length * 2)];
        var written = System.Text.Encoding.UTF8.GetChars(utf8, worker.Scratch);
        return FzfResultRank.ForDefaultScheme(0, worker.Scratch.AsSpan(0, written), EmptyPatternMatch).SortKey;
    }

    // Delta churn is always small (live USN/watcher batches, not bulk scans), so both loops just check
    // the row's own full path against the filter prefix -- correct for renamed/moved/added rows alike,
    // unlike the row-index ancestor cache above (a snapshot-only optimization for the hot base-row path).
    private static void MatchDeltaRows(Snapshot snapshot, DeltaOverlay delta, FzfPattern pattern, bool matchAll, NameSearch.DirectoryContext ctx, FzfTopN topN, string[]? fileNamePatterns)
    {
        var slab = new FzfSlab();
        var queryLen = pattern.GetTotalTermLength();

        foreach (var (row, record) in delta.BaseOverrides)
        {
            if (record.Name.Length == 0)
                continue;
            if (!MatchesFileScope(record.Name, record.Flags, fileNamePatterns))
                continue;
            var match = EmptyPatternMatch;
            if (!matchAll && !SearchMatcherRow.TryMatchNameOrAliases(pattern, record.Name, record.Aliases, record.ProviderIds, queryLen, slab, out match))
                continue;
            if (ctx.FilterLower != null && !delta.GetFullPath(row).StartsWith(ctx.FilterLower, StringComparison.OrdinalIgnoreCase))
                continue;
            topN.Add(FzfResultRank.ForDefaultScheme(row, record.Name, match));
        }
        for (var i = 0; i < delta.Added.Count; i++)
        {
            var record = delta.Added[i];
            if (record.Removed || record.Name.Length == 0)
                continue;
            if (!MatchesFileScope(record.Name, record.Flags, fileNamePatterns))
                continue;
            var match = EmptyPatternMatch;
            if (!matchAll && !SearchMatcherRow.TryMatchNameOrAliases(pattern, record.Name, record.Aliases, record.ProviderIds, queryLen, slab, out match))
                continue;
            if (ctx.FilterLower != null && !delta.GetFullPath(record).StartsWith(ctx.FilterLower, StringComparison.OrdinalIgnoreCase))
                continue;
            topN.Add(FzfResultRank.ForDefaultScheme(snapshot.Count + i, record.Name, match));
        }
    }
}
