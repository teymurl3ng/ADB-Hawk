namespace AdbView.WinUiMigration.NativeUi;

using AdbView.Presentation;
using AdbView.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

internal sealed partial class SnapshotPresenter {
    private bool _openPairingWhenAvailable;
    public PairingPage? Pairing { get; private set; }
    public Dictionary<string, Button> RepairButtons => _discovery.RepairButtons;
    public Button CheckButton => _discovery.CheckButton;

    private Button AddAction(string heading, Action action, bool enabled = true) {
        var button = new Button { Content = heading, MinHeight = 36, IsEnabled = enabled };
        button.Click += (_, _) => { if (!_disposed) { action(); } };
        _messages.Children.Add(button);
        return button;
    }

    private void ReconcilePairing(AppSnapshot snapshot) {
        if (!ShellActionAvailability.Healthy(snapshot) || snapshot.Pairing is null && snapshot.PairingQr is null) {
            if (Pairing is not null) { LeavePairing(); LeaveDetails(); RestoreList(); }
            return;
        }
        if (_openPairingWhenAvailable) { _openPairingWhenAvailable = false; OpenPairing(); }
        Pairing?.Apply(snapshot);
    }
    public void OpenPairing() {
        if (_disposed || Snapshot is not { } snapshot || !ShellActionAvailability.Healthy(snapshot)
            || snapshot.Pairing is null && snapshot.PairingQr is null) { return; }
        if (Pairing is not null) { return; }
        if (DetailsId is null) {
            object? focused = FocusManager.GetFocusedElement(_root.XamlRoot);
            string? focusedId = _rows.FirstOrDefault(row => ReferenceEquals(row.Value.Button, focused)).Key;
            SaveListBookmark(focusedId);
        }
        InvalidateRestore(); _navigation.Back(); _details?.Dispose(); _details = null;
        Pairing = new PairingPage(_textScale, PairPrimary, PairCancel);
        Pairing.Apply(snapshot);
        ContentMotion.Navigate("pairing", Pairing.Root);
        UpdatePresentationWork();
        _back.Visibility = Visibility.Visible;
        FirstFocus.Focus(FocusState.Programmatic);
    }
    private void LeavePairing() {
        Pairing?.Dispose(); Pairing = null;
    }
    public void OnPresentationHidden() {
        SetPresentationVisible(false);
    }
    public void OpenFromNotification() { Back(); }
    public void StartPairing(string id) {
        if (_disposed || Snapshot is not { } snapshot || !ShellActionAvailability.CanStart(snapshot, id)) { return; }
        _openPairingWhenAvailable = true; _actions.StartPairing(id);
    }
    public void StartQr() {
        if (_disposed || Snapshot is not { } snapshot || !ShellActionAvailability.CanStartQr(snapshot)) { return; }
        _openPairingWhenAvailable = true; _actions.StartQr();
    }
    public void PairPrimary() {
        if (_disposed || Pairing is not { } page || Snapshot is not { Pairing: { } operation } snapshot) { return; }
        if (operation.CanRetry && ShellActionAvailability.QrOrigin(operation)) { StartQr(); return; }
        if (ShellActionAvailability.CanSubmit(snapshot, operation.OfferId, page.Code)) {
            _actions.SubmitPairing(operation.OfferId, page.Code); page.ClearDraft();
            page.Apply(snapshot);
        }
        else if (operation.CanRetry && ShellActionAvailability.CanStart(snapshot, operation.OfferId)) {
            _actions.StartPairing(operation.OfferId);
        }
    }
    public void PairCancel() {
        if (_disposed || Pairing is null || Snapshot is not { } snapshot) { return; }
        if (snapshot.Pairing is { } operation && ShellActionAvailability.CanCancel(snapshot, operation.OfferId)) {
            _actions.CancelPairing(operation.OfferId);
        }
        else if (ShellActionAvailability.CanCancelQr(snapshot)) { _actions.CancelQr(); }
    }
    public void CheckDiscovery() {
        if (!_disposed && Snapshot is { } snapshot && ShellActionAvailability.CanCheck(snapshot)) { _actions.CheckDiscovery(); }
    }
    public void Repair(string id) {
        if (!_disposed && Snapshot is { } snapshot && ShellActionAvailability.CanRepair(snapshot, id)) { _actions.RepairDiscovery(id); }
    }
}
