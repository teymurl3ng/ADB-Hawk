namespace AdbView.Tests.Presentation;

using AdbView.Bridge;
using AdbView.Presentation;
using AdbView.State;
using Xunit;

public sealed class PairingPresentationTests {
    [Fact]
    public void MetadataPreservesDraftButReplacementAndPhaseChangesEraseIt() {
        PairingOperationView operation = FixtureSnapshotProvider.GetSnapshot("pairing-code").Pairing!;
        var draft = new PairingDraft();
        draft.Reconcile(operation); draft.Set("12x3٤456");
        Assert.Equal("123456", draft.Code);
        draft.Reconcile(operation with { Detail = "Updated fixture detail" });
        Assert.Equal("123456", draft.Code);
        draft.Reconcile(operation with { OfferId = "replacement" });
        Assert.Empty(draft.Code);
        draft.Set("123456"); draft.Reconcile(operation);
        Assert.Empty(draft.Code);
        draft.Set("123456");
        draft.Reconcile(operation with { Phase = PairingPhase.Failed, CanSubmitCode = false, CanRetry = true });
        Assert.Empty(draft.Code);
        draft.Set("123456"); draft.Clear();
        Assert.Empty(draft.Code);
    }

    [Fact]
    public void QrAndInvalidOperationsCannotRetainManualDraft() {
        var draft = new PairingDraft();
        PairingOperationView operation = FixtureSnapshotProvider.GetSnapshot("pairing-code").Pairing!;
        draft.Reconcile(operation); draft.Set("123456");
        draft.Reconcile(operation with { OfferId = "service-adbview-fixture" });
        Assert.Empty(draft.Code);
        draft.Set("123456"); draft.Reconcile(null);
        Assert.Empty(draft.Code);
        draft.Set("123456"); draft.Reconcile(FixtureSnapshotProvider.GetSnapshot("pairing-progress").Pairing);
        Assert.Empty(draft.Code);
    }

    [Theory]
    [InlineData("pairing-code", true, true, false)]
    [InlineData("pairing-progress", false, true, false)]
    [InlineData("pairing-success", false, false, false)]
    [InlineData("pairing-failure", true, true, true)]
    public void CommandsUseSnapshotAvailabilityAndExactIdentity(string fixture, bool submit, bool cancel, bool retry) {
        AppSnapshot snapshot = FixtureSnapshotProvider.GetSnapshot(fixture);
        string id = snapshot.Pairing!.OfferId;
        Assert.Equal(submit, ShellActionAvailability.CanSubmit(snapshot, id, "123456"));
        Assert.Equal(cancel, ShellActionAvailability.CanCancel(snapshot, id));
        Assert.Equal(retry, ShellActionAvailability.CanStart(snapshot, id));
        Assert.False(ShellActionAvailability.CanSubmit(snapshot, "other", "123456"));
        Assert.False(ShellActionAvailability.CanCancel(snapshot, "other"));
        Assert.False(ShellActionAvailability.CanSubmit(snapshot, id, "12345"));
        Assert.False(ShellActionAvailability.CanSubmit(snapshot, id, "１２３４５６"));
        AppSnapshot outage = snapshot with { Health = FixtureSnapshotProvider.GetSnapshot("adb-unavailable").Health };
        Assert.False(ShellActionAvailability.CanSubmit(outage, id, "123456"));
        Assert.False(ShellActionAvailability.CanCancel(outage, id));
        Assert.False(ShellActionAvailability.CanStart(outage, id));
        Assert.False(ShellActionAvailability.CanStartQr(outage));
    }

    [Fact]
    public void OffersAndQrRetriesDoNotCrossOperationIdentity() {
        AppSnapshot offer = FixtureSnapshotProvider.GetSnapshot("pairing-offer");
        Assert.True(ShellActionAvailability.CanStart(offer, offer.PairingOffers[0].Id));
        Assert.False(ShellActionAvailability.CanStart(offer, "replaced"));
        AppSnapshot busy = offer with { Pairing = FixtureSnapshotProvider.GetSnapshot("pairing-progress").Pairing };
        Assert.False(ShellActionAvailability.CanStart(busy, offer.PairingOffers[0].Id));
        AppSnapshot failed = FixtureSnapshotProvider.GetSnapshot("pairing-failure");
        failed = failed with { Pairing = failed.Pairing! with { OfferId = "service-adbview-fixture" } };
        Assert.True(ShellActionAvailability.CanStartQr(failed));
        Assert.False(ShellActionAvailability.CanStart(failed, failed.Pairing!.OfferId));
        Assert.False(ShellActionAvailability.CanSubmit(failed, failed.Pairing.OfferId, "123456"));
        AppSnapshot qr = FixtureSnapshotProvider.GetSnapshot("pairing-qr");
        Assert.True(ShellActionAvailability.CanCancelQr(qr));
        Assert.False(ShellActionAvailability.CanStartQr(qr));
    }
}
