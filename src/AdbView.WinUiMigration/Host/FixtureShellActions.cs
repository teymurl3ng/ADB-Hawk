namespace AdbView.WinUiMigration.Host;

using AdbView.Host;

/// <summary>No live backend or credentials retained. Verification observes action identities/counts.</summary>
internal sealed class FixtureShellActions : IShellActions {
    public int Starts { get; private set; }
    public int Submits { get; private set; }
    public int Cancels { get; private set; }
    public int QrStarts { get; private set; }
    public int QrCancels { get; private set; }
    public int Checks { get; private set; }
    public int Repairs { get; private set; }
    public string? LastIdentity { get; private set; }
    public bool SubmittedValidCode { get; private set; }
    public void StartPairing(string offerId) { Starts++; LastIdentity = offerId; }
    public void SubmitPairing(string offerId, string code) {
        Submits++; LastIdentity = offerId; SubmittedValidCode = AdbView.Adb.PairingCode.IsValid(code);
    }
    public void CancelPairing(string offerId) { Cancels++; LastIdentity = offerId; }
    public void StartQr() { QrStarts++; }
    public void CancelQr() { QrCancels++; }
    public void CheckDiscovery() { Checks++; }
    public void RepairDiscovery(string repairId) { Repairs++; LastIdentity = repairId; }
}
