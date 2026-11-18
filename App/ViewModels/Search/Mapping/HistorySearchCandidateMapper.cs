using System.IO;
using Lertaro.Core;
using Lertaro.Core.SearchIndex;
using Lertaro.PluginSdk.Services;
using Lertaro.App.Services;

namespace Lertaro.App.ViewModels.Search.Mapping;

// Re-introduces stored path history whose own NAME matches the query. This is only path frecency: there is
// no query-to-path association layer, so an old keyword can never make an unrelated name appear.
internal static class HistorySearchCandidateMapper
{
    private const int MaxCandidates = 50;

    public static List<SearchResultMapper.RankedCandidate> Collect(
        FuzzyQuery fuzzy,
        string? scope,
        int typeRank = int.MaxValue) =>
        CollectNamed(fuzzy, scope, typeRank);

    public static List<SearchResultMapper.RankedCandidate> CollectNamed(
        FuzzyQuery fuzzy,
        string? scope,
        int typeRank = int.MaxValue) =>
        CollectNamed(
            fuzzy,
            scope,
            SearchHistoryStore.GetEntries(),
            SafeFileExists,
            SafeDirectoryExists,
            typeRank);

    internal static List<SearchResultMapper.RankedCandidate> Collect(
        FuzzyQuery fuzzy,
        string? scope,
        IEnumerable<HistoryEntry> entries,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        int typeRank = int.MaxValue) =>
        CollectNamed(fuzzy, scope, entries, fileExists, directoryExists, typeRank);

    internal static List<SearchResultMapper.RankedCandidate> CollectNamed(
        FuzzyQuery fuzzy,
        string? scope,
        IEnumerable<HistoryEntry> entries,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        int typeRank = int.MaxValue)
    {
        if (fuzzy.IsEmpty)
            return [];

        var matches = new List<(HistoryEntry Entry, string StoredName, MatchRank Match)>();
        var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedScope = string.IsNullOrEmpty(scope) ? null : SearchResultHelper.NormalizePath(scope);
        var leftMatches = 0;
        foreach (var entry in entries)
        {
            var storedName = entry.Kind == HistoryEntryKind.Application
                ? Path.GetFileNameWithoutExtension(entry.Path)
                : Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(storedName))
                continue;

            var match = fuzzy.Rank(storedName);
            if (!match.IsMatch)
                continue;

            matches.Add((entry, storedName, match));
            if (match.IsLeftContiguous && ++leftMatches >= MaxCandidates)
                break;
        }

        matches.Sort(static (a, b) =>
        {
            if (a.Match.IsLeftContiguous != b.Match.IsLeftContiguous)
                return a.Match.IsLeftContiguous ? -1 : 1;

            var byCount = b.Entry.Count.CompareTo(a.Entry.Count);
            if (byCount != 0)
                return byCount;

            var byQuality = MatchRank.CompareQuality(a.Match, b.Match);
            if (byQuality != 0)
                return byQuality;

            var byTime = b.Entry.Time.CompareTo(a.Entry.Time);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Entry.Path, b.Entry.Path);
        });

        var candidates = new List<SearchResultMapper.RankedCandidate>(MaxCandidates);
        foreach (var (entry, _, _) in matches)
        {
            if (candidates.Count >= MaxCandidates)
                break;

            if (!TryCreateResult(entry, fuzzy.Text, normalizedScope, fileExists, directoryExists,
                    out var result, out var normalizedPath)
                || !candidatePaths.Add(normalizedPath)
                || IsExcludedFromSearch(result))
                continue;

            var match = fuzzy.Rank(result.Name);
            if (!match.IsMatch)
                continue;

            candidates.Add(new SearchResultMapper.RankedCandidate(
                result,
                IsFavorite: false,
                HistoryCount: entry.Count,
                TypeRank: typeRank,
                Match: match,
                NormalizedPath: normalizedPath,
                IsHistoryOnly: false));
        }

        return candidates;
    }

    internal static bool SafeFileExists(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            return SearchReachabilityGate.IsPathReachable(path);
        return File.Exists(path);
    }

    internal static bool SafeDirectoryExists(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            return SearchReachabilityGate.IsPathReachable(path);
        return Directory.Exists(path);
    }

    internal static bool IsExcludedFromSearch(
        AppSearchResult row,
        IReadOnlySet<string> unreachable,
        Func<string, bool> isHiddenOrSystem) =>
        !SearchReachabilityGate.IsPathReachable(row.FullPath, unreachable)
        || IsHiddenSystemLocalPath(row.FullPath, isHiddenOrSystem);

    internal static bool IsExcludedFromSearch(AppSearchResult row) =>
        IsExcludedFromSearch(
            row,
            SearchReachabilityGate.UnreachableSources,
            FileSystemItemFilter.IsHiddenOrSystem);

    private static bool IsHiddenSystemLocalPath(string path, Func<string, bool> isHiddenOrSystem) =>
        path.Length > 2 && path[1] == ':' && isHiddenOrSystem(path);

    internal static bool TryCreateResult(
        HistoryEntry entry,
        string query,
        string? normalizedScope,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        out AppSearchResult result,
        out string normalizedPath)
    {
        result = null!;
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(entry.Path) || !Path.IsPathFullyQualified(entry.Path))
            return false;

        try
        {
            var rawPath = entry.Path.Trim().Trim('"');
            var path = WslPath.IsPath(rawPath)
                ? rawPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                : Path.GetFullPath(rawPath);
            var isDirectory = entry.Kind == HistoryEntryKind.Folder;
            if (!(isDirectory ? directoryExists(path) : fileExists(path)))
                return false;

            normalizedPath = SearchResultHelper.NormalizePath(path);
            if (normalizedScope != null &&
                (!SearchResultHelper.IsPathInsideScope(normalizedPath, normalizedScope) ||
                 string.Equals(normalizedPath, normalizedScope, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (entry.Kind == HistoryEntryKind.Application &&
                SearchableItemMapper.TryCreateApplicationResult(path, query, out result))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(path) ?? string.Empty;
            var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmedPath);
            var isApplication = entry.Kind == HistoryEntryKind.Application;
            result = new AppSearchResult
            {
                Name = string.IsNullOrEmpty(name) ? path : (isApplication ? Path.GetFileNameWithoutExtension(name) : name),
                FullPath = path,
                ParentDir = isApplication ? string.Empty : parent,
                ContextDirectory = isDirectory ? path : parent,
                IsDir = isDirectory,
                Drive = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar).TrimEnd(':') ?? string.Empty,
                ResultKind = isApplication ? "Application" : "File",
                InstantResultOnExecute = isApplication ? () => FileExecutor.OpenFileOrFolder(path) : null,
                InstantResultActionArgument = isApplication ? path : string.Empty,
                SearchQuery = query
            };
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
