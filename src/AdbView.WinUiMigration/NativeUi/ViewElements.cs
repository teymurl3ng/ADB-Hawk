namespace AdbView.WinUiMigration.NativeUi;

using AdbView.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

internal static class ViewElements {
    public static TextBlock Text(double size, double textScale = 1) => new() {
        FontFamily = new FontFamily("Segoe UI Variable"),
        FontSize = size * textScale,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    public static ScrollViewer Scroll(object content) => new() {
        Content = content,
        IsTabStop = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    public static void SetText(TextBlock block, string? text) {
        string value = text ?? string.Empty;
        if (block.Text != value) {
            block.Text = value;
        }
        Visibility visible = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (block.Visibility != visible) {
            block.Visibility = visible;
        }
    }

    public static SolidColorBrush SemanticBrush(SeverityKind severity, ShellView shell) {
        if (shell.ForcedColors) {
            return (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        }
        bool light = shell.Theme == ThemeKind.Light;
        uint color = severity switch {
            SeverityKind.Positive => light ? 0xFF16A34A : 0xFF4ADE80,
            SeverityKind.Attention => light ? 0xFFD97706 : 0xFFFBBF24,
            SeverityKind.Failure => light ? 0xFFDC2626 : 0xFFF87171,
            SeverityKind.Informational => light ? 0xFF0284C7 : 0xFF38BDF8,
            _ => light ? 0xFF64748B : 0xFFA3A3A3,
        };
        return new SolidColorBrush(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));
    }

    public static Viewbox Phone() => new() {
        Width = 32,
        Height = 32,
        Child = new Microsoft.UI.Xaml.Shapes.Path {
            Data = PhoneGeometry(),
            Width = 24,
            Height = 24,
            Stroke = (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            StrokeThickness = 2,
            Stretch = Stretch.Uniform,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        },
    };

    public static PathGeometry PhoneGeometry() {
        var outline = new PathFigure { StartPoint = new Point(7, 3), IsClosed = true };
        outline.Segments.Add(new LineSegment { Point = new Point(17, 3) });
        outline.Segments.Add(new ArcSegment { Point = new Point(19, 5), Size = new Size(2, 2), SweepDirection = SweepDirection.Clockwise });
        outline.Segments.Add(new LineSegment { Point = new Point(19, 19) });
        outline.Segments.Add(new ArcSegment { Point = new Point(17, 21), Size = new Size(2, 2), SweepDirection = SweepDirection.Clockwise });
        outline.Segments.Add(new LineSegment { Point = new Point(7, 21) });
        outline.Segments.Add(new ArcSegment { Point = new Point(5, 19), Size = new Size(2, 2), SweepDirection = SweepDirection.Clockwise });
        outline.Segments.Add(new LineSegment { Point = new Point(5, 5) });
        outline.Segments.Add(new ArcSegment { Point = new Point(7, 3), Size = new Size(2, 2), SweepDirection = SweepDirection.Clockwise });
        var dot = new PathFigure { StartPoint = new Point(12, 18) };
        dot.Segments.Add(new LineSegment { Point = new Point(12.01, 18) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(outline);
        geometry.Figures.Add(dot);
        return geometry;
    }
}
