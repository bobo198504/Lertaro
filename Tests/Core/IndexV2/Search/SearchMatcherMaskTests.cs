using Lertaro.Core.IndexV2.Search;
using Lertaro.Core.SearchIndex.Fzf;
using Lertaro.Core.Tests.IndexV2;

namespace Lertaro.Core.Tests.IndexV2.Search;

[TestClass]
public sealed class SearchMatcherMaskTests
{
    [TestMethod]
    public void LowHighAndBothMaskHalvesReturnTheSameNames()
    {
        using var fixture = LiveIndexFixture.Build("D",
        [
            LiveIndexFixture.Root(),
            new FileRecord(2, 1, "alpha.txt", FileRecordFlags.None),
            new FileRecord(3, 1, "zulu.txt", FileRecordFlags.None),
            new FileRecord(4, 1, "123.txt", FileRecordFlags.None),
            new FileRecord(5, 1, "中.txt", FileRecordFlags.None),
            new FileRecord(6, 1, "文.txt", FileRecordFlags.None),
        ]);

        CollectionAssert.AreEqual(new[] { "alpha.txt" }, Search(fixture, "a"));
        CollectionAssert.AreEqual(new[] { "zulu.txt" }, Search(fixture, "z"));
        CollectionAssert.AreEqual(new[] { "123.txt" }, Search(fixture, "3"));
        CollectionAssert.AreEqual(new[] { "中.txt" }, Search(fixture, "中"));
    }

    private static string[] Search(LiveIndexFixture fixture, string query)
    {
        var hits = SearchMatcher.RentHitList();
        try
        {
            fixture.Index.Read<object?>((snapshot, _) =>
            {
                SearchMatcher.MatchUniques(snapshot, FzfPattern.Parse(query), hits);
                return null;
            });
            return hits.Select(hit => SnapshotName(fixture, hit.Uid)).OrderBy(name => name).ToArray();
        }
        finally
        {
            SearchMatcher.ReturnHitList(hits);
        }
    }

    private static string SnapshotName(LiveIndexFixture fixture, int uid) =>
        fixture.Index.Read((snapshot, _) => snapshot.GetUniqueName(uid));
}
