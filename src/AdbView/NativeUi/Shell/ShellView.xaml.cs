namespace AdbView.NativeUi.Shell;

using System.Windows.Controls;

/// <summary>
/// Stable shell layout (decision 0015): header + scroll region never rebuild — regions
/// appear/disappear via visibility. Code-behind intentionally minimal; all state flows
/// through <see cref="NativeShellViewModel"/>.
/// </summary>
#pragma warning disable CA1515 // XAML x:Class requires a public partial class.
public partial class ShellView : UserControl {
    public ShellView() {
        InitializeComponent();
    }

    /// <summary>Pinned-mode title-bar drag is handled by the native window in I3; placeholder keeps the XAML wire valid.</summary>
    private void OnHeaderDrag(object sender, System.Windows.Input.MouseButtonEventArgs e) {
    }
}
