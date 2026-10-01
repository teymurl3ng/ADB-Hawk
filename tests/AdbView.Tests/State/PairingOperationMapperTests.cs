#pragma warning disable CA1707

namespace AdbView.Tests.State;

using AdbView.Adb.Model;
using AdbView.State;
using Xunit;
using DomainPhase = AdbView.Adb.Model.PairingPhase;

/// <summary>
/// Failure supporting line selected by cause — decision 0007 / DESIGN.md §15.
/// </summary>
public sealed class PairingOperationMapperTests {
    [Theory]
    [InlineData((int)PairingFailureKind.None)]
    [InlineData((int)PairingFailureKind.Rejected)]
    [InlineData((int)PairingFailureKind.ProtocolFailure)]
    [InlineData((int)PairingFailureKind.MalformedResponse)]
    public void Failed_DefaultKinds_UseFrozenWrongCodeLine(int kindRaw) {
        var view = PairingOperationMapper.Map("o1", DomainPhase.Failed, (PairingFailureKind)kindRaw, "Phone");
        Assert.NotNull(view);
        Assert.Equal("Pairing failed", view!.Title);
        Assert.Equal(Vocabulary.Pairing.FailedDetail, view.Detail);
        Assert.True(view.CanRetry);
    }

    [Fact]
    public void Failed_Timeout_UsesPhoneLine() {
        var view = PairingOperationMapper.Map("o1", DomainPhase.Failed, PairingFailureKind.Timeout, "Phone");
        Assert.Equal("The phone did not respond in time. Try again.", view!.Detail);
    }

    [Fact]
    public void Failed_RateLimited_UsesAttemptsLine() {
        var view = PairingOperationMapper.Map("o1", DomainPhase.Failed, PairingFailureKind.RateLimited, "Phone");
        Assert.Equal("Too many attempts. Wait a moment and try again.", view!.Detail);
    }

    [Fact]
    public void Failed_InvalidRequest_UsesOfferChangedLine() {
        var view = PairingOperationMapper.Map("o1", DomainPhase.Failed, PairingFailureKind.InvalidRequest, "Phone");
        Assert.Equal("The pairing offer changed. Tap Pair and enter the new code.", view!.Detail);
    }

    [Theory]
    [InlineData((int)DomainPhase.AwaitingCode)]
    [InlineData((int)DomainPhase.Pairing)]
    [InlineData((int)DomainPhase.PairedWaitingForDevice)]
    public void NonFailed_Phases_UnaffectedByKind(int phaseRaw) {
        var phase = (DomainPhase)phaseRaw;
        var view = PairingOperationMapper.Map("o1", phase, PairingFailureKind.Timeout, "Phone");
        Assert.NotNull(view);
        Assert.NotEqual("The phone did not respond in time. Try again.", view!.Detail);
        if (phase == DomainPhase.AwaitingCode) {
            Assert.Equal(Vocabulary.Pairing.CodeEntryDetail, view.Detail);
        }
    }

    [Fact]
    public void Cleared_MapsToNull() {
        Assert.Null(PairingOperationMapper.Map("o1", DomainPhase.Cleared, PairingFailureKind.Cancelled, "Phone"));
    }
}
