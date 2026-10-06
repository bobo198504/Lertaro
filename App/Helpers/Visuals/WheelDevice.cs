using System.Runtime.InteropServices;

namespace Lertaro.App.Helpers.Visuals;

/// <summary>
/// Which kind of wheel a WM_MOUSEWHEEL came from -- the C# port of the reaper plugin's device.h, which
/// is the authority for this rule (SmoothWheelScroll for reaper / src / device.h).
/// </summary>
/// <remarks>
/// Three senders share the one message and differ only in what they put in the delta and in the
/// message's extra-info word: a NOTCHED mouse reports a whole WHEEL_DELTA (120) as discrete clicks; a
/// FREE-SPINNING wheel reports a small, REGULAR share of a notch (always 15, or 30, many times a turn);
/// a TOUCHPAD reports small values with NO fixed step, and Windows tags the message as touch or pen.
///
/// The classification is a marker, not a behaviour: it says what arrived, never what to do with it. The
/// caller decides that, and it only eases the first two -- a touchpad is already smooth, so easing it a
/// second time is what made it feel wrong.
/// </remarks>
internal static class WheelDevice
{
    internal enum Kind
    {
        /// <summary>Not enough evidence yet (the first messages of a gesture): never guess, pass through.</summary>
        Unknown = 0,
        Notched,
        FreeSpin,
        Touchpad,
    }

    /// <summary>One notch, in the wheel's own units; Windows defines the wheel delta so this is one notch.</summary>
    private const int WheelDelta = 120;

    /// <summary>The touch/pen signature in GetMessageExtraInfo(): the high three bytes carry it, the low byte is the contact count.</summary>
    private const long TouchSignature = 0xFF515700L;
    private const long TouchMask = 0xFFFFFF00L;

    private const int Window = 8;      // recent sub-notch magnitudes kept
    private const int MinSamples = 3;  // fewer than this: say "unknown" rather than guess
    private const int Levels = 1;      // a flywheel counting hardware detents reports exactly one magnitude

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();

    /// <summary>The extra-info word of the message being handled right now, or zero when unavailable.</summary>
    internal static long ExtraInfo() => GetMessageExtraInfo().ToInt64();

    private static readonly Tracker CurrentTracker = new();

    /// <summary>Classify one wheel message; the evidence carries over between calls, per gesture.</summary>
    internal static Kind Classify(int delta) => CurrentTracker.Feed(delta, ExtraInfo());

    private sealed class Tracker
    {
        private readonly int[] _magnitudes = new int[Window];
        private int _count;
        private bool _varied;  // this gesture has shown more than one magnitude -- locked to touchpad
        private bool _marked;  // the OS touch/pen marker was seen

        internal Kind Feed(int delta, long extraInfo)
        {
            if (delta == 0) return Last();

            // 1. The OS marker wins outright: it is an explicit statement, so a touchpad whose values
            //    happen to look regular is still a touchpad.
            if ((extraInfo & TouchMask) == TouchSignature)
            {
                _marked = true;
                return Kind.Touchpad;
            }

            var magnitude = Math.Abs(delta);

            // 2. A whole notch is a notched mouse whatever came before, and it starts a fresh gesture:
            //    the sub-notch evidence and the "has varied" lock are both cleared.
            if (magnitude % WheelDelta == 0)
            {
                _count = 0;
                _varied = false;
                return Kind.Notched;
            }

            // 3. Sub-notch: keep the recent magnitudes and read how many DISTINCT ones there are.
            for (var i = 0; i + 1 < Window; i++) _magnitudes[i] = _magnitudes[i + 1];
            _magnitudes[Window - 1] = magnitude;
            if (_count < Window) _count++;
            if (_count >= MinSamples && DistinctMagnitudes() >= 2) _varied = true;
            return SubNotchVerdict();
        }

        private Kind Last()
        {
            if (_marked) return Kind.Touchpad;
            return _count == 0 ? Kind.Unknown : SubNotchVerdict();
        }

        private int DistinctMagnitudes()
        {
            var levels = 0;
            for (var i = Window - _count; i < Window; i++)
            {
                var seen = false;
                for (var j = Window - _count; j < i; j++)
                {
                    if (_magnitudes[j] != _magnitudes[i]) continue;
                    seen = true;
                    break;
                }

                if (!seen) levels++;
            }

            return levels;
        }

        private Kind SubNotchVerdict()
        {
            if (_count < MinSamples) return Kind.Unknown;

            // A value that is steady NOW is not proof of a flywheel if this gesture has ALREADY varied: a
            // touchpad creeping slowly can emit eight identical small values in a row, which would read as
            // "one fixed step" and leak the touchpad into the model. A real free-spinning wheel reports
            // its fixed step from the first message and never varies, so once varied it stays touchpad.
            if (_varied) return Kind.Touchpad;
            return DistinctMagnitudes() <= Levels ? Kind.FreeSpin : Kind.Touchpad;
        }
    }
}