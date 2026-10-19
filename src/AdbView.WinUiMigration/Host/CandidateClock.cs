namespace AdbView.WinUiMigration.Host;

internal static class CandidateClock {
    public static DateTimeOffset Read(bool live, TimeSpan fixtureElapsed, Func<DateTimeOffset>? systemClock = null) =>
        live ? (systemClock is null ? AdbView.App.SystemClock.UtcNow : systemClock())
            : AdbView.Bridge.FixtureSnapshotProvider.T0 + fixtureElapsed;
}
