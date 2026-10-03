namespace AdbView.Host;

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;
using Serilog;

/// <summary>
/// Owns the QR pairing session (decision 0010): generates the single-use credential, publishes
/// the rendered code, and — when the phone starts advertising the requested instance name —
/// hands the pre-generated secret to the existing pairing flow (pairStart + pairSubmit), after
/// which the standard pairing cards take over. The secret never logs (R-13); the session is
/// single-use and expires via the reducer's sanctioned 2-minute timer.
/// </summary>
internal sealed class QrPairingSession {
    private readonly Action<AdbViewEvent> _post;
    private readonly Action<string> _startOffer;
    private readonly Action<string, string> _submitCode;

    private readonly object _gate = new();
    private QrPairingRequest? _request;
    private bool _fired;

    public QrPairingSession(
        Action<AdbViewEvent> post,
        Action<string> startOffer,
        Action<string, string> submitCode) {
        _post = post;
        _startOffer = startOffer;
        _submitCode = submitCode;
    }

    public bool IsActive {
        get {
            lock (_gate) {
                return _request is not null && !_fired;
            }
        }
    }

    /// <summary>Test-only accessor (InternalsVisibleTo). Never called by production paths.</summary>
    internal QrPairingRequest? GetActiveRequestForTest() {
        lock (_gate) {
            return _request;
        }
    }

    /// <summary>Starts a session; a no-op while one is already active.</summary>
    public void Start() {
        lock (_gate) {
            if (_request is not null && !_fired) {
                return;
            }

            _request = QrPairingRequest.Generate();
            _fired = false;
        }

        // Render outside the gate; QRCoder is pure computation on the payload.
        string svg = _request.RenderSvg();
        string svgDataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(svg));

        var view = new PairingQrView {
            SvgDataUri = svgDataUri,
            Instructions = [
                Vocabulary.Pairing.QrInstructionPhone,
                Vocabulary.Pairing.QrInstructionScan,
            ],
            CancelLabel = Vocabulary.Pairing.QrCancelLabel,
        };

        // The payload/secret never reaches the log — only the instance name does (R-13).
        Log.Information("QR pairing session started ({Instance}).", _request.InstanceName);
        _post(new PairingQrStartedEvent(view));
    }

    /// <summary>User cancel: quiet clear (generation-guarded in the reducer).</summary>
    public void Cancel() {
        QrPairingRequest? request;
        lock (_gate) {
            request = _request;
            if (request is null || _fired) {
                return;
            }

            _request = null;
        }

        Log.Information("QR pairing session cancelled ({Instance}).", request.InstanceName);
        // Generation 0 = user-initiated cancel: the reducer accepts whatever session is active.
        _post(new PairingQrClearedEvent(0));
    }

    /// <summary>AppHost forwards accepted snapshots; a matching advertisement fires the pair once.</summary>
    public void OnSnapshotPublished(AppSnapshot snapshot, DeviceEngineState deviceState) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(deviceState);

        if (snapshot.PairingQr is null) {
            return;
        }

        QrPairingRequest? request;
        bool fired;
        lock (_gate) {
            request = _request;
            fired = _fired;
        }

        if (request is null || fired) {
            return;
        }

        // Effective set: instance-level union (decision 0016, same rule as the reducer).
        ImmutableArray<RawMdnsService> effective = State.MdnsEffectiveSet.Merge(
            deviceState.LatestMdnsServices,
            deviceState.LatestFallbackServices);

        RawMdnsService? match = effective.FirstOrDefault(service =>
            service.Kind == MdnsServiceKind.Pairing
            && string.Equals(service.InstanceName, request.InstanceName, StringComparison.Ordinal));
        if (match is null) {
            return;
        }

        // The offer binding carries the endpoint; its id is the row's DiscoveryKey.
        string offerId = match.ServiceType + "\u001f" + match.InstanceName;
        if (!deviceState.OfferBindings.ContainsKey(offerId)) {
            Log.Information(
                "QR pairing: advertisement for {Instance} seen; offer not yet mapped.",
                request.InstanceName);
            return;
        }

        lock (_gate) {
            if (_fired || _request is null) {
                return;
            }

            _fired = true;
        }

        string secret = request.Secret;
        Log.Information("QR pairing: phone scanned; starting pair for {Instance}.", request.InstanceName);
        _startOffer(offerId);
        // PairingSession.Submit is synchronous fire-and-forget into the pair coordinator; the
        // standard pairing cards take over. The QR layer clears quietly (generation 0 = active).
        try {
            _submitCode(offerId, secret);
            _post(new PairingQrClearedEvent(0));
            Log.Information("QR pairing: handoff complete ({Instance}).", request.InstanceName);
        }
#pragma warning disable CA1031 // R-08: handoff failures are logged; the pairing flow owns user-visible errors.
        catch (Exception ex) {
            Log.Warning(ex, "QR pairing: handoff failed ({Instance}).", request.InstanceName);
        }
#pragma warning restore CA1031
    }
}
