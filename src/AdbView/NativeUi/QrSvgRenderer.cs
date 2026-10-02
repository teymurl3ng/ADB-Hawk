namespace AdbView.NativeUi;

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Serilog;

/// <summary>
/// Rasterizes the backend's QR SVG data URI (<c>PairingQrView.SvgDataUri</c>, QRCoder output)
/// into a frozen WPF <see cref="DrawingImage"/>. WPF cannot decode SVG, the snapshot contract
/// is frozen (BridgeProtocol 1), and adding a rasterizer package is out of scope (R-09) — so the
/// renderer consumes exactly the two shape kinds QRCoder emits (one path of dark modules, plus
/// optional rects). Display-only: a malformed payload renders nothing and is logged (R-08).
/// </summary>
internal static class QrSvgRenderer {
    private const string Base64Marker = "base64,";

    public static ImageSource? Render(string svgDataUri) {
        var marker = svgDataUri.IndexOf(Base64Marker, StringComparison.Ordinal);
        if (marker < 0) {
            Log.Warning("QR render skipped: snapshot data URI has no base64 payload");
            return null;
        }

        byte[] svgBytes;
        try {
            svgBytes = Convert.FromBase64String(svgDataUri[(marker + Base64Marker.Length)..]);
        }
        catch (FormatException ex) {
            Log.Warning(ex, "QR render skipped: malformed base64 payload");
            return null;
        }

        XDocument document;
        try {
            using var stream = new MemoryStream(svgBytes);
            document = XDocument.Load(stream);
        }
        catch (System.Xml.XmlException ex) {
            Log.Warning(ex, "QR render skipped: malformed SVG payload");
            return null;
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg") {
            Log.Warning("QR render skipped: payload is not an SVG document");
            return null;
        }

        var group = new DrawingGroup();

        var bounds = ReadBounds(root);
        if (bounds is { Width: > 0, Height: > 0 } size) {
            // White plate first — the QR must scan against the card in either theme.
            group.Children.Add(new GeometryDrawing {
                Brush = Brushes.White,
                Geometry = new RectangleGeometry(new Rect(0, 0, size.Width, size.Height)),
            });
        }

        foreach (var element in root.Descendants()) {
            if (element.Name.LocalName == "rect") {
                if (IsWhiteFill(element)) {
                    continue;
                }

                var rect = ReadRect(element);
                if (rect is null) {
                    continue;
                }

                group.Children.Add(new GeometryDrawing {
                    Brush = Brushes.Black,
                    Geometry = new RectangleGeometry(rect.Value),
                });
            }
            else if (element.Name.LocalName == "path") {
                var data = (string?)element.Attribute("d");
                if (string.IsNullOrEmpty(data)) {
                    continue;
                }

                // QRCoder emits the SVG path mini-language (M/h/v/z), which WPF's
                // Geometry.Parse accepts directly. Frozen so the image is thread-safe.
                var geometry = Geometry.Parse(data);
                geometry.Freeze();
                group.Children.Add(new GeometryDrawing {
                    Brush = Brushes.Black,
                    Geometry = geometry,
                });
            }
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static Rect? ReadBounds(XElement root) {
        var viewBox = (string?)root.Attribute("viewBox");
        if (viewBox is not null) {
            var parts = viewBox.Split(' ');
            if (parts.Length == 4
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) {
                return new Rect(x, y, w, h);
            }
        }

        if (TryReadPositiveLength(root, "width", out var width)
            && TryReadPositiveLength(root, "height", out var height)) {
            return new Rect(0, 0, width, height);
        }

        return null;
    }

    private static bool TryReadPositiveLength(XElement element, string name, out double value) {
        value = 0;
        var raw = (string?)element.Attribute(name);
        if (raw is null || raw.Length == 0) {
            return false;
        }

        // Strip a unit suffix ("px"); QRCoder writes bare numbers, but be tolerant.
        var numeric = raw.TrimEnd('p', 'x', 't', 'e', 'm', '%');
        if (!double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || value <= 0) {
            value = 0;
            return false;
        }

        return true;
    }

    private static Rect? ReadRect(XElement element) {
        var hasX = TryReadPositiveLength(element, "x", out var x);
        var hasY = TryReadPositiveLength(element, "y", out var y);
        var hasWidth = TryReadPositiveLength(element, "width", out var width);
        var hasHeight = TryReadPositiveLength(element, "height", out var height);
        if (!hasWidth || !hasHeight) {
            return null;
        }

        // TryReadPositiveLength rejects negatives by design; QRCoder modules start at >= 0.
        return new Rect(hasX ? x : 0, hasY ? y : 0, width, height);
    }

    private static bool IsWhiteFill(XElement element) {
        var fill = (string?)element.Attribute("fill");
        if (fill is null) {
            return false;
        }

        return fill.Equals("white", StringComparison.OrdinalIgnoreCase)
            || fill.Equals("#fff", StringComparison.OrdinalIgnoreCase)
            || fill.Equals("#ffffff", StringComparison.OrdinalIgnoreCase);
    }
}
