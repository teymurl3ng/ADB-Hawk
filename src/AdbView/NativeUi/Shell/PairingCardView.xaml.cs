namespace AdbView.NativeUi.Shell;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

/// <summary>
/// Phase-morphing pairing card (port of ui PairingCard.tsx). All state lives in
/// <see cref="PairingCardViewModel"/>; code-behind only manages input focus parity with the
/// WebView hidden input (auto-focus when the code region becomes visible).
/// </summary>
#pragma warning disable CA1515 // XAML x:Class requires a public partial class.
public partial class PairingCardView : UserControl {
    public PairingCardView() {
        InitializeComponent();
    }

    private void OnCodeInputVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) {
        // UIElement (WPF) is the right abstraction here; focusing is all the input needs.
        if (e.NewValue is true && sender is UIElement focusTarget) {
            focusTarget.Focus();
            Keyboard.Focus(focusTarget);
        }
    }
}
