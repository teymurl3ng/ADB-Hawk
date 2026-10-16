namespace AdbView.Presentation;

using AdbView.Adb;
using AdbView.State;

/// <summary>Snapshot-derived availability only; the backend still validates bindings and generations.</summary>
internal static class ShellActionAvailability {
    public static bool Healthy(AppSnapshot snapshot) => snapshot.Health.Kind == GlobalHealthKind.Healthy;
    public static bool QrOrigin(PairingOperationView operation) => operation.OfferId.Contains(QrPairingRequest.InstancePrefix, StringComparison.Ordinal);
    public static bool CanStart(AppSnapshot snapshot, string id) => Healthy(snapshot)
        && (snapshot.PairingOffers.Any(offer => offer.Id == id)
            && snapshot.Pairing?.Phase is not (PairingPhase.AwaitingCode or PairingPhase.Pairing)
            || snapshot.Pairing is { CanRetry: true } operation && operation.OfferId == id && !QrOrigin(operation));
    public static bool CanSubmit(AppSnapshot snapshot, string id, string code) => Healthy(snapshot)
        && snapshot.Pairing is { } operation && operation.OfferId == id && !QrOrigin(operation)
        && (operation.CanSubmitCode || operation.CanRetry) && PairingCode.IsValid(code);
    public static bool CanCancel(AppSnapshot snapshot, string id) => Healthy(snapshot)
        && snapshot.Pairing is { CanCancel: true } operation && operation.OfferId == id;
    public static bool CanStartQr(AppSnapshot snapshot) => Healthy(snapshot) && snapshot.PairingQr is null
        && (snapshot.Pairing is null || snapshot.Pairing is { CanRetry: true } operation && QrOrigin(operation));
    public static bool CanCancelQr(AppSnapshot snapshot) => Healthy(snapshot) && snapshot.PairingQr is not null;
    public static bool CanCheck(AppSnapshot snapshot) => Healthy(snapshot)
        && snapshot.DiscoveryHealth is { CanCheckAgain: true } health && !health.Repairs.Any(repair => repair.IsRunning);
    public static bool CanRepair(AppSnapshot snapshot, string id) => Healthy(snapshot)
        && snapshot.DiscoveryHealth is { } health && health.Repairs.Any(repair => repair.Id == id && !repair.IsRunning);
}

/// <summary>Only an unsubmitted presentation draft, never an optimistic copy of backend state.</summary>
internal sealed class PairingDraft {
    private string? _identity;
    private PairingPhase? _phase;
    public string Code { get; private set; } = string.Empty;
    public void Reconcile(PairingOperationView? operation) {
        if (_identity != operation?.OfferId || _phase != operation?.Phase
            || operation is null || ShellActionAvailability.QrOrigin(operation)
            || !(operation.CanSubmitCode || operation.CanRetry)) { Clear(); }
        _identity = operation?.OfferId;
        _phase = operation?.Phase;
    }
    public void Set(string raw) => Code = new string(raw.Where(char.IsAsciiDigit).Take(6).ToArray());
    public void Clear() => Code = string.Empty;
}
