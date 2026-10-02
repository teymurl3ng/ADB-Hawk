namespace AdbView.NativeUi.Shell;

using System.Windows.Controls;

/// <summary>
/// Discovery-health warning card (decision 0008 + amendment). Displays backend-rendered copy and
/// forwards opaque repair ids / check-again requests; zero wording or logic of its own (R-07/R-15).
/// </summary>
#pragma warning disable CA1515 // XAML x:Class requires a public partial class.
public partial class DiscoveryHealthCardView : UserControl {
    public DiscoveryHealthCardView() {
        InitializeComponent();
    }
}
