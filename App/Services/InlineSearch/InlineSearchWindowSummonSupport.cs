using System.Windows.Interop;
using System.Windows.Threading;
using Lertaro.App.ViewModels.Search;
using Lertaro.App.Helpers.Visuals;
using Lertaro.Core;
using Lertaro.Core.Hook;
using Lertaro.Core.Hook.InlineSearch;

namespace Lertaro.App.Services;

// Split out purely to keep InlineSearchManager under the repo's per-file line limit. This class owns getting
// the card onto the screen and delegates the resulting state back to its one InlineSearchManager owner.
//
// It used to own making the card too, and made a new one for every summon. That is what the summon's own
// latency was spent on: a fresh window brings its view model, its visual tree and its first composition with
// it, measured at 170-258ms during which the window is already on screen with nothing painted in it
// (scratch/blackprobe). The window is now built once and kept; a summon is a re-docking and a Show.
internal sealed class InlineSearchWindowSummonSupport
{
    private readonly InlineSearchManager _manager;

    public InlineSearchWindowSummonSupport(InlineSearchManager manager) => _manager = manager;

    /// <summary>Builds and paints the card once at the parking row while startup is idle.</summary>
    public bool Prewarm()
    {
        if (_manager.Window != null)
            return false;

        var window = Create();
        _manager.Window = window;
        // Mark parked before Show so visibility callbacks do not treat this as an active inline session.
        _manager.IsCardParked = true;
        window.ShowActivated = false;
        new WindowInteropHelper(window).EnsureHandle();
        WindowParking.Park(window);
        window.Show();
        window.ParkAfterPrewarm();
        // Show is the point where Windows may still replace the parked coordinates during a startup display/DPI
        // settle. Re-apply them after the first frame and fall back to hiding if the native rectangle is still
        // on-screen, so a failed prewarm can never leave a search card permanently visible at a screen corner.
        WindowParking.Park(window);
        WindowParking.AfterFrame(window, () =>
        {
            if (_manager.Window != window || !_manager.IsCardParked)
                return;

            WindowParking.Park(window);
            if (!WindowParking.IsParked(window))
            {
                window.Hide();
                Logger.Log("[InlineSearchManager] Prewarmed card escaped parking; hidden until summon.", LogLevel.Warn);
            }
        });
        return true;
    }

    /// <summary>Brings the card up for whichever window the tracker is on now.</summary>
    public bool Summon(string initialText)
    {
        PowerThrottlingHelper.WindowShowing("inline");
        // Cancels any idle trim armed by CloseInlineSearch: trimming just before a summon is strictly
        // worse than not trimming, since those pages are about to be needed again. Mirrors the quick
        // window's own WindowShowing call.
        IdleWorkingSetTrimmer.WindowShowing();

        var tracker = _manager.ExplorerTracker;

        // The card is already on screen: this is a keystroke after the first one, and re-summoning would wipe
        // what the user has typed into it. The create-per-summon version got the same early exit by existing.
        if (_manager.Window is { IsVisible: true } && !_manager.IsCardParked) return false;

        _manager.Window ??= Create();
        var window = _manager.Window;
        _manager.IsCardParked = false;

        // Before the dock: ownership is a property of the HWND, and a card built just above does not have one
        // yet, so the order here decided whether its first session was docked at all.
        new WindowInteropHelper(window).EnsureHandle();

        _manager.CurrentHostHwnd = tracker.ActiveHwnd;
        _manager.KeyboardHook.IsInlineSearchVisible = true;
        _manager.KeyboardHook.IsInlineWindowOnScreen = true;
        _manager.MouseHook.Start();

        window.HostOnto(tracker.ActiveHwnd);
        // Everything a brand-new card would have come with, since this one came back instead: the folder it is
        // reading, an empty box, and no half-open actions menu.
        window.BeginSession();
        // Show from the mirror immediately. IsInlineWindowOnScreen above asks the Hook's paced poller
        // for a fresh path; its normal PathCaptured event repairs this scope and re-runs the current query.
        window.ViewModel.SearchScope = tracker.ActivePath;
        // The first typed character is what summoned this card. Put it into the box before the sizing
        // and reveal passes, so the first visible frame is already the real query instead of an empty
        // box followed by a second query and layout pass one dispatcher turn later.
        window.UpdateSearchDisplay(initialText);

        // Sized before it is shown, not from its Loaded handler like every later pass: the window is opaque
        // now, so the card's height has to be the window's height before the first frame or the frame that
        // goes up is the XAML's own 550 DIP with a small card pinned inside one edge of it -- a slab where
        // the transparent window used to show nothing. ApplyCardHeight is safe this early by its own
        // measurement comment (it charges the search bar and banner at their natural sizes until WPF has
        // arranged them).
        //
        // And sized after the dock width, which is the half that matters now that the card is reused: the
        // search bar and the path banner report the arranged widths the PREVIOUS host left behind, a card
        // measured against those lands at the wrong height, and the correction becomes a resize the user
        // watches. PositionWindowImmediate is what puts the new width on the window and lays it out, and
        // ApplyCardHeight re-positions through the positioner, so the target the reveal lands on is the one
        // computed from the real width and the real height together.
        window.Positioner.PositionWindowImmediate();
        window.CardSizing.ApplyCardHeight(forSummon: true);
        // Shown at the parking row and landed a render pass later: the dock-then-Show order this replaces left
        // the card on screen for 170-258ms with nothing painted in it, which is the black frame.
        window.Positioner.Reveal();
        window.ViewModel.EnsureServiceMonitoringActive();

        var foreground = ExplorerNativeHooks.GetForegroundWindow();
        var isTextInputFocused = foreground != IntPtr.Zero && InputFocusEvaluator.IsForegroundTextInputFocused(foreground);
        if (!isTextInputFocused && !tracker.IsActiveWindowDialog)
        {
            if (window.ActivateAndFocusSearchBox())
            {
                _manager.KeyboardHook.IsInlineSearchVisible = false;
                _manager.KeyboardHook.Stop();
            }
            else
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_manager.Window != window || !window.IsVisible) return;
                    if (window.ActivateAndFocusSearchBox())
                    {
                        _manager.KeyboardHook.IsInlineSearchVisible = false;
                        _manager.KeyboardHook.Stop();
                    }
                }), DispatcherPriority.Input);
            }
        }
        else
        {
            var dialogHwnd = tracker.ActiveHwnd;
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (dialogHwnd == IntPtr.Zero) return;
                ExplorerNativeHooks.SetForegroundWindow(dialogHwnd);
                var editBox = ExplorerNativeHooks.FindSubEditBox(dialogHwnd);
                if (editBox != IntPtr.Zero) ExplorerNativeHooks.SetFocus(editBox);
            }), DispatcherPriority.Input);
        }

        Logger.Log($"[InlineSearchManager] Card shown. Scope: {window.ViewModel.SearchScope}", LogLevel.Debug);
        return true;
    }

    // The one place the card's own view model is built, so the scope it starts reading from and the inline
    // context flag cannot be set for a summon and missed for the next one.
    private InlineSearchWindow Create()
    {
        var viewModel = new QuickSearchViewModel
        {
            IsInlineSearchContext = true
        };
        var window = new InlineSearchWindow(viewModel, _manager);

        // Win32 destroys an owned window along with its owner, and while the card is on screen it is owned by
        // the host it was docked to. WPF reads that WM_DESTROY as a close, so the cached reference would be a
        // window Show and EnsureHandle both refuse to touch afterwards -- the crash dialog every later summon
        // produced. Dropping it here is what makes the next summon build a new card; the same guard every
        // other cached window in this app carries (see AppWindowManager).
        window.Closed += (_, _) =>
        {
            // A superseded card can outlive the manager's reference to it; only the cached one may touch the
            // manager, and only it can be the card Win32 took down with its host.
            if (_manager.Window != window) return;
            _manager.Window = null;
            // Heal the hook flags right here, not on the next foreground change: with no cached window every
            // later CloseInlineSearch call returns before the reset, and the user's keystrokes stay swallowed
            // in whatever window they moved to until something makes the hook raise ExplorerActivated again.
            _manager.CloseInlineSearch("CardClosedByOwner");
        };

        return window;
    }
}
