namespace AdbView.NativeUi.Cards;

#pragma warning disable CA1515

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdbView.NativeUi.Shell;


/// <summary>
/// Device card: collapsed header+footer always visible, details expand in place.
/// All state flows through <see cref="AdbView.NativeUi.DeviceCardViewModel"/>.
/// </summary>
public partial class DeviceCardView : UserControl {
    public DeviceCardView() {
        InitializeComponent();
    }

    private void OnHeaderClick(object sender, MouseButtonEventArgs e) {
        if (DataContext is DeviceCardViewModel vm) {
            vm.ToggleExpandCommand.Execute(null);
        }
    }
}
