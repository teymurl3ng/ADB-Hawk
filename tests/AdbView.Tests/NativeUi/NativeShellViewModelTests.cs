namespace AdbView.Tests.NativeUi;

using System;
using System.Collections.Immutable;
using System.Text;
using AdbView.NativeUi;
using AdbView.NativeUi.Shell;
using AdbView.State;
using Xunit;

public sealed class NativeShellViewModelTests {
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string OfferId = "offer-1";
    private const string DeviceName = "Teymur's S24";

    private static PairingOperationView Operation(PairingPhase phase, string? detail = null) => new() {
        OfferId = OfferId,
        DisplayName = DeviceName,
        Phase = phase,
        Title = "Pairing with " + DeviceName,
        Detail = detail,
        CanSubmitCode = phase == PairingPhase.AwaitingCode,
        CanCancel = true,
        CanRetry = phase == PairingPhase.Failed,
        CodeLength = 6,
    };

    private static PairingOfferView Offer() => new() {
        Id = OfferId,
        DisplayName = DeviceName,
        Heading = "Wireless pairing available",
        Detail = "Waiting for a pairing code",
        ActionLabel = "Pair",
    };

    private static DiscoveryHealthView HealthView(
        string title = "Wireless discovery isn't working",
        bool canCheckAgain = true,
        ImmutableArray<DiscoveryRepairCardAction>? repairs = null) => new() {
            Title = title,
            Detail = DeviceName + " needs attention.",
            Evidence = ImmutableArray.Create("ADB server reports wireless discovery as disabled."),
            ActionLabel = "Check again",
            CanCheckAgain = canCheckAgain,
            Repairs = repairs ?? ImmutableArray<DiscoveryRepairCardAction>.Empty,
            ResultLine = null,
        };

    private static DiscoveryRepairCardAction Repair(string id, bool isRunning = false) => new() {
        Id = id,
        Label = "Turn wireless discovery on",
        IsAdmin = false,
        IsRunning = isRunning,
        RunningLabel = "Turning it on…",
    };

    private static string SvgDataUri(string inner) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(inner));

    private static string ValidQrSvg =>
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"21\" height=\"21\" viewBox=\"0 0 21 21\">"
        + "<rect width=\"21\" height=\"21\" fill=\"#ffffff\"/>"
        + "<path fill=\"#000000\" d=\"M0 0h7v7H0z\"/></svg>";

    private static PairingQrView QrView(string svg) => new() {
        SvgDataUri = SvgDataUri(svg),
        Instructions = ImmutableArray.Create("Scan with the phone's camera.", "Pairing completes automatically."),
        CancelLabel = "Cancel",
    };

    private static AppSnapshot Snapshot(
        PairingOperationView? pairing = null,
        PairingQrView? qr = null,
        DiscoveryHealthView? health = null) =>
        StateEngine.CreateDefaultSnapshot() with {
            GeneratedAt = T0,
            PairingOffers = pairing is null
                ? ImmutableArray<PairingOfferView>.Empty
                : ImmutableArray.Create(Offer()),
            Pairing = pairing,
            PairingQr = qr,
            DiscoveryHealth = health,
        };

    // ---------- pairing card ----------

    [Fact]
    public void CodeEntryCreatesCardWithOfferNameAndSuppressesOtherRegions() {
        var vm = new NativeShellViewModel();

        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.AwaitingCode)));

        Assert.NotNull(vm.PairingCard);
        Assert.Equal(PairingCardState.CodeEntry, vm.PairingCard!.State);
        Assert.Equal(DeviceName, vm.PairingCard.DeviceNameDisplay);
        Assert.True(vm.ShowPairingCard);
        Assert.False(vm.ShowQrEntry);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public void PhaseTransitionsUpdateSameInstance() {
        var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.AwaitingCode)));
        var card = vm.PairingCard!;

        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Pairing)));
        Assert.Same(card, vm.PairingCard);
        Assert.Equal(PairingCardState.Progress, card.State);

        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Failed, "The code didn't match.")));
        Assert.Same(card, vm.PairingCard);
        Assert.Equal(PairingCardState.Failure, card.State);
        Assert.Equal("The code didn't match.", card.DetailText);
    }

    [Fact]
    public void ReenteringCodePhaseResetsTypedCode() {
        var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.AwaitingCode)));
        var card = vm.PairingCard!;
        foreach (var digit in "123456") {
            card.AppendDigit(digit);
        }

        Assert.True(card.CanSubmit);
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Pairing)));
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.AwaitingCode)));

        Assert.Equal(string.Empty, card.CodeInput);
        Assert.False(card.CanSubmit);
    }

    [Fact]
    public void SubmitRaisesPairSubmitWithOfferIdAndCode() {
        var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.AwaitingCode)));
        var card = vm.PairingCard!;
        foreach (var digit in "248173") {
            card.AppendDigit(digit);
        }

        string? action = null;
        string? offerId = null;
        string? code = null;
        vm.PairingActionRequested += (a, o, c) => {
            action = a;
            offerId = o;
            code = c;
        };

        card.SubmitCommand.Execute(null);

        Assert.Equal("pairSubmit", action);
        Assert.Equal(OfferId, offerId);
        Assert.Equal("248173", code);
    }

    [Fact]
    public void SubmitBelowSixDigitsIsInert() {
        var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.AwaitingCode)));
        var card = vm.PairingCard!;
        card.AppendDigit('1');

        var raised = false;
        vm.PairingActionRequested += (_, _, _) => raised = true;
        card.Submit();

        Assert.False(raised);
        Assert.False(card.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void RetrySubmitsCompleteCodeElseRestartsPairing() {
        var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Failed)));
        var card = vm.PairingCard!;

        var actions = ImmutableArray.CreateBuilder<string>();
        vm.PairingActionRequested += (a, _, _) => actions.Add(a);

        card.RetryCommand.Execute(null);
        Assert.Equal("pairStart", actions[0]);

        foreach (var digit in "248173") {
            card.AppendDigit(digit);
        }

        card.RetryCommand.Execute(null);
        Assert.Equal("pairSubmit", actions[1]);
    }

    [Fact]
    public void NullPairingClearsCardAndRestoresEntry() {
        var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Pairing)));
        Assert.True(vm.ShowPairingCard);

        vm.ApplySnapshot(Snapshot());

        Assert.Null(vm.PairingCard);
        Assert.False(vm.ShowPairingCard);
        Assert.True(vm.ShowQrEntry);
        Assert.True(vm.ShowEmptyState);
    }

    // ---------- discovery health ----------

    [Fact]
    public void HealthCardMapsRepairsAndForwardsOpaqueIds() {
        var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(health: HealthView(repairs: ImmutableArray.Create(
            Repair("repair-turn-on"),
            Repair("repair-slow", isRunning: true)))));

        Assert.NotNull(vm.DiscoveryHealth);
        Assert.True(vm.ShowDiscoveryHealth);
        Assert.Equal(2, vm.DiscoveryHealth!.Repairs.Length);
        Assert.True(vm.DiscoveryHealth.AnyRepairRunning);

        string? raised = null;
        vm.DiscoveryRepairRequested += id => raised = id;
        var turnOn = vm.DiscoveryHealth.Repairs[0];
        vm.DiscoveryHealth.RepairCommand.Execute(turnOn);

        Assert.Equal("repair-turn-on", raised);
        Assert.False(vm.DiscoveryHealth.RepairCommand.CanExecute(vm.DiscoveryHealth.Repairs[1]));
    }

    [Fact]
    public void HealthCardIsKeptWhenConfigurationUnchanged() {
        var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(health: HealthView()));
        var card = vm.DiscoveryHealth!;

        // Same configuration on a new instance (every snapshot rebuilds candidates) => keep card.
        vm.ApplySnapshot(Snapshot(health: HealthView()));

        Assert.Same(card, vm.DiscoveryHealth);

        vm.ApplySnapshot(Snapshot(health: HealthView(title: "Wireless discovery was restored")));

        Assert.NotSame(card, vm.DiscoveryHealth);
    }

    [Fact]
    public void CheckAgainIsGatedAndForwarded() {
        var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(health: HealthView(canCheckAgain: false)));
        var card = vm.DiscoveryHealth!;
        Assert.False(card.CheckAgainCommand.CanExecute(null));

        var raised = false;
        vm.DiscoveryRecheckRequested += () => raised = true;

        vm.ApplySnapshot(Snapshot(health: HealthView()));
        Assert.NotSame(card, vm.DiscoveryHealth);
        Assert.True(vm.DiscoveryHealth!.CheckAgainCommand.CanExecute(null));
        vm.DiscoveryHealth.CheckAgainCommand.Execute(null);
        Assert.True(raised);

        raised = false;
        vm.ApplySnapshot(Snapshot(health: HealthView(repairs: ImmutableArray.Create(Repair("r", isRunning: true)))));
        Assert.False(vm.DiscoveryHealth!.CheckAgainCommand.CanExecute(null));
        Assert.False(raised);
    }

    // ---------- pairing QR ----------

    [Fact]
    public void QrCardRendersSvgOnceAndSkipsIdenticalRebuilds() {
        var vm = new NativeShellViewModel();
        var qr = QrView(ValidQrSvg);

        vm.ApplySnapshot(Snapshot(qr: qr));
        Assert.NotNull(vm.PairingQr);
        Assert.NotNull(vm.PairingQr!.QrImage);
        Assert.False(vm.ShowQrEntry);
        Assert.False(vm.ShowEmptyState);
        var card = vm.PairingQr;

        // Session snapshots repeat the same payload — the card must not rebuild (no re-raster).
        vm.ApplySnapshot(Snapshot(qr: qr));
        Assert.Same(card, vm.PairingQr);

        vm.ApplySnapshot(Snapshot(qr: QrView(ValidQrSvg.Replace("21", "25", StringComparison.Ordinal))));
        Assert.NotSame(card, vm.PairingQr);

        vm.ApplySnapshot(Snapshot());
        Assert.Null(vm.PairingQr);
        Assert.True(vm.ShowQrEntry);
    }

    [Fact]
    public void QrCancelIsForwarded() {
        var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(qr: QrView(ValidQrSvg)));

        var raised = false;
        vm.QrCancelRequested += () => raised = true;
        vm.PairingQr!.CancelCommand.Execute(null);

        Assert.True(raised);
    }

    // ---------- renderer ----------

    [Fact]
    public void RendererRasterizesPathAndRects() {
        var image = QrSvgRenderer.Render(SvgDataUri(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"21\" height=\"21\">"
            + "<rect width=\"21\" height=\"21\" fill=\"#ffffff\"/>"
            + "<rect x=\"2\" y=\"2\" width=\"3\" height=\"3\"/>"
            + "<path fill=\"#000000\" d=\"M10 10h5v5H10z\"/></svg>"));

        Assert.NotNull(image);
        Assert.True(image!.Width > 0);
    }

    [Theory]
    [InlineData("not-a-data-uri")]
    [InlineData("data:image/svg+xml;base64,!!!not-base64!!!")]
    public void RendererRejectsMalformedPayloads(string payload) {
        Assert.Null(QrSvgRenderer.Render(payload));
    }

    [Fact]
    public void RendererRejectsNonSvgPayload() {
        var notSvg = SvgDataUri("<html><body>hi</body></html>");
        Assert.Null(QrSvgRenderer.Render(notSvg));
    }
}
