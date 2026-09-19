namespace AdbView.State;

using AdbView.Adb.Model;
using DomainPhase = AdbView.Adb.Model.PairingPhase;

/// <summary>
/// Maps domain PairingProgressed into PairingOperationView (DESIGN.md §15).
/// </summary>
internal static class PairingOperationMapper {
    public static PairingOperationView? Map(
        string offerId,
        DomainPhase phase,
        string displayName) {
        return phase switch {
            DomainPhase.Cleared => null,
            DomainPhase.AwaitingCode => new PairingOperationView {
                OfferId = offerId,
                DisplayName = displayName,
                Phase = PairingPhase.AwaitingCode,
                Title = Vocabulary.Pairing.CodeEntryTitle(displayName),
                Detail = Vocabulary.Pairing.CodeEntryDetail,
                CanSubmitCode = true,
                CanCancel = true,
                CanRetry = false,
                CodeLength = 6,
            },
            DomainPhase.Pairing => new PairingOperationView {
                OfferId = offerId,
                DisplayName = displayName,
                Phase = PairingPhase.Pairing,
                Title = Vocabulary.Pairing.ProgressTitle(displayName),
                Detail = Vocabulary.Pairing.ProgressDetail,
                CanSubmitCode = false,
                CanCancel = true,
                CanRetry = false,
                CodeLength = 6,
            },
            DomainPhase.PairedWaitingForDevice => new PairingOperationView {
                OfferId = offerId,
                DisplayName = displayName,
                Phase = PairingPhase.PairedWaitingForDevice,
                Title = Vocabulary.Pairing.SuccessTitle(displayName),
                Detail = Vocabulary.Pairing.SuccessDetail,
                CanSubmitCode = false,
                CanCancel = true,
                CanRetry = false,
                CodeLength = 6,
            },
            DomainPhase.Failed => new PairingOperationView {
                OfferId = offerId,
                DisplayName = displayName,
                Phase = PairingPhase.Failed,
                Title = Vocabulary.Pairing.FailedTitle,
                Detail = Vocabulary.Pairing.FailedDetail,
                CanSubmitCode = false,
                CanCancel = true,
                CanRetry = true,
                CodeLength = 6,
            },
            _ => null,
        };
    }
}
