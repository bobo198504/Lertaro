using Lertaro.App.ViewModels.Search.Mapping;
using Lertaro.Core;
using Lertaro.Core.SearchIndex;
using Lertaro.PluginSdk.Services;

namespace Lertaro.App.Tests.ViewModels.Search.Mapping;

[TestClass]
public sealed class FrequentHistoryCandidateMapperTests
{
    private static HistoryEntry Entry(
        string keyword,
        string path,
        int count,
        HistoryEntryKind kind = HistoryEntryKind.File) =>
        new(keyword, path, kind, 0, count);

    [TestMethod]
    public void Collect_RecallsFrequentNameMatchOutsideTheBoundedEnginePage()
    {
        var entries = new[]
        {
            Entry("ape", @"C:\Portable\Apex\Apex.exe", count: 18),
        };

        var candidates = FrequentHistoryCandidateMapper.Collect(
            FuzzyQuery.Parse("a"),
            scope: null,
            entries,
            _ => true,
            _ => false,
            typeRank: 2);

        Assert.HasCount(1, candidates);
        Assert.AreEqual(@"C:\Portable\Apex\Apex.exe", candidates[0].Result.FullPath);
        Assert.AreEqual(18, candidates[0].HistoryCount);
        Assert.AreEqual(0, candidates[0].Match.Start);
        Assert.IsFalse(candidates[0].IsHistoryOnly);
    }

    [TestMethod]
    public void Collect_IgnoresLowUseAndNameMismatch()
    {
        var entries = new[]
        {
            Entry("ape", @"C:\Portable\Apex\Apex.exe", count: 2),
            Entry("beta", @"C:\Portable\Beta\Beta.exe", count: 20),
        };

        var candidates = FrequentHistoryCandidateMapper.Collect(
            FuzzyQuery.Parse("a"),
            scope: null,
            entries,
            _ => true,
            _ => false);

        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Collect_RespectsExplicitLowPriority()
    {
        var entries = new[]
        {
            Entry("ape", @"C:\Portable\Apex\Apex.exe", count: 18),
        };
        var context = new RankContext((_, _) =>
            new Core.ExclusionRuleSet.PriorityVerdict(IndexRuleEffect.Low, false));

        var candidates = FrequentHistoryCandidateMapper.Collect(
            FuzzyQuery.Parse("a"),
            scope: null,
            entries,
            _ => true,
            _ => false,
            context);

        Assert.IsEmpty(candidates);
    }
}
