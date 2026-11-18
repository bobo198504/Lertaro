using Lertaro.Core;
using Microsoft.Win32;

namespace Lertaro.App.Services;

/// <summary>
/// Runs the first search's one-time work while the app sits idle after startup, so the user's first keystroke
/// does not pay it.
/// </summary>
/// <remarks>
/// Measured with the render probe: the first paint of a session cost ~60 ms against 4-9 ms for every later one,
/// with only 181 rows on screen -- the data was never the cost. What that first paint actually pays is the JIT
/// of the result-mapping path plus the one-time first touch of the reads it does: the history snapshot, the
/// favorites' reachability probes, and the Start-Menu/settings catalog. All of that is read-only, and the mapper
/// records nothing (history records OPENED items, not searches), so warming it here cannot look like a search
/// the user made.
/// </remarks>
internal static class SearchWarmup
{
    private static readonly object PowerSubscriptionLock = new();
    private static bool _powerSubscribed;

    /// <summary>Schedules the warm-up on a pool thread; returns immediately.</summary>
    /// <remarks>
    /// Deliberately not lazy: warming on the first summon (double Ctrl) would move the cost from the first
    /// keystroke to the first summon, which is the same stall one gesture later. If the user does start typing
    /// first, this simply runs alongside the real search -- it holds no lock and shares no state beyond the
    /// read-only caches the mapper already uses, so the worst case is a little extra parallel work.
    /// </remarks>
    public static void Schedule()
    {
        EnsurePowerSubscription();
        _ = Task.Run(async () =>
        {
            var started = Environment.TickCount64;
            try
            {
                await Task.Yield();

                // The mapping warm-up below cannot warm the elevated service's query path. Run one tiny,
                // invisible local query first: this pays the service-side pipe/index/reader JIT before the
                // user's first keystroke, while still being far too small to behave like a broad search.
                var pipeWarm = WarmServiceQueryWhenReadyAsync();

                WarmUiMappingPath();
                await pipeWarm.ConfigureAwait(false);

                // Info, not Debug: whether this ran is the first thing to check when the first search still
                // stalls, and a silent warm-up made that unknowable.
                Logger.Log($"[SearchWarmup] Warm-up finished in {Environment.TickCount64 - started} ms.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SearchWarmup] Warm-up failed after {Environment.TickCount64 - started} ms: {ex.Message}", LogLevel.Info);
            }
        });

        async Task WarmServiceQueryWhenReadyAsync()
        {
            var started = Environment.TickCount64;
            using var service = new Core.Services.Search.SearchService();
            while (true)
            {
                try
                {
                    if (await service.PingAsync().ConfigureAwait(false))
                    {
                        var status = await service.GetStatusAsync().ConfigureAwait(false);
                        if (status.State is "ready" or "idle")
                        {
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await service.WarmupAsync(timeout.Token).ConfigureAwait(false);
                            Logger.Log($"[SearchWarmup] Service query warm-up finished after ready in {Environment.TickCount64 - started} ms.", LogLevel.Info);
                            return;
                        }
                    }
                }
                catch
                {
                    // The service can disappear while it is restarting; the next poll reconnects.
                }

                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    private static void WarmUiMappingPath()
    {
        // One query nothing can match warms catalog/history/favorites. The second query and synthetic batch
        // warm fuzzy matching, highlighting and ranking without recording a search.
        ViewModels.Search.Mapping.SearchResultMapper.BuildQuickResults(null, "\u0001warmup", null, null, false);
        ViewModels.Search.Mapping.SearchResultMapper.BuildQuickResults(null, "a", null, null, false);
        var warmRows = new List<SearchResult>
        {
            new() { Path = @"C:\Warmup\a.txt", Name = "a", IsDir = false },
            new() { Path = @"C:\Warmup\ab", Name = "ab", IsDir = false },
            new() { Path = @"C:\Warmup\warmup", Name = "warmup", IsDir = true },
        };
        ViewModels.Search.Mapping.SearchResultMapper.BuildQuickResults(warmRows, "a", null, null, false);
    }

    private static void EnsurePowerSubscription()
    {
        lock (PowerSubscriptionLock)
        {
            if (_powerSubscribed)
                return;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _powerSubscribed = true;
        }
    }

    private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
            return;

        _ = Task.Run(() =>
        {
            var started = Environment.TickCount64;
            try
            {
                WarmUiMappingPath();
                Logger.Log($"[SearchWarmup] Resume UI warm-up finished in {Environment.TickCount64 - started} ms.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SearchWarmup] Resume UI warm-up failed: {ex.Message}", LogLevel.Info);
            }
        });
    }
}
