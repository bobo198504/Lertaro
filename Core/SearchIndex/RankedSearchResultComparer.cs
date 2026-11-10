namespace Lertaro.Core.SearchIndex;

/// <summary>
/// Full-window V3 ordering over pre-decorated <see cref="SearchResult"/> rows. Mirrors
/// <see cref="RankKeyComparer"/> exactly (with <c>TypeRank = 0</c> and <c>IsHistoryOnly = false</c>) so the
/// quick and full windows preserve pairwise order across shared candidates.
/// </summary>
public sealed class RankedSearchResultComparer : IComparer<SearchResult>
{
    public static readonly RankedSearchResultComparer Instance = new();

    public int Compare(SearchResult? x, SearchResult? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x == null)
            return -1;
        if (y == null)
            return 1;

        var sectionX = (RankSection)(x.RankFlags & 3);
        var sectionY = (RankSection)(y.RankFlags & 3);

        // Same skeleton as the quick window's kernel: the two boundaries outrank quality, and quality outranks
        // the section. Pinned is the user's explicit "always on top"; HistoryTail is a weak history-only row.
        if ((sectionX == RankSection.Pinned) != (sectionY == RankSection.Pinned))
            return sectionX == RankSection.Pinned ? -1 : 1;
        if ((sectionX == RankSection.HistoryTail) != (sectionY == RankSection.HistoryTail))
            return sectionX == RankSection.HistoryTail ? 1 : -1;

        return CompareSlow(x, y);
    }

    // The original field-by-field ordering. Kept as the definition of what RankOrderKey packs, and the
    // path every pair takes when a row carries a history tie.
    internal int CompareSlow(SearchResult x, SearchResult y)
    {
        var sectionX = SectionOf(x);
        var sectionY = SectionOf(y);
        var c = ((byte)sectionX).CompareTo((byte)sectionY);
        if (c != 0)
            return c;

        var gradeX = SearchResultRanking.GradeOf(x);
        var gradeY = SearchResultRanking.GradeOf(y);
        var tierX = (HistoryTier)((x.RankFlags >> 3) & 3);
        var tierY = (HistoryTier)((y.RankFlags >> 3) & 3);
        var tieX = RankPolicy.HistoryTie(isHistoryOnly: false, tierX, x.HistoryScoreX100);
        var tieY = RankPolicy.HistoryTie(isHistoryOnly: false, tierY, y.HistoryScoreX100);
        var prioX = PriorityOrderOf(x);
        var prioY = PriorityOrderOf(y);
        // Fork: the left edge, contiguity and coverage lead the keys inside a section, so they have to reach
        // the kernel. All three live in the high 32 bits the engine packed (start << 24, coverage at 13..21,
        // contiguity at 5..12); unpack them here instead of widening the signal fields, so the packed key
        // stays the single source of truth.
        var rankX = new SectionRank(
            x.RankScore,
            RankPolicy.HistoryTie(isHistoryOnly: false, tierX, x.HistoryScoreX100),
            0,
            (int)((x.RankSortKey >> 56) & 0x7F),
            CoverageOf(x.RankSortKey),
            ContiguityOf(x.RankSortKey));
        var rankY = new SectionRank(
            y.RankScore,
            RankPolicy.HistoryTie(isHistoryOnly: false, tierY, y.HistoryScoreX100),
            0,
            (int)((y.RankSortKey >> 56) & 0x7F),
            CoverageOf(y.RankSortKey),
            ContiguityOf(y.RankSortKey));

        // Match quality first, then the section -- see RankKeyComparer for why: a demoted path must lose to an
        // equally good row, not to one whose letters were scattered across the name.
        var byStart = rankX.Start.CompareTo(rankY.Start);
        if (byStart != 0)
            return byStart;

        var byContiguity = rankY.Contiguity.CompareTo(rankX.Contiguity);
        if (byContiguity != 0)
            return byContiguity;

        var compare = ((byte)sectionX).CompareTo((byte)sectionY);
        if (compare != 0)
            return compare;

        switch (sectionX)
        {
            case RankSection.Pinned:
                var pinX = (x.RankScore & SearchResultRanking.FlagAlwaysTop) != 0 ? 1 : 0;
                var pinY = (y.RankScore & SearchResultRanking.FlagAlwaysTop) != 0 ? 1 : 0;
                c = pinY.CompareTo(pinX);
                if (c != 0)
                    return c;
                var favPinX = (x.RankScore & SearchResultRanking.FlagFavoritePin) != 0 ? 1 : 0;
                var favPinY = (y.RankScore & SearchResultRanking.FlagFavoritePin) != 0 ? 1 : 0;
                c = favPinY.CompareTo(favPinX);
                if (c != 0)
                    return c;
                c = gradeX.CompareTo(gradeY);
                if (c != 0)
                    return c;
                c = prioX.CompareTo(prioY);
                if (c != 0)
                    return c;
                c = tierY.CompareTo(tierX);
                if (c != 0)
                    return c;
                c = tieY.CompareTo(tieX);
                if (c != 0)
                    return c;
                break;

            case RankSection.Low:
                c = gradeX.CompareTo(gradeY);
                if (c != 0)
                    return c;
                c = tierY.CompareTo(tierX);
                if (c != 0)
                    return c;
                c = tieY.CompareTo(tieX);
                if (c != 0)
                    return c;
                break;

            default:
                c = gradeX.CompareTo(gradeY);
                if (c != 0)
                    return c;
                var favX = (x.RankScore & SearchResultRanking.FlagFavorite) != 0;
                var favY = (y.RankScore & SearchResultRanking.FlagFavorite) != 0;
                if (favX != favY)
                    return favX ? -1 : 1;
                c = prioX.CompareTo(prioY);
                if (c != 0)
                    return c;
                c = tierY.CompareTo(tierX);
                if (c != 0)
                    return c;
                c = tieY.CompareTo(tieX);
                if (c != 0)
                    return c;
                break;
        }

        // Packed quality, best first ("smaller is better" in high 32 bits; 0 means unpacked/worst).
        var keyX = x.RankSortKey == 0 ? uint.MaxValue : (uint)(x.RankSortKey >> 32);
        var keyY = y.RankSortKey == 0 ? uint.MaxValue : (uint)(y.RankSortKey >> 32);
        c = keyX.CompareTo(keyY);
        if (c != 0)
            return c;

        return RankPolicy.CompareCommonTail(TailOf(x), TailOf(y));
    }

    private static RankSection SectionOf(SearchResult result) =>
        result.RankFlags == 0 && result.RankScore == 0
            ? RankSection.Boosted
            : (RankSection)(result.RankFlags & 3);

    private static int PriorityOrderOf(SearchResult result) =>
        result.RankFlags == 0 && result.RankScore == 0
            ? RankPolicy.PriorityOrder(IndexRuleEffect.Normal)
            : (result.RankScore >> SearchResultRanking.PriorityShift) & 3;

    private static RankTail TailOf(SearchResult result)
    {
        var path = result.Path.Length > 3 && result.Path[^1] == '\\'
            ? result.Path.TrimEnd('\\')
            : result.Path;

        return new RankTail(
            result.IsDir,
            result.Name,
            path,
            RankPolicy.DriveRank(result.Drive),
            result.RankSortKey & 0xFFFF_FFFFUL);
    }

    // Coverage lives in bits 13..21 of the high 32 as "smaller is better" (see SearchResultRelevance:
    // coveragePoint = (1 - coverage) * CoverageMax, CoverageMax = 0x1FF). Unpack it back to 0..1 so the kernel
    // compares it the same way the quick window does.
    private static double CoverageOf(ulong rankSortKey)
        => 1.0 - (((rankSortKey >> 45) & 0x3FF) / 511.0);

    // Contiguity sits in bits 5..12 of the high 32, same "smaller is better" encoding with an 8-bit span
    // (contiguityPoint = (1 - contiguity) * 0xFF), so the packed bits 37..44 of the full key.
    private static double ContiguityOf(ulong rankSortKey)
        => 1.0 - (((rankSortKey >> 37) & 0xFF) / 255.0);
}
