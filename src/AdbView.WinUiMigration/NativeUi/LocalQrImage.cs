namespace AdbView.WinUiMigration.NativeUi;

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

/// <summary>Only the backend's local QRCoder SVG subset. No URI/resource loading or payload diagnostics.</summary>
internal sealed class LocalQrImage : IDisposable {
    private readonly Func<Task>? _beforeAttachForVerification;
    private string? _source;
    private long _generation;
    private bool _disposed;
    public Image Image { get; } = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center };
    public Task Pending { get; private set; } = Task.CompletedTask;
    public int DecodeCount { get; private set; }
    public LocalQrImage(Func<Task>? beforeAttachForVerification = null) { _beforeAttachForVerification = beforeAttachForVerification; }
    public void Apply(string? source) {
        if (_disposed || _source == source) { return; }
        _source = source;
        long generation = ++_generation;
        Image.Source = null; Image.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(Image, string.Empty);
        Pending = source is null ? Task.CompletedTask : DecodeAsync(source, generation);
    }
    private async Task DecodeAsync(string source, long generation) {
        try {
            const string prefix = "data:image/svg+xml;base64,";
            if (!source.StartsWith(prefix, StringComparison.Ordinal)) { throw new FormatException(); }
            byte[] bytes = Convert.FromBase64String(source[prefix.Length..]);
            using var stream = new MemoryStream(bytes);
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) {
                XDocument document = XDocument.Load(reader);
                XElement root = document.Root ?? throw new FormatException();
                if (root.Name.LocalName != "svg" || root.DescendantsAndSelf().Any(element =>
                    element.Name.NamespaceName != "http://www.w3.org/2000/svg" || element.Name.LocalName is not ("svg" or "path" or "rect")
                    || element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration
                        && (attribute.Name.LocalName is not ("width" or "height" or "viewBox" or "version" or "shape-rendering" or "x" or "y" or "d" or "fill")
                            || attribute.Value.Contains("url", StringComparison.OrdinalIgnoreCase))))) {
                    throw new FormatException();
                }
                double extent = double.Parse(root.Attribute("width")?.Value ?? throw new FormatException(), CultureInfo.InvariantCulture);
                if (!double.IsFinite(extent) || extent <= 0 || extent > 256) { throw new FormatException(); }
                Image.Width = extent; Image.Height = extent;
            }
            stream.Position = 0;
            var image = new SvgImageSource { RasterizePixelWidth = Image.Width, RasterizePixelHeight = Image.Height };
            DecodeCount++;
            SvgImageSourceLoadStatus status = await image.SetSourceAsync(stream.AsRandomAccessStream());
            if (_beforeAttachForVerification is not null) { await _beforeAttachForVerification().ConfigureAwait(true); }
            if (_disposed || generation != _generation) { return; }
            if (status != SvgImageSourceLoadStatus.Success) {
                Log.Warning("QR presentation: local SVG decoding failed"); return;
            }
            Image.Source = image; Image.Visibility = Visibility.Visible;
            AutomationProperties.SetName(Image, "Pair a device over Wi-Fi");
        }
        catch (Exception exception) when (exception is FormatException or XmlException or IOException or System.Runtime.InteropServices.COMException or ArgumentException) {
            // SVG contains a credential. Never log the payload or decoder exception text.
            Log.Warning("QR presentation: invalid local SVG");
        }
    }
    public void Dispose() {
        if (_disposed) { return; }
        Apply(null); _disposed = true;
    }
}
