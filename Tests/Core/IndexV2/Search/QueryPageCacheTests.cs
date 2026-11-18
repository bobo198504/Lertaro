using Lertaro.Core.IndexV2.Search;

namespace Lertaro.Core.Tests.IndexV2.Search;

[TestClass]
public sealed class QueryPageCacheTests
{
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
