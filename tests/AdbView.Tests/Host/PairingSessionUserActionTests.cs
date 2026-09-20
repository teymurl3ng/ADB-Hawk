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
}
