using System.IO;
using Lertaro.App.ViewModels.Search.Mapping;
using Lertaro.Core.SearchIndex;
using Lertaro.PluginSdk.Services;

namespace Lertaro.App.Tests.ViewModels.Search.Mapping;

[TestClass]
public sealed class HistorySearchCandidateMapperTests
{
    [TestMethod]
    public void Collect_QueryMatchesRecordedKeyword_AddsExistingPathAsHistoryOnly()
    {
        var entries = new[] { Entry("bcomp", @"C:\Apps\BCompare.exe", HistoryEntryKind.File) };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("bc"), null, entries, _ => true, _ => false);

        Assert.HasCount(1, results);
        Assert.AreEqual(@"C:\Apps\BCompare.exe", results[0].Result.FullPath);
        Assert.IsFalse(results[0].IsHistoryOnly,
            "the path's own name matched, so this is a normal name hit with history on top");
        Assert.IsFalse(results[0].IsFavorite);
        Assert.AreEqual(1, results[0].HistoryCount);
    }

    // A history row is a file path, so it has to carry the caller's Files rank rather than a group of its
    // own: inside the Boosted section TypeRank is compared before match quality, so int.MaxValue here would
    // put every history row below every indexed file that reached the same section.
    [TestMethod]
    public void Collect_CarriesTheCallersFilesTypeRank()
    {
        var entries = new[] { Entry("bcomp", @"C:\Apps\BCompare.exe", HistoryEntryKind.File) };

        var results = HistorySearchCandidateMapper.Collect(
            FuzzyQuery.Parse("bc"), null, entries, _ => true, _ => false, typeRank: 3);

        Assert.HasCount(1, results);
        Assert.AreEqual(3, results[0].TypeRank);
    }

    [TestMethod]
    public void Collect_HistoryMatchesWithEqualQualityPreferHigherCount()
    {
        var entries = new[]
        {
            Entry("bcomp", @"C:\Apps\LessUsed.exe", HistoryEntryKind.File, count: 2),
            Entry("bcomp", @"C:\Apps\MoreUsed.exe", HistoryEntryKind.File, count: 5)
        };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("bc"), null, entries, _ => true, _ => false);

        CollectionAssert.AreEqual(
            new[] { @"C:\Apps\MoreUsed.exe", @"C:\Apps\LessUsed.exe" },
            results.Select(result => result.Result.FullPath).ToList());
    }

    // Picking a lower result for a query makes it the top one next time, even over one opened more often.
    [TestMethod]
    public void Collect_MostRecentPickBeatsMoreFrequentOlderOne()
    {
        var entries = new[]
        {
            Entry("atlas", @"C:\Games\Atlas", HistoryEntryKind.Folder, count: 5, time: 100),
            Entry("atlas", @"D:\Projects\code-atlas", HistoryEntryKind.Folder, count: 1, time: 200)
        };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("atlas"), null, entries, _ => false, _ => true);

        CollectionAssert.AreEqual(
            new[] { @"D:\Projects\code-atlas", @"C:\Games\Atlas" },
            results.Select(result => result.Result.FullPath).ToList());
    }

    [TestMethod]
    public void Collect_HistoryMatchesPreferCountBeforeMatchWeight()
    {
        var entries = new[]
        {
            Entry("bc", @"C:\Apps\Exact.exe", HistoryEntryKind.File, count: 1),
            Entry("bcomp", @"C:\Apps\Frequent.exe", HistoryEntryKind.File, count: 5)
        };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("bc"), null, entries, _ => true, _ => false);

        Assert.AreEqual(@"C:\Apps\Frequent.exe", results[0].Result.FullPath);
    }

    [TestMethod]
    public void Collect_UnrelatedKeyword_DoesNotAddPath()
    {
        var entries = new[] { Entry("other", @"C:\Apps\BCompare.exe", HistoryEntryKind.File) };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("bc"), null, entries, _ => true, _ => false);

        Assert.IsEmpty(results);
    }

    [TestMethod]
    public void CollectNamed_ReAddsAFrequentPathByNameWhenItsOldKeywordNoLongerMatches()
    {
        var entries = new[]
        {
            Entry("a", @"D:\Tools\Alpha\alpha.exe", HistoryEntryKind.Application, count: 20)
        };

        var results = HistorySearchCandidateMapper.CollectNamed(
            FuzzyQuery.Parse("ap"), null, entries, _ => true, _ => false);

        var candidate = Assert.ContainsSingle(results);
        Assert.AreEqual(@"D:\Tools\Alpha\alpha.exe", candidate.Result.FullPath);
        Assert.AreEqual(20, candidate.HistoryCount);
        Assert.IsFalse(candidate.IsHistoryOnly,
            "the path's own name matches, so this is a normal name hit with history on top");
    }

    [TestMethod]
    public void CollectNamed_DoesNotAddAPathWhoseNameDoesNotMatch()
    {
        var entries = new[]
        {
            Entry("ap", @"C:\Tools\unrelated.exe", HistoryEntryKind.Application, count: 20)
        };

        var results = HistorySearchCandidateMapper.CollectNamed(
            FuzzyQuery.Parse("ap"), null, entries, _ => true, _ => false);

        Assert.IsEmpty(results);
    }

    [TestMethod]
    public void CollectNamed_LeftEdgeNameMatch_OutranksAHigherCountMidNameMatch()
    {
        var entries = new[]
        {
            Entry("wiz", @"D:\Apps\Wizard.lnk",
                HistoryEntryKind.Application, count: 5),
            Entry("wid", @"D:\Apps\ToolWidget\ToolWidget.exe",
                HistoryEntryKind.Application, count: 5),
        };

        var candidates = HistorySearchCandidateMapper.CollectNamed(
            FuzzyQuery.Parse("w"), null, entries, _ => true, _ => false);
        Assert.HasCount(2, candidates, "both history paths must reach the same left-priority comparison");
        var ranked = RankedCandidateOrdering.Order(candidates,
            new RankContext((_, _) => new Lertaro.Core.ExclusionRuleSet.PriorityVerdict(
                Lertaro.Core.IndexRuleEffect.Normal, false)));

        Assert.AreEqual(@"D:\Apps\Wizard.lnk",
            ranked[0].Result.FullPath);
    }

    [TestMethod]
    public void CollectNamed_WhenNoLeftMatch_KeepsHighUseMatchPastRecentCrowd()
    {
        var entries = Enumerable.Range(0, 60)
            .Select(i => Entry("cc", $@"C:\Recent\zcc-{i}.exe", HistoryEntryKind.File, time: 1_000 + i))
            .Append(Entry("cc", @"D:\Tools\zcc.exe", HistoryEntryKind.File, count: 20, time: 1))
            .ToArray();

        var results = HistorySearchCandidateMapper.CollectNamed(
            FuzzyQuery.Parse("cc"), null, entries, _ => true, _ => false);

        Assert.IsTrue(results.Any(r => r.Result.FullPath == @"D:\Tools\zcc.exe"),
            "a high-use middle match must reach the shared ranking even when newer middle matches fill a page");
    }

    [TestMethod]
    public void CollectNamed_LeftMatchesFillTheRecallCapBeforeMiddleMatches()
    {
        var entries = Enumerable.Range(0, 60)
            .Select(i => Entry("cc", $@"C:\Left\cc-{i}.exe", HistoryEntryKind.File, time: 1_000 + i))
            .Append(Entry("cc", @"D:\Tools\zcc.exe", HistoryEntryKind.File, count: 99, time: 1))
            .ToArray();

        var results = HistorySearchCandidateMapper.CollectNamed(
            FuzzyQuery.Parse("cc"), null, entries, _ => true, _ => false);

        Assert.HasCount(50, results);
        Assert.IsFalse(results.Any(r => r.Result.FullPath == @"D:\Tools\zcc.exe"),
            "left-priority history candidates own the limited recall page first");
    }

    [TestMethod]
    public void CollectNamed_LeftEdgeApplicationsBeatLongerHistoryFolders()
    {
        var entries = new[]
        {
            Entry("u", @"D:\Groups\SharedPlugins", HistoryEntryKind.Folder, count: 5),
            Entry("og", @"D:\Groups\OpenGroup", HistoryEntryKind.Folder, count: 5),
            Entry("gad", @"D:\Apps\Gadget\Gadget.exe",
                HistoryEntryKind.Application, count: 3),
            Entry("gal", @"D:\Apps\Gallery\Gallery.exe",
                HistoryEntryKind.Application, count: 6),
        };

        var candidates = HistorySearchCandidateMapper.CollectNamed(
            FuzzyQuery.Parse("g"), null, entries, _ => true, _ => true);
        var ranked = RankedCandidateOrdering.Order(candidates,
            new RankContext((_, _) => new Lertaro.Core.ExclusionRuleSet.PriorityVerdict(
                Lertaro.Core.IndexRuleEffect.Normal, false)));

        Assert.AreEqual(@"D:\Apps\Gallery\Gallery.exe",
            ranked[0].Result.FullPath);
        Assert.AreEqual(@"D:\Apps\Gadget\Gadget.exe", ranked[1].Result.FullPath);
    }

    [TestMethod]
    public void Collect_MissingPath_DoesNotAddPath()
    {
        var entries = new[] { Entry("bcomp", @"C:\Apps\BCompare.exe", HistoryEntryKind.File) };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("bc"), null, entries, _ => false, _ => false);

        Assert.IsEmpty(results);
    }

    [TestMethod]
    public void Collect_FolderHistory_CreatesDirectoryResult()
    {
        var entries = new[] { Entry("proj", @"C:\Work\Project", HistoryEntryKind.Folder) };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("pro"), null, entries, _ => false, _ => true);

        Assert.HasCount(1, results);
        Assert.IsTrue(results[0].Result.IsDir);
        Assert.AreEqual("File", results[0].Result.ResultKind);
    }

    [TestMethod]
    public void Collect_ApplicationHistoryUsesApplicationFallbackPresentation()
    {
        var entries = new[] { Entry("word", @"C:\Apps\Word.lnk", HistoryEntryKind.Application) };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("wo"), null, entries, _ => true, _ => false);

        Assert.HasCount(1, results);
        Assert.AreEqual("Word", results[0].Result.Name);
        Assert.AreEqual("Application", results[0].Result.ResultKind);
        Assert.AreEqual(string.Empty, results[0].Result.ParentDir);
        Assert.IsNotNull(results[0].Result.InstantResultOnExecute);
    }

    [TestMethod]
    public void Collect_WslHistoryUsesLexicalPathNormalization()
    {
        var probedPath = string.Empty;
        var entries = new[] { Entry("cache", @"\\wsl$\Ubuntu/home/testuser/~cache/file.txt", HistoryEntryKind.File) };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("cache"), null, entries, path =>
        {
            probedPath = path;
            return true;
        }, _ => false);

        Assert.HasCount(1, results);
        Assert.AreEqual(@"\\wsl$\Ubuntu\home\testuser\~cache\file.txt", probedPath);
        Assert.AreEqual(probedPath, results[0].Result.FullPath);
    }

    [TestMethod]
    public void Collect_PathOutsideScope_DoesNotAddPath()
    {
        var entries = new[] { Entry("bcomp", @"D:\Apps\BCompare.exe", HistoryEntryKind.File) };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("bc"), @"C:\Work", entries, _ => true, _ => false);

        Assert.IsEmpty(results);
    }

    [TestMethod]
    public void Collect_ScopeItself_DoesNotAddPath()
    {
        var entries = new[] { Entry("work", @"C:\Work", HistoryEntryKind.Folder) };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("work"), @"C:\Work", entries, _ => false, _ => true);

        Assert.IsEmpty(results);
    }

    [TestMethod]
    public void Collect_SamePathRememberedUnderMultipleKeywords_IsReturnedOnce()
    {
        var entries = new[]
        {
            Entry("bcomp", @"C:\Apps\BCompare.exe", HistoryEntryKind.File),
            Entry("bcompare", @"C:\Apps\BCompare.exe", HistoryEntryKind.File)
        };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("bc"), null, entries, _ => true, _ => false);

        Assert.HasCount(1, results);
    }

    [TestMethod]
    public void Collect_UncPathUsesLexicalPathAndChecksExistence()
    {
        var entries = new[] { Entry("report", @"\\remote-server\share\report.docx", HistoryEntryKind.File) };

        var results = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("rep"), null, entries, _ => true, _ => false);

        Assert.HasCount(1, results);
        Assert.AreEqual(@"\\remote-server\share\report.docx", results[0].Result.FullPath);
    }

    // ---- The bar the index side holds its own rows to, applied to the rows only this window can build ----

    [TestMethod]
    public void IsExcludedFromSearch_SourceThisSessionFoundUnreachable_IsExcluded()
    {
        var row = new AppSearchResult { FullPath = @"Z:\Docs\report.docx" };
        var unreachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Z" };

        Assert.IsTrue(HistorySearchCandidateMapper.IsExcludedFromSearch(row, unreachable, _ => false));
        Assert.IsFalse(HistorySearchCandidateMapper.IsExcludedFromSearch(row, new HashSet<string>(), _ => false),
            "a drive nobody reported unreachable is not excluded for being unreachable");
    }

    [TestMethod]
    public void IsExcludedFromSearch_HiddenOrSystemLocalPath_IsExcluded()
    {
        var row = new AppSearchResult { FullPath = @"C:\Docs\report.docx" };

        Assert.IsTrue(HistorySearchCandidateMapper.IsExcludedFromSearch(row, new HashSet<string>(), _ => true));
        Assert.IsFalse(HistorySearchCandidateMapper.IsExcludedFromSearch(row, new HashSet<string>(), _ => false));
    }

    // The attribute probe is never asked of a network path: File.GetAttributes has no timeout, and this runs
    // while a search paints -- the same multi-second stall the mappers' own SafeFileExists guards against.
    [TestMethod]
    public void IsExcludedFromSearch_NeverProbesAttributesOfANetworkPath()
    {
        var probed = new List<string>();
        var row = new AppSearchResult { FullPath = @"\\remote-server\share\report.docx" };

        Assert.IsFalse(HistorySearchCandidateMapper.IsExcludedFromSearch(row, new HashSet<string>(), path =>
        {
            probed.Add(path);
            return true;
        }));
        Assert.IsEmpty(probed, "a hidden network share must not cost a synchronously-blocking attribute read here");
    }

    // The wiring, end to end on a real file: the row is built, the existence probe passes (it is real), and
    // the only reason it is then dropped is the attribute -- which is what keeps a hidden file from being
    // listed here and never in the full window.
    [TestMethod]
    public void Collect_HiddenLocalFile_DoesNotAddPath()
    {
        var directory = Directory.CreateTempSubdirectory("lertaro-hidden-").FullName;
        var path = Path.Combine(directory, "bcompare.exe");
        File.WriteAllText(path, "x");
        try
        {
            var entries = new[] { Entry("bcomp", path, HistoryEntryKind.File) };

            var visible = HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("bc"), null, entries,
                File.Exists, Directory.Exists);
            Assert.HasCount(1, visible, "the control: the same row while the file is not hidden");

            File.SetAttributes(path, FileAttributes.Hidden);

            Assert.IsEmpty(HistorySearchCandidateMapper.Collect(FuzzyQuery.Parse("bc"), null, entries,
                File.Exists, Directory.Exists));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HistoryEntry Entry(string keyword, string path, HistoryEntryKind kind, int count = 1, long time = 100) =>
        new(keyword, path, kind, time, count);
}
