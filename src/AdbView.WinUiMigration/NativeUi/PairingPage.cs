namespace AdbView.WinUiMigration.NativeUi;

using AdbView.Presentation;
using AdbView.State;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

/// <summary>Stable controls; all semantic text and action availability come from the accepted snapshot.</summary>
internal sealed class PairingPage : IDisposable {
    private readonly PairingDraft _draft = new();
    private readonly TextBlock _heading;
    private readonly TextBlock _detail;
    private readonly TextBlock _instructions;
    private readonly LocalQrImage _qr = new();
    private readonly Action _primary;
    private readonly Action _cancel;
    private bool _updating;
    private bool _disposed;
    private bool _visible;
    private bool _motionEnabled;
    private bool _busy;
    public Grid Root { get; } = new() { RowSpacing = 12 };
    public TextBox CodeInput { get; } = new() {
        MaxLength = 6,
        MinHeight = 40,
        FontSize = 24,
        CharacterSpacing = 300,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        InputScope = new Microsoft.UI.Xaml.Input.InputScope {
            Names = { new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.Number } },
        },
    };
    public Button Primary { get; } = new() { Content = "Pair", MinHeight = 36, MinWidth = 88 };
    public Button Cancel { get; } = new() { Content = "Cancel", MinHeight = 36, MinWidth = 88 };
    public string Code => _draft.Code;
    public LocalQrImage Qr => _qr;
    public ProgressRing BusyIndicator { get; } = new() { Width = 20, Height = 20, IsActive = false, IsIndeterminate = true, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };

    public PairingPage(double textScale, Action primary, Action cancel) {
        _primary = primary; _cancel = cancel;
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var body = new StackPanel { Spacing = 12, Padding = new Thickness(0, 0, 8, 8) };
        _heading = ViewElements.Text(15, textScale); _heading.FontWeight = FontWeights.SemiBold;
        _detail = ViewElements.Text(12, textScale);
        _instructions = ViewElements.Text(12, textScale);
        body.Children.Add(_heading); body.Children.Add(_detail); body.Children.Add(BusyIndicator); body.Children.Add(CodeInput);
        body.Children.Add(_qr.Image); body.Children.Add(_instructions);
        Root.Children.Add(ViewElements.Scroll(body));
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 88 });
        Grid.SetColumn(Cancel, 1); Grid.SetColumn(Primary, 2);
        actions.Children.Add(Cancel); actions.Children.Add(Primary);
        Grid.SetRow(actions, 1); Root.Children.Add(actions);
        CodeInput.TextChanging += OnTextChanging;
        Primary.Click += OnPrimary; Cancel.Click += OnCancel;
        Root.Loaded += OnLoaded; Root.Unloaded += OnUnloaded;
    }

    public void Apply(AppSnapshot snapshot) {
        PairingOperationView? operation = snapshot.Pairing;
        _busy = operation?.Phase == PairingPhase.Pairing || operation is { Phase: PairingPhase.AwaitingCode } && ShellActionAvailability.QrOrigin(operation);
        UpdateBusy();
        _draft.Reconcile(operation);
        SetInput();
        bool typed = operation is not null && !ShellActionAvailability.QrOrigin(operation)
            && (operation.CanSubmitCode || operation.CanRetry);
        CodeInput.Visibility = typed ? Visibility.Visible : Visibility.Collapsed;
        CodeInput.IsEnabled = typed;
        ViewElements.SetText(_heading, operation?.Title ?? "Pair a device over Wi-Fi");
        ViewElements.SetText(_detail, operation?.Detail);
        AutomationProperties.SetName(CodeInput, operation?.Detail ?? "Enter the six-digit code shown on the phone.");
        bool retryQr = operation is { CanRetry: true } && ShellActionAvailability.QrOrigin(operation);
        Primary.Content = retryQr ? "Scan again" : operation?.CanRetry == true ? "Retry" : "Pair";
        bool primaryVisible = typed || retryQr;
        Primary.Visibility = primaryVisible ? Visibility.Visible : Visibility.Collapsed;
        Primary.IsEnabled = operation is not null && (retryQr ? ShellActionAvailability.CanStartQr(snapshot)
            : operation.CanRetry ? ShellActionAvailability.CanStart(snapshot, operation.OfferId)
            : ShellActionAvailability.CanSubmit(snapshot, operation.OfferId, Code));
        Cancel.IsEnabled = operation is null ? ShellActionAvailability.CanCancelQr(snapshot)
            : ShellActionAvailability.CanCancel(snapshot, operation.OfferId);
        Cancel.Content = snapshot.PairingQr?.CancelLabel ?? "Cancel";
        _qr.Apply(operation is null ? snapshot.PairingQr?.SvgDataUri : null);
        ViewElements.SetText(_instructions, operation is null && snapshot.PairingQr is { } qr ? string.Join(Environment.NewLine, qr.Instructions) : null);
    }

    public void ClearDraft() { _draft.Clear(); SetInput(); }
    public void SetPresentation(bool visible, bool motionEnabled) { _visible = visible; _motionEnabled = motionEnabled; UpdateBusy(); }
    private void UpdateBusy() {
        bool active = !_disposed && _visible && _motionEnabled && _busy && Root.IsLoaded;
        BusyIndicator.IsActive = active;
        BusyIndicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnLoaded(object sender, RoutedEventArgs args) => UpdateBusy();
    private void OnUnloaded(object sender, RoutedEventArgs args) { BusyIndicator.IsActive = false; }
    private void SetInput() {
        _updating = true;
        if (CodeInput.Text != _draft.Code) { CodeInput.Text = _draft.Code; }
        _updating = false;
    }
    private void OnTextChanging(TextBox sender, TextBoxTextChangingEventArgs args) {
        if (_updating || _disposed) { return; }
        _draft.Set(sender.Text); SetInput();
        // Action execution revalidates the current snapshot again. No truth is changed here.
        if (Primary.Content is string text && text == "Pair") { Primary.IsEnabled = Code.Length == 6 && CodeInput.IsEnabled; }
    }
    private void OnPrimary(object sender, RoutedEventArgs args) { if (!_disposed) { _primary(); } }
    private void OnCancel(object sender, RoutedEventArgs args) { if (!_disposed) { _cancel(); } }
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        CodeInput.TextChanging -= OnTextChanging; Primary.Click -= OnPrimary; Cancel.Click -= OnCancel;
        Root.Loaded -= OnLoaded; Root.Unloaded -= OnUnloaded; UpdateBusy();
        ClearDraft(); CodeInput.IsEnabled = false; Primary.IsEnabled = false; Cancel.IsEnabled = false;
        _qr.Dispose();
    }
}
