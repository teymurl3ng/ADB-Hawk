namespace AdbView.WinUiMigration.Host;

using AdbView.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;

// Sole native bounds writer. Content/snapshot/navigation code has no reference to it.
internal sealed class PanelWindowController(Window window) {
    public int NativeBoundsWrites { get; private set; }
    public int BubbleBoundsWrites { get; private set; }

    public void PlaceBubble(Window bubble) {
        var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));
        double scale = PInvoke.GetDpiForWindow(hwnd) / 96.0;
        RectInt32 work = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int size = (int)Math.Round(32 * scale), margin = (int)Math.Round(8 * scale);
        bubble.AppWindow.ResizeClient(new SizeInt32(size, size));
        bubble.AppWindow.Move(new PointInt32(work.X + work.Width - bubble.AppWindow.Size.Width - margin,
            work.Y + work.Height - bubble.AppWindow.Size.Height - margin));
        BubbleBoundsWrites++;
    }

    public void PlaceBeforeShow() {
        var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));
        double scale = PInvoke.GetDpiForWindow(hwnd) / 96.0;
        AppWindow appWindow = window.AppWindow;
        RectInt32 work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        PanelBounds bounds = PanelPlacement.Calculate(new PanelBounds(work.X, work.Y, work.Width, work.Height), scale,
            appWindow.Size.Width - appWindow.ClientSize.Width, appWindow.Size.Height - appWindow.ClientSize.Height);
        appWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        NativeBoundsWrites++;
    }
}
