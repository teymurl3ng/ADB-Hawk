namespace AdbView.Host;

using System;
using System.Drawing;
using AdbView.State;

/// <summary>
/// The flyout shell surface AppHost drives. Implemented by the WinForms FlyoutWindow
/// (default) and the native WPF NativeFlyoutWindow (--ui=native, decision 0015 Phase I1).
/// All Bounds/Size members are in PHYSICAL pixels on both implementations.
/// </summary>
internal interface IFlyoutShell {
    /// <summary>True when UI work must be marshaled to the shell thread.</summary>
    bool InvokeRequired { get; }

    /// <summary>True when the host's message loop is pumping (gates synchronous UI calls).</summary>
    bool HasMessageLoop { get; }

    object Invoke(Delegate method, params object[] args);

    void BeginInvoke(Delegate method, params object[] args);

    bool IsHandleCreated { get; }

    IntPtr Handle { get; }

    int DeviceDpi { get; }

    bool Visible { get; }

    /// <summary>Physical-pixel bounds. Set re-places the window without activation.</summary>
    Rectangle Bounds { get; set; }

    bool TopMost { get; set; }

    bool IsPinned { get; set; }

    event EventHandler? Deactivate;

    void Show();

    void Hide();

    void RecalcNonClientFrame();

    void ApplyShellAppearance(ThemeKind theme, bool transparency);
}
