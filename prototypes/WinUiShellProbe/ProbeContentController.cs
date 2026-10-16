namespace WinUiShellProbe;

using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

// Synthetic UI workload only. Production state/vocabulary remains in the existing app.
internal sealed class ProbeContentController : IDisposable {
    private readonly Grid _host;
    private readonly Button _back;
    private SurfaceMotionProbe? _transition;
    private ProgressRing? _progress;
    private TextBlock? _revisionLabel;
    private ScrollViewer? _listView;
    private ScrollViewer? _detailView;
    private StackPanel? _pairingView;
    private TextBlock? _detailTitle;
    private TextBox? _code;
    private bool _motionEnabled;
    private bool _visible = true;
    private int _selectedDevice;

    public ProbeContentController(Grid host, Button back, bool motionEnabled) {
        _host = host;
        _back = back;
        _motionEnabled = motionEnabled;
        back.Click += OnBack;
        ShowList(false);
    }

    public long Revision { get; private set; } = -1;
    public int DeviceCount { get; private set; } = 1;
    public string Route { get; private set; } = "list";
    public bool IsAnimating => _transition?.IsActive == true;
    public bool IsProgressActive => _progress?.IsActive == true;

    public bool ApplySnapshot(long revision, int deviceCount) {
        if (revision <= Revision) {
            return false;
        }
        Revision = revision;
        bool countChanged = DeviceCount != deviceCount;
        DeviceCount = deviceCount;
        if (countChanged) {
            _listView = null;
        }
        if (_revisionLabel is not null) {
            _revisionLabel.Text = string.Create(CultureInfo.InvariantCulture, $"Synthetic revision {revision}");
        }
        if ((Route == "list" && countChanged) || (Route == "details" && _selectedDevice >= deviceCount)) {
            ShowList(false);
        }
        return true;
    }

    public void ShowDetails(int index) {
        if (index < 0 || index >= DeviceCount) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        if (Route == "details" && _selectedDevice == index) {
            return;
        }
        _selectedDevice = index;
        if (_detailView is null) {
            var body = new StackPanel { Spacing = 12 };
            _detailTitle = Label(string.Empty);
            body.Children.Add(_detailTitle);
            body.Children.Add(Label("Connected · USB — synthetic fixture"));
            body.Children.Add(Label("Serial: fixture-only-serial"));
            body.Children.Add(Label("Android version: fixture"));
            _detailView = new ScrollViewer { Content = body };
        }
        _detailTitle!.Text = string.Create(CultureInfo.InvariantCulture, $"Fixture phone {index + 1}");
        Navigate("details", _detailView, true);
    }

    public void ShowPairing() {
        if (_pairingView is null) {
            _pairingView = new StackPanel { Spacing = 12 };
            _pairingView.Children.Add(Label("Pairing fixture"));
            _code = new TextBox { Header = "Synthetic code", PlaceholderText = "Fixture data only", MaxLength = 6 };
            _pairingView.Children.Add(_code);
            _progress = new ProgressRing { IsActive = false, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left };
            _pairingView.Children.Add(_progress);
            _pairingView.Children.Add(Label("Working — no network operation is running"));
            _revisionLabel = Label(string.Empty);
            _pairingView.Children.Add(_revisionLabel);
        }
        _revisionLabel!.Text = string.Create(CultureInfo.InvariantCulture, $"Synthetic revision {Revision}");
        Navigate("pairing", _pairingView, true);
        _progress!.IsActive = _motionEnabled && _visible;
    }

    public void ShowList(bool animate = true) {
        if (_listView is not null) {
            Navigate("list", _listView, animate);
            return;
        }
        var body = new StackPanel { Spacing = 12 };
        if (DeviceCount == 0) {
            body.Children.Add(Label("No ADB devices — synthetic empty fixture"));
        }
        for (int index = 0; index < DeviceCount; index++) {
            int deviceIndex = index;
            var row = new Button {
                Content = string.Create(CultureInfo.InvariantCulture, $"Fixture phone {index + 1} · Connected · USB"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 48,
            };
            AutomationProperties.SetName(row, (string)row.Content);
            row.Click += (sender, args) => ShowDetails(deviceIndex);
            body.Children.Add(row);
        }
        var pair = new Button { Content = "Open pairing fixture", HorizontalAlignment = HorizontalAlignment.Stretch };
        pair.Click += (sender, args) => ShowPairing();
        body.Children.Add(pair);
        _listView = new ScrollViewer { Content = body };
        Navigate("list", _listView, animate);
    }

    public void SetMotionEnabled(bool enabled) {
        _motionEnabled = enabled;
        if (!enabled) {
            _transition?.Settle(true);
        }
        if (!enabled && _progress is not null) {
            _progress.IsActive = false;
        }
    }

    public void SetPresentationVisible(bool visible) {
        _visible = visible;
        _transition?.Settle(true);
        if (!visible && _code is not null) {
            _code.Text = string.Empty;
        }
        if (_progress is not null) {
            _progress.IsActive = visible && _motionEnabled && Route == "pairing";
        }
    }

    public void Dispose() {
        _back.Click -= OnBack;
        _transition?.Dispose();
        _transition = null;
        if (_progress is not null) {
            _progress.IsActive = false;
        }
    }

    private void OnBack(object sender, RoutedEventArgs args) => ShowList();

    private void Navigate(string route, FrameworkElement view, bool animate) {
        if (Route == route && _host.Children.Count == 1 && ReferenceEquals(_host.Children[0], view)) {
            return;
        }
        _transition?.Dispose();
        if (_progress is not null) {
            _progress.IsActive = false;
        }
        if (Route == "pairing" && _code is not null) {
            _code.Text = string.Empty;
        }
        Route = route;
        _back.Visibility = route == "list" ? Visibility.Collapsed : Visibility.Visible;
        _host.Children.Clear();
        _host.Children.Add(view);
        _transition = new SurfaceMotionProbe(view, horizontal: true, reverse: route == "list");
        if (animate && _motionEnabled && _visible) {
            _transition.Settle(false);
            view.Loaded += OnIncomingLoaded;
        }
        else {
            _transition.Settle(true);
        }
    }

    private void OnIncomingLoaded(object sender, RoutedEventArgs args) {
        if (sender is FrameworkElement view) {
            view.Loaded -= OnIncomingLoaded;
            if (_host.Children.Count == 1 && ReferenceEquals(_host.Children[0], view)) {
                if (_motionEnabled && _visible) {
                    _transition?.SetShown(true);
                }
                else {
                    _transition?.Settle(true);
                }
            }
        }
    }

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
}
