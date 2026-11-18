using Lertaro.App.Services.Tray;

namespace Lertaro.App.Tests.Services.Tray;

[TestClass]
public sealed class TrayIconServiceTests
{
    [TestMethod]
    public void LeftClick_DoubleClickBurst_HandlesOnlyTheFirstClick()
    {
        Assert.IsTrue(TrayIconService.ShouldHandleLeftClick(10_000, previousTicks: 0, doubleClickTimeMs: 500));
        Assert.IsFalse(TrayIconService.ShouldHandleLeftClick(10_250, previousTicks: 10_000, doubleClickTimeMs: 500));
        Assert.IsTrue(TrayIconService.ShouldHandleLeftClick(10_501, previousTicks: 10_000, doubleClickTimeMs: 500));
    }
}
