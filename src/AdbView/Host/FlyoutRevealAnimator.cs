namespace AdbView.Host;

using System;
using System.Drawing;
using Serilog;

internal interface ITickSource : IDisposable {
    void Start(Action tick);
    void Stop();
}

/// <summary>
/// Fluent-curve presentation animator (decision 0013, amended by 0023): drives the flyout
/// window's bounds for the open/close reveal, which is a <b>position-only</b> translation of a
/// fixed-size window (see <see cref="AnimateSlide"/>); the height itself is an instant self-size
/// step and never interpolates (decisions 0012/0021). Retargeting replaces the target from the
/// last applied frame (R-06). Reduced motion bypasses this class entirely at the call sites.
/// </summary>
internal sealed class FlyoutRevealAnimator {
    internal static readonly TimeSpan GrowthDuration = TimeSpan.FromMilliseconds(167);
    internal static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(167);
    internal static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(83);

    private readonly ITickSource _ticks;
    private readonly Action<Rectangle> _applyBounds;
    private readonly Func<long> _elapsedMs;

    private Rectangle _from;
    private Rectangle _to;
    private long _startTicks;
    private long _durationTicks;
    private bool _running;
    private Action? _completed;
    private bool _positionOnly;

    // Tick-quality evidence (decision 0023): frames actually applied, and the worst gap between
    // ticks. Logged once per animation so a jitter regression is visible in the log with no
    // tooling — the animation's own duration is the reference the gap is read against.
    private int _framesApplied;
    private long _lastTickMs;
    private long _worstGapMs;

    public FlyoutRevealAnimator(ITickSource tickSource, Action<Rectangle> applyBounds, Func<long>? elapsedMs = null) {
        _ticks = tickSource;
        _applyBounds = applyBounds;
        _elapsedMs = elapsedMs ?? (() => Environment.TickCount64);
    }

    public bool IsAnimating => _running;

    /// <summary>Last applied frame — the retarget origin (R-06 latest state wins).</summary>
    public Rectangle LastApplied { get; private set; }

    /// <summary>Starts (or retargets) a bounds animation. If one is running, the target is
    /// replaced and the flight continues from the last applied frame.</summary>
    public void AnimateBounds(Rectangle from, Rectangle to, TimeSpan duration, Action? completed = null) =>
        Start(from, to, duration, completed, positionOnly: false);

    /// <summary>
    /// Decision 0023 / DESIGN §24: the open/close reveal is a <b>position-only</b> translation —
    /// the window's size is fixed for the flight, so the slide never resizes the HWND per frame.
    /// Animating a shadowed window's size every frame is the pattern Microsoft's AnimateWindow
    /// doc calls "visually distracting, jerky". Endpoints that differ in size mean the caller's
    /// origin came from a stale window rect; the target size wins and the mismatch is logged.
    /// </summary>
    public void AnimateSlide(Rectangle from, Rectangle to, TimeSpan duration, Action? completed = null) {
        if (from.Size != to.Size) {
            Log.Warning(
                "FlyoutRevealAnimator: slide endpoints differ in size ({From} -> {To}); forcing the target size.",
                from,
                to);
            from = new Rectangle(from.X, from.Y, to.Width, to.Height);
        }

        Start(from, to, duration, completed, positionOnly: true);
    }

    private void Start(Rectangle from, Rectangle to, TimeSpan duration, Action? completed, bool positionOnly) {
        if (from == to) {
            _applyBounds(to);
            completed?.Invoke();
            return;
        }

        _from = from;
        _to = to;
        _positionOnly = positionOnly;
        LastApplied = from;
        _startTicks = _elapsedMs();
        _durationTicks = Math.Max(1, (long)duration.TotalMilliseconds);
        _framesApplied = 0;
        _lastTickMs = _startTicks;
        _worstGapMs = 0;

        if (!_running) {
            _completed = completed;
            _running = true;
            _ticks.Start(Tick);
            Log.Information("FlyoutRevealAnimator: bounds animation started {From}->{To}.", from, to);
        }
        else {
            // Retarget (R-06): the flight continues from the last applied frame, and any pending
            // completion (e.g. light-dismiss arming from an open reveal) must still run.
            var prior = _completed;
            if (prior is not null && completed is not null) {
                var added = completed;
                completed = () => {
                    prior();
                    added();
                };
            }
            else if (completed is null) {
                completed = prior;
            }

            _completed = completed;
            Log.Information("FlyoutRevealAnimator: retargeted to {To}.", to);
        }

        Tick(); // apply the first frame immediately (progress 0 = from)
    }

    /// <summary>Stops any running animation without applying the target.</summary>
    public void Cancel() {
        if (!_running) {
            return;
        }

        _running = false;
        _ticks.Stop();
        _completed = null;
        Log.Information("FlyoutRevealAnimator: cancelled.");
    }

    /// <summary>Stops the animation and snaps to the target (used when reduced motion toggles
    /// mid-flight or a caller needs terminal state without the remaining frames).</summary>
    public void CompleteImmediately() {
        if (!_running) {
            return;
        }

        _running = false;
        _ticks.Stop();
        _applyBounds(_to);
        var done = _completed;
        _completed = null;
        done?.Invoke();
    }

    private void Tick() {
        if (!_running) {
            _ticks.Stop();
            return;
        }

        long now = _elapsedMs();
        long gap = now - _lastTickMs;
        _lastTickMs = now;
        if (gap > _worstGapMs) {
            _worstGapMs = gap;
        }

        double progress = Math.Min(1.0, (double)(now - _startTicks) / _durationTicks);
        double eased = Easing.FluentPointToPoint(progress);

        // A slide is a position-only translation (decision 0023 / DESIGN §24): the size is the
        // target's for every frame, so the reveal clips nothing and resizes nothing.
        int width = _positionOnly ? _to.Width : _from.Width + (int)Math.Round((_to.Width - _from.Width) * eased);
        int height = _positionOnly ? _to.Height : _from.Height + (int)Math.Round((_to.Height - _from.Height) * eased);

        var frame = Rectangle.Round(new RectangleF(
            _from.X + ((_to.X - _from.X) * (float)eased),
            _from.Y + ((_to.Y - _from.Y) * (float)eased),
            width,
            height));

        // Skip a frame identical to the one the window is already at. The caller places the
        // window on `from` before starting (and a frame callback can fire more than once per
        // composed frame), so re-applying the same rect is pure redundant SetWindowPos churn.
        if (frame != LastApplied) {
            LastApplied = frame;
            _framesApplied++;
            _applyBounds(frame);
        }

        if (progress >= 1.0) {
            _running = false;
            _ticks.Stop();
            Log.Information(
                "FlyoutRevealAnimator: completed {Frames} frames over a {Duration} ms flight; worst tick gap {WorstGap} ms.",
                _framesApplied,
                _durationTicks,
                _worstGapMs);
            var done = _completed;
            _completed = null;
            done?.Invoke();
        }
    }
}

/// <summary>cubic-bezier(0.55, 0.55, 0, 1) — the DESIGN §24 Fluent Point-to-Point curve.</summary>
internal static class Easing {
    private const float P1X = 0.55f;
    private const float P1Y = 0.55f;
    private const float P2X = 0.0f;
    private const float P2Y = 1.0f;

    public static double FluentPointToPoint(double t) {
        t = Math.Clamp(t, 0.0, 1.0);
        if (t is 0.0 or 1.0) {
            return t;
        }

        // Solve the x parametric for t (x is monotone in t for this curve) by bisection.
        double lo = 0.0;
        double hi = 1.0;
        for (int i = 0; i < 24; i++) {
            double mid = (lo + hi) / 2.0;
            double x = BezierComponent(P1X, P2X, mid);
            if (x < t) {
                lo = mid;
            }
            else {
                hi = mid;
            }
        }

        double solved = (lo + hi) / 2.0;
        return BezierComponent(P1Y, P2Y, solved);
    }

    private static double BezierComponent(double p1, double p2, double t) {
        double u = 1.0 - t;
        // B(t) = 3u²t·p1 + 3ut²·p2 + t³
        return (3.0 * u * u * t * p1) + (3.0 * u * t * t * p2) + (t * t * t);
    }
}
