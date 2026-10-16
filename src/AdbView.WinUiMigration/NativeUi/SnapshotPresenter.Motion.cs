namespace AdbView.WinUiMigration.NativeUi;

using AdbView.State;

internal sealed partial class SnapshotPresenter {
    public bool PresentationVisible => _presentationVisible;
    public void SetPresentationVisible(bool visible) {
        if (_disposed) { return; }
        _presentationVisible = visible;
        ContentMotion.SetVisible(visible);
        if (!visible) {
            _details?.ClearFeedback();
            _openPairingWhenAvailable = false;
            if (Pairing is not null || DetailsId is not null) { Back(); }
        }
        UpdatePresentationWork();
    }
    private void OnMotionPolicyChanged() {
        if (!_motionPolicy.Enabled) { _details?.ClearFeedback(); }
        UpdatePresentationWork();
    }
    private void UpdatePresentationWork() {
        if (_disposed) { return; }
        Pairing?.SetPresentation(_presentationVisible, _motionPolicy.Enabled);
        bool agesVisible = _presentationVisible && Pairing is null && Snapshot is { Health.Kind: GlobalHealthKind.Healthy } snapshot
            && (DetailsId is not null || !snapshot.Devices.IsEmpty);
        AgeTicker.SetNeeded(agesVisible);
        if (agesVisible) { UpdateAges(); }
    }
    private void UpdateAges() {
        if (!_presentationVisible || Pairing is not null || Snapshot is not { Health.Kind: GlobalHealthKind.Healthy } snapshot) { return; }
        DateTimeOffset now = _clock();
        if (DetailsId is { } id) {
            DeviceView? device = snapshot.Devices.FirstOrDefault(device => device.Id == id);
            if (device is not null) { _details?.UpdateAge(device, now); }
        }
        else {
            foreach (DeviceView device in snapshot.Devices) { _rows[device.Id].UpdateAge(device, now); }
        }
    }
}
