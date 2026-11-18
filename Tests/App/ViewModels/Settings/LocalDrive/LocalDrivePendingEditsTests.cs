using Lertaro.App.ViewModels.Settings.LocalDrive;
using Lertaro.Core;
using Lertaro.Core.Indexer.Usn;
using Lertaro.Core.Services.Search;

namespace Lertaro.App.Tests.ViewModels.Settings.LocalDrive;

[TestClass]
public sealed class LocalDrivePendingEditsTests
{
    [TestMethod]
    public async Task StatusBurst_AppliesTheLatestSnapshot()
    {
        using var service = new SearchService();
        var viewModel = new LocalDriveSettingsViewModel(service, () => { });
        try
        {
            _ = viewModel.UpdateStatusAsync(
                new UsnIndexer.IndexerStatus { State = "ready", Drives = [new UsnIndexer.DriveIndexStatus { Drive = "C", State = "ready" }] },
                new MachineSettings());
            _ = viewModel.UpdateStatusAsync(
                new UsnIndexer.IndexerStatus { State = "loading-cache", Drives = [new UsnIndexer.DriveIndexStatus { Drive = "C", State = "loading-cache" }] },
                new MachineSettings());
            await viewModel.UpdateStatusAsync(
                new UsnIndexer.IndexerStatus { State = "indexing", Drives = [new UsnIndexer.DriveIndexStatus { Drive = "C", State = "indexing" }] },
                new MachineSettings());

            Assert.AreEqual(LocalDriveSettingsHelper.TranslateState("indexing"), viewModel.LocalDrives.Single().State);
            Assert.IsFalse(viewModel.CanRebuild);
        }
        finally { viewModel.Cleanup(); }
    }

    [TestMethod]
    public async Task ServiceUnavailable_PreservesEditedSelectionAndDisablesGlobalActions()
    {
        using var service = new SearchService();
        var viewModel = new LocalDriveSettingsViewModel(service, () => { });
        try
        {
            var status = new UsnIndexer.IndexerStatus { State = "ready", Drives = [new UsnIndexer.DriveIndexStatus { Drive = "C", State = "ready" }] };
            await viewModel.UpdateStatusAsync(status, new MachineSettings());
            Assert.IsFalse(viewModel.HasPendingEdits);
            var item = viewModel.LocalDrives.Single();
            item.IsEnabled = !item.IsEnabled;

            await viewModel.UpdateStatusAsync(new UsnIndexer.IndexerStatus { State = "error" }, new MachineSettings());

            Assert.AreSame(item, viewModel.LocalDrives.Single());
            Assert.IsTrue(item.IsEnabled);
            Assert.IsTrue(viewModel.HasPendingEdits);
            Assert.IsFalse(item.CanEditEnabled);
            Assert.IsFalse(item.CanRunRowAction);
        }
        finally { viewModel.Cleanup(); }
    }
}
