namespace AdbView.NativeUi;

using System;
using System.Threading;
using System.Threading.Tasks;
using AdbView.State;
using Serilog;

/// <summary>
/// Turns the product's aggregate tray status into what the bubble shows, and owns the one
/// lifetime rule the bubble adds: transient states appear for <see cref="TransientLifetime"/>
/// and then the bubble hides (decision 0020 / DESIGN.md §30).
///
/// The expiry is a single one-shot, generation-guarded wait — the same shape the state engine
/// already uses for tracker-failure grace and the 60 s pairing wait — retargeted by the next
/// state and cancelled outright by a permanent state. It is not periodic polling (R-05), and it
/// deliberately lives here rather than in the reducer so `AppSnapshot`/`BridgeProtocol` stay
/// untouched. Clock and delay are injected for deterministic tests.
/// </summary>
internal sealed class StatusBubblePresenter : IDisposable {
    /// <summary>How long a transient state is shown before the bubble hides (DESIGN.md §30).</summary>
    public static readonly TimeSpan TransientLifetime = TimeSpan.FromSeconds(60);

    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();

    private BubbleState? _current;
    private TrayStatusKind _lastAggregate = TrayStatusKind.Idle;
    private string _lastDetail = string.Empty;
    private string? _lastConnectionDetail;
    private long _generation;
    private CancellationTokenSource? _expiryCts;
    private int _disposed;

    public StatusBubblePresenter(
        Func<DateTimeOffset> clock,
        Func<TimeSpan, CancellationToken, Task> delay) {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    /// <summary>Raised whenever the bubble's content changes (including to/shown-from nothing).</summary>
    public event Action<BubbleState?>? Changed;

    /// <summary>What the bubble should show; null means hidden.</summary>
    public BubbleState? Current {
        get {
            lock (_gate) {
                return _current;
            }
        }
    }

    /// <summary>
    /// Feeds one authoritative aggregate. <paramref name="detail"/> is the product's existing
    /// status text (the tray tooltip), so the bubble never authors copy of its own.
    /// </summary>
    public void Accept(TrayStatusKind aggregate, string detail) {
        ArgumentNullException.ThrowIfNull(detail);

        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        BubbleState? next;
        bool armExpiry = false;

        lock (_gate) {
            TrayStatusKind previous = _lastAggregate;
            bool dropped = StatusBubblePolicy.IsConnection(previous)
                && aggregate == TrayStatusKind.Idle;

            if (dropped) {
                // Decision 0020: the aggregate collapses straight from connected to idle, so the
                // red "just disconnected" episode exists only here. It reuses the last detail we
                // had while connected, which is the phone the user just lost.
                next = StatusBubblePolicy.Disconnected(_lastConnectionDetail ?? detail);
                armExpiry = true;
            }
            else {
                next = StatusBubblePolicy.FromTrayStatus(aggregate, detail);
                armExpiry = next is { Lifetime: BubbleLifetime.Transient };
            }

            _lastAggregate = aggregate;
            _lastDetail = detail;
            if (StatusBubblePolicy.IsConnection(aggregate)) {
                _lastConnectionDetail = detail;
            }
        }

        Publish(next, armExpiry);
    }

    /// <summary>Applies a state, cancelling or arming the single expiry as required.</summary>
    private void Publish(BubbleState? next, bool armExpiry) {
        long generation;
        CancellationTokenSource? toCancel = null;

        lock (_gate) {
            if (Equals(_current, next) && (!armExpiry || _expiryCts is not null)) {
                // Unchanged, and nothing new to arm: leave the running expiry alone.
                return;
            }

            _current = next;
            generation = ++_generation;

            if (armExpiry) {
                toCancel = _expiryCts;
                _expiryCts = new CancellationTokenSource();
            }
            else {
                // A permanent state (or hiding) supersedes any pending transient.
                toCancel = _expiryCts;
                _expiryCts = null;
            }
        }

        CancelAndDispose(toCancel);

        if (armExpiry) {
            _ = ExpireAfterLifetimeAsync(generation);
        }

        Changed?.Invoke(Current);
    }

    private async Task ExpireAfterLifetimeAsync(long generation) {
        CancellationToken token;
        lock (_gate) {
            if (_expiryCts is null) {
                return;
            }

            token = _expiryCts.Token;
        }

        try {
            await _delay(TransientLifetime, token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // R-08: cancellation is the normal path; anything else is logged.
        catch (OperationCanceledException) {
            return;
        }
        catch (Exception ex) {
            Log.Warning(ex, "StatusBubble: transient expiry wait failed; hiding the bubble");
        }
#pragma warning restore CA1031

        bool superseded;
        lock (_gate) {
            superseded = generation != _generation || _expiryCts is null;
            if (!superseded) {
                _current = null;
                _expiryCts = null;
                _generation++;
            }
        }

        if (!superseded) {
            Log.Information(
                "StatusBubble: transient state expired after {Seconds}s; hiding.",
                (int)TransientLifetime.TotalSeconds);
            Changed?.Invoke(null);
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? cts) {
        if (cts is null) {
            return;
        }

        try {
            cts.Cancel();
        }
        catch (ObjectDisposedException) {
        }

        cts.Dispose();
    }

    /// <summary>Test-only: the detail text most recently observed for a connection.</summary>
    internal string? LastConnectionDetail {
        get {
            lock (_gate) {
                return _lastConnectionDetail;
            }
        }
    }

    /// <summary>Test-only: the client clock, exposed so tests can assert relative timing intent.</summary>
    internal DateTimeOffset Now => _clock();

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        CancellationTokenSource? toCancel;
        lock (_gate) {
            toCancel = _expiryCts;
            _expiryCts = null;
            _current = null;
        }

        CancelAndDispose(toCancel);
    }
}
