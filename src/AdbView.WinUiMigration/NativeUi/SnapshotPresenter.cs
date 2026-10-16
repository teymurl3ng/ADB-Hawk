namespace AdbView.WinUiMigration.NativeUi;

using System.Collections.Immutable;
using AdbView.Host;
using AdbView.Presentation;
using AdbView.State;
using AdbView.WinUiMigration.NativeUi.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

internal sealed partial class SnapshotPresenter : IDisposable {
    private readonly Grid _pageHost;
    private readonly Button _back;
    private readonly Button _pin;
    private readonly FrameworkElement _root;
    private readonly Func<string, bool> _copyText;
    private readonly IShellActions _actions;
    private readonly DiscoveryCard _discovery;
    private readonly MotionPolicy _motionPolicy;
    private readonly Func<DateTimeOffset> _clock;
    private bool _presentationVisible;
    public ContentTransitionController ContentMotion { get; }
    public VisibleAgeTicker AgeTicker { get; }
    private readonly StackPanel _listBody = new() { Spacing = 12, Padding = new Thickness(0, 0, 8, 8) };
    private readonly StackPanel _messages = new() { Spacing = 8 };
    private readonly StackPanel _rowsPanel = new() { Spacing = 12 };
    private readonly Dictionary<string, DeviceRow> _rows = new(StringComparer.Ordinal);
    private readonly ListNavigation _navigation = new();
    private readonly double _textScale;
    private bool _disposed;
    private DeviceDetails? _details;
    private long _restoreGeneration;
    private RoutedEventHandler? _restoreLoaded;
    public AppSnapshot? Snapshot { get; private set; }
    public ScrollViewer ListScroll { get; }
    public IReadOnlyDictionary<string, DeviceRow> Rows => _rows;
    public DeviceDetails? Details => _details;
    public string? DetailsId => _navigation.DetailsId;
    public int AppliedCount { get; private set; }
    public event Action? PinRequested;
    public event Action<string, string>? CopyRequested;

    public SnapshotPresenter(FrameworkElement root, Grid pageHost, Button back, Button pin, double textScale, Func<string, bool> copyText, IShellActions actions, MotionPolicy motionPolicy, Func<DateTimeOffset> clock) {
        _root = root;
        _pageHost = pageHost;
        _back = back;
        _pin = pin;
        _textScale = textScale;
        _copyText = copyText;
        _actions = actions;
        _motionPolicy = motionPolicy; _clock = clock;
        ContentMotion = new ContentTransitionController(pageHost, motionPolicy);
        AgeTicker = new VisibleAgeTicker(root.DispatcherQueue, UpdateAges);
        _motionPolicy.Changed += OnMotionPolicyChanged;
        _discovery = new DiscoveryCard(textScale, CheckDiscovery, Repair);
        _listBody.Children.Add(_messages);
        _listBody.Children.Add(_rowsPanel);
        _listBody.Children.Add(_discovery.Root);
        ListScroll = ViewElements.Scroll(_listBody);
        ContentMotion.Navigate("list", ListScroll, animate: false);
        _back.Click += OnBack;
        _pin.Click += OnPin;
    }

    public void Apply(AppSnapshot snapshot) {
        if (_disposed || Snapshot is { } current && snapshot.Revision <= current.Revision) {
            return;
        }
        if (Snapshot is { } prior && (prior.Shell.Theme != snapshot.Shell.Theme || prior.Shell.ForcedColors != snapshot.Shell.ForcedColors)) {
            ContentMotion.Settle();
        }
        Snapshot = snapshot;
        _motionPolicy.SetSnapshotReduced(snapshot.Shell.ReducedMotion);
        AppliedCount++;
        ElementTheme theme = snapshot.Shell.Theme == ThemeKind.Light ? ElementTheme.Light : ElementTheme.Dark;
        if (_root.RequestedTheme != theme) { _root.RequestedTheme = theme; }
        if (_root is Grid background) {
            background.Background = snapshot.Shell.ForcedColors
                ? (SolidColorBrush)Application.Current.Resources["LayerFillColorDefaultBrush"]
                : new SolidColorBrush(snapshot.Shell.Theme == ThemeKind.Light
                    ? Windows.UI.Color.FromArgb(255, 243, 244, 246) : Windows.UI.Color.FromArgb(255, 32, 35, 41));
        }
        string pinName = snapshot.Shell.Pinned ? "Unpin flyout" : "Pin flyout";
        if (AutomationProperties.GetName(_pin) != pinName || _pin.Content is null) {
            AutomationProperties.SetName(_pin, pinName);
            ToolTipService.SetToolTip(_pin, pinName);
            _pin.Content = new SymbolIcon(snapshot.Shell.Pinned ? Symbol.UnPin : Symbol.Pin);
        }
        var ids = snapshot.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray()) {
            _rowsPanel.Children.Remove(_rows[id].Button);
            _rows.Remove(id);
        }
        for (int index = 0; index < snapshot.Devices.Length; index++) {
            DeviceView device = snapshot.Devices[index];
            if (!_rows.TryGetValue(device.Id, out DeviceRow? row)) {
                row = new DeviceRow(device.Id, _textScale, OpenDevice);
                _rows.Add(device.Id, row);
            }
            row.Apply(device, snapshot);
            int existing = _rowsPanel.Children.IndexOf(row.Button);
            if (existing != index) {
                if (existing >= 0) {
                    _rowsPanel.Children.RemoveAt(existing);
                }
                _rowsPanel.Children.Insert(index, row.Button);
            }
        }
        // Only messages may rebuild. Device rows and open detail controls retain identity.
        _messages.Children.Clear();
        if (snapshot.Health.Kind != GlobalHealthKind.Healthy) {
            AddMessage(snapshot.Health.Title, 15);
            AddMessage(snapshot.Health.Detail);
            AddMessage(snapshot.Health.Hint);
            _rowsPanel.Visibility = Visibility.Collapsed;
        }
        else {
            _rowsPanel.Visibility = Visibility.Visible;
            foreach (SubsystemWarningView warning in snapshot.Warnings) {
                AddMessage(warning.Title, 13.5);
                AddMessage(warning.Detail);
            }
            AddMessage(snapshot.DeviceCountLabel);
            if (snapshot.Devices.IsEmpty && snapshot.Pairing is null && snapshot.PairingOffers.IsEmpty && snapshot.PairingQr is null) {
                AddMessage("No ADB devices", 15);
                AddMessage("Connect a device using USB or wireless debugging.");
                AddMessage("Wireless discovery active");
            }
            if (snapshot.Pairing is { } operation) {
                AddAction(operation.Title, OpenPairing);
                AddMessage(operation.Detail);
            }
            foreach (PairingOfferView offer in snapshot.PairingOffers) {
                AddMessage(offer.Heading, 13.5);
                AddMessage(offer.DisplayName);
                AddMessage(offer.Detail);
                AddAction(offer.ActionLabel, () => StartPairing(offer.Id), ShellActionAvailability.CanStart(snapshot, offer.Id));
            }
            if (snapshot.PairingQr is not null) { AddAction("Pair a device over Wi-Fi", OpenPairing); }
            else if (snapshot.Pairing is null) { AddAction("Pair a device over Wi-Fi", StartQr, ShellActionAvailability.CanStartQr(snapshot)); }
        }
        _discovery.Apply(snapshot);
        if (_navigation.Reconcile(snapshot)) {
            LeaveDetails();
            RestoreList();
        }
        else if (DetailsId is { } detailsId) {
            _details!.Apply(snapshot.Devices.Single(device => device.Id == detailsId), snapshot);
        }
        ReconcilePairing(snapshot);
        UpdatePresentationWork();
    }

    private void AddMessage(string? value, double size = 12) {
        if (string.IsNullOrEmpty(value)) {
            return;
        }
        TextBlock block = ViewElements.Text(size, _textScale);
        block.Text = value;
        _messages.Children.Add(block);
    }

    public void OpenDevice(string id) {
        if (_disposed || DetailsId == id || Snapshot is not { } snapshot || !_rows.TryGetValue(id, out DeviceRow? row)) {
            return;
        }
        if (DetailsId is null) {
            SaveListBookmark(id);
        }
        if (!_navigation.Open(id, snapshot)) {
            return;
        }
        LeavePairing();
        InvalidateRestore();
        _details?.Dispose();
        _details = new DeviceDetails(_textScale, key => RequestCopy(id, key));
        _details.Apply(snapshot.Devices.Single(device => device.Id == id), snapshot);
        ContentMotion.Navigate("details:" + id, _details.Scroll);
        UpdatePresentationWork();
        _back.Visibility = Visibility.Visible;
        if (_back.IsLoaded) {
            _back.Focus(FocusState.Programmatic);
        }
    }

    private void SaveListBookmark(string? focusedId) {
        _root.UpdateLayout();
        var ordered = Snapshot!.Devices.Select(device => device.Id).ToImmutableArray();
        string? anchor = ordered.FirstOrDefault(id => Top(_rows[id].Button) + _rows[id].Button.ActualHeight > ListScroll.VerticalOffset);
        int index = anchor is null ? 0 : ordered.IndexOf(anchor);
        _navigation.Remember(new ListBookmark(anchor, anchor is null ? 0 : Math.Max(0, ListScroll.VerticalOffset - Top(_rows[anchor].Button)),
            index, focusedId, focusedId is null ? index : ordered.IndexOf(focusedId)));
    }

    private double Top(FrameworkElement element) => element.TransformToVisual(_listBody).TransformPoint(new Point(0, 0)).Y;

    public void Back() {
        if (Pairing is not null) {
            LeavePairing(); LeaveDetails(animate: true); RestoreList(); return;
        }
        if (DetailsId is null) {
            return;
        }
        _navigation.Back();
        LeaveDetails(animate: true);
        RestoreList();
    }

    private void LeaveDetails(bool animate = false) {
        _details?.Dispose();
        _details = null;
        ContentMotion.Navigate("list", ListScroll, reverse: true, animate: animate);
        UpdatePresentationWork();
        _back.Visibility = Visibility.Collapsed;
    }

    private void RestoreList() {
        InvalidateRestore();
        long generation = _restoreGeneration;
        if (!ListScroll.IsLoaded) {
            _restoreLoaded = (_, _) => {
                ListScroll.Loaded -= _restoreLoaded;
                _restoreLoaded = null;
                RestoreLoadedList(generation);
            };
            ListScroll.Loaded += _restoreLoaded;
        }
        else {
            RestoreLoadedList(generation);
        }
    }

    private void InvalidateRestore() {
        _restoreGeneration++;
        if (_restoreLoaded is not null) {
            ListScroll.Loaded -= _restoreLoaded;
            _restoreLoaded = null;
        }
    }

    private void RestoreLoadedList(long generation) {
        if (_disposed || generation != _restoreGeneration || DetailsId is not null || Snapshot is not { } snapshot) { return; }
        _root.UpdateLayout();
        ListBookmark bookmark = _navigation.Resolve(snapshot.Devices.Select(device => device.Id).ToImmutableArray());
        if (snapshot.Health.Kind == GlobalHealthKind.Healthy && bookmark.FocusId is { } focus && _rows.TryGetValue(focus, out DeviceRow? row)) {
            row.Button.Focus(FocusState.Programmatic);
        }
        else {
            ListScroll.Focus(FocusState.Programmatic);
        }
        // Native focus can enqueue a bring-into-view request. Restore the keyed offset
        // after that request, once the reattached scroll surface has loaded.
        if (!_root.DispatcherQueue.TryEnqueue(() => {
            if (_disposed || generation != _restoreGeneration || DetailsId is not null || Snapshot is not { } latest) { return; }
            _root.UpdateLayout();
            ListBookmark current = _navigation.Resolve(latest.Devices.Select(device => device.Id).ToImmutableArray());
            if (current.AnchorId is { } anchor && _rows.TryGetValue(anchor, out DeviceRow? anchoredRow)) {
                ListScroll.ChangeView(null, Top(anchoredRow.Button) + current.Offset, null, true);
            }
        })) {
            throw new InvalidOperationException("List focus/scroll restoration dispatcher rejected delivery.");
        }
    }

    public void RequestCopy(string key) {
        if (DetailsId is { } id) { RequestCopy(id, key); }
    }

    public async void RequestCopy(string id, string key) {
        if (_disposed || DetailsId != id || Snapshot is not { } snapshot
            || snapshot.Health.Kind != GlobalHealthKind.Healthy) {
            return;
        }
        DeviceView? device = snapshot.Devices.FirstOrDefault(device => device.Id == id);
        CopyFieldView? field = device?.Detail.CopyFields.FirstOrDefault(field => field.Key == key);
        if (field is null) {
            return;
        }
        _details?.ClearFeedback();
        CopyRequested?.Invoke(id, key);
        if (_copyText(field.Value) && _details is { } details) {
            await details.ShowCopiedAsync(key).ConfigureAwait(true);
        }
    }

    public void RequestPin() {
        if (!_disposed) { PinRequested?.Invoke(); }
    }
    private void OnPin(object sender, RoutedEventArgs args) => RequestPin();
    private void OnBack(object sender, RoutedEventArgs args) => Back();

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _motionPolicy.Changed -= OnMotionPolicyChanged;
        AgeTicker.Dispose(); ContentMotion.Dispose();
        InvalidateRestore();
        _details?.Dispose();
        _details = null;
        LeavePairing();
        _back.Click -= OnBack;
        _pin.Click -= OnPin;
        _pageHost.Children.Clear();
        _rowsPanel.Children.Clear();
        _rows.Clear();
    }

    public Control FirstFocus => Pairing is { } pairing ? pairing.CodeInput.Visibility == Visibility.Visible ? pairing.CodeInput : _back : DetailsId is not null ? _back
        : Snapshot is { Health.Kind: GlobalHealthKind.Healthy } && _rows.Count > 0 ? _rows[Snapshot.Devices[0].Id].Button : ListScroll;

    public bool HasText(string? text) => text is null || Descendants(_pageHost).OfType<TextBlock>().Any(block => block.Text == text && block.IsLoaded && IsVisibleInTree(block));

    private static bool IsVisibleInTree(DependencyObject element) {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current)) {
            if (current is UIElement { Visibility: not Visibility.Visible }) { return false; }
        }
        return true;
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root) {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) {
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, index))) {
                yield return child;
            }
        }
    }
}
