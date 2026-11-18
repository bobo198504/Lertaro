using Lertaro.PluginSdk.Helpers;

namespace Lertaro.Plugins.CoreExtensions.Tests.Helpers;

[TestClass]
public sealed class StartMenuShortcutResolverTests
{
    [TestMethod]
    public void OfflineShortcutIsNotOpenedToResolveItsTarget()
    {
        var directory = Directory.CreateTempSubdirectory("lertaro-cloud-link-");
        var path = Path.Combine(directory.FullName, "app.lnk");
        var target = Environment.ProcessPath!;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Save();
            Assert.AreEqual(target, StartMenuShortcutResolver.ResolveShortcutTarget(path), ignoreCase: true);

            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
            Assert.IsNull(StartMenuShortcutResolver.ResolveShortcutTarget(path));
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            directory.Delete(recursive: true);
        }
    }
}
