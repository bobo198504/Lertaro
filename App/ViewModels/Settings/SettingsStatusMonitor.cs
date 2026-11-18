using Lertaro.Core;
using Lertaro.Core.Indexer.NetworkDrive;
using Lertaro.Core.Indexer.Usn;

using Lertaro.Core.Services.Search;

using Lertaro.Core.Services.Network;
namespace Lertaro.App.ViewModels.Settings;

/// <summary>
/// Owns the "is the search service reachable, what's its current status" read path for the Settings
/// window: polls/subscribes to the USN indexer status and network-drive statuses, coalesces bursts of
/// pushes into at most one queued UI update, and exposes the latest snapshot for
/// <see cref="SettingsViewModel"/>'s own ApplyUiState to render. Kept separate from
/// <see cref="SettingsViewModel"/>'s write path (Apply(): persisting settings and deciding which index
/// rebuilds to trigger), which has nothing to do with this monitoring.
/// </summary>
internal sealed class SettingsStatusMonitor
{
    private readonly SearchService _searchService;
    private readonly Action _onStatusUpdated;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;
    private readonly object _subscriptionLock = new();
    private readonly CancellationTokenSource _statusSubscriptionCts = new();
    private Task? _statusSubscriptionTask;

    private UsnIndexer.IndexerStatus _latestStatus = new() { State = "error" };
    private IReadOnlyList<NetworkIndexStatus> _latestNetworkStatuses = Array.Empty<NetworkIndexStatus>();
    private MachineSettings _latestMachineSettings = new();

    // 0 idle, 1 queued, 2 running. Interlocked because status pushes arrive on the stream thread and
    // RefreshLists' Task.Run thread as well as the UI thread. A dirty bit makes a push that lands while
    // the UI callback is running schedule exactly one follow-up, so the final loading-cache/ready
    // transition cannot be dropped between coalesced updates.
    private int _uiUpdateState;
    private int _uiUpdateDirty;

    public UsnIndexer.IndexerStatus LatestStatus => _latestStatus;
    public IReadOnlyList<NetworkIndexStatus> LatestNetworkStatuses => _latestNetworkStatuses;
    public MachineSettings LatestMachineSettings => _latestMachineSettings;

    public SettingsStatusMonitor(SearchService searchService, Action onStatusUpdated)
    {
        _searchService = searchService;
        _onStatusUpdated = onStatusUpdated;

        _refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += (s, e) => RefreshLists();
        _refreshTimer.Start();
        UserNetworkDriveSearch.StatusesChanged += OnNetworkStatusesChanged;
        EnsureStatusSubscription();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _statusSubscriptionCts.Cancel();
        UserNetworkDriveSearch.StatusesChanged -= OnNetworkStatusesChanged;
    }

    public void RefreshLists() => _ = Task.Run(async () =>
    {
        MachineSettings settings;
        var isServiceReady = false;

        try
        {
            isServiceReady = await _searchService.PingAsync();
            if (isServiceReady)
            {
                settings = await _searchService.GetMachineSettingsAsync();
                _latestNetworkStatuses = _searchService.GetNetworkIndexStatuses();
            }
            else
            {
                settings = new MachineSettings();
                _latestNetworkStatuses = Array.Empty<NetworkIndexStatus>();
            }
        }
        catch
        {
            isServiceReady = false;
            settings = new MachineSettings();
            _latestNetworkStatuses = Array.Empty<NetworkIndexStatus>();
        }

        _latestMachineSettings = settings;
        if (!isServiceReady)
            _latestStatus = new UsnIndexer.IndexerStatus { State = "error" };

        EnsureStatusSubscription();
        ScheduleApplyUiState();
    });

    private void EnsureStatusSubscription()
    {
        lock (_subscriptionLock)
        {
            if (_statusSubscriptionTask is { IsCompleted: false })
                return;

            _statusSubscriptionTask = StartStatusSubscriptionAsync(_statusSubscriptionCts.Token);
        }
    }

    private async Task StartStatusSubscriptionAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return;

        try
        {
            await SearchStatusStream.SubscribeAsync(status =>
            {
                _latestStatus = status;
                ScheduleApplyUiState();
            }, token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void OnNetworkStatusesChanged(IReadOnlyList<NetworkIndexStatus> statuses)
    {
        _latestNetworkStatuses = statuses;
        ScheduleApplyUiState();
    }

    private void ScheduleApplyUiState()
    {
        Interlocked.Exchange(ref _uiUpdateDirty, 1);
        if (Interlocked.CompareExchange(ref _uiUpdateState, 1, 0) != 0)
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Interlocked.Exchange(ref _uiUpdateState, 0);
            return;
        }
        _ = dispatcher.BeginInvoke(new Action(DrainApplyUiState), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void DrainApplyUiState()
    {
        Interlocked.Exchange(ref _uiUpdateState, 2);
        try
        {
            do
            {
                Interlocked.Exchange(ref _uiUpdateDirty, 0);
                _onStatusUpdated();
            }
            while (Volatile.Read(ref _uiUpdateDirty) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref _uiUpdateState, 0);
            if (Volatile.Read(ref _uiUpdateDirty) != 0)
                ScheduleApplyUiState();
        }
    }
}
