namespace AdbView.Presentation;

internal readonly record struct PanelBounds(int X, int Y, int Width, int Height);

internal static class PanelPlacement {
    public static PanelBounds Calculate(PanelBounds workArea, double scale, int borderWidth, int borderHeight) {
        if (!double.IsFinite(scale) || scale <= 0) {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }
        if (workArea.Width <= 0 || workArea.Height <= 0 || borderWidth < 0 || borderHeight < 0) {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }
        int marginX = Math.Min((int)Math.Round(8 * scale), (workArea.Width - 1) / 2);
        int marginY = Math.Min((int)Math.Round(8 * scale), (workArea.Height - 1) / 2);
        int width = Math.Min((int)Math.Round(376 * scale) + borderWidth, workArea.Width - 2 * marginX);
        int height = Math.Min((int)Math.Round(480 * scale) + borderHeight, workArea.Height - 2 * marginY);
        return new PanelBounds(workArea.X + workArea.Width - width - marginX,
            workArea.Y + workArea.Height - height - marginY, width, height);
    }
}
