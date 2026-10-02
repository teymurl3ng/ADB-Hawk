namespace AdbView.NativeUi.Shell;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdbView.State;

/// <summary>
/// Stable-shell view model (decision 0015): the layout never rebuilds — regions appear/disappear
/// and device cards update in place. Snapshots arrive via <see cref="ApplySnapshot"/> (marshaled
/// to the shell Dispatcher by AppHost).
/// </summary>
internal sealed class NativeShellViewModel : INotifyPropertyChanged {
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ageTicker;
    private string? _expandedDeviceId;
    private bool _pinned;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? StartQrRequested;
    public event Action<bool>? PinChangeRequested;

    public NativeShellViewModel() {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Devices = new ObservableCollection<DeviceCardViewModel>();
        _ageTicker = new DispatcherTimer(DispatcherPriority.Background) {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ageTicker.Tick += (_, _) => RefreshAges();
        _ageTicker.Start();

        StartQrCommand = new RelayCommand(_ => StartQrRequested?.Invoke());
        TogglePinCommand = new RelayCommand(_ => PinChangeRequested?.Invoke(!Pinned));
    }

    public ObservableCollection<DeviceCardViewModel> Devices { get; }

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
            OnPropertyChanged(nameof(PinToolTip));
        }
    }

    public Geometry PinIconGeometry => (Geometry)Application.Current.Resources[Pinned ? "Icon.Pin" : "Icon.PinOff"];

    public string PinToolTip => Pinned ? "Unpin flyout" : "Pin flyout";

    public bool HasDevices => Devices.Count > 0;

    public bool ShowEmptyState => Devices.Count == 0;

    /// <summary>I1 scope: the QR entry row is always visible when the flyout has content
    /// (the QR session card itself lands in Phase I2).</summary>
#pragma warning disable CA1822 // Constant in I1 by design; becomes instance-driven in I2.
    public bool ShowQrEntry => true;
#pragma warning restore CA1822

    /// <summary>Applies an authoritative snapshot. Must be called on the shell Dispatcher.</summary>
    public void ApplySnapshot(AppSnapshot snapshot) {
        ApplyDevices(snapshot.Devices, snapshot.GeneratedAt);
    }

    private void ApplyDevices(ImmutableArray<DeviceView> devices, DateTimeOffset generatedAt) {
        // Update in place by Id (stable identity): existing cards mutate, new ones append,
        // vanished ones leave. No collection rebuild — the list never resets.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var view in devices) {
            seen.Add(view.Id);
            var existing = FindById(view.Id);
            if (existing is not null) {
                existing.UpdateFrom(view, generatedAt);
                continue;
            }

            var vm = new DeviceCardViewModel(
                view,
                generatedAt,
                _expandedDeviceId == view.Id,
                OnDeviceExpandRequested);
            Devices.Add(vm);
        }

        for (var i = Devices.Count - 1; i >= 0; i--) {
            if (!seen.Contains(Devices[i].Id)) {
                var removed = Devices[i];
                removed.PropertyChanged -= OnDeviceCardPropertyChanged;
                Devices.RemoveAt(i);
                if (_expandedDeviceId == removed.Id) {
                    _expandedDeviceId = null;
                }
            }
        }

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void OnDeviceExpandRequested(DeviceCardViewModel card) {
        // Single-expand policy: expanding one card collapses the rest (latest click wins).
        _expandedDeviceId = card.IsExpanded ? card.Id : null;
        foreach (var other in Devices) {
            if (other.Id != card.Id && other.IsExpanded) {
                other.Collapse();
            }
        }
    }

    private void OnDeviceCardPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        // Reserved for card→shell feedback (future phases).
    }

    private DeviceCardViewModel? FindById(string id) {
        foreach (var vm in Devices) {
            if (vm.Id == id) {
                return vm;
            }
        }

        return null;
    }

    private void RefreshAges() {
        var now = DateTimeOffset.Now;
        foreach (var device in Devices) {
            device.RefreshAge(now);
        }
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One device card. Updated in place by <see cref="NativeShellViewModel.ApplyDevices"/>.</summary>
internal sealed class DeviceCardViewModel : INotifyPropertyChanged {
    private readonly Action<DeviceCardViewModel> _notifyExpand;
    private string _copiedField = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _metaTemplate = string.Empty;
    private DateTimeOffset _stateSince;

    public DeviceCardViewModel(
        DeviceView view,
        DateTimeOffset generatedAt,
        bool isExpanded,
        Action<DeviceCardViewModel> notifyExpand) {
        _notifyExpand = notifyExpand;
        IsExpanded = isExpanded;
        ApplyView(view, generatedAt);
        ToggleExpandCommand = new RelayCommand(_ => NotifyExpand());
        CopyAddressCommand = new RelayCommand(
            _ => Copy(Address!),
            _ => !string.IsNullOrEmpty(Address));
        CopySerialCommand = new RelayCommand(
            _ => Copy(Serial!),
            _ => !string.IsNullOrEmpty(Serial));
    }

    public string Id { get; } = string.Empty;

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

    public void NotifyExpand() {
        IsExpanded = !IsExpanded;
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(ChevronIconGeometry));
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
        OnPropertyChanged(nameof(ChevronToolTip));
        OnPropertyChanged(nameof(CardCursor));
    }

    public void UpdateFrom(DeviceView view, DateTimeOffset generatedAt) => ApplyView(view, generatedAt);

    private void ApplyView(DeviceView view, DateTimeOffset generatedAt) {
        DisplayName = view.DisplayName;
        _metaTemplate = view.MetaTemplate;
        _stateSince = view.StateSince;
        State = view.State;
        Severity = view.Severity;
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

    public void RefreshAge(DateTimeOffset now) => RefreshAge(_metaTemplate, _stateSince, now);

    private void RefreshAge(string template, DateTimeOffset since, DateTimeOffset now) {
        var raw = AgeFormatter.Format(template, since, now);
        if (AgeText != raw) {
            AgeText = raw;
            OnPropertyChanged(nameof(AgeText));
        }
    }

    public string StatePillText => Severity switch {
        SeverityKind.Positive => "Connected",
        SeverityKind.Attention => "Attention",
        SeverityKind.Failure => "Error",
        _ => "Unknown",
    };

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

    public string BatteryText => BatteryPercent is null ? string.Empty : BatteryPercent.Value.ToString(CultureInfo.InvariantCulture) + "%";

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
        Clipboard.SetText(value);
        _copiedField = value == Address ? "address" : "serial";
        RaiseCopyStates();
    }

    private void RaiseCopyStates() {
        OnPropertyChanged(nameof(ShowAddressCopy));
        OnPropertyChanged(nameof(ShowAddressCopied));
        OnPropertyChanged(nameof(ShowSerialCopy));
        OnPropertyChanged(nameof(ShowSerialCopied));
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
