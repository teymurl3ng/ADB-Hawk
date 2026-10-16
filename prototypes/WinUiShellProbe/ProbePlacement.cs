namespace WinUiShellProbe;

using System;
using Windows.Graphics;

internal static class ProbePlacement {
    public static SizeInt32 ClientSize(RectInt32 area, double scale, SizeInt32 border) {
        if (scale <= 0 || !double.IsFinite(scale)) {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }
        int margin = (int)Math.Round(8 * scale);
        return new SizeInt32(Math.Max(1, Math.Min((int)Math.Round(376 * scale), area.Width - 2 * margin - border.Width)),
            Math.Max(1, Math.Min((int)Math.Round(480 * scale), area.Height - 2 * margin - border.Height)));
    }

    public static PointInt32 Position(RectInt32 area, SizeInt32 outer, double scale) {
        int margin = (int)Math.Round(8 * scale);
        return new PointInt32(area.X + area.Width - outer.Width - margin, area.Y + area.Height - outer.Height - margin);
    }

    public static bool Verify() {
        foreach (double scale in new[] { 1, 1.25, 1.5, 1.75, 2 }) {
            foreach (var area in new[] { new RectInt32(0, 0, 1920, 1040), new RectInt32(-1920, -900, 1280, 800), new RectInt32(-300, -200, 240, 180) }) {
                var border = new SizeInt32(6, 6);
                var client = ClientSize(area, scale, border);
                var outer = new SizeInt32(client.Width + border.Width, client.Height + border.Height);
                var point = Position(area, outer, scale);
                int margin = (int)Math.Round(8 * scale);
                if (point.X < area.X || point.Y < area.Y || point.X + outer.Width != area.X + area.Width - margin
                    || point.Y + outer.Height != area.Y + area.Height - margin || client.Width <= 0 || client.Height <= 0) {
                    return false;
                }
            }
        }
        return true;
    }
}
