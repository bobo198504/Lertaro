using Lertaro.Core.IndexV2.Search;

namespace Lertaro.Core.Tests.IndexV2.Search;

[TestClass]
public sealed class QueryPageCacheTests
{
    [TestMethod]
    public void RetainSingleCharacterPages_KeepsWarmPagesAndDropsOtherQueries()
    {
        var cache = new QueryPageCache();
        var singleKey = QueryPageCache.KeyFor("a", null, null);
        var longKey = QueryPageCache.KeyFor("alphabet", null, null);
        var row = new SearchResult { Name = "a.txt", Path = @"C:\cache\a.txt" };
        cache.Store(singleKey, [row]);
        cache.Store(longKey, [row]);

        Assert.AreEqual(1, cache.TouchSingleCharacterPages());
        cache.RetainSingleCharacterPages();

        Assert.IsTrue(cache.TryReplay(singleKey, 1, _ => { }, CancellationToken.None));
        Assert.IsFalse(cache.TryReplay(longKey, 1, _ => { }, CancellationToken.None));
    }

    [TestMethod]
    public void Store_BoundsRetainedRowsAcrossManyDistinctPages()
    {
        var cache = new QueryPageCache();

        for (var page = 0; page < 80; page++)
        {
            cache.Store($"query-{page}", Enumerable.Range(0, QueryPageCache.MaxTotalRows / 8)
                .Select(index => new SearchResult { Name = $"row-{index}", Path = $@"C:\cache\{index}" })
                .ToList());
        }

        Assert.IsLessThanOrEqualTo(QueryPageCache.MaxTotalRows, cache.RetainedRowCount);
        Assert.IsGreaterThan(0, cache.RetainedRowCount);
    }
}
