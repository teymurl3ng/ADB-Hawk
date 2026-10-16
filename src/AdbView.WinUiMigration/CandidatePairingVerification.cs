namespace AdbView.WinUiMigration;

using System.Collections.Immutable;
using AdbView.Bridge;
using AdbView.State;
using AdbView.WinUiMigration.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

internal sealed partial class CandidateWindow {
    private async Task VerifyPairingScenariosAsync() {
        int writes = _windowController.NativeBoundsWrites;
        var size = AppWindow.Size;
        long revision = 10;
        AppSnapshot Snapshot(string name) => DeviceFixtures.Snapshot(name, ++revision);
        async Task Publish(AppSnapshot snapshot) => await PublishAndDrainAsync(snapshot).ConfigureAwait(true);
        PairingPage page = _presenter.Pairing!;
        TextBox input = page.CodeInput;
        Button primary = page.Primary;
        Button cancel = page.Cancel;
        Root.UpdateLayout();
        double actionY = primary.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point()).Y;
        double cancelX = cancel.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point()).X;
        input.Text = "12x345";
        Check("typed-code-sanitizes-ascii-and-keeps-submit-disabled", input.Text == "12345" && !primary.IsEnabled);
        input.Text = "123456";
        input.Select(2, 0);
        await Publish(Snapshot("pairing")).ConfigureAwait(true);
        Check("metadata-keeps-textbox-draft-focus-and-caret", ReferenceEquals(input, _presenter.Pairing!.CodeInput)
            && input.Text.Length == 6 && input.SelectionStart == 2 && ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), input));
        Check("code-actions-retain-control-identity", ReferenceEquals(primary, page.Primary) && ReferenceEquals(cancel, page.Cancel));
        await InvokeButtonAsync(primary).ConfigureAwait(true);
        Check("submit-forwards-current-operation-without-persisting-code", _fixtureActions.Submits == 1
            && _fixtureActions.SubmittedValidCode && _fixtureActions.LastIdentity == "offer-1" && input.Text.Length == 0);
        Check("submit-does-not-infer-progress-or-connected", _presenter.Snapshot!.Pairing!.Phase == PairingPhase.AwaitingCode && _presenter.Snapshot.Devices.IsEmpty);
        await Publish(Snapshot("pairing-progress")).ConfigureAwait(true);
        Check("progress-immediately-removes-code-affordance", ReferenceEquals(page, _presenter.Pairing)
            && input.Visibility == Visibility.Collapsed && !input.IsEnabled && input.Text.Length == 0 && page.Primary.Visibility == Visibility.Collapsed);
        Root.UpdateLayout();
        Check("progress-action-row-stays-fixed", Math.Abs(cancel.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point()).Y - actionY) <= 1
            && Math.Abs(cancel.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point()).X - cancelX) <= 1);
        _presenter.Back();
        Root.UpdateLayout(); await DispatcherTurnAsync().ConfigureAwait(true);
        Check("back-keeps-operation-and-does-not-cancel", _fixtureActions.Cancels == 0 && _presenter.Pairing is null
            && HasText(_presenter.Snapshot!.Pairing!.Title) && HasText(_presenter.Snapshot.Pairing.Detail));
        _presenter.OpenPairing();
        await InvokeButtonAsync(_presenter.Pairing!.Cancel).ConfigureAwait(true);
        _presenter.Back();
        await Publish(Snapshot("empty")).ConfigureAwait(true);
        Check("late-cancel-completion-after-back-keeps-list", _fixtureActions.Cancels == 1 && _presenter.Pairing is null && HasText("No ADB devices"));
        int submits = _fixtureActions.Submits;
        page.Primary.IsEnabled = true; // A detached control must have no action handlers.
        await InvokeButtonAsync(page.Primary).ConfigureAwait(true);
        Check("detached-action-cannot-submit-new-operation", _fixtureActions.Submits == submits);

        AppSnapshot offer = Snapshot("pairing-offer");
        await Publish(offer).ConfigureAwait(true);
        Button oldOffer = SnapshotPresenter.Descendants(PageHost).OfType<Button>().Single(button => button.Content is string text && text == "Pair");
        AppSnapshot replacementOffer = offer with { Revision = ++revision, PairingOffers = [offer.PairingOffers[0] with { Id = "replacement-offer" }] };
        await Publish(replacementOffer).ConfigureAwait(true);
        await InvokeButtonAsync(oldOffer).ConfigureAwait(true);
        Check("withdrawn-offer-button-cannot-start-replacement", _fixtureActions.Starts == 0);
        Button newOffer = SnapshotPresenter.Descendants(PageHost).OfType<Button>().Single(button => button.Content is string text && text == "Pair");
        await InvokeButtonAsync(newOffer).ConfigureAwait(true);
        Check("offer-start-uses-current-opaque-identity", _fixtureActions.Starts == 1 && _fixtureActions.LastIdentity == "replacement-offer");
        AppSnapshot code = Snapshot("pairing");
        code = code with { Pairing = code.Pairing! with { OfferId = "replacement-offer" } };
        await Publish(code).ConfigureAwait(true);
        Check("start-opens-only-authoritative-operation", _presenter.Pairing is not null && HasText(code.Pairing!.Title));
        page = _presenter.Pairing!; input = page.CodeInput; input.Text = "123456";
        AppSnapshot other = code with {
            Revision = ++revision,
            PairingOffers = [offer.PairingOffers[0] with { DisplayName = "Stale offer name" }],
            Pairing = code.Pairing! with { OfferId = "another-offer", DisplayName = "Current phone", Title = Vocabulary.Pairing.CodeEntryTitle("Current phone") }
        };
        await Publish(other).ConfigureAwait(true);
        Check("operation-replacement-clears-old-draft", ReferenceEquals(page, _presenter.Pairing) && input.Text.Length == 0);
        Check("operation-wording-is-authoritative-over-offer-name", HasText(other.Pairing!.Title) && !HasText("Stale offer name"));
        input.Text = "123456";
        await InvokeButtonAsync(page.Primary).ConfigureAwait(true);
        Check("replacement-operation-submit-uses-new-identity", _fixtureActions.LastIdentity == "another-offer" && _fixtureActions.Submits == submits + 1);
        submits = _fixtureActions.Submits;
        await Publish(Snapshot("empty")).ConfigureAwait(true);
        Check("offer-withdrawal-removes-pairing-immediately", _presenter.Pairing is null && !input.IsEnabled);

        await Publish(Snapshot("pairing-failure")).ConfigureAwait(true);
        _presenter.OpenPairing(); page = _presenter.Pairing!;
        int starts = _fixtureActions.Starts;
        await InvokeButtonAsync(page.Primary).ConfigureAwait(true);
        Check("retry-empty-draft-restarts-existing-operation", _fixtureActions.Starts == starts + 1 && _fixtureActions.LastIdentity == "offer-1");
        page.CodeInput.Text = "123456";
        await InvokeButtonAsync(page.Primary).ConfigureAwait(true);
        Check("retry-complete-code-submits-immediately", _fixtureActions.Submits == submits + 1);

        await Publish(Snapshot("pairing-success")).ConfigureAwait(true);
        Check("pairing-success-stays-waiting-without-device-truth", HasText("Waiting for ADB connection…")
            && _presenter.Snapshot!.Devices.IsEmpty && !HasText("Connected"));
        AppSnapshot wait = _presenter.Snapshot!;
        await Publish(wait with { Revision = ++revision, PairingOffers = [] }).ConfigureAwait(true);
        Check("waiting-survives-offer-withdrawal", _presenter.Pairing is not null && HasText(wait.Pairing!.Detail));
        AppSnapshot offline = Snapshot("offline");
        await Publish(wait with { Revision = ++revision, Devices = offline.Devices }).ConfigureAwait(true);
        Check("disconnected-device-does-not-complete-pairing-wait", HasText(wait.Pairing!.Detail)
            && _presenter.Snapshot!.Devices.All(device => device.State != DeviceStateKind.Connected));
        HideSurface();
        await Publish(wait with { Revision = ++revision }).ConfigureAwait(true);
        ShowSurface(); Root.UpdateLayout();
        Check("hidden-reopened-operation-remains-reachable-without-draft", _presenter.Pairing is null && HasText(wait.Pairing!.Title));
        _presenter.OpenPairing();
        await Publish(Snapshot("adb-unavailable")).ConfigureAwait(true);
        Check("global-outage-clears-pairing-actions-immediately", _presenter.Pairing is null && HasText(_presenter.Snapshot!.Health.Title));
        int cancels = _fixtureActions.Cancels;
        _presenter.PairCancel(); _presenter.StartPairing("offer-1"); _presenter.StartQr();
        Check("outage-rejects-stale-actions", _fixtureActions.Cancels == cancels && _fixtureActions.QrStarts == 0);

        await Publish(Snapshot("empty")).ConfigureAwait(true);
        Button entry = SnapshotPresenter.Descendants(PageHost).OfType<Button>().Single(button => button.Content is string text && text == "Pair a device over Wi-Fi");
        await InvokeButtonAsync(entry).ConfigureAwait(true);
        Check("qr-entry-forwards-explicit-action-only", _fixtureActions.QrStarts == 1 && _presenter.Snapshot!.PairingQr is null);
        AppSnapshot qr = Snapshot("pairing-qr");
        await Publish(qr).ConfigureAwait(true);
        page = _presenter.Pairing!;
        await page.Qr.Pending.ConfigureAwait(true);
        Check("qr-renders-nonsecret-qrcoder-output-locally", page.Qr.Image.Source is not null && page.Qr.Image.Visibility == Visibility.Visible);
        object? image = page.Qr.Image.Source;
        await Publish(qr with { Revision = ++revision }).ConfigureAwait(true);
        Check("metadata-retains-same-qr-image-with-one-decode", ReferenceEquals(image, page.Qr.Image.Source) && page.Qr.DecodeCount == 1);
        await InvokeButtonAsync(page.Cancel).ConfigureAwait(true);
        Check("qr-cancel-forwards-existing-action", _fixtureActions.QrCancels == 1);
        AppSnapshot qrOperation = Snapshot("pairing-progress");
        qrOperation = qrOperation with { Pairing = qrOperation.Pairing! with { OfferId = "service-adbview-fixture" } };
        await Publish(qrOperation).ConfigureAwait(true);
        Check("qr-handoff-removes-image-and-accessibility-immediately", page.Qr.Image.Source is null
            && page.Qr.Image.Visibility == Visibility.Collapsed && AutomationProperties.GetName(page.Qr.Image).Length == 0 && page.CodeInput.Visibility == Visibility.Collapsed);
        AppSnapshot qrFailure = Snapshot("pairing-failure");
        qrFailure = qrFailure with { Pairing = qrFailure.Pairing! with { OfferId = qrOperation.Pairing!.OfferId } };
        await Publish(qrFailure).ConfigureAwait(true);
        Check("qr-failure-offers-scan-again-without-manual-slots", page.Primary.Content is string label && label == "Scan again"
            && page.CodeInput.Visibility == Visibility.Collapsed);
        await InvokeButtonAsync(page.Primary).ConfigureAwait(true);
        Check("scan-again-requests-fresh-backend-session", _fixtureActions.QrStarts == 2);
        // The existing backend can publish the operation before its QR-cleared event.
        AppSnapshot manual = Snapshot("pairing") with { PairingQr = qr.PairingQr };
        await Publish(manual).ConfigureAwait(true);
        Check("qr-to-manual-reuses-page-with-new-operation-identity", ReferenceEquals(page, _presenter.Pairing)
            && page.CodeInput.Visibility == Visibility.Visible && page.CodeInput.Text.Length == 0 && page.Qr.Image.Source is null);
        page.CodeInput.Text = "123456";
        HideSurface(); ShowSurface();
        Check("hide-erases-draft-without-cancellation", page.CodeInput.Text.Length == 0 && _fixtureActions.Cancels == cancels);
        _presenter.OpenPairing();
        Check("reopened-code-entry-starts-empty", _presenter.Pairing!.CodeInput.Text.Length == 0);
        _presenter.OpenFromNotification();
        Root.UpdateLayout(); await DispatcherTurnAsync().ConfigureAwait(true);
        Check("notification-link-shows-current-list-with-active-operation", _presenter.Pairing is null && HasText(manual.Pairing!.Title));
        await Publish(qr with { Revision = ++revision }).ConfigureAwait(true);
        _presenter.OpenPairing(); page = _presenter.Pairing!;
        Task obsoleteDecode = page.Qr.Pending;
        await Publish(Snapshot("empty")).ConfigureAwait(true);
        await obsoleteDecode.ConfigureAwait(true);
        Check("late-qr-decode-cannot-restore-invalidated-image", page.Qr.Image.Source is null && _presenter.Pairing is null);

        AppSnapshot diagnosis = Snapshot("discovery-health");
        diagnosis = diagnosis with {
            DiscoveryHealth = diagnosis.DiscoveryHealth! with {
                Repairs = [
            new DiscoveryRepairCardAction { Id = "fixture-repair", Label = "Allow discovery", IsAdmin = true, IsRunning = false, RunningLabel = "Allowing discovery…" },
        ]
            }
        };
        await Publish(diagnosis).ConfigureAwait(true);
        Button checkButton = _presenter.CheckButton;
        Button repairButton = _presenter.RepairButtons["fixture-repair"];
        repairButton.Focus(FocusState.Programmatic);
        await Publish(diagnosis with { Revision = ++revision }).ConfigureAwait(true);
        Check("diagnosis-metadata-retains-controls-and-focus", ReferenceEquals(checkButton, _presenter.CheckButton)
            && ReferenceEquals(repairButton, _presenter.RepairButtons["fixture-repair"])
            && ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), repairButton));
        await InvokeButtonAsync(_presenter.CheckButton!).ConfigureAwait(true);
        await InvokeButtonAsync(_presenter.RepairButtons["fixture-repair"]).ConfigureAwait(true);
        Check("diagnosis-explicit-check-and-repair-actions", _fixtureActions.Checks == 1 && _fixtureActions.Repairs == 1 && _fixtureActions.LastIdentity == "fixture-repair");
        AppSnapshot running = diagnosis with {
            Revision = ++revision,
            DiscoveryHealth = diagnosis.DiscoveryHealth! with {
                Repairs = [diagnosis.DiscoveryHealth.Repairs[0] with { IsRunning = true }],
            }
        };
        await Publish(running).ConfigureAwait(true);
        Check("running-repair-disables-check-and-own-action", !_presenter.CheckButton!.IsEnabled && !_presenter.RepairButtons["fixture-repair"].IsEnabled);
        _presenter.CheckDiscovery(); _presenter.Repair("fixture-repair");
        Check("running-repair-revalidates-programmatic-actions", _fixtureActions.Checks == 1 && _fixtureActions.Repairs == 1);
        AppSnapshot undo = diagnosis with {
            Revision = ++revision,
            DiscoveryHealth = diagnosis.DiscoveryHealth! with {
                Repairs = [diagnosis.DiscoveryHealth.Repairs[0] with { Id = "fixture-undo", Label = "Undo" }],
                ResultLine = "Discovery allowed.",
            }
        };
        await Publish(undo).ConfigureAwait(true);
        _presenter.Repair("fixture-repair");
        await InvokeButtonAsync(_presenter.RepairButtons["fixture-undo"]).ConfigureAwait(true);
        Check("undo-uses-new-current-opaque-action", _fixtureActions.Repairs == 2 && _fixtureActions.LastIdentity == "fixture-undo" && HasText(undo.DiscoveryHealth!.ResultLine));
        await VerifyQrRejectionAsync(qr.PairingQr!.SvgDataUri).ConfigureAwait(true);
        AppSnapshot crowded = DeviceFixtures.Snapshot("many-devices", ++revision) with { Pairing = manual.Pairing };
        await Publish(crowded).ConfigureAwait(true);
        _presenter.Rows[crowded.Devices[10].Id].Button.Focus(FocusState.Programmatic);
        await SetScrollAsync(700).ConfigureAwait(true);
        double listOffset = BodyScroll.VerticalOffset;
        _presenter.OpenPairing(); Root.UpdateLayout();
        _presenter.Back(); await SettleScrollAsync().ConfigureAwait(true);
        Check("pairing-back-restores-list-scroll-and-keyed-focus", Math.Abs(BodyScroll.VerticalOffset - listOffset) <= 1
            && ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), _presenter.Rows[crowded.Devices[10].Id].Button));
        Check("pairing-phases-never-write-native-size", _windowController.NativeBoundsWrites == writes
            && AppWindow.Size.Width == size.Width && AppWindow.Size.Height == size.Height);
    }

    private async Task VerifyQrRejectionAsync(string validFixture) {
        using var rejected = new LocalQrImage();
        static string Encode(string xml) => "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xml));
        string[] invalid = ["not-a-data-uri", "data:image/svg+xml;base64,invalid", Encode("<not-svg/>"),
            Encode("<!DOCTYPE svg [<!ENTITY sample 'rejected'>]><svg xmlns='http://www.w3.org/2000/svg' width='148' height='148'/>"),
            Encode("<svg xmlns='http://www.w3.org/2000/svg' width='148' height='148'><rect width='148' height='148' fill='url(https://example.invalid/)'/></svg>")];
        for (int index = 0; index < invalid.Length; index++) {
            rejected.Apply(invalid[index]); await rejected.Pending.ConfigureAwait(true);
            Check("qr-rejects-malformed-or-external-input-" + index, rejected.Image.Source is null && rejected.DecodeCount == 0);
        }
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delayed = new LocalQrImage(() => { reached.SetResult(); return release.Task; });
        delayed.Apply(validFixture);
        Task pending = delayed.Pending;
        await reached.Task.ConfigureAwait(true);
        Check("qr-decode-is-deliberately-pending-before-invalidation", !pending.IsCompleted);
        delayed.Apply(null); release.SetResult();
        await pending.ConfigureAwait(true);
        Check("obsolete-decoder-completion-cannot-reattach-source", delayed.Image.Source is null && delayed.Image.Visibility == Visibility.Collapsed);
    }
}
