namespace Lertaro.Core.SearchIndex;

// Shared monotonic ranking kernel (V3). The quick/inline windows sort RankKey arrays via RankKeyComparer;
// the full window decorates SearchResult rows once via SearchResultRanking and sorts via
// RankedSearchResultComparer. Both windows share the section, grade, band, tie-break and tail rules;
// only the quick/inline window carries the configured result-type order:
//
//   Section 0 Pinned       explicit High+AlwaysTop pins first, then strong user Favorites
//   Section 1 Boosted      ordinary High/Normal textual matches (Grade 0..3: ExactIdentity .. SecondaryField)
//   Section 2 Low          explicit Low-priority textual matches (Grade 0..3)
//   Section 3 HistoryTail  pure learned query->path history associations (Grade 4, never jumps ahead of text)

public enum RankSection
{
    Pinned = 0,
    Boosted = 1,
    Low = 2,
    HistoryTail = 3,
}

public enum HistoryTier
{
    None = 0,
    Tail = 1,
    H3 = 2,
    H5 = 3,
}

public readonly record struct RankSignals(
    MatchRank Match,
    bool IsHistoryOnly,
    bool IsFavorite,
    IndexRuleEffect Priority,
    bool AlwaysTop,
    int HistoryScoreX100,
    int TypeRank,
    bool IsDir,
    string Name,
    string NormalizedPath,
    int Drive,
    ulong EngineTie = 0,
    bool TypeRankApplies = true,
    // False when the caller knows nothing outranks the file bucket in the user's result-type order: an
    // exact-named directory then keeps its ExactIdentity (see RankPolicy.GradeForRanking).
    bool DemoteDirectoryIdentity = true);

public readonly record struct RankKey(
    byte AlwaysTop,
    byte Section,
    int Score,
    int HistoryTie,
    int TypeRank,
    MatchRank Match,
    bool IsDir,
    string Name,
    string NormalizedPath,
    int Drive,
    ulong EngineTie,
    byte FavoritePin = 0,
    MatchEvidenceGrade Grade = MatchEvidenceGrade.DirectStrong,
    bool IsFavorite = false,
    IndexRuleEffect Priority = IndexRuleEffect.Normal,
    HistoryTier Tier = HistoryTier.None,
    bool TypeRankApplies = true)
{
    public RankTail Tail => new(IsDir, Name, NormalizedPath, Drive, EngineTie);
    // The configured result-type order is a late tie-break, not a group: section, grade, priority,
    // history band and the match placement (contiguity, shape, start) all run first; only coverage and
    // match tier come after it. It only compares the three built-in buckets
    // (Applications, System settings, Files); every other row shares one rank behind them.
    public int EffectiveTypeRank => TypeRank;
}

public static class RankPolicy
{
    /// <summary>
    /// The ranking model's version, written into the calibration log so rows collected under different
    /// policies can be told apart afterwards. Bump it with any change to the score, the sections or the key
    /// order. It is a code constant, not a setting: a policy version that a user could edit measures nothing.
    /// </summary>
    public const int Version = 5;

    public const int StrongWeight = 10;
    public const int PriorityWeight = 8;
    public const int FavoriteStrongScore = 44;
    // A habit outranks everything a text match can earn. The owner's rule is that the thing used most
    // often should come first, and a rarely used exact match should not jump ahead of it -- so the
    // history tiers sit above FavoriteStrongScore (44) rather than between the match weights (26 max)
    // and it. Previously H5 scored 34 and H3 22, which put H3 below an ordinary strong match and kept
    // a habitual row off the top of the list.
    public const int HistoryFiveScore = 60;
    public const int HistoryThreeScore = 50;
    public const int HistoryTailScore = 6;

    // History buckets (redesign 3.4). The thresholds decide the tier; the caps only truncate, so a fourth
    // 5+ row drops into H3 rather than out of the boosted rows. Scores arrive as X100 from
    // SearchHistoryStore.ScoreSnapshot(now), already decayed -- the budget must not decay twice.
    public const int HistoryFiveThresholdX100 = 500;
    public const int HistoryThreeThresholdX100 = 300;
    public const int HistoryFiveMax = 3;
    public const int HistoryThreeMax = 10;

    public static bool IsPreferenceEligible(MatchEvidenceGrade grade) =>
        grade is MatchEvidenceGrade.ExactIdentity or MatchEvidenceGrade.DirectStrong;

    public static int PriorityOrder(IndexRuleEffect priority) => priority switch
    {
        IndexRuleEffect.High => 0,
        IndexRuleEffect.Normal => 1,
        _ => 2,
    };

    public static RankSection Section(
        IndexRuleEffect priority,
        bool explicitAlwaysTop,
        bool favoritePin,
        bool isHistoryOnly,
        MatchEvidenceGrade grade)
    {
        if (explicitAlwaysTop || favoritePin)
            return RankSection.Pinned;
        if (isHistoryOnly || grade == MatchEvidenceGrade.LearnedAlias)
            return RankSection.HistoryTail;
        if (priority == IndexRuleEffect.Low)
            return RankSection.Low;
        return RankSection.Boosted;
    }

    public static RankSection Section(in RankSignals s, HistoryTier tier, in MatchEvidence evidence = default)
    {
        var grade = GradeForRanking(s, evidence);
        var explicitAlwaysTop = s.AlwaysTop && s.Priority == IndexRuleEffect.High;
        var favoritePin = !explicitAlwaysTop && s.IsFavorite && !s.IsHistoryOnly && IsPreferenceEligible(grade);
        return Section(s.Priority, explicitAlwaysTop, favoritePin, s.IsHistoryOnly, grade);
    }

    /// <summary>
    /// The history key, on a 0..1000 scale: how much the user actually uses this row, as the store already
    /// decayed it.
    /// </summary>
    /// <remarks>
    /// It used to hand back the score only for H5/H3 rows and zero for everything else, which made the
    /// budget's thirteen slots decide whether a row's own history counted at all. A name opened every day
    /// but ranked eleventh in some other query fell to zero here and then lost on coverage to any short
    /// name -- the history was recorded, displayed ("常用"), and never reached the ordering. The budget
    /// still shapes the sections and the tail; it no longer gates the key.
    ///
    /// A row with no history scores 0 here, so this key is neutral between rows the user never touched and
    /// matching decides between them.
    /// </remarks>
    public static int HistoryTie(bool isHistoryOnly, HistoryTier tier, int historyScoreX100) =>
        historyScoreX100;

    public static HistoryTier EffectiveHistoryTier(
        HistoryTier tier,
        MatchEvidenceGrade grade,
        bool isHistoryOnly)
    {
        if (isHistoryOnly || grade == MatchEvidenceGrade.LearnedAlias)
            return tier;
        return IsPreferenceEligible(grade) && tier is HistoryTier.H5 or HistoryTier.H3
            ? tier
            : HistoryTier.None;
    }

    public static int HistoryTie(
        bool isHistoryOnly,
        HistoryTier tier,
        int historyScoreX100,
        MatchEvidenceGrade grade = MatchEvidenceGrade.DirectStrong)
    {
        if (isHistoryOnly || grade == MatchEvidenceGrade.LearnedAlias)
            return tier is HistoryTier.H5 or HistoryTier.H3 or HistoryTier.Tail ? historyScoreX100 : 0;
        return IsPreferenceEligible(grade) && tier is HistoryTier.H5 or HistoryTier.H3
            ? historyScoreX100
            : 0;
    }

    public static RankKey BuildKey(in RankSignals s, HistoryTier tier, in MatchEvidence evidence = default)
    {
        var grade = GradeForRanking(s, evidence);
        var explicitAlwaysTop = s.AlwaysTop && s.Priority == IndexRuleEffect.High;
        var favoritePin = !explicitAlwaysTop && s.IsFavorite && !s.IsHistoryOnly && IsPreferenceEligible(grade);
        var section = Section(s.Priority, explicitAlwaysTop, favoritePin, s.IsHistoryOnly, grade);
        var effectiveTier = EffectiveHistoryTier(tier, grade, s.IsHistoryOnly);
        var historyTie = HistoryTie(s.IsHistoryOnly, effectiveTier, s.HistoryScoreX100, grade);
        var diagScore = DiagnosticScore(section, explicitAlwaysTop, favoritePin, grade, s.Priority, effectiveTier);

        return new RankKey(
            explicitAlwaysTop ? (byte)1 : (byte)0,
            (byte)section,
            diagScore,
            historyTie,
            s.TypeRank,
            s.IsHistoryOnly ? MatchRank.NoMatch : s.Match,
            s.IsDir,
            s.Name,
            s.NormalizedPath,
            s.Drive,
            s.EngineTie,
            favoritePin ? (byte)1 : (byte)0,
            grade,
            s.IsFavorite,
            s.Priority,
            effectiveTier,
            s.TypeRankApplies);
    }

    // Directories are containers, not identities: an exact folder name is a strong direct match but never
    // an ExactIdentity -- as long as the user ranks the file bucket behind another type, so that the
    // configured type order (Applications before Files) can put an app ahead of an unrelated folder with the
    // same leading word. Once Files is the top of that order the cap has nothing left to protect, and
    // demoting would instead strip the strongest evidence from the bucket the user ranked first: a settings
    // row that only matched through a keyword would outrank every exact-named folder while the file bucket
    // still led the order. Callers turn the cap off for exactly that case (RankContext.DemoteDirectoryIdentity).
    private static MatchEvidenceGrade GradeForRanking(in RankSignals s, in MatchEvidence evidence)
    {
        var strong = !s.IsHistoryOnly && s.Match.IsStrong;
        var grade = MatchEvidence.ResolveGrade(evidence, s.Match, s.IsHistoryOnly, strong);
        return s.IsDir && s.DemoteDirectoryIdentity && grade == MatchEvidenceGrade.ExactIdentity
            ? MatchEvidenceGrade.DirectStrong
            : grade;
    }

    /// <summary>
    /// Assigns capped H5/H3 tiers by decayed score. Only history-only or strong filename matches qualify;
    /// remaining history-only rows become Tail. Candidate enumeration order does not affect the result.
    /// </summary>
    public static IReadOnlyDictionary<string, HistoryTier> BuildHistoryBudget(
        IEnumerable<RankSignals> candidates,
        int fiveMax = HistoryFiveMax,
        int threeMax = HistoryThreeMax) =>
        BuildHistoryBudget(candidates, tierEligiblePaths: null, fiveMax, threeMax);

    /// <summary>
    /// Same budget, but only rows on <paramref name="tierEligiblePaths"/> may spend an H5/H3 slot.
    /// A Grade 2/3 row cannot read a lifted tier (<see cref="EffectiveHistoryTier"/> drops it), so it
    /// must not spend a slot whose lift it will never use. History-only rows always qualify.
    /// </summary>
    public static IReadOnlyDictionary<string, HistoryTier> BuildHistoryBudget(
        IEnumerable<RankSignals> candidates,
        IReadOnlySet<string>? tierEligiblePaths,
        int fiveMax = HistoryFiveMax,
        int threeMax = HistoryThreeMax)
    {
        fiveMax = Math.Max(0, fiveMax);
        threeMax = Math.Max(0, threeMax);
        var tiers = new Dictionary<string, HistoryTier>(StringComparer.OrdinalIgnoreCase);
        var qualified = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var historyOnlyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in candidates)
        {
            var path = s.NormalizedPath;
            if (s.IsHistoryOnly)
            {
                historyOnlyPaths.Add(path);
            }
            else if (tierEligiblePaths != null && !tierEligiblePaths.Contains(path))
            {
                // The row stays in the candidate set, but it cannot spend a history slot: its grade
                // never reads the tier, so letting it qualify would only mislabel the trace and badge
                // (or, on a tied score, take a band from a row that can use it).
                continue;
            }

            if (s.HistoryScoreX100 < HistoryThreeThresholdX100)
                continue;
            if (!s.IsHistoryOnly && !s.Match.IsStrong)
                continue;

            if (!qualified.TryGetValue(path, out var best) || s.HistoryScoreX100 > best)
                qualified[path] = s.HistoryScoreX100;
        }

        var ordered = new List<(string Path, int Score)>(qualified.Count);
        foreach (var (path, score) in qualified)
            ordered.Add((path, score));
        ordered.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(a.Path, b.Path);
        });

        var fiveTaken = 0;
        var threeTaken = 0;
        foreach (var (path, score) in ordered)
        {
            if (score >= HistoryFiveThresholdX100 && fiveTaken < fiveMax)
            {
                fiveTaken++;
                Assign(tiers, path, HistoryTier.H5);
            }
            else if (threeTaken < threeMax)
            {
                threeTaken++;
                Assign(tiers, path, HistoryTier.H3);
            }
        }

        foreach (var path in historyOnlyPaths)
            Assign(tiers, path, HistoryTier.Tail);

        return tiers;
    }

    private static void Assign(Dictionary<string, HistoryTier> tiers, string path, HistoryTier tier)
    {
        if (!tiers.TryGetValue(path, out var existing) || tier > existing)
            tiers[path] = tier;
    }

    /// <summary>
    /// The keys every section compares before it reaches match quality, so both windows share one rule.
    /// TypeRank is 0 in the full window, which has no display type layer; the low-priority section's
    /// TypeRank is NOT compared here -- see CompareSectionRank.
    /// </summary>
    public static int CompareSectionRank(RankSection section, in SectionRank x, in SectionRank y)
    {
        if (section == RankSection.HistoryTail)
        {
            var byHistory = y.HistoryTie.CompareTo(x.HistoryTie);
            if (byHistory != 0)
                return byHistory;

            // Fork: the left edge, then coverage, then the type order -- the same sequence every other
            // section uses, so a history-tail row is ordered by its match, not by its type alone.
            var byStartTail = x.Start.CompareTo(y.Start);
            if (byStartTail != 0)
                return byStartTail;

            var byCoverageTail = y.Coverage.CompareTo(x.Coverage);
            if (byCoverageTail != 0)
                return byCoverageTail;

            return x.TypeRank.CompareTo(y.TypeRank);
        }

        // Match quality is one layer, and it is the layer that decides order: where the query sits in the
        // name, then how tightly it sits there, then how much of the name it fills.
        //
        // The left edge leads. A name whose query lands at its first character is what the user meant, and no
        // amount of usage or type preference changes that -- a row used every day must not outrank a row
        // whose name simply starts with what was typed.
        var byStart = x.Start.CompareTo(y.Start);
        if (byStart != 0)
            return byStart;

        var byContiguity = y.Contiguity.CompareTo(x.Contiguity);
        if (byContiguity != 0)
            return byContiguity;

        // Usage next, as a band: among rows that matched equally well, one the user opens is ahead of one
        // they never touch.
        //
        // Low is excluded. That section exists because the user explicitly demoted the path, and an explicit
        // instruction outranks an implicit one: letting history lead there would silently undo the setting
        // for exactly the paths someone bothered to mark.
        if (section != RankSection.Low)
        {
            var byHistory = y.HistoryTie.CompareTo(x.HistoryTie);
            if (byHistory != 0)
                return byHistory;
        }

        // The configured result-type order, ahead of coverage.
        //
        // Coverage divides the matched length by the NAME's length, so a short name fills a larger share of
        // itself and beats a longer one no matter what type the user ranked first: a three-letter query
        // against a ten-letter application name scores 0.3 while against a five-letter file name it scores
        // 0.6, purely because the file name is shorter. Reading the type order first is what lets "I put
        // applications first" mean something when the names differ in length.
        //
        // No section exemption. History and other implicit signals are dropped in Low, but the type order is
        // an explicit setting and applies wherever two rows of different types compete.
        var byType = x.TypeRank.CompareTo(y.TypeRank);
        if (byType != 0)
            return byType;

        // How much of the name the query fills, then the strong/weak flag. Both windows read these here so
        // they cannot disagree.
        var byCoverage = y.Coverage.CompareTo(x.Coverage);
        if (byCoverage != 0)
            return byCoverage;

        var byScore = y.Score.CompareTo(x.Score);
        if (byScore != 0)
            return byScore;

        // Low stops here: match quality is the caller's to compare, and TypeRank was already settled above.
        if (section == RankSection.Low)
            return 0;

        return x.TypeRank.CompareTo(y.TypeRank);
    }

    /// <summary>The keys every section shares, in the order both windows have always used.</summary>
    public static int CompareCommonTail(in RankTail x, in RankTail y)
    {
        var compare = x.IsDir.CompareTo(y.IsDir);
        if (compare != 0)
            return compare;

        compare = SearchNameOrderComparer.Instance.Compare(x.Name, y.Name);
        if (compare != 0)
            return compare;

        compare = x.Name.Length.CompareTo(y.Name.Length);
        if (compare != 0)
            return compare;

        compare = x.NormalizedPath.Length.CompareTo(y.NormalizedPath.Length);
        if (compare != 0)
            return compare;

        compare = x.Drive.CompareTo(y.Drive);
        if (compare != 0)
            return compare;

        compare = string.Compare(x.NormalizedPath, y.NormalizedPath, StringComparison.OrdinalIgnoreCase);
        if (compare != 0)
            return compare;

        compare = string.CompareOrdinal(x.NormalizedPath, y.NormalizedPath);
        if (compare != 0)
            return compare;

        return x.EngineTie.CompareTo(y.EngineTie);
    }

    public static int DriveRank(string? drive) => string.IsNullOrEmpty(drive) ? 0 : char.ToUpperInvariant(drive[0]);

    // The full-window ordering up to (and including) the packed quality key, as one integer: section,
    // then the fields THAT SECTION compares, then the quality key -- exactly the sequence
    // RankedSearchResultComparer's slow path runs before the history tie. Because two rows in different
    // sections never compare past the section bits, the section-specific slots may differ per section;
    // a slot a section does not compare is left zero for all of its rows and therefore has no effect.
    //
    // History tie is deliberately absent: it is an unbounded score, and the slow path still handles every
    // pair where it is non-zero.
    //
    //   bits 45-44  section
    //   bit  43     not alwaysTop        (Pinned)
    //   bit  42     not favoritePin      (Pinned)
    //   bits 41-39  grade
    //   bit  38     not favorite         (Boosted/HistoryTail)
    //   bits 37-36  priority order
    //   bits 35-34  not history tier
    //   bits 33-2   packed quality key
    public static ulong OrderKey(
        RankSection section,
        bool alwaysTop,
        bool favoritePin,
        MatchEvidenceGrade grade,
        bool isFavorite,
        IndexRuleEffect priority,
        HistoryTier tier,
        ulong rankSortKey)
    {
        var quality = rankSortKey == 0 ? uint.MaxValue : (uint)(rankSortKey >> 32);
        var key = (ulong)(byte)section << 44;
        switch (section)
        {
            case RankSection.Pinned:
                key |= (alwaysTop ? 0UL : 1UL) << 43;
                key |= (favoritePin ? 0UL : 1UL) << 42;
                key |= (ulong)PriorityOrder(priority) << 36;
                break;
            case RankSection.Low:
                break;
            default:
                key |= (isFavorite ? 0UL : 1UL) << 38;
                key |= (ulong)PriorityOrder(priority) << 36;
                break;
        }

        key |= (ulong)(int)grade << 39;
        key |= (ulong)(3 - ((int)tier & 3)) << 34;
        return key | ((ulong)quality << 2);
    }
}

/// <summary>Per-section ordering inputs. See RankPolicy.CompareSectionRank for what each section reads.</summary>
/// <param name="Start">Leftmost matched character; the first key inside a section (0 for rows with no name match).</param>
/// <param name="Contiguity">How tightly the matched characters sit together, 0..1; the second key.</param>
/// <param name="Coverage">How much of the name the query fills, 0..1; the third key.</param>
public readonly record struct SectionRank(
    int Score,
    int HistoryTie,
    int TypeRank,
    int Start = 0,
    double Coverage = 0,
    double Contiguity = 0);

/// <summary>The tail every section shares, already reduced to values a comparer can read.</summary>
public readonly record struct RankTail(bool IsDir, string Name, string NormalizedPath, int Drive, ulong EngineTie);

public sealed class RankKeyComparer : IComparer<RankKey>
{
    public static readonly RankKeyComparer Instance = new();

    public int Compare(RankKey x, RankKey y)
    {
        var xSection = (RankSection)x.Section;
        var ySection = (RankSection)y.Section;

        // Pinned stays on top whatever the match looks like: "always on top" is an explicit instruction and
        // the only section that is meant to outrank quality outright.
        if ((xSection == RankSection.Pinned) != (ySection == RankSection.Pinned))
            return xSection == RankSection.Pinned ? -1 : 1;

        // HistoryTail is a weak history-only row; it belongs behind anything that matched the query itself.
        if ((xSection == RankSection.HistoryTail) != (ySection == RankSection.HistoryTail))
            return xSection == RankSection.HistoryTail ? 1 : -1;

        // Match quality FIRST: where the query sits in the name and how tightly it sits there is what the
        // user actually typed. A row whose letters land at the start of its name is the one they meant, and
        // it outranks a habit -- a path opened often but matching somewhere in the middle should not take the
        // top away from a name that begins with the query.
        var byStart = x.Match.Start.CompareTo(y.Match.Start);
        if (byStart != 0)
            return byStart;

        var byContiguity = y.Match.Contiguity.CompareTo(x.Match.Contiguity);
        if (byContiguity != 0)
            return byContiguity;

        // Only a genuine habit crosses the match layer, and among habits the more-used one comes first.
        //
        // "Habit" is the H5 threshold (five opens). Below it, history does not get to overrule how well the
        // text matched: a folder the user passed through once should not beat a name that begins with the
        // query. That is what keeps the whole of history light while still letting what they reach for daily
        // -- a chat app, a project folder -- surface on a single letter.
        //
        // Both sides are tested: if either row is habitual the comparison runs, so a habitual row known to
        // this key wins over a non-habitual one, and two habitual rows order by score. Two ordinary rows
        // fall through unchanged.
        var xHabitual = x.HistoryTie >= RankPolicy.HistoryFiveThresholdX100;
        var yHabitual = y.HistoryTie >= RankPolicy.HistoryFiveThresholdX100;
        if (xHabitual || yHabitual)
        {
            var byHistory = y.HistoryTie.CompareTo(x.HistoryTie);
            if (byHistory != 0)
                return byHistory;
        }

        var byType = x.TypeRank.CompareTo(y.TypeRank);
        if (byType != 0)
            return byType;

        // Only now does the section decide: a demoted row still ranks behind an equally good one, so the
        // setting keeps its meaning without overriding what the user typed.
        var compare = x.Section.CompareTo(y.Section);
        if (compare != 0)
            return compare;

        var section = xSection;
        var rankX = new SectionRank(x.Score, x.HistoryTie, x.TypeRank, x.Match.Start, x.Match.Coverage, x.Match.Contiguity);
        var rankY = new SectionRank(y.Score, y.HistoryTie, y.TypeRank, y.Match.Start, y.Match.Coverage, y.Match.Contiguity);

        compare = RankPolicy.CompareSectionRank(section, in rankX, in rankY);
        if (compare != 0)
            return compare;

        compare = MatchRank.CompareQuality(x.Match, y.Match);
        if (compare != 0)
            return compare;

        // TypeRank is compared inside CompareSectionRank for every section, Low included, so it is already
        // settled by the time this point is reached.
        return RankPolicy.CompareCommonTail(x.Tail, y.Tail);
    }

    // The type order sits inside the quality ladder: after where the query sits in the name, before how much
    // of the name it covers (see MatchRank.CompareQualityPlacement). A shorter file name must not outvote the
    // user's "Applications first" just because the same prefix explains more of it.
    private static int CompareQualityThenType(in RankKey x, in RankKey y)
    {
        var c = MatchRank.CompareQualityPlacement(x.Match, y.Match);
        if (c != 0)
            return c;
        c = CompareTypeRank(x, y);
        return c != 0 ? c : MatchRank.CompareQualityDetail(x.Match, y.Match);
    }

    // Only the three built-in buckets (Applications / System settings / Files) are ordered against each other;
    // every other row shares one rank behind them. That shared rank cannot be "skip the comparison": with
    // coverage after the type step, a pairwise skip makes the sort cyclic (app < file by type, file <
    // plugin row by coverage, plugin row < app by coverage).
    private static int CompareTypeRank(in RankKey x, in RankKey y) =>
        CoreTypeRank(x).CompareTo(CoreTypeRank(y));

    private static int CoreTypeRank(in RankKey key) => key.TypeRankApplies ? key.TypeRank : int.MaxValue;
}
