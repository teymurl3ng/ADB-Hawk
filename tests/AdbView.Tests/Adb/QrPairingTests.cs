namespace AdbView.Tests.Adb;

using System;
using System.Collections.Immutable;
using System.Text;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Host;
using AdbView.State;
using Xunit;

public sealed class QrPairingRequestTests {
    [Fact]
    public void GenerateProducesSixDigitSecretAndPrefixedInstanceName() {
        var request = QrPairingRequest.Generate();

        Assert.True(request.Secret.Length == 6);
        Assert.All(request.Secret, c => Assert.True(char.IsAsciiDigit(c)));
        Assert.StartsWith(QrPairingRequest.InstancePrefix, request.InstanceName, StringComparison.Ordinal);
        Assert.Equal(QrPairingRequest.InstancePrefix.Length + 8, request.InstanceName.Length);
    }

    [Fact]
    public void PayloadMatchesAospWifiFormat() {
        var request = QrPairingRequest.Generate();

        Assert.Equal(
            "WIFI:T:ADB;S:" + request.InstanceName + ";P:" + request.Secret + ";;",
            request.BuildPayload());
        // Secret/instance must not contain the WIFI: field separator or the host:pair split char.
        Assert.DoesNotContain(";", request.Secret, StringComparison.Ordinal);
        Assert.DoesNotContain(":", request.Secret, StringComparison.Ordinal);
    }

    [Fact]
    public void ToStringIsRedacted() {
        var request = QrPairingRequest.Generate();

        string text = request.ToString();
        Assert.DoesNotContain(request.Secret, text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsDifferAcrossSessions() {
        // 6 digits from a CSPRNG: collision across two draws is ~1e-6 — acceptable flake floor,
        // but assert on the pair (secret, instance) which is effectively never equal.
        var a = QrPairingRequest.Generate();
        var b = QrPairingRequest.Generate();
        Assert.False(a.Secret == b.Secret && a.InstanceName == b.InstanceName);
    }

    [Fact]
    public void RenderSvgProducesSvgDocument() {
        var request = QrPairingRequest.Generate();

        string svg = request.RenderSvg();

        Assert.StartsWith("<svg", svg.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("svg", svg, StringComparison.OrdinalIgnoreCase);
        // Deterministic for the same payload.
        Assert.Equal(svg, request.RenderSvg());
    }
}

public sealed class QrPairingSessionTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class EventSink {
        public System.Collections.Generic.List<AdbViewEvent> Events { get; } = new();
        public void Post(AdbViewEvent evt) => Events.Add(evt);
    }

    private sealed class PairingActions {
        public System.Collections.Generic.List<string> Started { get; } = new();
        public System.Collections.Generic.List<(string OfferId, string Code)> Submitted { get; } = new();

        public void Start(string offerId) => Started.Add(offerId);
        public void Submit(string offerId, string code) {
            Submitted.Add((offerId, code));
        }
    }

    private static RawMdnsService PairingRow(string instance) => new() {
        InstanceName = instance,
        ServiceType = "_adb-tls-pairing._tcp",
        Kind = MdnsServiceKind.Pairing,
        Ipv4 = "192.168.18.248",
        Port = 41953,
        Format = RawMdnsFormat.Fallback,
    };

    private static DeviceEngineState StateWith(RawMdnsService row, string offerId) =>
        DeviceEngineState.Empty with {
            LatestFallbackServices = ImmutableArray.Create(row),
            OfferBindings = ImmutableDictionary<string, PairingOfferBinding>.Empty.Add(
                offerId,
                new PairingOfferBinding(offerId, row.Ipv4!, row.Port, null, "Galaxy S24")),
        };

    private static AppSnapshot SnapshotWithQr() =>
        StateEngine.CreateDefaultSnapshot() with {
            GeneratedAt = T0,
            PairingQr = new PairingQrView {
                SvgDataUri = "data:image/svg+xml;base64,PHN2Zy8+",
                Instructions = ImmutableArray.Create("line"),
                CancelLabel = "Cancel",
            },
        };

    [Fact]
    public void StartPublishesViewWithInstructionsAndCancel() {
        var sink = new EventSink();
        var actions = new PairingActions();
        var session = new QrPairingSession(sink.Post, actions.Start, actions.Submit);

        session.Start();

        var started = Assert.IsType<PairingQrStartedEvent>(sink.Events.Single());
        Assert.Equal(2, started.View.Instructions.Length);
        Assert.Equal("Cancel", started.View.CancelLabel);
        Assert.StartsWith("data:image/svg+xml;base64,", started.View.SvgDataUri, StringComparison.Ordinal);
        Assert.True(session.IsActive);
    }

    [Fact]
    public void SecondStartWhileActiveIsRejected() {
        var sink = new EventSink();
        var session = new QrPairingSession(sink.Post, new PairingActions().Start, new PairingActions().Submit);

        session.Start();
        session.Start();

        Assert.Single(sink.Events);
    }

    [Fact]
    public void MatchingInstanceTriggersStartAndSubmitWithGeneratedSecretOnce() {
        var sink = new EventSink();
        var actions = new PairingActions();
        var session = new QrPairingSession(sink.Post, actions.Start, actions.Submit);
        session.Start();
        sink.Events.Clear();

        var request = session.GetActiveRequestForTest();
        string offerId = "_adb-tls-pairing._tcp\u001f" + request!.InstanceName;
        session.OnSnapshotPublished(
            SnapshotWithQr(),
            StateWith(PairingRow(request.InstanceName), offerId));
        // Continuous presence does not re-fire.
        session.OnSnapshotPublished(
            SnapshotWithQr(),
            StateWith(PairingRow(request.InstanceName), offerId));

        Assert.Equal(new[] { offerId }, actions.Started);
        var submit = Assert.Single(actions.Submitted);
        Assert.Equal(offerId, submit.OfferId);
        Assert.Equal(request.Secret, submit.Code); // the generated secret rides the existing pair flow
        Assert.False(session.IsActive); // handed off; QR layer cleared
    }

    [Fact]
    public void NonMatchingInstanceIsIgnored() {
        var sink = new EventSink();
        var actions = new PairingActions();
        var session = new QrPairingSession(sink.Post, actions.Start, actions.Submit);
        session.Start();

        session.OnSnapshotPublished(
            SnapshotWithQr(),
            StateWith(PairingRow("adb-other-instance"), "_adb-tls-pairing._tcp\u001fadb-other-instance"));

        Assert.Empty(actions.Started);
        Assert.Empty(actions.Submitted);
        Assert.True(session.IsActive);
    }

    [Fact]
    public void CancelClearsQuietly() {
        var sink = new EventSink();
        var actions = new PairingActions();
        var session = new QrPairingSession(sink.Post, actions.Start, actions.Submit);
        session.Start();
        sink.Events.Clear();

        session.Cancel();

        var cleared = Assert.IsType<PairingQrClearedEvent>(sink.Events.Single());
        Assert.Equal(0, cleared.Generation); // user-initiated: clears whatever session is active
        Assert.False(session.IsActive);
    }

    [Fact]
    public void MatchWithoutBindingWaitsForTheOfferMap() {
        var sink = new EventSink();
        var actions = new PairingActions();
        var session = new QrPairingSession(sink.Post, actions.Start, actions.Submit);
        session.Start();
        sink.Events.Clear();

        var request = session.GetActiveRequestForTest();
        // Advertisement present, binding not yet mapped (mapper lag).
        var state = DeviceEngineState.Empty with {
            LatestFallbackServices = ImmutableArray.Create(PairingRow(request!.InstanceName)),
        };
        session.OnSnapshotPublished(SnapshotWithQr(), state);

        Assert.Empty(actions.Started);
        Assert.True(session.IsActive);

        // Binding arrives → fires exactly once.
        session.OnSnapshotPublished(
            SnapshotWithQr(),
            StateWith(PairingRow(request.InstanceName), "_adb-tls-pairing._tcp\u001f" + request.InstanceName));
        Assert.Single(actions.Started);
    }

    [Fact]
    public void RejectedHandoffKeepsTheQrSessionForARescan() {
        // Decision 0017: the handoff used to clear the QR and log success unconditionally, even
        // when the pair coordinator refused the start ("pairing operation already active") —
        // which hid the failure and removed the only affordance the user had.
        var sink = new EventSink();
        var actions = new PairingActions();
        bool startTakesEffect = false;
        var session = new QrPairingSession(
            sink.Post, actions.Start, actions.Submit, isStartActive: _ => startTakesEffect);
        session.Start();
        sink.Events.Clear();

        var request = session.GetActiveRequestForTest();
        string offerId = "_adb-tls-pairing._tcp" + request!.InstanceName;
        session.OnSnapshotPublished(
            SnapshotWithQr(),
            StateWith(PairingRow(request.InstanceName), offerId));

        // Refused: no submit, no clear, and the session stays live so the code can be re-scanned.
        Assert.Single(actions.Started);
        Assert.Empty(actions.Submitted);
        Assert.DoesNotContain(sink.Events, e => e is PairingQrClearedEvent);
        Assert.True(session.IsActive);

        // A later scan (once the slot is free) still hands off exactly once.
        startTakesEffect = true;
        session.OnSnapshotPublished(
            SnapshotWithQr(),
            StateWith(PairingRow(request.InstanceName), offerId));

        Assert.Equal(2, actions.Started.Count);
        var submit = Assert.Single(actions.Submitted);
        Assert.Equal(request.Secret, submit.Code);
        Assert.Contains(sink.Events, e => e is PairingQrClearedEvent);
        Assert.False(session.IsActive);
    }
}
