#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
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
        DiscoveryHealthView? health = null,
        ImmutableArray<PairingOfferView>? offers = null,
        bool reducedMotion = false) =>
        StateEngine.CreateDefaultSnapshot() with {
            GeneratedAt = T0,
            Shell = StateEngine.CreateDefaultSnapshot().Shell with { ReducedMotion = reducedMotion },
            PairingOffers = offers
                ?? (pairing is null
                    ? ImmutableArray<PairingOfferView>.Empty
                    : ImmutableArray.Create(Offer())),
            Pairing = pairing,
            PairingQr = qr,
            DiscoveryHealth = health,
        };

    // ---------- pairing card ----------

    [Fact]
    public void CodeEntryCreatesCardWithOfferNameAndSuppressesOtherRegions() {
        using var vm = new NativeShellViewModel();

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
        using var vm = new NativeShellViewModel();
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
        using var vm = new NativeShellViewModel();
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
        using var vm = new NativeShellViewModel();
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
        using var vm = new NativeShellViewModel();
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
        using var vm = new NativeShellViewModel();
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
        using var vm = new NativeShellViewModel();
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
        using var vm = new NativeShellViewModel();
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
        using var vm = new NativeShellViewModel();
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
        using var vm = new NativeShellViewModel();
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

    // ---------- offer-phase card (decision 0016 web parity) ----------

    [Fact]
    public void OfferCardAppearsWhenNoOperationIsActive() {
        using var vm = new NativeShellViewModel();

        vm.ApplySnapshot(Snapshot());
        vm.ApplySnapshot(Snapshot(offers: ImmutableArray.Create(Offer())));

        Assert.NotNull(vm.PairingCard);
        Assert.Equal(PairingCardState.Offer, vm.PairingCard!.State);
        Assert.Equal(DeviceName, vm.PairingCard.DeviceNameDisplay);
        Assert.True(vm.ShowPairingCard);
        Assert.False(vm.ShowQrEntry);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public void OfferPairButtonRaisesPairStartWithOfferId() {
        using var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(offers: ImmutableArray.Create(Offer())));

        string? action = null;
        string? offerId = null;
        vm.PairingActionRequested += (a, o, _) => {
            action = a;
            offerId = o;
        };

        vm.PairingCard!.StartCommand.Execute(null);

        Assert.Equal("pairStart", action);
        Assert.Equal(OfferId, offerId);
    }

    [Fact]
    public void OfferWithdrawalClearsTheCard() {
        using var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(offers: ImmutableArray.Create(Offer())));
        Assert.NotNull(vm.PairingCard);

        vm.ApplySnapshot(Snapshot());

        Assert.Null(vm.PairingCard);
        Assert.True(vm.ShowQrEntry);
    }

    [Fact]
    public void DifferentOfferIdRecreatesTheCard() {
        using var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(offers: ImmutableArray.Create(Offer())));
        var first = vm.PairingCard!;

        var secondOffer = Offer() with { Id = "offer-2", DisplayName = "Other Phone" };
        vm.ApplySnapshot(Snapshot(offers: ImmutableArray.Create(secondOffer)));

        Assert.NotSame(first, vm.PairingCard);
        Assert.Equal("offer-2", vm.PairingCard!.OfferId);
        Assert.Equal("Other Phone", vm.PairingCard.DeviceNameDisplay);
    }

    [Fact]
    public void OperationDisplayNameIsAuthoritativeOverOfferRows() {
        using var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.AwaitingCode)));

        Assert.Equal(DeviceName, vm.PairingCard!.DeviceNameDisplay);
    }

    // ---------- shell state round-trip ----------

    [Fact]
    public void SnapshotPinStateUpdatesTheViewModel() {
        using var vm = new NativeShellViewModel();
        Assert.False(vm.Pinned);

        // The reducer owns pin state; the snapshot is what the shell renders.
        var pinnedSnapshot = Snapshot();
        pinnedSnapshot = pinnedSnapshot with { Shell = pinnedSnapshot.Shell with { Pinned = true } };
        vm.ApplySnapshot(pinnedSnapshot);

        Assert.True(vm.Pinned);
        Assert.Equal("Unpin flyout", vm.PinToolTip);
    }

    [Fact]
    public void ThemeResourceRefreshRaisesBindableBrushes() {
        using var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.AwaitingCode)));
        var card = vm.PairingCard!;

        var raised = new HashSet<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.RefreshThemeResources();

        Assert.Contains("PinForeground", raised);
        Assert.Contains("CardSurface", raised);
        Assert.Contains("CardBorder", raised);
    }

    // ---------- pairing QR ----------

    [Fact]
    public void QrCardRendersSvgOnceAndSkipsIdenticalRebuilds() {
        using var vm = new NativeShellViewModel();
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
        using var vm = new NativeShellViewModel();
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

    // ---------- decision 0017: QR operations have no typed-code affordances ----------

    private const string QrOfferId = "_adb-tls-pairing._tcpadbview-f34ffd13";

    [Fact]
    public void QrOperationHidesSlotsAndShowsProgress() {
        using var vm = new NativeShellViewModel();

        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Pairing) with { OfferId = QrOfferId }));

        var card = vm.PairingCard!;
        Assert.True(card.IsQrOriginated);
        Assert.False(card.ShowSlots);        // the app holds the code; nothing to type
        Assert.False(card.ShowCodeEntryTitle);
        Assert.True(card.ShowProgress);
        Assert.False(card.ShowPrimaryAction); // in flight: nothing for the user to press
    }

    [Fact]
    public void QrFailureOffersScanAgain() {
        using var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Failed, "The code didn't match.")
            with { OfferId = QrOfferId }));

        var card = vm.PairingCard!;
        Assert.True(card.IsQrOriginated);
        Assert.True(card.ShowPrimaryAction);
        Assert.Equal("Scan again", card.PrimaryLabel);
        Assert.False(card.ShowSlots);

        string? action = null;
        vm.PairingActionRequested += (a, _, _) => action = a;
        card.PrimaryCommand.Execute(null);
        Assert.Equal("pairQrStart", action);
    }

    [Fact]
    public void TypedCodeCardKeepsSlotsAndPair() {
        using var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.AwaitingCode)));

        var card = vm.PairingCard!;
        Assert.False(card.IsQrOriginated);
        Assert.True(card.ShowSlots);
        Assert.True(card.ShowCodeEntryTitle);
        Assert.True(card.ShowCodeActionRow);
        Assert.True(card.ShowPrimaryAction);
        Assert.Equal("Pair", card.PrimaryLabel);
    }

    [Fact]
    public void TypedCodeFailureOffersRetryNotScanAgain() {
        using var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Failed, "Wrong code.")));

        var card = vm.PairingCard!;
        Assert.False(card.IsQrOriginated);
        Assert.True(card.ShowSlots);           // web parity: the failure card keeps the slots
        Assert.Equal("Retry", card.PrimaryLabel);
    }

    // ---------- copied badge (decision 0021 audit: the badge never cleared) ----------

    private sealed class ManualDelaySource {
        private readonly List<(TaskCompletionSource Tcs, TimeSpan Span, CancellationToken Token)> _pending = new();
        private readonly List<TimeSpan> _allRequests = new();

        /// <summary>Every wait ever requested, including cancelled ones — unlike <see cref="Pending"/>,
        /// which only counts live waits.</summary>
        public TimeSpan[] Requested {
            get {
                lock (_pending) {
                    return _allRequests.ToArray();
                }
            }
        }

        public int Pending {
            get {
                lock (_pending) {
                    return _pending.Count;
                }
            }
        }

        public Task WaitAsync(TimeSpan span, CancellationToken token) {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) {
                _allRequests.Add(span);
                _pending.Add((tcs, span, token));
            }

            token.Register(() => {
                lock (_pending) {
                    _pending.RemoveAll(p => p.Tcs == tcs);
                }

                tcs.TrySetCanceled(token);
            });
            return tcs.Task;
        }

        public void CompleteFirst() {
            TaskCompletionSource tcs;
            lock (_pending) {
                tcs = _pending[0].Tcs;
                _pending.RemoveAt(0);
            }

            tcs.TrySetResult();
        }
    }

    private static DeviceCardViewModel MakeCard(
        ManualDelaySource? delay = null,
        List<string>? clipboard = null) {
        return DeviceCardViewModel.Create(
            new DeviceView {
                Id = "usb:R8",
                DisplayName = "Galaxy A06",
                State = DeviceStateKind.Connected,
                Severity = SeverityKind.Positive,
                StateLabel = "Connected",
                StatusLine = "Connected · USB",
                Transport = TransportKind.Usb,
                TransportLabel = "USB",
                MetaTemplate = "Connected {age} ago",
                StateSince = T0,
                Detail = new DeviceDetailView {
                    Serial = "R8VXA02SNKH",
                    ExactModel = "SM-A065F",
                    AndroidVersion = "16",
                    Address = null,
                    CopyFields = ImmutableArray.Create(
                        new CopyFieldView { Key = "serial", Label = "Serial", Value = "R8VXA02SNKH" }),
                },
            },
            T0,
            isExpanded: false,
            notifyExpand: _ => { },
            setClipboard: value => clipboard?.Add(value),
            delay: delay!.WaitAsync);
    }

    [Fact]
    public async Task CopiedBadgeClearsAfterTheWebHold() {
        var delay = new ManualDelaySource();
        using var card = MakeCard(delay: delay);

        card.CopySerialCommand.Execute(null);
        Assert.True(card.ShowSerialCopied);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), Assert.Single(delay.Requested));

        delay.CompleteFirst();
        await WaitForConditionAsync(() => card.ShowSerialCopy);
        Assert.False(card.ShowSerialCopied);
    }

    [Fact]
    public async Task SecondCopySupersedesTheFirstClear() {
        var delay = new ManualDelaySource();
        using var card = MakeCard(delay: delay);

        card.CopySerialCommand.Execute(null);
        // A second copy while the first hold is pending: the first clear must be cancelled.
        card.CopySerialCommand.Execute(null);
        Assert.Equal(2, delay.Requested.Length);
        Assert.Equal(1, delay.Pending); // the first wait left the queue when cancelled

        delay.CompleteFirst(); // the surviving (second) hold
        await WaitForConditionAsync(() => card.ShowSerialCopy);
        Assert.False(card.ShowSerialCopied);
    }

    [Fact]
    public async Task ClipboardFailureShowsNoBadge() {
        var delay = new ManualDelaySource();
        using var card = DeviceCardViewModel.Create(
            new DeviceView {
                Id = "usb:R8",
                DisplayName = "Galaxy A06",
                State = DeviceStateKind.Connected,
                Severity = SeverityKind.Positive,
                StateLabel = "Connected",
                StatusLine = "Connected · USB",
                Transport = TransportKind.Usb,
                TransportLabel = "USB",
                MetaTemplate = "Connected {age} ago",
                StateSince = T0,
                Detail = new DeviceDetailView {
                    Serial = "R8",
                    ExactModel = null,
                    AndroidVersion = null,
                    Address = null,
                    CopyFields = ImmutableArray<CopyFieldView>.Empty,
                },
            },
            T0,
            isExpanded: false,
            notifyExpand: _ => { },
            setClipboard: _ => throw new InvalidOperationException("clipboard locked"),
            delay: delay.WaitAsync);

        card.CopySerialCommand.Execute(null);

        Assert.True(card.ShowSerialCopy);  // the copy icon stays
        Assert.False(card.ShowSerialCopied); // no false "Copied"
        Assert.Equal(0, delay.Pending); // and nothing to clear later
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 3000) {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            if (condition()) {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("condition not met");
    }

    // ---------- decision 0022 parity fixes ----------

    [Fact]
    public void PillShowsTheBackendStateLabel() {
        using var vm = new NativeShellViewModel();
        var device = MakeDeviceView("usb:R8", DeviceStateKind.Offline, "Device offline");
        vm.ApplySnapshot(CreateDefaultSnapshot() with {
            GeneratedAt = T0,
            Devices = ImmutableArray.Create(device),
        });

        Assert.Equal("Device offline", vm.Devices[0].StatePillText);
    }

    private static DeviceView MakeDeviceView(string id, DeviceStateKind state, string stateLabel) =>
        new DeviceView {
            Id = id,
            DisplayName = "Galaxy A06",
            State = state,
            Severity = state == DeviceStateKind.Connected ? SeverityKind.Positive : SeverityKind.Failure,
            StateLabel = stateLabel,
            StatusLine = stateLabel,
            Transport = TransportKind.Usb,
            TransportLabel = "USB",
            MetaTemplate = "Connected {age} ago",
            StateSince = T0,
            Detail = new DeviceDetailView {
                Serial = null,
                ExactModel = null,
                AndroidVersion = null,
                Address = null,
                CopyFields = ImmutableArray<CopyFieldView>.Empty,
            },
        };

    private static AppSnapshot CreateDefaultSnapshot() => StateEngine.CreateDefaultSnapshot();

    [Fact]
    public async Task SuccessCardHoldsThroughTheDwellThenClears() {
        var delay = new ManualDelaySource();
        using var vm = new NativeShellViewModel(delay.WaitAsync);
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.PairedWaitingForDevice)));
        Assert.NotNull(vm.PairingCard);

        // The operation clears (device appeared / wait expired): the success card holds.
        vm.ApplySnapshot(Snapshot());
        Assert.NotNull(vm.PairingCard);
        Assert.Equal(TimeSpan.FromMilliseconds(750), Assert.Single(delay.Requested));

        delay.CompleteFirst();
        long deadline = Environment.TickCount64 + 3000;
        while (vm.PairingCard is not null && Environment.TickCount64 < deadline) {
            await Task.Delay(10);
        }

        Assert.Null(vm.PairingCard);
    }

    [Fact]
    public async Task NewPairingOperationCancelsTheDwell() {
        var delay = new ManualDelaySource();
        using var vm = new NativeShellViewModel(delay.WaitAsync);
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.PairedWaitingForDevice)));
        vm.ApplySnapshot(Snapshot());
        Assert.NotNull(vm.PairingCard); // dwelling

        // A new pairing operation arrives during the dwell: it wins immediately and the
        // pending dwell is cancelled outright (nothing left to complete).
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.AwaitingCode)));
        Assert.Equal(PairingCardState.CodeEntry, vm.PairingCard!.State);
        Assert.Equal(0, delay.Pending);

        await Task.Delay(80);
        Assert.Equal(PairingCardState.CodeEntry, vm.PairingCard!.State); // still showing, not cleared
    }

    // ---------- reduced-motion spinner gate (decision 0024) ----------

    [Fact]
    public void SpinnerRunsWhenMotionEnabled() {
        using var vm = new NativeShellViewModel();

        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Pairing)));

        Assert.NotNull(vm.PairingCard);
        Assert.True(vm.PairingCard!.SpinnerEnabled);
    }

    [Fact]
    public void SpinnerIsGatedUnderReducedMotion() {
        using var vm = new NativeShellViewModel();

        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Pairing), reducedMotion: true));

        Assert.NotNull(vm.PairingCard);
        Assert.False(vm.PairingCard!.SpinnerEnabled);
    }

    [Fact]
    public void OfferCardSpinnerIsGatedUnderReducedMotion() {
        using var vm = new NativeShellViewModel();

        vm.ApplySnapshot(Snapshot(offers: ImmutableArray.Create(Offer()), reducedMotion: true));

        Assert.NotNull(vm.PairingCard);
        Assert.False(vm.PairingCard!.SpinnerEnabled);
    }

    [Fact]
    public void SpinnerFollowsTheGateAcrossSnapshots() {
        using var vm = new NativeShellViewModel();
        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Pairing)));
        var card = vm.PairingCard!;
        Assert.True(card.SpinnerEnabled);

        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Pairing), reducedMotion: true));
        Assert.Same(card, vm.PairingCard);
        Assert.False(card.SpinnerEnabled);

        vm.ApplySnapshot(Snapshot(pairing: Operation(PairingPhase.Pairing)));
        Assert.Same(card, vm.PairingCard);
        Assert.True(card.SpinnerEnabled);
    }
}
