namespace AdbView.WinUiMigration.NativeUi;

using AdbView.Presentation;
using AdbView.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

/// <summary>Keyed native actions retain identity/focus on metadata-only revisions.</summary>
internal sealed class DiscoveryCard {
    private readonly TextBlock _heading;
    private readonly TextBlock _detail;
    private readonly TextBlock _result;
    private readonly StackPanel _evidence = new() { Spacing = 4 };
    private readonly StackPanel _repairs = new() { Spacing = 8 };
    private readonly double _textScale;
    private readonly Action<string> _repair;
    private string[] _evidenceText = [];
    public StackPanel Root { get; } = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    public Dictionary<string, Button> RepairButtons { get; } = new(StringComparer.Ordinal);
    public Button CheckButton { get; } = new() { MinHeight = 36 };

    public DiscoveryCard(double textScale, Action check, Action<string> repair) {
        _textScale = textScale; _repair = repair;
        _heading = ViewElements.Text(13.5, textScale); _detail = ViewElements.Text(12, textScale); _result = ViewElements.Text(12, textScale);
        Root.Children.Add(_heading); Root.Children.Add(_detail); Root.Children.Add(_evidence);
        Root.Children.Add(_result); Root.Children.Add(_repairs); Root.Children.Add(CheckButton);
        CheckButton.Click += (_, _) => check();
    }
    public void Apply(AppSnapshot snapshot) {
        if (!ShellActionAvailability.Healthy(snapshot) || snapshot.DiscoveryHealth is not { } health) {
            Root.Visibility = Visibility.Collapsed; CheckButton.IsEnabled = false;
            foreach (Button button in RepairButtons.Values) { button.IsEnabled = false; }
            return;
        }
        Root.Visibility = Visibility.Visible;
        ViewElements.SetText(_heading, health.Title); ViewElements.SetText(_detail, health.Detail); ViewElements.SetText(_result, health.ResultLine);
        if (!_evidenceText.SequenceEqual(health.Evidence)) {
            _evidenceText = health.Evidence.ToArray(); _evidence.Children.Clear();
            foreach (string line in health.Evidence) { var text = ViewElements.Text(12, _textScale); text.Text = line; _evidence.Children.Add(text); }
        }
        foreach (string id in RepairButtons.Keys.Where(id => !health.Repairs.Any(repair => repair.Id == id)).ToArray()) {
            Button removed = RepairButtons[id]; removed.IsEnabled = false; _repairs.Children.Remove(removed); RepairButtons.Remove(id);
        }
        for (int index = 0; index < health.Repairs.Length; index++) {
            DiscoveryRepairCardAction repair = health.Repairs[index];
            if (!RepairButtons.TryGetValue(repair.Id, out Button? button)) {
                button = new Button { MinHeight = 36 };
                string id = repair.Id; button.Click += (_, _) => _repair(id);
                RepairButtons.Add(id, button);
            }
            string name = repair.IsRunning ? repair.RunningLabel : repair.Label;
            if (AutomationProperties.GetName(button) != name || button.Tag is not bool admin || admin != repair.IsAdmin) {
                AutomationProperties.SetName(button, name); button.Tag = repair.IsAdmin;
                if (repair.IsAdmin) {
                    var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    content.Children.Add(new FontIcon { Glyph = "\uEA18", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14 });
                    var text = ViewElements.Text(12, _textScale); text.Text = name; content.Children.Add(text); button.Content = content;
                }
                else { button.Content = name; }
            }
            button.IsEnabled = ShellActionAvailability.CanRepair(snapshot, repair.Id);
            int existing = _repairs.Children.IndexOf(button);
            if (existing != index) {
                if (existing >= 0) { _repairs.Children.RemoveAt(existing); }
                _repairs.Children.Insert(index, button);
            }
        }
        if (CheckButton.Content is not string current || current != health.ActionLabel) { CheckButton.Content = health.ActionLabel; }
        CheckButton.IsEnabled = ShellActionAvailability.CanCheck(snapshot);
    }
}
