using System.IO;
using Lertaro.Core;
using Lertaro.Core.SearchIndex;
using Lertaro.PluginSdk.Services;

namespace Lertaro.App.ViewModels.Search.Mapping;

// The index returns a bounded page before the App can apply its history-aware ranking. A frequently used
// portable executable can lose every slot to unrelated short-name matches, so its history never reaches the
// ordering pass. Re-introduce high-use paths whose OWN NAMES match the query; the stored search keyword is
// irrelevant here, and final placement still comes from the shared ranking kernel.
internal static class FrequentHistoryCandidateMapper
{
    internal const int MinCount = SearchHistoryStore.MinAssociationOrganicOpens;
    internal const int MaxCandidates = 20;

    public static List<SearchResultMapper.RankedCandidate> Collect(
        FuzzyQuery fuzzy,
        string? scope,
        RankContext? rankContext,
        int typeRank) =>
        Collect(
            fuzzy,
            scope,
            SearchHistoryStore.GetEntries(),
            HistorySearchCandidateMapper.SafeFileExists,
            HistorySearchCandidateMapper.SafeDirectoryExists,
            rankContext,
            typeRank);

    internal static List<SearchResultMapper.RankedCandidate> Collect(
        FuzzyQuery fuzzy,
        string? scope,
        IEnumerable<HistoryEntry> entries,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        RankContext? rankContext = null,
        int typeRank = int.MaxValue)
    {
        if (fuzzy.IsEmpty)
            return [];

        var normalizedScope = string.IsNullOrEmpty(scope) ? null : SearchResultHelper.NormalizePath(scope);
        var candidates = new List<SearchResultMapper.RankedCandidate>(MaxCandidates);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries
                     .Where(entry => entry.Count >= MinCount)
                     .OrderByDescending(entry => entry.Count)
                     .ThenByDescending(entry => entry.Time))
        {
            if (!RawNameMatches(fuzzy, entry))
                continue;
            if (!HistorySearchCandidateMapper.TryCreateResult(
                    entry, fuzzy.Text, normalizedScope, fileExists, directoryExists,
                    out var result, out var normalizedPath))
                continue;
            if (!paths.Add(normalizedPath))
                continue;
            if (rankContext?.PriorityResolver?.Invoke(normalizedPath, result.IsDir).Effect == IndexRuleEffect.Low)
                continue;
            if (HistorySearchCandidateMapper.IsExcludedFromSearch(result))
                continue;

            var matched = fuzzy.RankWithEvidence(
                result.Name,
                MatchEvidenceField.FileName,
                isDirectory: result.IsDir);
            if (!matched.Rank.IsMatch)
                continue;

            candidates.Add(new SearchResultMapper.RankedCandidate(
                result,
                IsFavorite: false,
                HistoryCount: entry.Count,
                TypeRank: typeRank,
                Match: matched.Rank,
                NormalizedPath: normalizedPath,
                Source: RankingCandidateSource.History,
                Evidence: matched.Evidence));
            if (candidates.Count >= MaxCandidates)
                break;
        }

        return candidates;
    }

    private static bool RawNameMatches(FuzzyQuery fuzzy, HistoryEntry entry)
    {
        try
        {
            var path = entry.Path.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(path);
            if (entry.Kind == HistoryEntryKind.Application)
                name = Path.GetFileNameWithoutExtension(name);
            return name.Length > 0 && fuzzy.Rank(name).IsMatch;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
