namespace AdbView.WinUiMigration;

using System.Collections.Immutable;
using AdbView.Bridge;
using AdbView.State;
using AdbView.WinUiMigration.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

internal sealed partial class CandidateWindow {
    private Action<AppSnapshot>? _afterApplyForVerification;

    private async Task PublishAndDrainAsync(AppSnapshot snapshot) {
        Check("accept-revision-" + snapshot.Revision, _mailbox.Publish(snapshot));
        await DispatcherTurnAsync().ConfigureAwait(true);
        Root.UpdateLayout();
    }

    private async Task VerifyListScenariosAsync() {
        var initialSize = AppWindow.Size;
        var initialPosition = AppWindow.Position;
        int initialWrites = _windowController.NativeBoundsWrites;
        AppSnapshot many = DeviceFixtures.Snapshot("many-devices", 20);
        await PublishAndDrainAsync(many).ConfigureAwait(true);
        DeviceRow originalRow = _presenter.Rows[many.Devices[0].Id];
        int applied = _presenter.AppliedCount;
        Check("reject-20-to-19", !_mailbox.Publish(many with { Revision = 19 }));
        Check("reject-duplicate-20", !_mailbox.Publish(many));
        await DispatcherTurnAsync().ConfigureAwait(true);
        Check("rejected-revisions-do-not-render", _presenter.AppliedCount == applied);

        await PublishAndDrainAsync(many with { Revision = 21 }).ConfigureAwait(true);
        Check("same-value-revision-preserves-row", ReferenceEquals(originalRow, _presenter.Rows[many.Devices[0].Id]));
        AppSnapshot battery = many with { Revision = 22, Devices = many.Devices.SetItem(0, many.Devices[0] with { BatteryPercent = 42 }) };
        await PublishAndDrainAsync(battery).ConfigureAwait(true);
        Check("battery-only-update-retains-controls", ReferenceEquals(originalRow.Name, _presenter.Rows[many.Devices[0].Id].Name)
            && originalRow.Battery.Text == "42%" && originalRow.Status.Text == many.Devices[0].StatusLine);

        await InvokeButtonAsync(originalRow.Button).ConfigureAwait(true);
        Check("device-row-click-opens-current-details", _presenter.DetailsId == many.Devices[0].Id);
        Root.UpdateLayout();
        DeviceDetails originalDetails = _presenter.Details!;
        await InvokeButtonAsync(originalDetails.CopyFields["serial"].Button).ConfigureAwait(true);
        Check("copy-reads-current-snapshot-value", _fixtureCopies.Last() == many.Devices[0].Detail.CopyFields.Single(field => field.Key == "serial").Value);
        AppSnapshot wifi = FixtureSnapshotProvider.GetSnapshot("connected-wifi");
        DeviceView changedTransport = wifi.Devices[0] with { Id = many.Devices[0].Id };
        AppSnapshot transported = many with { Revision = 23, Devices = many.Devices.SetItem(0, changedTransport) };
        await PublishAndDrainAsync(transported).ConfigureAwait(true);
        Check("transport-change-keeps-device-row-and-detail", ReferenceEquals(originalRow, _presenter.Rows[changedTransport.Id])
            && ReferenceEquals(originalDetails, _presenter.Details) && _presenter.DetailsId == changedTransport.Id);
        _presenter.RequestCopy("address");
        Check("transport-change-copies-exact-new-address", _fixtureCopies.Last() == changedTransport.Detail.CopyFields.Single(field => field.Key == "address").Value);
        _fixtureCopySucceeds = false;
        _presenter.RequestCopy("address");
        Check("clipboard-failure-never-shows-copied", _presenter.Details!.CopyFields["address"].Feedback.Visibility == Visibility.Collapsed);
        _fixtureCopySucceeds = true;

        AppSnapshot removed = transported with { Revision = 24, Devices = transported.Devices.RemoveAt(0) };
        await PublishAndDrainAsync(removed).ConfigureAwait(true);
        int copyCount = _fixtureCopies.Count;
        _presenter.RequestCopy("address");
        Check("removal-detaches-live-detail-and-hit-targets", _presenter.DetailsId is null && _presenter.Details is null
            && !_presenter.Rows.ContainsKey(changedTransport.Id)
            && !SnapshotPresenter.Descendants(PageHost).Contains(originalDetails.Scroll) && _fixtureCopies.Count == copyCount);

        _presenter.OpenDevice(removed.Devices[0].Id);
        _presenter.RequestCopy(changedTransport.Id, "serial");
        Check("old-page-copy-command-cannot-target-new-device", _fixtureCopies.Count == copyCount);
        Check("detached-copy-buttons-are-disabled", originalDetails.CopyFields.Values.All(field => !field.Button.IsEnabled));
        AppSnapshot unhealthy = removed with { Revision = 25, Health = FixtureSnapshotProvider.GetSnapshot("adb-unavailable").Health };
        await PublishAndDrainAsync(unhealthy).ConfigureAwait(true);
        Check("health-loss-removes-stale-details-immediately", _presenter.DetailsId is null && HasText(unhealthy.Health.Title));

        await PublishAndDrainAsync(many with { Revision = 26 }).ConfigureAwait(true);
        await SettleScrollAsync().ConfigureAwait(true);
        string selected = many.Devices[20].Id;
        _presenter.Rows[selected].Button.Focus(FocusState.Keyboard);
        await SettleScrollAsync().ConfigureAwait(true);
        await SetScrollAsync(RowTop(selected) + 7).ConfigureAwait(true);
        double anchorOffset = BodyScroll.VerticalOffset - RowTop(selected);
        Check("fixture-scroll-settled-at-requested-anchor", Math.Abs(anchorOffset - 7) <= 1);
        _presenter.OpenDevice(selected);
        AppSnapshot shifted = many with { Revision = 27, Devices = many.Devices.RemoveRange(0, 3) };
        await PublishAndDrainAsync(shifted).ConfigureAwait(true);
        await InvokeButtonAsync(BackButton).ConfigureAwait(true);
        await SettleScrollAsync().ConfigureAwait(true);
        Check("back-restores-keyed-focus", ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), _presenter.Rows[selected].Button));
        Check("back-restores-keyed-anchor-offset", Math.Abs(BodyScroll.VerticalOffset - RowTop(selected) - anchorOffset) <= 1);

        _presenter.OpenDevice(selected);
        int removedOrdinal = shifted.Devices.Select(device => device.Id).ToImmutableArray().IndexOf(selected);
        AppSnapshot noSelected = shifted with { Revision = 28, Devices = shifted.Devices.RemoveAt(removedOrdinal) };
        await PublishAndDrainAsync(noSelected).ConfigureAwait(true);
        await SettleScrollAsync().ConfigureAwait(true);
        Check("removed-focus-falls-back-to-nearest-neighbor", _presenter.DetailsId is null
            && ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), _presenter.Rows[noSelected.Devices[removedOrdinal].Id].Button));

        var commands = new List<int>();
        void CountPin() => commands.Add(1);
        _presenter.PinRequested += CountPin;
        for (int index = 0; index < 100; index++) {
            _mailbox.Publish(noSelected with { Revision = 100 + index });
            _presenter.RequestPin();
        }
        _presenter.PinRequested -= CountPin;
        await DispatcherTurnAsync().ConfigureAwait(true);
        Check("coalescing-never-drops-user-commands", commands.Count == 100 && _presenter.Snapshot!.Revision == 199);
        Check("pin-request-does-not-render-optimistic-truth", !(_presenter.Snapshot!.Shell.Pinned));

        _afterApplyForVerification = snapshot => {
            if (snapshot.Revision == 200) {
                Check("publish-201-during-real-drain", _mailbox.Publish(snapshot with { Revision = 201 }));
            }
        };
        await PublishAndDrainAsync(noSelected with { Revision = 200 }).ConfigureAwait(true);
        await DispatcherTurnAsync().ConfigureAwait(true);
        _afterApplyForVerification = null;
        Check("real-dispatcher-retains-newest-during-drain-reset", _presenter.Snapshot!.Revision == 201);
        await PublishAndDrainAsync(noSelected with { Revision = 202, Shell = noSelected.Shell with { Pinned = true } }).ConfigureAwait(true);
        Check("pin-snapshot-controls-icon-and-tooltip", AutomationProperties.GetName(PinButton) == "Unpin flyout"
            && (string)ToolTipService.GetToolTip(PinButton) == "Unpin flyout" && ((SymbolIcon)PinButton.Content).Symbol == Symbol.UnPin);
        _presenter.RequestPin();
        Check("pin-command-waits-for-authoritative-snapshot", _presenter.Snapshot!.Shell.Pinned
            && AutomationProperties.GetName(PinButton) == "Unpin flyout");

        long revision = 300;
        foreach (string fixture in new[] { "empty", "list", "two-devices", "multi-device", "many-devices", "long-names", "unknown", "missing-metadata" }) {
            AppSnapshot snapshot = DeviceFixtures.Snapshot(fixture, revision++);
            await PublishAndDrainAsync(snapshot).ConfigureAwait(true);
            Check("row-count-" + fixture, _presenter.Rows.Count == snapshot.Devices.Length);
            Check("no-horizontal-overflow-" + fixture, BodyScroll.ScrollableWidth <= 1);
            if (!snapshot.Devices.IsEmpty) {
                _presenter.OpenDevice(snapshot.Devices[0].Id);
                Root.UpdateLayout();
                Check("snapshot-detail-fields-" + fixture, snapshot.Devices[0].Detail.CopyFields.All(field =>
                    _presenter.Details!.CopyFields[field.Key].Value.Text == field.Value));
                if (fixture == "missing-metadata") {
                    Check("missing-metadata-has-no-invented-copy-actions", _presenter.Details!.CopyFields.Count == 0);
                }
                _presenter.Back();
            }
        }
        await SettleScrollAsync().ConfigureAwait(true);
        Check("zero-native-bounds-writes-after-show", _windowController.NativeBoundsWrites == initialWrites);
        Check("content-and-navigation-preserve-native-rectangle", AppWindow.Size.Width == initialSize.Width && AppWindow.Size.Height == initialSize.Height
            && AppWindow.Position.X == initialPosition.X && AppWindow.Position.Y == initialPosition.Y);
    }

    private double RowTop(string id) => _presenter.Rows[id].Button.TransformToVisual((UIElement)BodyScroll.Content).TransformPoint(new Point(0, 0)).Y;

    private async Task InvokeButtonAsync(Button button) {
        var peer = new ButtonAutomationPeer(button);
        object pattern = peer.GetPattern(PatternInterface.Invoke);
        Check("native-button-invoke-pattern", pattern is IInvokeProvider);
        ((IInvokeProvider)pattern).Invoke();
        await DispatcherTurnAsync().ConfigureAwait(true);
        Root.UpdateLayout();
    }

    private async Task SetScrollAsync(double offset) {
        Root.UpdateLayout();
        BodyScroll.ChangeView(null, offset, null, true);
        await SettleScrollAsync().ConfigureAwait(true);
    }

    private async Task SettleScrollAsync() {
        // Test-only bounded layout wait, no production polling or presentation clock.
        await Task.Delay(50).ConfigureAwait(true);
        Root.UpdateLayout();
        await DispatcherTurnAsync().ConfigureAwait(true);
    }
}
