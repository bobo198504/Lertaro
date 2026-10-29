using System.Diagnostics;
using Lertaro.Core.Services.Search;

namespace Lertaro.Core.Hook.Ipc;

// Split out of HookIpcClient to keep that file under the line-count limit. The App never spawns the hook
// process itself anymore -- every launch (elevated or not) is asked of the already-running --service
// instance over IPC (see HookProcessBroker on the Service side), so there's no App-side "runas" UAC
// fallback left to trigger. If the Service is unreachable (not installed, still starting up after
// login/boot, etc.) this just returns null; HookIpcClient's own RunLoop retries with a backoff.
internal sealed class HookLaunchBroker : IDisposable
{
    private readonly SearchService _searchService = new();

    public async Task<Process?> LaunchAsync(bool requestElevation, CancellationToken token)
    {
        var primary = await RequestAsync(requestElevation, token).ConfigureAwait(false);
        if (primary.Hook != null)
            return primary.Hook;

        // The elevated hook is asked for first because it is the only one that sees keystrokes typed into
        // elevated windows. The Service grants it only when this session's user is genuinely an
        // administrator and refuses otherwise (see SessionProcessLauncher) -- which is exactly what a
        // standard user's session looks like from here. An unelevated hook still registers the global
        // hotkeys, so retry one level down rather than leaving the App with no hook at all.
        //
        // This is what made the first double-tap after launch do nothing: a declined elevated request
        // returned null, and the App only got a hook after HookIpcClient's backoff loop came around again.
        // One extra round trip replaces that wait.
        Logger.Log($"[HookIpcClient] Hook launch declined ({primary.Error}); retrying at the other privilege level.", LogLevel.Debug);
        var fallback = await RequestAsync(!requestElevation, token).ConfigureAwait(false);
        if (fallback.Hook == null)
            return null;

        Logger.Log($"[HookIpcClient] Hook launched at the fallback privilege level (PID {fallback.Hook.Id}) -- hotkeys over elevated windows stay out of reach.", LogLevel.Info);
        return fallback.Hook;
    }

    // Never throws: RunLoop's backoff only runs on a null result, so an exception escaping here would retry
    // the launch on a hot loop instead.
    private async Task<(Process? Hook, string? Error)> RequestAsync(bool requestElevation, CancellationToken token)
    {
        try
        {
            var (ok, pid, error) = await _searchService.RequestHookLaunchAsync(requestElevation, token).ConfigureAwait(false);
            if (!ok)
            {
                if (!string.IsNullOrEmpty(error))
                {
                    // The hook launch is retried on a backing-off delay by HookIpcClient, so a batch of these
                    // is expected while the service is still coming up on boot; downgrade to Debug
                    // inside the cold-start window and keep real failures afterwards visible.
                    var level = ServicePipeReadinessGate.Instance.IsColdStart(Environment.TickCount64)
                        ? LogLevel.Debug
                        : LogLevel.Warn;
                    Logger.Log($"[HookIpcClient] Service declined hook launch: {error}", level);
                }
                return (null, error ?? "declined");
            }

            return (Process.GetProcessById(pid), null);
        }
        catch (Exception ex)
        {
            Logger.Log($"[HookIpcClient] Service hook-launch request failed: {ex.Message}", LogLevel.Warn);
            return (null, ex.Message);
        }
    }

    public void Dispose() => _searchService.Dispose();
}
