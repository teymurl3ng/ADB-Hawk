namespace AdbView.Host;

using AdbView.State;

/// <summary>Existing user actions. Implementations never retain or log submitted codes.</summary>
internal interface IShellActions {
    void StartPairing(string offerId);
    void SubmitPairing(string offerId, string code);
    void CancelPairing(string offerId);
    void StartQr();
    void CancelQr();
    void CheckDiscovery();
    void RepairDiscovery(string repairId);
}

/// <summary>Adapter for the unchanged backend sessions; native composition supplies existing diagnosis actions.</summary>
internal sealed class BackendShellActions(BackendRuntime runtime, Action checkDiscovery, Action<string> repairDiscovery) : IShellActions {
    private AppSnapshot Snapshot => runtime.StateEngine.CurrentSnapshot;

    public void StartPairing(string offerId) {
        if (Presentation.ShellActionAvailability.CanStart(Snapshot, offerId)) { runtime.PairingSession?.Start(offerId); }
    }
    public void SubmitPairing(string offerId, string code) {
        if (Presentation.ShellActionAvailability.CanSubmit(Snapshot, offerId, code)) { runtime.PairingSession?.Submit(offerId, code); }
    }
    public void CancelPairing(string offerId) {
        if (Presentation.ShellActionAvailability.CanCancel(Snapshot, offerId) && runtime.PairingSession?.IsActive(offerId) == true) {
            runtime.PairingSession.Cancel(offerId);
        }
    }
    public void StartQr() {
        if (!Presentation.ShellActionAvailability.CanStartQr(Snapshot)) { return; }
        runtime.QrPairingSession?.Start();
        if (runtime.QrPairingSession?.IsActive == true) { runtime.MdnsFallbackLifecycle?.StartQrQueryWindow(); }
    }
    public void CancelQr() {
        if (Presentation.ShellActionAvailability.CanCancelQr(Snapshot)) { runtime.QrPairingSession?.Cancel(); }
    }
    public void CheckDiscovery() {
        if (Presentation.ShellActionAvailability.CanCheck(Snapshot)) { checkDiscovery(); }
    }
    public void RepairDiscovery(string repairId) {
        if (Presentation.ShellActionAvailability.CanRepair(Snapshot, repairId)) { repairDiscovery(repairId); }
    }
}
