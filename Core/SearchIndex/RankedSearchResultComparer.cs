namespace Lertaro.Core.SearchIndex;

/// <summary>
/// The full window's ordering, the equivalent of <see cref="RankKeyComparer"/> over rows that carry their
/// keys in fields instead of a copied key array. The key order is deliberately the same one the quick
/// window uses, and both read it from <see cref="RankPolicy.CompareSectionRank"/> and
/// <see cref="RankPolicy.CompareCommonTail"/>, so one query cannot produce two file orders.
/// </summary>
/// <remarks>
/// Two differences follow from what this window has: no display type layer (TypeRank is always 0), and no
/// history-tail rows, because the window adds no history rows of its own -- history only lifts a row it
/// found, and only through the threshold tier <see cref="SearchResultRanking"/> wrote.
/// Reads only decorated fields, so a row that was never decorated compares as a weak normal-priority row.
/// </remarks>
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

        var tierX = (HistoryTier)((x.RankFlags >> 3) & 3);
        var tierY = (HistoryTier)((y.RankFlags >> 3) & 3);
        var tieX = RankPolicy.HistoryTie(isHistoryOnly: false, tierX, x.HistoryScoreX100);
        var tieY = RankPolicy.HistoryTie(isHistoryOnly: false, tierY, y.HistoryScoreX100);

        // Owner's final ladder, mirrored from RankKeyComparer: favorites, lifted history, left placement,
        // file/folder, then the remaining match quality.
        var xFavorite = x.RankScore == RankPolicy.FavoriteStrongScore;
        var yFavorite = y.RankScore == RankPolicy.FavoriteStrongScore;
        if (xFavorite != yFavorite)
            return xFavorite ? -1 : 1;

        var rankX = new SectionRank(
            x.RankScore,
            tieX,
            0,
            (int)((x.RankSortKey >> 56) & 0x7F),
            CoverageOf(x.RankSortKey),
            ContiguityOf(x.RankSortKey));
        var rankY = new SectionRank(
            y.RankScore,
            tieY,
            0,
            (int)((y.RankSortKey >> 56) & 0x7F),
            CoverageOf(y.RankSortKey),
            ContiguityOf(y.RankSortKey));

        var historyTierX = sectionX == RankSection.Low ? HistoryTier.None : tierX;
        var historyTierY = sectionY == RankSection.Low ? HistoryTier.None : tierY;
        var xHistoryLifted = historyTierX >= HistoryTier.H3;
        var yHistoryLifted = historyTierY >= HistoryTier.H3;
        if (xHistoryLifted != yHistoryLifted)
            return xHistoryLifted ? -1 : 1;

        var c = historyTierY.CompareTo(historyTierX);
        if (c != 0)
            return c;

        var xLeftContiguous = rankX.Start == 0 && rankX.Contiguity >= 1;
        var yLeftContiguous = rankY.Start == 0 && rankY.Contiguity >= 1;
        if (xLeftContiguous != yLeftContiguous)
            return xLeftContiguous ? -1 : 1;

        var xHistory = sectionX == RankSection.Low ? 0 : tieX;
        var yHistory = sectionY == RankSection.Low ? 0 : tieY;
        c = yHistory.CompareTo(xHistory);
        if (c != 0)
            return c;

        var compare = x.IsDir.CompareTo(y.IsDir);
        if (compare != 0)
            return compare;

        compare = rankY.Contiguity.CompareTo(rankX.Contiguity);
        if (compare != 0)
            return compare;

        compare = rankY.Coverage.CompareTo(rankX.Coverage);
        if (compare != 0)
            return compare;

        compare = tierX.CompareTo(tierY);
        if (compare != 0)
            return compare;

        compare = ((byte)sectionX).CompareTo((byte)sectionY);
        if (compare != 0)
            return compare;

        compare = ((uint)(x.RankSortKey >> 32)).CompareTo((uint)(y.RankSortKey >> 32));
        if (compare != 0)
            return compare;

        return RankPolicy.CompareCommonTail(TailOf(x), TailOf(y));
    }

    // Trailing separator trimmed, exactly as the window's own comparer has always done: folder rows arrive
    // spelled both ways, and a tie that depends on which one a caller happened to use is not an order.
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
