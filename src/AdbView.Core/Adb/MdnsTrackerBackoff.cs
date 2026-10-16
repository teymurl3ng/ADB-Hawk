namespace AdbView.Adb;

using System;

/// <summary>
/// mDNS tracker reconnect backoff (ENGINEERING.md §11 row 8):
/// 1s, 2s, 5s, 15s, 30s cap, ±20% jitter.
/// Distinct from <see cref="DeviceTrackerBackoff"/> and server <see cref="Backoff"/>.
/// </summary>
internal sealed class MdnsTrackerBackoff {
    private static readonly TimeSpan[] BaseIntervals =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    private readonly Func<double> _jitterProvider;
    private int _step;

    public MdnsTrackerBackoff(Func<double>? jitterProvider = null) {
        _jitterProvider = jitterProvider
            ?? (() => (System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 401) / 1000.0) - 0.20);
    }

    public int CurrentStep => _step;

    public TimeSpan NextDelay() {
        int index = Math.Min(_step, BaseIntervals.Length - 1);
        var baseInterval = BaseIntervals[index];
        _step++;

        double jitter = Math.Clamp(_jitterProvider(), -0.20, 0.20);
        double totalSeconds = baseInterval.TotalSeconds * (1.0 + jitter);
        if (totalSeconds < 0) {
            totalSeconds = 0;
        }

        return TimeSpan.FromSeconds(totalSeconds);
    }

    public void Reset() {
        _step = 0;
    }
}
