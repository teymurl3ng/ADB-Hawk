namespace AdbView.WinUiMigration.NativeUi;

using System.Globalization;
using AdbView.NativeUi.Shell;
using AdbView.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

internal sealed class DeviceRow {
    public Button Button { get; }
    public TextBlock Name { get; }
    public TextBlock Status { get; }
    public TextBlock Battery { get; }
    public string AgeText => _age.Text;
    private readonly TextBlock _age;
    private readonly Ellipse _marker;

    public DeviceRow(string id, double textScale, Action<string> open) {
        Name = ViewElements.Text(15, textScale);
        Status = ViewElements.Text(12, textScale);
        Battery = ViewElements.Text(12, textScale);
        _age = ViewElements.Text(12, textScale);
        // Row text participates in its native button; selectable values live in details.
        Name.IsTextSelectionEnabled = false;
        Status.IsTextSelectionEnabled = false;
        Battery.IsTextSelectionEnabled = false;
        _age.IsTextSelectionEnabled = false;
        var identity = new Grid { ColumnSpacing = 12 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.Children.Add(ViewElements.Phone());
        var naming = new StackPanel { Spacing = 4 };
        naming.Children.Add(Name);
        naming.Children.Add(_age);
        Grid.SetColumn(naming, 1);
        identity.Children.Add(naming);
        var chevron = new Viewbox { Width = 12, Height = 12, Child = new SymbolIcon(Symbol.Forward) };
        Grid.SetColumn(chevron, 2);
        identity.Children.Add(chevron);
        var state = new Grid { ColumnSpacing = 8 };
        state.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        state.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        state.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _marker = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        state.Children.Add(_marker);
        Grid.SetColumn(Status, 1);
        state.Children.Add(Status);
        Grid.SetColumn(Battery, 2);
        state.Children.Add(Battery);
        var card = new StackPanel { Spacing = 12 };
        card.Children.Add(identity);
        card.Children.Add(state);
        Button = new Button {
            Content = card,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 32,
            Tag = id,
        };
        Button.Click += (_, _) => open(id);
    }

    public void Apply(DeviceView device, AppSnapshot snapshot) {
        ViewElements.SetText(Name, device.DisplayName);
        ViewElements.SetText(Status, device.StatusLine);
        ViewElements.SetText(Battery, device.BatteryPercent?.ToString(CultureInfo.InvariantCulture) is { } battery ? battery + "%" : null);
        ViewElements.SetText(_age, AgeFormatter.Format(device.MetaTemplate, device.StateSince, snapshot.GeneratedAt));
        AutomationProperties.SetName(Button, device.DisplayName + ", " + device.StatusLine);
        _marker.Fill = ViewElements.SemanticBrush(device.Severity, snapshot.Shell);
    }
    public void UpdateAge(DeviceView device, DateTimeOffset now) => ViewElements.SetText(_age, AgeFormatter.Format(device.MetaTemplate, device.StateSince, now));
}
