#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051

namespace AdbView.Tests.Host;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Host;
using AdbView.State;
using Xunit;
using DomainPhase = AdbView.Adb.Model.PairingPhase;

public sealed class PairingSessionUserActionTests {
    private sealed class FakePairClient : IHostPairClient {
        public TaskCompletionSource<HostPairResult> Gate { get; } = new();

        public Task<HostPairResult> PairAsync(PairingEndpoint endpoint, string code, System.Threading.CancellationToken ct) =>
            Gate.Task.WaitAsync(ct);
    }

    [Fact]
    public void Start_Stale_Offer_Emits_Failed() {
        var events = new List<AdbViewEvent>();
        using var session = new PairingSession(events.Add, _ => null);
        session.Start("missing");
        Assert.Contains(events, e => e is PairingProgressedEvent p
            && p.OfferId == "missing"
            && p.Phase == DomainPhase.Failed);
    }

    [Fact]
    public void Start_Same_Offer_While_Awaiting_Is_Idempotent() {
        var binding = new PairingOfferBinding("o1", "192.168.1.2", 37101, null, "Phone");
        var events = new List<AdbViewEvent>();
        using var session = new PairingSession(
            events.Add,
            id => id == "o1" ? binding : null);
        session.Start("o1");
        int afterFirst = events.Count;
        session.Start("o1");
        Assert.Equal(afterFirst, events.Count);
        Assert.Equal(DomainPhase.AwaitingCode, session.Coordinator.CurrentPhase);
    }

    [Fact]
    public async Task Submit_Emits_Pairing_Before_Host_Completes() {
        var fake = new FakePairClient();
        var coordinator = new PairingCoordinator(pairClient: fake);
        var binding = new PairingOfferBinding("o1", "192.168.1.2", 37101, null, "Phone");
        var events = new List<AdbViewEvent>();
        using var session = new PairingSession(events.Add, _ => binding, coordinator);
        session.Start("o1");
        events.Clear();

        var submit = session.Coordinator.SubmitAsync("o1", "123456");
        await Task.Delay(30);
        Assert.Contains(events, e => e is PairingProgressedEvent p && p.Phase == DomainPhase.Pairing);
        Assert.Equal(DomainPhase.Pairing, coordinator.CurrentPhase);

        fake.Gate.SetResult(new HostPairResult(true, PairingFailureKind.None, null));
        await submit;
        Assert.Equal(DomainPhase.PairedWaitingForDevice, coordinator.CurrentPhase);
    }

    [Fact]
    public async Task Duplicate_Submit_While_Pairing_Is_Idempotent() {
        var fake = new FakePairClient();
        var coordinator = new PairingCoordinator(pairClient: fake);
        var binding = new PairingOfferBinding("o1", "192.168.1.2", 37101, null, "Phone");
        using var session = new PairingSession(_ => { }, _ => binding, coordinator);
        session.Start("o1");
        var first = coordinator.SubmitAsync("o1", "123456");
        await Task.Delay(20);
        await coordinator.SubmitAsync("o1", "123456"); // must not throw / second host call
        fake.Gate.SetResult(new HostPairResult(true, PairingFailureKind.None, null));
        await first;
        Assert.Equal(DomainPhase.PairedWaitingForDevice, coordinator.CurrentPhase);
    }

    [Fact]
    public async Task Submit_Wrong_Offer_Emits_Failed() {
        var coordinator = new PairingCoordinator();
        var binding = new PairingOfferBinding("o1", "192.168.1.2", 37101, null, "Phone");
        var events = new List<AdbViewEvent>();
        using var session = new PairingSession(events.Add, id => id == "o1" ? binding : null, coordinator);
        session.Start("o1");
        events.Clear();
        await coordinator.SubmitAsync("other", "123456");
        Assert.Contains(events, e => e is PairingProgressedEvent p
            && p.OfferId == "other"
            && p.Phase == DomainPhase.Failed);
        Assert.Equal(DomainPhase.AwaitingCode, coordinator.CurrentPhase);
    }

    [Fact]
    public void Start_DifferentOffer_While_Awaiting_Emits_Failed_ForNewOffer() {
        var bindingA = new PairingOfferBinding("o1", "192.168.1.2", 37101, null, "Phone A");
        var bindingB = new PairingOfferBinding("o2", "192.168.1.3", 37102, null, "Phone B");
        var events = new List<AdbViewEvent>();
        using var session = new PairingSession(
            events.Add,
            id => id == "o1" ? bindingA : id == "o2" ? bindingB : null);
        session.Start("o1");
        events.Clear();

        session.Start("o2");

        Assert.Contains(events, e => e is PairingProgressedEvent p
            && p.OfferId == "o2"
            && p.Phase == DomainPhase.Failed);
        Assert.Equal(DomainPhase.AwaitingCode, session.Coordinator.CurrentPhase);
        Assert.Equal("o1", session.Coordinator.ActiveOfferId);
    }

    [Fact]
    public void CancelIfOfferWithdrawn_NoActiveOperation_IsNoOp() {
        var events = new List<AdbViewEvent>();
        using var session = new PairingSession(events.Add, _ => null);
        session.CancelIfOfferWithdrawn();
        Assert.Empty(events);
    }

    [Fact]
    public void CancelIfOfferWithdrawn_OfferStillPresent_DoesNotCancel() {
        var binding = new PairingOfferBinding("o1", "192.168.1.2", 37101, null, "Phone");
        var events = new List<AdbViewEvent>();
        using var session = new PairingSession(events.Add, id => id == "o1" ? binding : null);
        session.Start("o1");
        events.Clear();

        session.CancelIfOfferWithdrawn();

        Assert.Equal(DomainPhase.AwaitingCode, session.Coordinator.CurrentPhase);
        Assert.Equal("o1", session.Coordinator.ActiveOfferId);
        Assert.Empty(events);
    }

    [Fact]
    public void CancelIfOfferWithdrawn_ActiveOfferGone_CancelsAndFreesCoordinator() {
        var bindingA = new PairingOfferBinding("o1", "192.168.1.2", 37101, null, "Phone A");
        var bindingB = new PairingOfferBinding("o2", "192.168.1.3", 37102, null, "Phone B");
        var events = new List<AdbViewEvent>();
        var bindings = new Dictionary<string, PairingOfferBinding> { ["o1"] = bindingA };
        using var session = new PairingSession(events.Add, id => bindings.GetValueOrDefault(id));
        session.Start("o1");
        events.Clear();

        bindings.Remove("o1");
        session.CancelIfOfferWithdrawn();

        Assert.Contains(events, e => e is PairingProgressedEvent p
            && p.OfferId == "o1"
            && p.Phase == DomainPhase.Cleared);
        Assert.Equal(DomainPhase.Cleared, session.Coordinator.CurrentPhase);
        Assert.Null(session.Coordinator.ActiveOfferId);

        // The coordinator is free again: a pairStart for a fresh offer is accepted.
        bindings["o2"] = bindingB;
        session.Start("o2");
        Assert.Equal(DomainPhase.AwaitingCode, session.Coordinator.CurrentPhase);
        Assert.Equal("o2", session.Coordinator.ActiveOfferId);
        Assert.DoesNotContain(events, e => e is PairingProgressedEvent p
            && p.OfferId == "o2"
            && p.Phase == DomainPhase.Failed);
    }

    [Fact]
    public async Task CancelIfOfferWithdrawn_DuringInFlightPair_Tolerated_LateSuccessLands() {
        // Decision 0014 reverses the old pin: Android closes the pairing advertisement the
        // moment pairing completes, so a withdrawal during the in-flight host:pair is EXPECTED.
        // The coordinator must survive it — attempt 3 in the live session lost its SUCCESS
        // result to exactly this cancel.
        var fake = new FakePairClient();
        var coordinator = new PairingCoordinator(pairClient: fake);
        var binding = new PairingOfferBinding("o1", "192.168.1.2", 37101, null, "Phone");
        var events = new List<AdbViewEvent>();
        var bindings = new Dictionary<string, PairingOfferBinding> { ["o1"] = binding };
        using var session = new PairingSession(events.Add, id => bindings.GetValueOrDefault(id), coordinator);
        session.Start("o1");
        events.Clear();

        var submit = coordinator.SubmitAsync("o1", "123456");
        await Task.Delay(30);
        Assert.Equal(DomainPhase.Pairing, coordinator.CurrentPhase);

        bindings.Remove("o1");
        session.CancelIfOfferWithdrawn();

        // The coordinator survived: phase still Pairing, offer id retained.
        Assert.Equal(DomainPhase.Pairing, coordinator.CurrentPhase);
        Assert.Equal("o1", coordinator.ActiveOfferId);

        // The in-flight result lands and shows (no longer dropped as stale).
        fake.Gate.SetResult(new HostPairResult(true, PairingFailureKind.None, null));
        await submit;
        Assert.Equal(DomainPhase.PairedWaitingForDevice, coordinator.CurrentPhase);
        Assert.Contains(events, e => e is PairingProgressedEvent p
            && p.Phase == DomainPhase.PairedWaitingForDevice);
    }

    [Fact]
    public async Task CancelIfOfferWithdrawn_WhileWaitingForDevice_Tolerated() {
        // Post-success withdrawal (Android closed the pairing advertisement) must not free the
        // coordinator — the post-pair connect still needs the preserved pairing host.
        var fake = new FakePairClient();
        var coordinator = new PairingCoordinator(pairClient: fake);
        var binding = new PairingOfferBinding("o1", "192.168.1.2", 37101, null, "Phone");
        var events = new List<AdbViewEvent>();
        var bindings = new Dictionary<string, PairingOfferBinding> { ["o1"] = binding };
        using var session = new PairingSession(events.Add, id => bindings.GetValueOrDefault(id), coordinator);
        session.Start("o1");
        var submit = coordinator.SubmitAsync("o1", "123456");
        fake.Gate.SetResult(new HostPairResult(true, PairingFailureKind.None, null));
        await submit;
        events.Clear();

        bindings.Remove("o1");
        session.CancelIfOfferWithdrawn();

        Assert.Equal(DomainPhase.PairedWaitingForDevice, coordinator.CurrentPhase);
        Assert.Equal("o1", coordinator.ActiveOfferId);
        Assert.Empty(events);
    }
}
