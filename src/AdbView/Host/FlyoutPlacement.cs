using System;
using System.Drawing;

namespace AdbView.Host;

internal static class FlyoutPlacement {
    internal enum TaskbarEdge {
        Bottom,
        Top,
        Left,
        Right
    }

    public static Rectangle CalculatePlacement(
        Rectangle anchorPhysicalRect,
        TaskbarEdge taskbarEdge,
        Rectangle monitorWorkAreaPhysical,
        double dpiScale,
        Size flyoutEffectiveSize,
        int effectiveGap = 12) {
        // Convert effective size to physical size
        int physicalWidth = (int)Math.Round(flyoutEffectiveSize.Width * dpiScale);
        int physicalHeight = (int)Math.Round(flyoutEffectiveSize.Height * dpiScale);
        int physicalGap = (int)Math.Round(effectiveGap * dpiScale);

        int x = 0;
        int y = 0;

        // Position based on taskbar edge
        switch (taskbarEdge) {
            case TaskbarEdge.Bottom:
            case TaskbarEdge.Top:
                // Center horizontally relative to the anchor
                x = anchorPhysicalRect.Left + (anchorPhysicalRect.Width / 2) - (physicalWidth / 2);

                if (taskbarEdge == TaskbarEdge.Bottom) {
                    y = anchorPhysicalRect.Top - physicalHeight - physicalGap;
                }
                else // Top
                {
                    y = anchorPhysicalRect.Bottom + physicalGap;
                }
                break;

            case TaskbarEdge.Left:
            case TaskbarEdge.Right:
                // Center vertically relative to the anchor
                y = anchorPhysicalRect.Top + (anchorPhysicalRect.Height / 2) - (physicalHeight / 2);

                if (taskbarEdge == TaskbarEdge.Left) {
                    x = anchorPhysicalRect.Right + physicalGap;
                }
                else // Right
                {
                    x = anchorPhysicalRect.Left - physicalWidth - physicalGap;
                }
                break;
        }

        // Clamp to monitor work area
        if (x < monitorWorkAreaPhysical.Left) {
            x = monitorWorkAreaPhysical.Left + physicalGap;
        }
        else if (x + physicalWidth > monitorWorkAreaPhysical.Right) {
            x = monitorWorkAreaPhysical.Right - physicalWidth - physicalGap;
        }

        if (y < monitorWorkAreaPhysical.Top) {
            y = monitorWorkAreaPhysical.Top + physicalGap;
        }
        else if (y + physicalHeight > monitorWorkAreaPhysical.Bottom) {
            y = monitorWorkAreaPhysical.Bottom - physicalHeight - physicalGap;
        }

        return new Rectangle(x, y, physicalWidth, physicalHeight);
    }

    public static Rectangle GetDefaultTrayAnchor(
        Rectangle monitorWorkArea,
        Rectangle monitorBounds,
        TaskbarEdge edge,
        double dpiScale) {
        int iconSize = (int)Math.Round(16 * dpiScale);
        int margin = (int)Math.Round(24 * dpiScale);

        return edge switch {
            TaskbarEdge.Bottom => new Rectangle(monitorWorkArea.Right - margin - iconSize, monitorWorkArea.Bottom + 4, iconSize, iconSize),
            TaskbarEdge.Top => new Rectangle(monitorWorkArea.Right - margin - iconSize, monitorBounds.Top + 4, iconSize, iconSize),
            TaskbarEdge.Left => new Rectangle(monitorBounds.Left + 4, monitorWorkArea.Bottom - margin - iconSize, iconSize, iconSize),
            TaskbarEdge.Right => new Rectangle(monitorWorkArea.Right + 4, monitorWorkArea.Bottom - margin - iconSize, iconSize, iconSize),
            _ => new Rectangle(monitorWorkArea.Right - margin - iconSize, monitorWorkArea.Bottom + 4, iconSize, iconSize)
        };
    }

    public static Rectangle ResolveAnchor(
        Rectangle exactIconRect,
        Rectangle monitorWorkArea,
        Rectangle monitorBounds,
        TaskbarEdge edge,
        double dpiScale) {
        if (exactIconRect.Width > 0 && exactIconRect.Height > 0) {
            return exactIconRect;
        }

        return GetDefaultTrayAnchor(monitorWorkArea, monitorBounds, edge, dpiScale);
    }
}

