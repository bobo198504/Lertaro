using Lertaro.App.ViewModels.Search;
using Lertaro.App.ViewModels.Search.Mapping;
using Lertaro.Core.SearchIndex;
using Lertaro.PluginSdk.Abstractions.Plugins;

namespace Lertaro.App.Tests.ViewModels.Search.Mapping;

[TestClass]
public sealed class SearchableItemMapperTests
{
    [TestMethod]
    public void MatchCatalogEntry_LeftAliasWinsOverLaterTitleSubstring()
    {
        var item = new SearchableItem
        {
            Title = "File Explorer",
            Aliases = [new SearchableItemAlias("Explorer", SearchableItemAliasKind.ProductMetadata)],
        };
        var entry = new SearchableItemCache.CacheEntry(item, ["Explorer"], null);

        var match = SearchableItemMapper.MatchCatalogEntry(
            FuzzyQuery.Parse("expl"),
            ["expl"],
            entry);

        Assert.IsTrue(match.IsLeftContiguous);
        Assert.AreEqual(0, match.Start);
    }

    [TestMethod]
    public void MatchCatalogEntry_InteriorAliasDoesNotInventLeftPosition()
    {
        var item = new SearchableItem
        {
            Title = "Notepad++",
            Aliases = [new SearchableItemAlias("Notepad", SearchableItemAliasKind.ProductMetadata)],
        };
        var entry = new SearchableItemCache.CacheEntry(item, ["Notepad"], null);

        var match = SearchableItemMapper.MatchCatalogEntry(
            FuzzyQuery.Parse("a"),
            ["a"],
            entry);

        Assert.IsFalse(match.IsLeftContiguous);
        Assert.AreEqual(5, match.Start);
    }
}
