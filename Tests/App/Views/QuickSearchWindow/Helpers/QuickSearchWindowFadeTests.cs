using Lertaro.App.Views.QuickSearchWindow.Helpers;

namespace Lertaro.App.Tests.Views.QuickSearchWindow.Helpers;

[TestClass]
public sealed class QuickSearchWindowFadeTests
{
    [TestMethod]
    public void AlphaAt_CoversTheFullAnimationRange()
    {
        Assert.AreEqual(0, QuickSearchWindowFade.AlphaAt(0, 255, 0));
        Assert.AreEqual(255, QuickSearchWindowFade.AlphaAt(0, 255, QuickSearchWindowFade.DurationMs));
        Assert.IsGreaterThan(0, QuickSearchWindowFade.AlphaAt(0, 255, 75));
        Assert.IsLessThan(255, QuickSearchWindowFade.AlphaAt(0, 255, 225));
    }

    [TestMethod]
    public void AlphaAt_FadeOutEndsFullyTransparent()
    {
        Assert.AreEqual(255, QuickSearchWindowFade.AlphaAt(255, 0, 0));
        Assert.AreEqual(0, QuickSearchWindowFade.AlphaAt(255, 0, QuickSearchWindowFade.DurationMs));
        Assert.IsGreaterThan(0, QuickSearchWindowFade.AlphaAt(255, 0, 75));
        Assert.IsLessThan(255, QuickSearchWindowFade.AlphaAt(255, 0, 225));
    }
}
