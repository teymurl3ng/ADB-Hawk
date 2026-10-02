namespace AdbView.NativeUi.Shell;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

internal enum PairingCardState {
    Offer,
    CodeEntry,
    Progress,
    Success,
    Failure,
}

/// <summary>
/// Pairing card across all phases (offer/code/progress/success/failure) — stable shell,
/// phase region visibility (decision 0014-consistency). Actions surface as opaque events
/// that AppHost routes to the pairing coordinators.
/// </summary>
internal sealed class PairingCardViewModel : INotifyPropertyChanged {
    private readonly Action<string, string, string?> _raiseAction;
    private string _code = string.Empty;
    private PairingCardState _state;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PairingCardViewModel(string offerId, string offerName, PairingCardState state, string? detail, Action<string, string, string?> raiseAction) {
        _offerId = offerId;
        _offerName = offerName;
        _raiseAction = raiseAction;
        _state = state;
        _detail = detail;
        StartCommand = new RelayCommand(_ => _raiseAction("pairStart", _offerId, null));
        SubmitCommand = new RelayCommand(_ => Submit(), _ => CanSubmit);
        CancelCommand = new RelayCommand(_ => _raiseAction("pairCancel", _offerId, null));
        // Failure-phase Retry: a complete code means retry the submit; otherwise restart pairing.
        RetryCommand = new RelayCommand(_ => {
            if (Code.Length == 6) {
                _raiseAction("pairSubmit", _offerId, Code);
            }
            else {
                _raiseAction("pairStart", _offerId, null);
            }
        });
    }

    public ICommand StartCommand { get; }

    public ICommand SubmitCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RetryCommand { get; }

    public void Update(PairingCardState state, string? detail) {
        var phaseChanged = State != state;
        State = state;
        Detail = detail;
        // Same contract as the WebView card: entering code entry (or failure, which shares the
        // code region) starts with an empty code. Same-state detail updates keep the typing.
        if (phaseChanged && state is PairingCardState.CodeEntry or PairingCardState.Failure) {
            Code = string.Empty;
            OnPropertyChanged(nameof(CodeInput));
        }
    }

    private string _offerId;

    private string _offerName;

    public string OfferName {
        get => _offerName;
        private set {
            _offerName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeviceNameDisplay));
        }
    }

    public PairingCardState State {
        get => _state;
        private set {
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(HeaderText));
            OnPropertyChanged(nameof(BodyText));
            OnPropertyChanged(nameof(IsCodeEntry));
            OnPropertyChanged(nameof(IsProgress));
            OnPropertyChanged(nameof(IsSuccess));
            OnPropertyChanged(nameof(IsFailure));
            OnPropertyChanged(nameof(IsOffer));
            OnPropertyChanged(nameof(ShowDigitSlots));
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(SuccessTitle));
            OnPropertyChanged(nameof(CardSurface));
            OnPropertyChanged(nameof(CardBorder));
        }
    }

    /// <summary>Shell surface tint follows the phase exactly as the WebView card (green/red/accent).</summary>
    public SolidColorBrush CardSurface => State switch {
        PairingCardState.Success => (SolidColorBrush)Application.Current.Resources["Win.GreenSurface"],
        PairingCardState.Failure => (SolidColorBrush)Application.Current.Resources["Win.RedSurface"],
        _ => (SolidColorBrush)Application.Current.Resources["Win.AccentSurface"],
    };

    public SolidColorBrush CardBorder => State switch {
        PairingCardState.Success => (SolidColorBrush)Application.Current.Resources["Win.GreenBorder"],
        PairingCardState.Failure => (SolidColorBrush)Application.Current.Resources["Win.RedBorder"],
        _ => (SolidColorBrush)Application.Current.Resources["Win.AccentBorder"],
    };

    /// <summary>Pair button tint mirrors readiness exactly as the WebView card (accent when 6 digits).</summary>
    public SolidColorBrush SubmitBackground => CanSubmit
        ? (SolidColorBrush)Application.Current.Resources["Win.Accent"]
        : (SolidColorBrush)Application.Current.Resources["Win.BtnHover"];

    public SolidColorBrush SubmitForeground => CanSubmit
        ? Brushes.White
        : (SolidColorBrush)Application.Current.Resources["Win.TextMuted"];

    private string? _detail;

    public string? Detail {
        get => _detail;
        private set {
            _detail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public static string OfferHeaderText => "Wireless pairing available";

    public string DeviceNameDisplay => OfferName;

    public string StateLabel => State switch {
        PairingCardState.Offer => "Wireless pairing available",
        PairingCardState.Progress => "Pairing",
        PairingCardState.Success => "Connected",
        PairingCardState.Failure => "Failed",
        _ => "Pairing",
    };

    public string HeaderText => State switch {
        PairingCardState.Offer => OfferHeaderText,
        PairingCardState.Progress => "Pairing with " + OfferName + "…",
        PairingCardState.Success => "Paired with " + OfferName,
        PairingCardState.Failure => "Pairing failed",
        _ => "Wireless pairing available",
    };

    public string BodyText => State switch {
        PairingCardState.Progress => "Keep the phone nearby while pairing completes.",
        PairingCardState.Failure => DetailText,
        _ => "Waiting for a pairing code",
    };

    public string DetailText => Detail ?? "Check the code and try again.";

    public string SuccessTitle => "Paired with " + OfferName;

    public bool IsCodeEntry => State is PairingCardState.CodeEntry or PairingCardState.Failure;

    /// <summary>Code-entry title block shows for code phases except failure (which shows its own header).</summary>
    public bool ShowCodeEntryTitle => IsCodeEntry && !IsFailure;

    public bool IsProgress => State is PairingCardState.Progress;

    public bool IsSuccess => State is PairingCardState.Success;

    public bool IsFailure => State is PairingCardState.Failure;

    public bool IsOffer => State is PairingCardState.Offer;

    public bool ShowDigitSlots => IsCodeEntry;

    public bool CanSubmit => Code.Length == 6;

    public string Code {
        get => _code;
        private set {
            _code = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(SubmitBackground));
            OnPropertyChanged(nameof(SubmitForeground));
            OnPropertyChanged(nameof(Digit1));
            OnPropertyChanged(nameof(Digit2));
            OnPropertyChanged(nameof(Digit3));
            OnPropertyChanged(nameof(Digit4));
            OnPropertyChanged(nameof(Digit5));
            OnPropertyChanged(nameof(Digit6));
        }
    }

    public string Digit1 => DigitAt(0);
    public string Digit2 => DigitAt(1);
    public string Digit3 => DigitAt(2);
    public string Digit4 => DigitAt(3);
    public string Digit5 => DigitAt(4);
    public string Digit6 => DigitAt(5);

    /// <summary>Sanitizing bind target for the invisible code input: digits only, max 6.</summary>
    public string CodeInput {
        get => Code;
        set {
            Code = SanitizeCode(value);
            OnPropertyChanged(nameof(CodeInput));
        }
    }

    private static string SanitizeCode(string? raw) {
        if (string.IsNullOrEmpty(raw)) {
            return string.Empty;
        }

        Span<char> digits = stackalloc char[6];
        var count = 0;
        foreach (var c in raw) {
            if (!char.IsAsciiDigit(c)) {
                continue;
            }

            digits[count++] = c;
            if (count == 6) {
                break;
            }
        }

        return new string(digits[..count]);
    }

    private string DigitAt(int index) =>
        index < Code.Length ? Code[index].ToString(CultureInfo.InvariantCulture) : "_";

    /// <summary>Appends a digit from the numpad (max 6, digits only).</summary>
    public void AppendDigit(char digit) {
        if (!char.IsAsciiDigit(digit) || Code.Length >= 6) {
            return;
        }

        Code += digit;
    }

    public void Backspace() {
        if (Code.Length > 0) {
            Code = Code[..^1];
        }
    }

    public void Submit() {
        if (CanSubmit) {
            _raiseAction("pairSubmit", _offerId, Code);
        }
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Discovery-health warning card (decision 0008) — evidence, repairs, result line.</summary>
internal sealed class DiscoveryHealthCardViewModel : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;

    private DiscoveryHealthCardViewModel(
        string title,
        string detail,
        ImmutableArray<string> evidence,
        string? resultLine,
        string actionLabel,
        bool canCheckAgain,
        ImmutableArray<RepairButton> repairs,
        Action<string> raiseRepair,
        Action raiseRecheck) {
        Title = title;
        Detail = detail;
        Evidence = evidence;
        ResultLine = resultLine;
        ActionLabel = actionLabel;
        CanCheckAgain = canCheckAgain;
        Repairs = repairs;
        RaiseRepair = raiseRepair;
        RepairCommand = new RelayCommand(
            parameter => RaiseRepair(((RepairButton)parameter!).Id),
            parameter => parameter is RepairButton button && !button.IsRunning);
        CheckAgainCommand = new RelayCommand(
            _ => raiseRecheck(),
            _ => CanCheckAgain && !AnyRepairRunning);
    }

    /// <summary>Runs one repair; the parameter is the opaque <see cref="RepairButton"/>.</summary>
    public ICommand RepairCommand { get; }

    /// <summary>Backend measurement ("Check again"); gated while a repair or check runs.</summary>
    public ICommand CheckAgainCommand { get; }

    public bool HasEvidence => Evidence.Length > 0;

    public bool HasResultLine => !string.IsNullOrEmpty(ResultLine);

    public bool HasRepairs => Repairs.Length > 0;

    public bool AnyRepairRunning {
        get {
            foreach (var repair in Repairs) {
                if (repair.IsRunning) {
                    return true;
                }
            }

            return false;
        }
    }

    public string Title { get; }

    public string Detail { get; }

    public ImmutableArray<string> Evidence { get; }

    public string? ResultLine { get; }

    public string ActionLabel { get; }

    public bool CanCheckAgain { get; }

    public ImmutableArray<RepairButton> Repairs { get; }

    private readonly Action<string> RaiseRepair;

    internal readonly record struct RepairButton(string Id, string Label, bool IsAdmin, bool IsRunning, string RunningLabel);

    /// <summary>Configuration equality — rebuilds that change nothing must not reset the card.</summary>
    public static bool SameConfiguration(DiscoveryHealthCardViewModel left, DiscoveryHealthCardViewModel right) {
        if (left.Title != right.Title || left.Detail != right.Detail
            || left.ActionLabel != right.ActionLabel || left.CanCheckAgain != right.CanCheckAgain
            || left.ResultLine != right.ResultLine
            || left.Evidence.Length != right.Evidence.Length
            || left.Repairs.Length != right.Repairs.Length) {
            return false;
        }

        for (var i = 0; i < left.Evidence.Length; i++) {
            if (left.Evidence[i] != right.Evidence[i]) {
                return false;
            }
        }

        for (var i = 0; i < left.Repairs.Length; i++) {
            if (left.Repairs[i] != right.Repairs[i]) {
                return false;
            }
        }

        return true;
    }

    public static RepairButton RepairButtonFactory(string id, string label, bool isAdmin, bool isRunning, string runningLabel) =>
        new RepairButton(id, label, isAdmin, isRunning, runningLabel);

    public static DiscoveryHealthCardViewModel Create(
        string title,
        string detail,
        ImmutableArray<string> evidence,
        string? resultLine,
        string actionLabel,
        bool canCheckAgain,
        ImmutableArray<RepairButton> repairs,
        Action<string> raiseRepair,
        Action raiseRecheck) {
        return new DiscoveryHealthCardViewModel(title, detail, evidence, resultLine, actionLabel, canCheckAgain, repairs, raiseRepair, raiseRecheck);
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Pairing QR session card view model (decision 0010) — display-only. The backend's
/// SVG data URI is rasterized once into a frozen WPF image; rebuilds with the same payload are
/// skipped by the shell (source-equality guard).</summary>
internal sealed class PairingQrCardViewModel {
    public PairingQrCardViewModel(
        string svgDataUri,
        string instructionLine1,
        string instructionLine2,
        string cancelLabel,
        Action cancel) {
        SourceSvgDataUri = svgDataUri;
        QrImage = NativeUi.QrSvgRenderer.Render(svgDataUri);
        InstructionLine1 = instructionLine1;
        InstructionLine2 = instructionLine2;
        CancelLabel = cancelLabel;
        CancelCommand = new RelayCommand(_ => cancel());
    }

    /// <summary>Exact snapshot payload this card was built from (rebuild guard).</summary>
    public string SourceSvgDataUri { get; }

    public ImageSource? QrImage { get; }

    public string InstructionLine1 { get; }

    public string InstructionLine2 { get; }

    public string CancelLabel { get; }

    public ICommand CancelCommand { get; }
}
