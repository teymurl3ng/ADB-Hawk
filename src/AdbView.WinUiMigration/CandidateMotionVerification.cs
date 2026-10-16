namespace AdbView.WinUiMigration;

using System.Collections.Immutable;
using System.Numerics;
using AdbView.Bridge;
using AdbView.State;
using AdbView.WinUiMigration.Host;
using AdbView.WinUiMigration.NativeUi;
using AdbView.WinUiMigration.NativeUi.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal sealed partial class CandidateWindow {
    private async Task VerifyMotionScenariosAsync() {
        int writes = _windowController.NativeBoundsWrites;
        var size = AppWindow.Size;
        _motionPolicy.SetSystemEnabled(true);
        AppSnapshot devices = DeviceFixtures.Snapshot("two-devices", 10);
        // Avoid AgeFormatter's deliberate first-five-seconds "Just now" plateau.
        devices = devices with { Devices = devices.Devices.Select(device => device with { StateSince = FixtureSnapshotProvider.T0 - TimeSpan.FromSeconds(46) }).ToImmutableArray() };
        await PublishAndDrainAsync(devices).ConfigureAwait(true);
        ContentTransitionController motion = _presenter.ContentMotion;
        string first = devices.Devices[0].Id, second = devices.Devices[1].Id;
        _presenter.OpenDevice(first);
        long generation = motion.Generation;
        DeviceDetails original = _presenter.Details!;
        Check("navigation-starts-only-incoming-container", motion.IsActive && motion.OwnedAnimationHandles == 2
            && PageHost.Children.Count == 1 && ReferenceEquals(PageHost.Children[0], original.Scroll));
        _presenter.OpenDevice(first);
        Check("same-target-navigation-is-no-op", generation == motion.Generation && ReferenceEquals(original, _presenter.Details));
        await InvokeButtonAsync(original.CopyFields["serial"].Button).ConfigureAwait(true);
        Check("input-during-navigation-is-immediate", _fixtureCopies.Count == 1 && motion.IsActive);
        AppSnapshot changed = devices with { Revision = 11, Devices = devices.Devices.SetItem(0, devices.Devices[0] with { BatteryPercent = 42 }) };
        await PublishAndDrainAsync(changed).ConfigureAwait(true);
        Check("snapshot-churn-does-not-restart-navigation", motion.Generation == generation && _presenter.Snapshot!.Revision == 11 && HasText("42%"));
        _presenter.OpenDevice(second);
        Check("new-navigation-replaces-live-destination-immediately", _presenter.DetailsId == second && !original.CopyFields["serial"].Button.IsEnabled
            && PageHost.Children.Count == 1 && motion.Generation > generation && motion.CompletionSubscriptions == 1 && motion.OwnedAnimationHandles == 2);
        await Task.Delay(240).ConfigureAwait(true);
        CheckSettled("replaced-navigation-settles-new-target", motion);
        Check("stale-completion-cannot-change-current-route", _presenter.DetailsId == second);
        foreach (int interruption in new[] { 0, 42, 83, 150 }) {
            _presenter.Back();
            if (interruption > 0) { await Task.Delay(interruption).ConfigureAwait(true); }
            _motionPolicy.SetSystemEnabled(false);
            CheckSettled("reduced-motion-settles-at-" + interruption, motion);
            long settled = motion.Generation;
            _motionPolicy.SetSystemEnabled(true);
            Check("reenable-only-affects-future-navigation-" + interruption, !motion.IsActive && motion.Generation == settled);
            _presenter.OpenDevice(first);
            HideSurface();
            Check("hide-stops-content-and-age-work-" + interruption, !motion.IsActive && motion.OwnedAnimationHandles == 0
                && motion.CompletionSubscriptions == 0 && !_presenter.AgeTicker.IsRunning && !Root.IsHitTestVisible && !AppWindow.IsVisible);
            ShowSurface();
            Check("fallback-b-reopen-is-immediate-" + interruption, _surface.State == SurfaceState.Open && AppWindow.IsVisible
                && Root.IsHitTestVisible && _presenter.DetailsId is null && !motion.IsActive);
        }
        long opened = _surface.Generation;
        ShowSurface(); ShowSurface();
        Check("repeated-open-is-no-op", opened == _surface.Generation);
        _presenter.OpenDevice(first);
        SuspendPresentation();
        Check("suspend-stops-all-presentation-work", _surface.Suspended && !AppWindow.IsVisible && !motion.IsActive && !_presenter.AgeTicker.IsRunning);
        ShowSurface();
        Check("suspended-show-cannot-reveal-window", !AppWindow.IsVisible);
        ResumePresentation(); ShowSurface();
        Check("resume-permits-explicit-show-without-replaying-motion", AppWindow.IsVisible && !motion.IsActive);
        Check("visible-age-ticker-started", _presenter.AgeTicker.IsRunning);
        long revision = _presenter.Snapshot!.Revision;
        int ticks = _presenter.AgeTicker.TickCount;
        string age = _presenter.Rows[first].AgeText;
        await Task.Delay(1100).ConfigureAwait(true);
        Check("age-tick-updates-only-visible-age-text", _presenter.AgeTicker.TickCount > ticks && _presenter.Rows[first].AgeText != age
            && _presenter.Snapshot.Revision == revision);
        HideSurface(); ticks = _presenter.AgeTicker.TickCount;
        await Task.Delay(1100).ConfigureAwait(true);
        Check("hidden-age-ticker-has-no-callbacks", !_presenter.AgeTicker.IsRunning && _presenter.AgeTicker.TickCount == ticks);
        ShowSurface();
        AppSnapshot busy = DeviceFixtures.Snapshot("pairing-progress", 12);
        await PublishAndDrainAsync(busy).ConfigureAwait(true);
        _presenter.OpenPairing(); Root.UpdateLayout(); await DispatcherTurnAsync().ConfigureAwait(true);
        await Task.Delay(220).ConfigureAwait(true);
        PairingPage pairing = _presenter.Pairing!;
        Stage("busy observation loaded=" + pairing.Root.IsLoaded + " policy=" + _motionPolicy.Enabled + " visible=" + _presenter.PresentationVisible + " ring=" + pairing.BusyIndicator.IsActive + " ageTimer=" + _presenter.AgeTicker.IsRunning);
        Check("busy-ring-only-while-page-visible-and-motion-enabled", pairing.BusyIndicator.IsActive && !_presenter.AgeTicker.IsRunning);
        _motionPolicy.SetSystemEnabled(false);
        Check("reduced-motion-busy-is-static-snapshot-wording", !pairing.BusyIndicator.IsActive && pairing.BusyIndicator.Visibility == Visibility.Collapsed
            && HasText(busy.Pairing!.Title) && HasText(busy.Pairing.Detail));
        _motionPolicy.SetSystemEnabled(true);
        Check("future-busy-clock-follows-live-policy", pairing.BusyIndicator.IsActive && !motion.IsActive);
        _presenter.Back(); Root.UpdateLayout();
        Check("detached-busy-page-has-no-progress-clock", !pairing.BusyIndicator.IsActive);
        _presenter.OpenPairing();
        await PublishAndDrainAsync(DeviceFixtures.Snapshot("adb-unavailable", 13)).ConfigureAwait(true);
        Check("semantic-invalidation-detaches-page-and-settles-without-motion", _presenter.Pairing is null && !motion.IsActive && HasText(_presenter.Snapshot!.Health.Title));
        await PublishAndDrainAsync(devices with { Revision = 14 }).ConfigureAwait(true);
        await VerifyMotionPreferenceSubscriptionAsync().ConfigureAwait(true);
        _motionPolicy.SetSystemEnabled(true);
        _presenter.OpenDevice(first);
        await PublishAndDrainAsync(devices with { Revision = 15, Shell = devices.Shell with { ReducedMotion = true } }).ConfigureAwait(true);
        CheckSettled("snapshot-reduced-motion-settles-current-effect", motion);
        _motionPolicy.SetSystemEnabled(true);
        Check("snapshot-disabled-wins-over-system-enable", !_motionPolicy.Enabled);
        await PublishAndDrainAsync(devices with { Revision = 16 }).ConfigureAwait(true);
        Check("snapshot-motion-enable-does-not-replay", _motionPolicy.Enabled && !motion.IsActive);
        _presenter.Back(); motion.Settle();
        // Warm the same native details/focus lifecycle without app-owned motion before
        // measuring additional handles from the 500 animated cycles. No forced GC.
        _motionPolicy.SetSystemEnabled(false);
        for (int index = 0; index < 500; index++) {
            _presenter.OpenDevice(first); _presenter.Back();
            if (index % 10 == 0) { await Task.Delay(16).ConfigureAwait(true); }
        }
        await Task.Delay(1000).ConfigureAwait(true);
        _motionPolicy.SetSystemEnabled(true);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh(); int handles = process.HandleCount; long privateBytes = process.PrivateMemorySize64;
        int privateStart = motion.OwnedAnimationHandles;
        for (int index = 0; index < 500; index++) {
            _presenter.OpenDevice(first);
            _presenter.Back();
            if (index % 10 == 0) { await Task.Delay(16).ConfigureAwait(true); }
        }
        motion.Settle();
        await Task.Delay(1000).ConfigureAwait(true);
        process.Refresh();
        Check("five-hundred-navigation-cycles-release-owned-animations", motion.OwnedAnimationHandles == 0 && privateStart == 0 && motion.CompletionSubscriptions == 0 && !motion.IsActive);
        Stage("motion handle observation before=" + handles + " after=" + process.HandleCount
            + " privateBefore=" + privateBytes + " privateAfter=" + process.PrivateMemorySize64);
        Check("five-hundred-cycles-have-bounded-process-handles", process.HandleCount <= handles + 10);
        Check("five-hundred-cycles-have-bounded-private-byte-growth", process.PrivateMemorySize64 <= privateBytes + 10 * 1024 * 1024);
        HideSurface();
        Check("five-hundred-cycles-hidden-have-no-age-clock", !_presenter.AgeTicker.IsRunning);
        ShowSurface();
        var isolatedHost = new Grid(); PageHost.Children.Add(isolatedHost);
        var disposableMotion = new ContentTransitionController(isolatedHost, _motionPolicy);
        disposableMotion.SetVisible(true); disposableMotion.Navigate("test", new Grid());
        disposableMotion.Dispose();
        await Task.Delay(220).ConfigureAwait(true);
        Check("dispose-during-transition-detaches-handles-and-completion", !disposableMotion.IsActive && disposableMotion.CompletionSubscriptions == 0
            && disposableMotion.OwnedAnimationHandles == 0 && isolatedHost.Opacity == 1 && isolatedHost.Translation == Vector3.Zero);
        PageHost.Children.Remove(isolatedHost);
        await VerifySnapshotMotionBurstAsync(devices with { Revision = 100 }).ConfigureAwait(true);
        Check("motion-never-writes-native-bounds", _windowController.NativeBoundsWrites == writes && AppWindow.Size.Width == size.Width && AppWindow.Size.Height == size.Height);
    }
    private void CheckSettled(string name, ContentTransitionController motion) => Check(name,
        !motion.IsActive && motion.OwnedAnimationHandles == 0 && motion.CompletionSubscriptions == 0 && PageHost.Opacity == 1 && PageHost.Translation == Vector3.Zero);

    private async Task VerifySnapshotMotionBurstAsync(AppSnapshot baseline) {
        await PublishAndDrainAsync(baseline).ConfigureAwait(true);
        string id = baseline.Devices[0].Id;
        int commands = 0;
        void OnPin() => commands++;
        _presenter.PinRequested += OnPin;
        var producer = Task.Run(async () => {
            var schedule = System.Diagnostics.Stopwatch.StartNew();
            for (int index = 1; index <= 1000; index++) {
                TimeSpan remaining = TimeSpan.FromMilliseconds(index * 10) - schedule.Elapsed;
                if (remaining > TimeSpan.Zero) { await Task.Delay(remaining).ConfigureAwait(false); }
                _mailbox.Publish(baseline with { Revision = 100 + index, Devices = baseline.Devices.SetItem(0, baseline.Devices[0] with { BatteryPercent = index % 100 }) });
            }
        });
        int navigations = 0;
        while (!producer.IsCompleted) {
            _presenter.OpenDevice(id); _presenter.RequestPin();
            await Task.Delay(42).ConfigureAwait(true);
            _presenter.Back(); navigations++;
            await Task.Delay(42).ConfigureAwait(true);
        }
        await producer.ConfigureAwait(true);
        await DispatcherTurnAsync().ConfigureAwait(true);
        _presenter.PinRequested -= OnPin;
        Check("thousand-snapshots-during-navigation-end-at-latest-truth", _presenter.Snapshot!.Revision == 1100
            && _presenter.Snapshot.Devices[0].BatteryPercent == 0 && HasText("0%"));
        Check("snapshot-coalescing-does-not-drop-user-commands", commands == navigations && navigations > 0);
        _presenter.ContentMotion.Settle(); HideSurface();
        Check("burst-ends-with-no-hidden-animation-age-or-progress-work", !_presenter.AgeTicker.IsRunning
            && !_presenter.ContentMotion.IsActive && _presenter.ContentMotion.OwnedAnimationHandles == 0);
        ShowSurface();
    }

    private async Task VerifyMotionPreferenceSubscriptionAsync() {
        var source = new FixtureMotionPreference();
        int delivered = 0;
        using var subscription = new MotionPreferenceSubscription(source, DispatcherQueue, enabled => { delivered++; ApplyMotionPreference(enabled); });
        ShellAppearanceChangedEvent? appearance = null;
        void RecordAppearance(ShellAppearanceChangedEvent value) => appearance = value;
        AppearanceChangedRequested += RecordAppearance;
        _presenter.OpenDevice(_presenter.Snapshot!.Devices[0].Id);
        await Task.Run(() => source.Set(false)).ConfigureAwait(true);
        await DispatcherTurnAsync().ConfigureAwait(true);
        Check("system-motion-event-marshals-and-settles-before-snapshot-roundtrip", !_motionPolicy.Enabled && !_presenter.ContentMotion.IsActive && appearance?.ReducedMotion == true);
        source.Set(true); source.Set(false); source.Set(true);
        await DispatcherTurnAsync().ConfigureAwait(true);
        Check("preference-burst-reads-latest-source", _motionPolicy.Enabled && appearance?.ReducedMotion == false);
        source.Set(false);
        subscription.Dispose();
        int afterDispose = delivered;
        await DispatcherTurnAsync().ConfigureAwait(true);
        source.Set(true); await DispatcherTurnAsync().ConfigureAwait(true);
        Check("queued-preference-callback-after-dispose-is-inert", delivered == afterDispose && source.Subscribers == 0);
        AppearanceChangedRequested -= RecordAppearance;
    }
    private sealed class FixtureMotionPreference : IMotionPreferenceSource {
        private Action? _changed;
        public bool Enabled { get; private set; } = true;
        public int Subscribers { get; private set; }
        public event Action? Changed { add { _changed += value; Subscribers++; } remove { _changed -= value; Subscribers--; } }
        public void Set(bool enabled) { Enabled = enabled; _changed?.Invoke(); }
    }
}
