namespace AdbView.WinUiMigration.NativeUi;

using AdbView.NativeUi;
using AdbView.State;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

// Pixels only: Core owns aggregate meaning and transient lifetime; Host owns placement/input.
internal sealed class BubbleWindow : Window, IDisposable {
    public Button OpenButton { get; } = new() {
        Width = 32,
        Height = 32,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
        IsTabStop = false,
        AllowFocusOnInteraction = false,
    };
    private readonly Grid _root = new();
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _marker = new() { Width = 14, Height = 14 };
    private bool _disposed;
    public event Action? OpenRequested;
    public BubbleWindow() {
        Title = "ADB View status";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter) {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false; presenter.IsMinimizable = false; presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        AutomationProperties.SetName(OpenButton, "Open ADB View");
        OpenButton.Content = _marker; _root.Children.Add(OpenButton); Content = _root;
        OpenButton.Click += OnOpen;
    }
    public void Apply(BubbleState state, ShellView shell) {
        _root.RequestedTheme = shell.Theme == ThemeKind.Light ? ElementTheme.Light : ElementTheme.Dark;
        _root.Background = (SolidColorBrush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        SeverityKind severity = state.Color switch {
            BubbleColor.Green => SeverityKind.Positive,
            BubbleColor.Amber => SeverityKind.Attention,
            BubbleColor.Red => SeverityKind.Failure,
            BubbleColor.Blue => SeverityKind.Informational,
            _ => SeverityKind.Neutral,
        };
        _marker.Fill = ViewElements.SemanticBrush(severity, shell);
        ToolTipService.SetToolTip(OpenButton, state.Detail);
    }
    private void OnOpen(object sender, RoutedEventArgs args) { if (!_disposed) { OpenRequested?.Invoke(); } }
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true; OpenButton.Click -= OnOpen; OpenRequested = null;
        ToolTipService.SetToolTip(OpenButton, null); Close(); GC.SuppressFinalize(this);
    }
}
