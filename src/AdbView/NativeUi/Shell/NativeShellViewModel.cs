namespace AdbView.NativeUi.Shell;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdbView.State;

/// <summary>
/// Stable-shell view model (decision 0015): the layout never rebuilds — regions appear/disappear
/// and device cards update in place. Snapshots arrive via <see cref="ApplySnapshot"/> (marshaled
/// to the shell Dispatcher by AppHost). Pairing/discovery-health actions surface as events that
/// AppHost routes to the existing backend coordinators.
/// </summary>
internal sealed class NativeShellViewModel : INotifyPropertyChanged, IDisposable {
    /// <summary>Cancels a pending success dwell (the shell outlives the view; nothing else to release).</summary>
    public void Dispose() {
        CancellationTokenSource? toCancel;
        lock (_dwellGate) {
            toCancel = _dwellCts;
            _dwellCts = null;
            _dwellArmedGeneration++;
        }

        try {
            toCancel?.Cancel();
        }
        catch (ObjectDisposedException) {
        }

        toCancel?.Dispose();
        _ageTicker.Stop();
    }

    private readonly Dispatcher _dispatcher;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly DispatcherTimer _ageTicker;
    private readonly ObservableCollection<DeviceCardViewModel> _devices;
    private string? _expandedDeviceId;
    private bool _pinned;
    private PairingCardViewModel? _pairingCard;
    private DiscoveryHealthCardViewModel? _discoveryHealth;
    private PairingQrCardViewModel? _pairingQr;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? StartQrRequested;
    public event Action? QrCancelRequested;
    public event Action<bool>? PinChangeRequested;
    public event Action<string, string, string?>? PairingActionRequested;
    public event Action<string>? DiscoveryRepairRequested;
    public event Action? DiscoveryRecheckRequested;

    public NativeShellViewModel(Func<TimeSpan, CancellationToken, Task>? delay = null) {
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _devices = new ObservableCollection<DeviceCardViewModel>();
        Devices = new ReadOnlyObservableCollection<DeviceCardViewModel>(_devices);
        _ageTicker = new DispatcherTimer(DispatcherPriority.Background) {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ageTicker.Tick += (_, _) => RefreshAges();
        _ageTicker.Start();

        StartQrCommand = new RelayCommand(_ => StartQrRequested?.Invoke());
        TogglePinCommand = new RelayCommand(_ => PinChangeRequested?.Invoke(!Pinned));
    }

    public ReadOnlyObservableCollection<DeviceCardViewModel> Devices { get; }

    public ICommand StartQrCommand { get; }

    public ICommand TogglePinCommand { get; }

    public bool Pinned {
        get => _pinned;
        private set {
            if (_pinned == value) {
                return;
            }

            _pinned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PinIconGeometry));
            OnPropertyChanged(nameof(PinForeground));
            OnPropertyChanged(nameof(PinToolTip));
        }
    }

    /// <summary>Applies the authoritative pin state from the snapshot (single source of truth:
    /// user clicks post a SetPinnedEvent; the snapshot round-trip updates this view model).</summary>
    internal void ApplyPinned(bool pinned) {
        if (Pinned == pinned) {
            return;
        }

        // Forensics 2026-10-03: without this line a stale binary was indistinguishable from a
        // silent reflection failure.
        Serilog.Log.Information("Shell pin applied from snapshot: pinned={Pinned}", pinned);
        Pinned = pinned;
    }

    /// <summary>
    /// Re-raises every theme-derived bindable after the app-level token dictionary was swapped
    /// (dark/light). Brush getters read <see cref="Application.Current.Resources"/> at get time;
    /// without this re-raise they would keep the previous theme's brushes.
    /// </summary>
    internal void RefreshThemeResources() {
        OnPropertyChanged(nameof(PinForeground));
        OnPropertyChanged(nameof(PinIconGeometry));
        OnPropertyChanged(nameof(PinToolTip));
        foreach (var device in _devices) {
            device.RefreshThemeResources();
        }

        PairingCard?.RefreshThemeResources();
    }

    public Geometry PinIconGeometry => (Geometry)Application.Current.Resources[Pinned ? "Icon.Pin" : "Icon.PinOff"];

    public SolidColorBrush PinForeground =>
        (SolidColorBrush)Application.Current.Resources[Pinned ? "Win.Accent" : "Win.TextSecondary"];

    public string PinToolTip => Pinned ? "Unpin flyout" : "Pin flyout";

    public bool HasDevices => _devices.Count > 0;

    public bool ShowEmptyState => _devices.Count == 0 && PairingCard is null && PairingQr is null;

    /// <summary>Entry row visible whenever no pairing operation or QR session is active
    /// (decision 0010 "always when idle").</summary>
    public bool ShowQrEntry => PairingCard is null && PairingQr is null;

    public PairingCardViewModel? PairingCard {
        get => _pairingCard;
        private set {
            _pairingCard = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowPairingCard));
            OnPropertyChanged(nameof(ShowQrEntry));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    public bool ShowPairingCard => PairingCard is not null;

    public DiscoveryHealthCardViewModel? DiscoveryHealth {
        get => _discoveryHealth;
        private set {
            _discoveryHealth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDiscoveryHealth));
        }
    }

    public bool ShowDiscoveryHealth => DiscoveryHealth is not null;

    public PairingQrCardViewModel? PairingQr {
        get => _pairingQr;
        private set {
            _pairingQr = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowPairingQr));
            OnPropertyChanged(nameof(ShowQrEntry));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    public bool ShowPairingQr => PairingQr is not null;

    /// <summary>Applies an authoritative snapshot. Must be called on the shell Dispatcher.</summary>
    public void ApplySnapshot(AppSnapshot snapshot) {
        // Pin state is snapshot-derived like everything else: user clicks post a SetPinnedEvent,
        // and this reflection is what updates the icon (the reducer is the single authority).
        ApplyPinned(snapshot.Shell.Pinned);
        ApplyDevices(snapshot.Devices, snapshot.GeneratedAt);
        ApplyPairing(snapshot);
        ApplyPairingQr(snapshot.PairingQr);
        ApplyDiscoveryHealth(snapshot.DiscoveryHealth);
    }

    private void ApplyPairing(AppSnapshot snapshot) {
        var op = snapshot.Pairing;

        if (op is null) {
            // Web parity (pairingPresentation.ts): a SUCCESS card is held 750 ms after the
            // operation clears, so the celebration is actually readable. Any new pairing
            // activity (a new operation or a QR session) cancels the dwell.
            if (PairingCard is not null
                && PairingCard.State == PairingCardState.Success
                && _pairingDwellGeneration == _dwellArmedGeneration) {
                ArmSuccessDwell();
                return;
            }

            // Web parity (adapters.ts): an offer with no active operation renders the
            // offer-phase card; its Pair button raises pairStart with the offer id.
            if (!snapshot.PairingOffers.IsDefaultOrEmpty) {
                var offer = snapshot.PairingOffers[0];
                if (PairingCard is null
                    || PairingCard.OfferId != offer.Id
                    || PairingCard.OfferName != offer.DisplayName) {
                    PairingCard = new PairingCardViewModel(
                        offer.Id, offer.DisplayName, PairingCardState.Offer, offer.Detail, RaisePairingAction);
                }
                else {
                    PairingCard.Update(PairingCardState.Offer, offer.Detail);
                }

                return;
            }

            if (PairingCard is not null) {
                PairingCard = null;
            }

            return;
        }

        CancelSuccessDwell();

        var state = op.Phase switch {
            State.PairingPhase.AwaitingCode => PairingCardState.CodeEntry,
            State.PairingPhase.Pairing => PairingCardState.Progress,
            State.PairingPhase.PairedWaitingForDevice => PairingCardState.Success,
            State.PairingPhase.Failed => PairingCardState.Failure,
            _ => PairingCardState.CodeEntry,
        };

        // Web parity: the operation's display name is authoritative (adapters.ts uses
        // snapshot.pairing.displayName), not an unrelated offer row.
        if (PairingCard is null) {
            PairingCard = new PairingCardViewModel(op.OfferId, op.DisplayName, state, op.Detail, RaisePairingAction);
            return;
        }

        PairingCard.Update(state, op.Detail);
    }

    /// <summary>How long a success pairing card stays after the operation clears
    /// (web parity: pairingPresentation.SuccessDwell, 750 ms).</summary>
    public static readonly TimeSpan SuccessDwell = TimeSpan.FromMilliseconds(750);

    private int _dwellArmedGeneration;
    private int _pairingDwellGeneration;
    private CancellationTokenSource? _dwellCts;

    private void ArmSuccessDwell() {
        int generation;
        CancellationTokenSource? toCancel;
        lock (_dwellGate) {
            generation = ++_dwellArmedGeneration;
            toCancel = _dwellCts;
            _dwellCts = new CancellationTokenSource();
        }

        CancelQuietly(toCancel);
        _ = ClearPairingCardAfterDwellAsync(generation, _dwellCts.Token);
    }

    private async Task ClearPairingCardAfterDwellAsync(int generation, CancellationToken token) {
        try {
            await _delay(SuccessDwell, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            return;
        }
#pragma warning disable CA1031 // R-08: the dwell wait must never crash the shell; log and clear.
        catch (Exception ex) {
            Serilog.Log.Warning(ex, "Shell: success-dwell wait failed; clearing anyway");
        }
#pragma warning restore CA1031

        bool superseded;
        PairingCardViewModel? card;
        lock (_dwellGate) {
            superseded = generation != _dwellArmedGeneration || _dwellCts is null;
            card = PairingCard;
            if (!superseded) {
                _pairingDwellGeneration = _dwellArmedGeneration;
            }
        }

        if (superseded) {
            return;
        }

        // Clear only if the card is still the success card we held (a newer snapshot may have
        // replaced it with an offer card; that one follows its own rules).
        if (card is not null && card.State == PairingCardState.Success) {
            PairingCard = null;
        }
    }

    private void CancelSuccessDwell() {
        CancellationTokenSource? toCancel;
        lock (_dwellGate) {
            toCancel = _dwellCts;
            _dwellCts = null;
        }

        CancelQuietly(toCancel);
    }

    private readonly object _dwellGate = new();

    private static void CancelQuietly(CancellationTokenSource? cts) {
        if (cts is null) {
            return;
        }

        try {
            cts.Cancel();
        }
        catch (ObjectDisposedException) {
        }

        cts.Dispose();
    }

    private void ApplyPairingQr(State.PairingQrView? qr) {
        if (qr is null) {
            if (PairingQr is not null) {
                PairingQr = null;
            }

            return;
        }

        CancelSuccessDwell();

        // Snapshots repeat while a session is live; rebuilding would re-rasterize the QR each
        // time. Same payload => keep the current card (display-only, so equality is the payload).
        if (PairingQr is not null && PairingQr.SourceSvgDataUri == qr.SvgDataUri) {
            return;
        }

        var lines = qr.Instructions;
        PairingQr = new PairingQrCardViewModel(
            qr.SvgDataUri,
            lines.Length > 0 ? lines[0] : string.Empty,
            lines.Length > 1 ? lines[1] : string.Empty,
            qr.CancelLabel,
            () => QrCancelRequested?.Invoke());
    }

    private void ApplyDiscoveryHealth(DiscoveryHealthView? health) {
        if (health is null) {
            if (DiscoveryHealth is not null) {
                DiscoveryHealth = null;
            }

            return;
        }

        var repairs = health.Repairs
            .Select(static r => DiscoveryHealthCardViewModel.RepairButtonFactory(r.Id, r.Label, r.IsAdmin, r.IsRunning, r.RunningLabel))
            .ToImmutableArray();
        var vm = DiscoveryHealthCardViewModel.Create(
            health.Title,
            health.Detail,
            health.Evidence,
            health.ResultLine,
            health.ActionLabel,
            health.CanCheckAgain,
            repairs,
            RaiseRepairAction,
            () => DiscoveryRecheckRequested?.Invoke());
        if (DiscoveryHealth is null || !DiscoveryHealthCardViewModel.SameConfiguration(DiscoveryHealth, vm)) {
            DiscoveryHealth = vm;
        }
    }

    private void RaisePairingAction(string action, string offerId, string? code) =>
        PairingActionRequested?.Invoke(action, offerId, code);

    private void RaiseRepairAction(string repairId) => DiscoveryRepairRequested?.Invoke(repairId);

    private void ApplyDevices(ImmutableArray<DeviceView> devices, DateTimeOffset generatedAt) {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var view in devices) {
            seen.Add(view.Id);
            var existing = FindById(view.Id);
            if (existing is not null) {
                existing.UpdateFrom(view, generatedAt);
                continue;
            }

            var vm = DeviceCardViewModel.Create(
                view,
                generatedAt,
                _expandedDeviceId == view.Id,
                OnDeviceExpandRequested);
            _devices.Add(vm);
        }

        for (var i = _devices.Count - 1; i >= 0; i--) {
            if (!seen.Contains(_devices[i].Id)) {
                var removed = _devices[i];
                removed.PropertyChanged -= OnDeviceCardPropertyChanged;
                removed.Dispose();
                _devices.RemoveAt(i);
                if (_expandedDeviceId == removed.Id) {
                    _expandedDeviceId = null;
                }
            }
        }

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void OnDeviceExpandRequested(DeviceCardViewModel card) {
        _expandedDeviceId = card.IsExpanded ? card.Id : null;
        foreach (var other in _devices) {
            if (other.Id != card.Id && other.IsExpanded) {
                other.Collapse();
            }
        }
    }

    private void OnDeviceCardPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        // Reserved for card→shell feedback (future phases).
    }

    private DeviceCardViewModel? FindById(string id) {
        foreach (var vm in _devices) {
            if (vm.Id == id) {
                return vm;
            }
        }

        return null;
    }

    private void RefreshAges() {
        var now = DateTimeOffset.Now;
        foreach (var device in _devices) {
            device.RefreshAge(now);
        }
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One device card. Updated in place by <see cref="NativeShellViewModel.ApplyDevices"/>.</summary>
internal sealed class DeviceCardViewModel : INotifyPropertyChanged, IDisposable {
    private readonly Action<DeviceCardViewModel> _notifyExpand;
    private string _copiedField = string.Empty;
    private string _metaTemplate = string.Empty;
    private DateTimeOffset _stateSince;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DeviceCardViewModel(
        DeviceView view,
        DateTimeOffset generatedAt,
        bool isExpanded,
        Action<DeviceCardViewModel> notifyExpand,
        Action<string>? setClipboard = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) {
        Id = view.Id;
        _notifyExpand = notifyExpand;
        IsExpanded = isExpanded;
        _setClipboard = setClipboard ?? (value => Clipboard.SetText(value));
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        ApplyView(view, generatedAt);
        ToggleExpandCommand = new RelayCommand(_ => NotifyExpand());
        CopyAddressCommand = new RelayCommand(
            _ => Copy(Address!),
            _ => !string.IsNullOrEmpty(Address));
        CopySerialCommand = new RelayCommand(
            _ => Copy(Serial!),
            _ => !string.IsNullOrEmpty(Serial));
    }

    private readonly Action<string> _setClipboard;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>How long the "Copied" badge replaces the copy icon (the web's 1200 ms hold;
    /// the native card never cleared it, so the copy button stayed hidden forever).</summary>
    public static readonly TimeSpan CopiedBadgeHold = TimeSpan.FromMilliseconds(1200);

    private int _copyGeneration;

    private CancellationTokenSource? _copyCts;

    public string Id { get; }

    public string DisplayName { get; private set; } = string.Empty;

    public string AgeText { get; private set; } = string.Empty;

    public DeviceStateKind State { get; private set; }

    public SeverityKind Severity { get; private set; }

    public TransportKind Transport { get; private set; }

    public string TransportLabel { get; private set; } = string.Empty;

    public int? BatteryPercent { get; private set; }

    public string? Address { get; private set; }

    public string? Serial { get; private set; }

    public string? Model { get; private set; }

    public string? AndroidVersion { get; private set; }

    public bool IsExpanded { get; private set; }

    public ICommand ToggleExpandCommand { get; }

    public ICommand CopyAddressCommand { get; }

    public ICommand CopySerialCommand { get; }

    public static DeviceCardViewModel Create(
        DeviceView view,
        DateTimeOffset generatedAt,
        bool isExpanded,
        Action<DeviceCardViewModel> notifyExpand,
        Action<string>? setClipboard = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) {
        return new DeviceCardViewModel(view, generatedAt, isExpanded, notifyExpand, setClipboard, delay);
    }

    public void UpdateFrom(DeviceView view, DateTimeOffset generatedAt) => ApplyView(view, generatedAt);

    private void ApplyView(DeviceView view, DateTimeOffset generatedAt) {
        DisplayName = view.DisplayName;
        _metaTemplate = view.MetaTemplate;
        _stateSince = view.StateSince;
        State = view.State;
        Severity = view.Severity;
        StateLabelText = view.StateLabel;
        Transport = view.Transport;
        TransportLabel = view.TransportLabel;
        BatteryPercent = view.BatteryPercent;
        Model = view.Detail.ExactModel;
        AndroidVersion = view.Detail.AndroidVersion;
        Address = view.Transport == TransportKind.WiFi ? view.Detail.Address : null;
        Serial = view.Detail.Serial;
        RefreshAge(generatedAt);
        RaiseChanged();
    }

    public void NotifyExpand() {
        IsExpanded = !IsExpanded;
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(ChevronIconGeometry));
        OnPropertyChanged(nameof(ChevronAngle));
        OnPropertyChanged(nameof(ChevronToolTip));
        OnPropertyChanged(nameof(CardCursor));
        _notifyExpand(this);
    }

    public void Collapse() {
        if (!IsExpanded) {
            return;
        }

        IsExpanded = false;
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(ChevronIconGeometry));
        OnPropertyChanged(nameof(ChevronAngle));
        OnPropertyChanged(nameof(ChevronToolTip));
        OnPropertyChanged(nameof(CardCursor));
    }

    /// <summary>DESIGN 948: the chevron's target rotation (animated over Motion.State by the
    /// card XAML; the geometry itself no longer swaps, so the rotation reads as one motion).</summary>
    public double ChevronAngle => IsExpanded ? 180 : 0;

    public void RefreshAge(DateTimeOffset now) => RefreshAge(_metaTemplate, _stateSince, now);

    private void RefreshAge(string template, DateTimeOffset since, DateTimeOffset now) {
        var raw = AgeFormatter.Format(template, since, now);
        if (AgeText != raw) {
            AgeText = raw;
            OnPropertyChanged(nameof(AgeText));
        }
    }

    /// <summary>Backend-rendered state label (DeviceView.StateLabel from Vocabulary) — the
    /// authoritative pill wording. The earlier severity-derived text was wrong for half the
    /// states (an offline device read "Error", recovery read "Unknown").</summary>
    public string StateLabelText { get; private set; } = string.Empty;

    public string StatePillText => string.IsNullOrEmpty(StateLabelText)
        ? Severity switch {
            SeverityKind.Positive => "Connected",
            _ => "Unknown",
        }
        : StateLabelText;

    public SolidColorBrush StatePillBackground => Severity switch {
        SeverityKind.Positive => (SolidColorBrush)Application.Current.Resources["Win.GreenSurface"],
        SeverityKind.Attention => (SolidColorBrush)Application.Current.Resources["Win.AmberSurface"],
        SeverityKind.Failure => (SolidColorBrush)Application.Current.Resources["Win.RedSurface"],
        SeverityKind.Informational => (SolidColorBrush)Application.Current.Resources["Win.SkySurface"],
        _ => Brushes.Transparent,
    };

    public SolidColorBrush StatePillBorder => Severity switch {
        SeverityKind.Positive => (SolidColorBrush)Application.Current.Resources["Win.GreenBorder"],
        SeverityKind.Attention => (SolidColorBrush)Application.Current.Resources["Win.AmberBorder"],
        SeverityKind.Failure => (SolidColorBrush)Application.Current.Resources["Win.RedBorder"],
        SeverityKind.Informational => (SolidColorBrush)Application.Current.Resources["Win.SkyBorder"],
        _ => (SolidColorBrush)Application.Current.Resources["Win.BorderSubtle"],
    };

    public SolidColorBrush StatePillDot => Severity switch {
        SeverityKind.Positive => (SolidColorBrush)Application.Current.Resources["Win.Green"],
        SeverityKind.Attention => (SolidColorBrush)Application.Current.Resources["Win.Amber"],
        SeverityKind.Failure => (SolidColorBrush)Application.Current.Resources["Win.Red"],
        SeverityKind.Informational => (SolidColorBrush)Application.Current.Resources["Win.Sky"],
        _ => (SolidColorBrush)Application.Current.Resources["Win.TextMuted"],
    };

    public SolidColorBrush StatePillForeground => Severity switch {
        SeverityKind.Positive => (SolidColorBrush)Application.Current.Resources["Win.GreenText"],
        SeverityKind.Attention => (SolidColorBrush)Application.Current.Resources["Win.AmberText"],
        SeverityKind.Failure => (SolidColorBrush)Application.Current.Resources["Win.RedText"],
        SeverityKind.Informational => (SolidColorBrush)Application.Current.Resources["Win.SkyText"],
        _ => (SolidColorBrush)Application.Current.Resources["Win.TextSecondary"],
    };

    public bool HasTransport => Transport != TransportKind.Unknown;

    public Geometry TransportIconGeometry =>
        (Geometry)Application.Current.Resources[Transport == TransportKind.WiFi ? "Icon.Wifi" : "Icon.Usb"];

    public bool HasBattery => BatteryPercent is not null;

    public string BatteryText => BatteryPercent is null
        ? string.Empty
        : BatteryPercent.Value.ToString(CultureInfo.InvariantCulture) + "%";

    public bool ShowDetails => IsExpanded;

    public Geometry ChevronIconGeometry =>
        (Geometry)Application.Current.Resources[IsExpanded ? "Icon.ChevronUp" : "Icon.ChevronDown"];

    public string ChevronToolTip => IsExpanded ? "Collapse device details" : "Expand device details";

    public string CardCursor => IsExpanded ? "Arrow" : "Hand";

    public bool HasModel => !string.IsNullOrEmpty(Model);

    public bool HasAndroid => !string.IsNullOrEmpty(AndroidVersion);

    public bool HasAddress => !string.IsNullOrEmpty(Address);

    public bool HasSerial => !string.IsNullOrEmpty(Serial);

    public bool ShowAddressCopy => _copiedField != "address";

    public bool ShowSerialCopy => _copiedField != "serial";

    public bool ShowAddressCopied => _copiedField == "address";

    public bool ShowSerialCopied => _copiedField == "serial";

    private void Copy(string value) {
        try {
            _setClipboard(value);
        }
#pragma warning disable CA1031 // R-08: a clipboard-in-use failure is logged and must not show a false "Copied".
        catch (Exception ex) {
            Serilog.Log.Warning(ex, "Device card: clipboard write failed; badge not shown");
            return;
        }
#pragma warning restore CA1031

        _copiedField = value == Address ? "address" : "serial";
        RaiseCopyStates();

        // Generation-guarded one-shot clear (the StatusBubblePresenter pattern): a second copy
        // supersedes the first's pending clear, and disposal cancels it outright.
        int generation;
        CancellationTokenSource? toCancel;
        lock (_copyGate) {
            generation = ++_copyGeneration;
            toCancel = _copyCts;
            _copyCts = new CancellationTokenSource();
        }

        CancelQuietly(toCancel);
        _ = ClearCopiedBadgeAfterHoldAsync(generation, _copyCts.Token);
    }

    private readonly object _copyGate = new();

    private async Task ClearCopiedBadgeAfterHoldAsync(int generation, CancellationToken token) {
        try {
            await _delay(CopiedBadgeHold, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            return;
        }
#pragma warning disable CA1031 // R-08: the clear wait must never crash the card; log and clear.
        catch (Exception ex) {
            Serilog.Log.Warning(ex, "Device card: copied-badge clear wait failed; clearing anyway");
        }
#pragma warning restore CA1031

        lock (_copyGate) {
            if (generation != _copyGeneration) {
                return; // superseded by a newer copy
            }

            _copiedField = string.Empty;
            _copyCts = null;
        }

        RaiseCopyStates();
    }

    /// <summary>Cancels a pending copied-badge clear when the card is removed from the list.</summary>
    public void Dispose() {
        CancellationTokenSource? toCancel;
        lock (_copyGate) {
            toCancel = _copyCts;
            _copyCts = null;
            _copyGeneration++;
        }

        CancelQuietly(toCancel);
    }

    private static void CancelQuietly(CancellationTokenSource? cts) {
        if (cts is null) {
            return;
        }

        try {
            cts.Cancel();
        }
        catch (ObjectDisposedException) {
        }

        cts.Dispose();
    }

    private void RaiseCopyStates() {
        OnPropertyChanged(nameof(ShowAddressCopy));
        OnPropertyChanged(nameof(ShowAddressCopied));
        OnPropertyChanged(nameof(ShowSerialCopy));
        OnPropertyChanged(nameof(ShowSerialCopied));
    }

    /// <summary>Re-raises theme-derived brushes after a dark/light dictionary swap.</summary>
    internal void RefreshThemeResources() {
        OnPropertyChanged(nameof(StatePillBackground));
        OnPropertyChanged(nameof(StatePillBorder));
        OnPropertyChanged(nameof(StatePillDot));
        OnPropertyChanged(nameof(StatePillForeground));
        OnPropertyChanged(nameof(TransportIconGeometry));
        OnPropertyChanged(nameof(ChevronIconGeometry));
    }

    private void RaiseChanged() {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(StatePillText));
        OnPropertyChanged(nameof(StatePillBackground));
        OnPropertyChanged(nameof(StatePillBorder));
        OnPropertyChanged(nameof(StatePillDot));
        OnPropertyChanged(nameof(StatePillForeground));
        OnPropertyChanged(nameof(TransportLabel));
        OnPropertyChanged(nameof(TransportIconGeometry));
        OnPropertyChanged(nameof(HasTransport));
        OnPropertyChanged(nameof(BatteryText));
        OnPropertyChanged(nameof(HasBattery));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(AndroidVersion));
        OnPropertyChanged(nameof(HasAndroid));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(HasAddress));
        OnPropertyChanged(nameof(Serial));
        OnPropertyChanged(nameof(HasSerial));
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Age text formatting — exact port of ui/src/bridge/age.ts.</summary>
internal static class AgeFormatter {
    public static string Format(string template, DateTimeOffset since, DateTimeOffset now) {
        var delta = now - since;
        if (delta < TimeSpan.Zero) {
            delta = TimeSpan.Zero;
        }

        string age;
        if (delta.TotalSeconds < 5) {
            age = "Just now";
        }
        else if (delta.TotalSeconds < 60) {
            age = string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalSeconds}s ago");
        }
        else if (delta.TotalMinutes < 60) {
            age = delta.TotalMinutes == 2
                ? "2 minutes ago"
                : string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalMinutes}m ago");
        }
        else {
            age = string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalHours}h ago");
        }

        return template.Replace("{age}", age, StringComparison.Ordinal);
    }
}

/// <summary>Minimal relay command.</summary>
internal sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand {
    public event EventHandler? CanExecuteChanged {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}
