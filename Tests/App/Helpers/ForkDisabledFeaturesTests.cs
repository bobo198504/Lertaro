using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lertaro.App.Tests.Helpers;

// Permanent fork policy: upstream updates must not silently reconnect these three features.
[TestClass]
public sealed class ForkDisabledFeaturesTests
{
    [TestMethod]
    public void FileDialogsDoNotSummonTheInlineCard()
    {
        var manager = ActiveCode(Source("App/Services/InlineSearch/InlineSearchManager.cs"));
        var activation = Between(manager,
            "_explorerTracker.OnExplorerActivated +=",
            "_explorerTracker.OnExplorerDeactivated +=");
        var pathCaptured = Between(manager,
            "_explorerTracker.OnPathCaptured +=",
            "private void PrewarmActiveFolderListing()");

        Assert.DoesNotContain("SummonWindow()", activation);
        Assert.DoesNotContain("SummonWindow()", pathCaptured);
    }

    [TestMethod]
    public void QuickLaunchPanelAndItsSettingsStayDisabled()
    {
        var quickWindow = ActiveCode(Source("App/ViewModels/Search/QuickSearchViewModel.cs"));
        var showSupport = ActiveCode(Source("App/Views/QuickSearchWindow/Helpers/QuickSearchWindowShowSupport.cs"));
        var settings = WithoutXmlComments(Source("App/Views/Settings/SettingsWindow.xaml"));

        StringAssert.Contains(quickWindow, "public Visibility LaunchPanelVisibility => Visibility.Collapsed;");
        Assert.DoesNotContain("_launchSources.RefreshAsync()", quickWindow);
        Assert.DoesNotContain("RefreshLaunchItems", showSupport);
        Assert.DoesNotContain("Tag=\"QuickLaunch\"", settings);
    }

    [TestMethod]
    public void QuickPanelAndItsHotkeyStayDisabled()
    {
        var app = ActiveCode(Source("App/App.xaml.cs"));
        var keyboard = ActiveCode(Source("Core/Hook/InlineSearch/KeyboardHookService.cs"));
        var hook = ActiveCode(Source("Core/Hook/Ipc/HookProcess.cs"));
        var settings = WithoutXmlComments(Source("App/Views/Settings/SettingsWindow.xaml"));

        Assert.DoesNotContain("HookClient.OnQuickPanelHotkey +=", app);
        Assert.DoesNotContain("new Services.QuickPanel.QuickPanelManager()", app);
        Assert.DoesNotContain("CheckQuickPanelHotkey(", keyboard);
        Assert.DoesNotContain("SendQuickPanelHotkey()", hook);
        Assert.DoesNotContain("Tag=\"QuickPanel\"", settings);
    }

    private static string ActiveCode(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string WithoutXmlComments(string xaml) =>
        Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string Between(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.IsGreaterThan(-1, start, $"could not find '{from}'");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.IsGreaterThan(-1, end, $"could not find '{to}' after '{from}'");
        return source.Substring(start, end - start);
    }

    private static string Source(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;

        Assert.IsNotNull(dir, "could not locate the repository root");
        var path = Path.Combine(dir!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.IsTrue(File.Exists(path), $"expected a file at {path}");
        return File.ReadAllText(path);
    }
}
