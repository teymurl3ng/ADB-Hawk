namespace AdbView.WinUiMigration.NativeUi;

using System.Globalization;
using AdbView.NativeUi.Shell;
using AdbView.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

internal sealed class DeviceDetails : IDisposable {
    private readonly StackPanel _body = new() { Spacing = 12, Padding = new Thickness(0, 0, 8, 8) };
    private readonly TextBlock _name;
    private readonly TextBlock _status;
    private readonly TextBlock _transport;
    private readonly TextBlock _guidance;
    private readonly TextBlock _model;
    private readonly TextBlock _android;
    private readonly StackPanel _modelField;
    private readonly StackPanel _androidField;
    private readonly TextBlock _battery;
    private readonly TextBlock _age;
    private readonly Dictionary<string, CopyField> _copyFields = new(StringComparer.Ordinal);
    private readonly double _textScale;
    private readonly int _copyStart;
    private readonly Action<string> _copy;
    private CancellationTokenSource? _copyLifetime;
    public ScrollViewer Scroll { get; }
    public IReadOnlyDictionary<string, CopyField> CopyFields => _copyFields;
    public string AgeText => _age.Text;

    public DeviceDetails(double textScale, Action<string> copy) {
        _textScale = textScale;
        _copy = copy;
        _name = AddText(15);
        _status = AddText(12);
        _transport = AddText(12);
        _guidance = AddText(12);
        (_modelField, _model) = AddField("Model");
        (_androidField, _android) = AddField("Android");
        _battery = AddText(12);
        _age = AddText(12);
        _copyStart = _body.Children.Count;
        Scroll = ViewElements.Scroll(_body);
    }

    private TextBlock AddText(double size) {
        TextBlock text = ViewElements.Text(size, _textScale);
        _body.Children.Add(text);
        return text;
    }

    private (StackPanel Root, TextBlock Value) AddField(string label) {
        var field = new StackPanel { Spacing = 4 };
        TextBlock heading = ViewElements.Text(12, _textScale);
        heading.Text = label;
        TextBlock value = ViewElements.Text(12, _textScale);
        field.Children.Add(heading);
        field.Children.Add(value);
        _body.Children.Add(field);
        return (field, value);
    }

    private static void SetField(TextBlock block, StackPanel field, string? value) {
        ViewElements.SetText(block, value);
        field.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Apply(DeviceView device, AppSnapshot snapshot) {
        ViewElements.SetText(_name, device.DisplayName);
        ViewElements.SetText(_status, device.StateLabel);
        ViewElements.SetText(_transport, device.TransportLabel);
        _status.Foreground = ViewElements.SemanticBrush(device.Severity, snapshot.Shell);
        ViewElements.SetText(_guidance, device.Guidance);
        SetField(_model, _modelField, device.Detail.ExactModel);
        AutomationProperties.SetName(_model, "Model: " + device.Detail.ExactModel);
        SetField(_android, _androidField, device.Detail.AndroidVersion);
        AutomationProperties.SetName(_android, "Android: " + device.Detail.AndroidVersion);
        ViewElements.SetText(_battery, device.BatteryPercent?.ToString(CultureInfo.InvariantCulture) is { } battery ? battery + "%" : null);
        ViewElements.SetText(_age, AgeFormatter.Format(device.MetaTemplate, device.StateSince, snapshot.GeneratedAt));
        foreach (string key in _copyFields.Keys.Where(key => !device.Detail.CopyFields.Any(field => field.Key == key)).ToArray()) {
            CopyField removed = _copyFields[key];
            removed.Dispose();
            _body.Children.Remove(removed.Root);
            _copyFields.Remove(key);
        }
        for (int index = 0; index < device.Detail.CopyFields.Length; index++) {
            CopyFieldView field = device.Detail.CopyFields[index];
            if (!_copyFields.TryGetValue(field.Key, out CopyField? control)) {
                control = new CopyField(field.Key, _textScale, _copy);
                _copyFields.Add(field.Key, control);
                _body.Children.Add(control.Root);
            }
            int current = _body.Children.IndexOf(control.Root);
            int target = _copyStart + index;
            if (current != target) { _body.Children.RemoveAt(current); _body.Children.Insert(target, control.Root); }
            if (control.Value.Text != field.Value) {
                ClearFeedback();
            }
            ViewElements.SetText(control.Heading, field.Label);
            ViewElements.SetText(control.Value, field.Value);
            AutomationProperties.SetName(control.Button, "Copy " + field.Label);
        }
    }

    public async Task ShowCopiedAsync(string key) {
        ClearFeedback();
        if (!_copyFields.TryGetValue(key, out CopyField? control)) {
            return;
        }
        var lifetime = new CancellationTokenSource();
        _copyLifetime = lifetime;
        ViewElements.SetText(control.Feedback, "Copied");
        try {
            await Task.Delay(TimeSpan.FromMilliseconds(1200), lifetime.Token).ConfigureAwait(true);
            if (ReferenceEquals(_copyLifetime, lifetime)) {
                ViewElements.SetText(control.Feedback, null);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
            // Navigation, a newer copy or disposal explicitly superseded this feedback.
        }
        finally {
            if (ReferenceEquals(_copyLifetime, lifetime)) {
                _copyLifetime = null;
            }
            lifetime.Dispose();
        }
    }
    public void UpdateAge(DeviceView device, DateTimeOffset now) => ViewElements.SetText(_age, AgeFormatter.Format(device.MetaTemplate, device.StateSince, now));

    public void ClearFeedback() {
        _copyLifetime?.Cancel();
        _copyLifetime = null;
        foreach (CopyField field in _copyFields.Values) {
            ViewElements.SetText(field.Feedback, null);
        }
    }

    public void Dispose() {
        _copyLifetime?.Cancel();
        _copyLifetime?.Dispose();
        _copyLifetime = null;
        ClearFeedback();
        foreach (CopyField field in _copyFields.Values) { field.Dispose(); }
        Scroll.Content = null;
        _body.Children.Clear();
    }

    internal sealed class CopyField : IDisposable {
        private readonly RoutedEventHandler _onClick;
        public StackPanel Root { get; } = new() { Spacing = 4 };
        public TextBlock Heading { get; }
        public TextBlock Value { get; }
        public Button Button { get; }
        public TextBlock Feedback { get; }
        public CopyField(string key, double scale, Action<string> copy) {
            Heading = ViewElements.Text(12, scale);
            Value = ViewElements.Text(12, scale);
            Feedback = ViewElements.Text(12, scale);
            Feedback.Visibility = Visibility.Collapsed;
            Button = new Button { Content = "Copy", MinHeight = 32, FontSize = 12 * scale };
            _onClick = (_, _) => copy(key);
            Button.Click += _onClick;
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(Button);
            actions.Children.Add(Feedback);
            Root.Children.Add(Heading);
            Root.Children.Add(Value);
            Root.Children.Add(actions);
        }
        public void Dispose() {
            Button.IsEnabled = false;
            Button.Click -= _onClick;
            Root.Children.Clear();
        }
    }
}
