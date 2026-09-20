using System.Runtime.InteropServices;
using Lertaro.Core.SearchIndex;
using Lertaro.Core.SearchIndex.Fzf;
using Lertaro.Core.IndexV2.Delta;
using Lertaro.Core.IndexV2.Persistence;
namespace Lertaro.Core.IndexV2.Search;
// Name-mode search: phase A matches unique names (SearchMatcher) + delta rows (renamed/added, matched
// individually since they aren't folded into the unique table until compaction); phase B fans each
// matched unique out through the uid->rows CSR and ranks everything with FzfTopN. Delta rows rank
// under their original row index (base overrides -- the old engine renames in place) or a synthetic
// index past Count in insertion order (added rows) -- FzfRank breaks ties by EntryIndex, so relative
// order stays equivalent to the old engine's append-and-rename-in-place behavior.
internal static class NameSearch
{
    // The scan keeps a WIDER unweighted top-N than what gets displayed, and only that headroom set gets
    // refined with the real percentage*consecutiveness weight (HighlightMask.ComputeWeight) afterward.
    // The weight is far too expensive to compute per candidate inside the hot scan -- it is dominated by
    // the DP fuzzy-highlight fallback for scattered matches -- so it is only ever paid for candidates
    // that could plausibly be shown.
    private const int RefinementHeadroomFactor = 5;

    public static void SearchStreaming(Snapshot snapshot, DeltaOverlay delta, FzfPattern pattern, int limit,
        Action<SearchResult> onResult, CancellationToken token, string? directoryFilterLower, string[]? fileNamePatterns = null)
    {
        if (!DriveAdmits(snapshot, pattern, out var matchAll))
            return;

        var directoryContext = ResolveDirectoryContext(snapshot, delta, directoryFilterLower);
        if (directoryContext.Excluded)
            return;

        // Nothing is truncated on the way through: the only ceiling is the index itself, since a search
        // cannot return more rows than exist. That bound matters for more than tidiness -- FzfTopN
        // pre-allocates twice its capacity, so deriving it from the caller's limit (which is now
        // effectively unbounded) would reserve arrays for a number of results nobody can reach.
        //
        // The arithmetic is widened deliberately. keep * RefinementHeadroomFactor overflows int once the
        // limit passes about 53 million, and a negative capacity used to reach FzfTopN.Finish and throw
        // from RemoveRange -- unreachable while the limit was clamped to 2000, immediate once it is not.
        var everything = snapshot.Count + delta.Added.Count;
        var keep = (int)Math.Min((long)Math.Max(limit, 8) * 8, everything);
        var scanKeep = matchAll || pattern.IsEmpty
            ? keep
            : (int)Math.Min((long)keep * RefinementHeadroomFactor, everything);
        var topN = new FzfTopN(Math.Max(scanKeep, 1));
        NameSearchRankCollector.CollectRanks(snapshot, delta, pattern, matchAll, directoryContext, topN, token, fileNamePatterns);

        var ranks = topN.Finish(scanKeep);
        if (!matchAll && !pattern.IsEmpty)
            RefineWithWeight(snapshot, delta, pattern, ranks);

        var seen = new HashSet<int>();
        var emitted = 0;
        foreach (var rank in ranks)
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(rank.EntryIndex))
                continue;
            var result = ResultBuilder.ToResult(snapshot, delta, rank);
            SearchResultRelevance.Apply(result, pattern);
            onResult(result);
            if (++emitted >= limit)
                break;
        }

        // The names alone did not fill the page: top it up with rows where a term is satisfied by an
        // ancestor folder instead. Gated on there being room left, so a query that already answers in
        // full never pays for it. Results still stream in phase order, but both paths receive the same
        // whole-path RankSortKey above, so every consumer that merges streamed results compares them on
        // relevance rather than on the order in which a phase happened to complete.
        //
        // Gating on emitted == 0 instead was too strict to be useful: one incidental name hit
        // suppressed the entire path pass, so a query whose initials matched both a file and a folder
        // returned only the file and hid every result under the folder.
        if (emitted < limit)
            PathTermFallback.SearchStreaming(snapshot, delta, pattern, limit - emitted, result =>
            {
                SearchResultRelevance.Apply(result, pattern);
                onResult(result);
            }, token, directoryFilterLower);
    }

    [ThreadStatic]
    private static Dictionary<string, double>? _weightsByName;

    // Bounded refinement: only ever runs over the scanKeep-sized headroom set above, never the full
    // matched set. Ranking-only (FzfResultRank.ApplyWeight never rejects), so this can't drop a result.
    private static void RefineWithWeight(Snapshot snapshot, DeltaOverlay delta, FzfPattern pattern, List<FzfRank> ranks)
    {
        // The weight depends on the name and the pattern, and on nothing else about the row -- so every
        // row sharing a name shares its weight, and a set of a few thousand holds only about two thirds
        // that many distinct names. Worth remembering because the calculation is not cheap for a query
        // that matches through an alias: it has to ask the provider for the candidate's own spellings.
        // Reused per thread rather than built per search: a query whose names all match literally gets
        // no benefit from the memo and should not pay to allocate one either.
        var weights = _weightsByName ??= new Dictionary<string, double>(StringComparer.Ordinal);
        weights.Clear();
        for (var i = 0; i < ranks.Count; i++)
        {
            var rank = ranks[i];
            var name = GetNameForEntry(snapshot, delta, rank.EntryIndex);
            if (name.Length == 0)
                continue;
            ref var weight = ref CollectionsMarshal.GetValueRefOrAddDefault(weights, name, out var known);
            if (!known)
                weight = HighlightMask.ComputeWeight(name, pattern);
            ranks[i] = FzfResultRank.ApplyWeight(rank, weight);
        }
        FzfRankRadixSorter.Sort(ranks);

        // Emptied here rather than only on the way in. Clearing on entry leaves the buckets from the last
        // search sitting in a thread static for as long as the process is idle, and a whole-drive query
        // sizes them to its own name count -- see SearchScratchPolicy.
        SearchScratchPolicy.ClearAndTrim(weights);
    }

    // Mirrors ResultBuilder.ToResult's entryIndex->name resolution (base row, possibly overridden, vs
    // an Added delta record past Snapshot.Count).
    private static string GetNameForEntry(Snapshot snapshot, DeltaOverlay delta, int entryIndex)
        => entryIndex >= snapshot.Count ? delta.Added[entryIndex - snapshot.Count].Name : delta.NameOf(entryIndex);

    // Mirrors Searcher's drive gate: a foreign-drive query returns nothing; a bare drive prefix with
    // no terms ("t:") matches everything (TryMatch trivially succeeds on an empty pattern).
    private static bool DriveAdmits(Snapshot snapshot, FzfPattern pattern, out bool matchAll)
    {
        matchAll = false;
        if (pattern.TargetDrive != null && !pattern.TargetDrive.Equals(snapshot.SourceKey, StringComparison.OrdinalIgnoreCase))
            return false;
        if (pattern.IsEmpty)
        {
            if (pattern.TargetDrive == null)
                return false;
            matchAll = true;
        }
        return true;
    }

    internal readonly record struct DirectoryContext(bool Excluded, int RootFilterRow, int AncestorRow, string? FilterLower);

    internal static DirectoryContext ResolveDirectoryContext(Snapshot snapshot, DeltaOverlay? delta, string? directoryFilterLower)
    {
        var sourceRootLower = snapshot.SourceRoot.ToLowerInvariant();
        if (directoryFilterLower != null && directoryFilterLower.Equals(sourceRootLower, StringComparison.Ordinal))
            directoryFilterLower = null;
        if (DirectoryFilterResolver.ExcludesSource(snapshot, directoryFilterLower))
            return new DirectoryContext(true, -1, -1, directoryFilterLower);
        if (directoryFilterLower == null)
            return new DirectoryContext(false, -1, -1, null);

        var rootFilterRow = -1;
        var ancestorRow = -1;
        if (DirectoryFilterResolver.TryResolve(snapshot, delta, directoryFilterLower, forceLastSegmentAsQuery: false, out var resolved, out var remainder))
        {
            // IsUnderCached only accepts snapshot rows. Keep an added directory as a path-prefix
            // filter so its live path still scopes both base and delta rows without indexing a
            // synthetic entry into the snapshot-only ancestor cache.
            if (remainder.Length == 0 && resolved < snapshot.Count)
                rootFilterRow = resolved;
            else if (resolved < snapshot.Count)
                ancestorRow = resolved;
        }
        return new DirectoryContext(false, rootFilterRow, ancestorRow, directoryFilterLower);
    }

    // True when `row` (a base-snapshot row) satisfies the resolved directory filter.
    internal static bool RowMatchesFilter(Snapshot snapshot, DeltaOverlay delta, int row, DirectoryContext ctx, Dictionary<int, bool> membership)
    {
        if (ctx.FilterLower == null)
            return true;
        if (ctx.RootFilterRow >= 0)
            return DirectoryFilterResolver.IsUnderCached(snapshot, row, ctx.RootFilterRow, membership);
        if (ctx.AncestorRow >= 0 && !DirectoryFilterResolver.IsUnderCached(snapshot, row, ctx.AncestorRow, membership))
            return false;
        return delta.GetFullPath(row).StartsWith(ctx.FilterLower, StringComparison.OrdinalIgnoreCase);
    }

}
