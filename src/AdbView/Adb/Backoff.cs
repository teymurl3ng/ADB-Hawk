namespace AdbView.Adb;

using System;

internal sealed class Backoff {
    private static readonly TimeSpan[] BaseIntervals = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    };

    private readonly Func<double> _jitterProvider;
    private int _step;

    public Backoff(Func<double>? jitterProvider = null) {
        // Jitter provider returns a factor in [-0.20, +0.20].
        // Default uses RandomNumberGenerator to produce uniform distribution in [-0.20, +0.20].
        _jitterProvider = jitterProvider ?? (() => (System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 401) / 1000.0) - 0.20);
    }

    public int CurrentStep => _step;

    public TimeSpan NextDelay() {
        int index = Math.Min(_step, BaseIntervals.Length - 1);
        var baseInterval = BaseIntervals[index];
        _step++;

        double jitter = _jitterProvider();
        jitter = Math.Clamp(jitter, -0.20, 0.20);

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
