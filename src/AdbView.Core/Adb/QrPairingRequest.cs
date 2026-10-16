namespace AdbView.Adb;

using System;
using System.Globalization;
using System.Security.Cryptography;
using QRCoder;

/// <summary>
/// One QR pairing session's credential (decision 0010): a six-digit numeric secret (same shape
/// the typed-code flow validates; adbqr-proven against the phone's camera handler) and a unique
/// mDNS instance name the phone will honor when it starts its pairing server after the scan.
/// The secret must never be logged or placed in argv (R-13); <see cref="ToString"/> is redacted.
/// </summary>
internal sealed record QrPairingRequest {
    public const string InstancePrefix = "adbview-";

    public string Secret { get; }
    public string InstanceName { get; }

    private QrPairingRequest(string secret, string instanceName) {
        Secret = secret;
        InstanceName = instanceName;
    }

    public static QrPairingRequest Generate() {
        // Secret: six decimal digits, cryptographically random, leading zeros preserved.
        // 100000..999999 keeps six significant digits without leading-zero ambiguity in logs.
        int secret = RandomNumberGenerator.GetInt32(100000, 1000000);

        // Instance: adbview-<8 hex> — unique per session so the coordinator can identify the
        // phone that scanned THIS code (Studio's "studio-<random>" rationale).
        Span<byte> entropy = stackalloc byte[4];
        RandomNumberGenerator.Fill(entropy);
        string instance = InstancePrefix + entropy[0].ToString("x2", CultureInfo.InvariantCulture)
            + entropy[1].ToString("x2", CultureInfo.InvariantCulture)
            + entropy[2].ToString("x2", CultureInfo.InvariantCulture)
            + entropy[3].ToString("x2", CultureInfo.InvariantCulture);

        return new QrPairingRequest(secret.ToString(CultureInfo.InvariantCulture), instance);
    }

    /// <summary>The WPA-format payload the phone's camera handler parses (AOSP adb_wifi.md).</summary>
    public string BuildPayload() =>
        "WIFI:T:ADB;S:" + InstanceName + ";P:" + Secret + ";;";

    /// <summary>Renders the payload as an SVG document (QRCoder, ECC level M — adbqr's default).</summary>
    public string RenderSvg() {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(BuildPayload(), QRCodeGenerator.ECCLevel.M);
        using var renderer = new SvgQRCode(data);
        return renderer.GetGraphic(4);
    }

    public override string ToString() =>
        InstancePrefix + "session(" + InstanceName + ", secret=***)";
}
