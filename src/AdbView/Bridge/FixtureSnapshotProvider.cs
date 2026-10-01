namespace AdbView.Bridge;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AdbView.State;

internal static class FixtureSnapshotProvider {
    public static readonly DateTimeOffset T0 = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    public static readonly ImmutableArray<string> CanonicalFixtureNames = [
        "empty",
        "connected-usb",
        "connected-wifi",
        "expanded",
        "unauthorized",
        "offline",
        "recovery",
        "bootloader",
        "sideload",
        "unknown",
        "multi-device",
        "pairing-offer",
        "pairing-code",
        "pairing-progress",
        "pairing-success",
        "pairing-failure",
        "discovery-health",
        "pairing-qr"
    ];

    private static ShellView CreateShell(bool isPinned) => new() {
        Pinned = isPinned,
        Theme = ThemeKind.Dark,
        TransparencyEnabled = false, // v1 opaque
        ForcedColors = false,
        ReducedMotion = false
    };

    private static DeviceDetailView CreateGalaxyS24Detail(bool isUsb) {
        var copyFields = isUsb
            ? ImmutableArray.Create(new CopyFieldView { Key = "serial", Label = "Serial", Value = "R5CW••••••••" })
            : ImmutableArray.Create(
                new CopyFieldView { Key = "address", Label = "Address", Value = "192.168.1.144:39421" },
                new CopyFieldView { Key = "serial", Label = "Serial", Value = "R5CW••••••••" }
            );

        return new DeviceDetailView {
            Serial = "R5CW••••••••",
            ExactModel = "SM-S921W",
            AndroidVersion = "16",
            Address = isUsb ? null : "192.168.1.144:39421",
            CopyFields = copyFields
        };
    }

    private static DeviceView CreateGalaxyS24(
        DeviceStateKind state,
        SeverityKind severity,
        string stateLabel,
        TransportKind transport,
        string transportLabel,
        int? battery,
        string metaTemplate,
        TimeSpan ageOffset,
        string? guidance = null,
        DateTimeOffset? connectedSince = null) {

        bool isUsb = transport == TransportKind.Usb;
        var stateSince = T0 - ageOffset;

        return new DeviceView {
            Id = "d1",
            DisplayName = "Galaxy S24",
            State = state,
            Severity = severity,
            StateLabel = stateLabel,
            StatusLine = $"{stateLabel} · {transportLabel}",
            Guidance = guidance,
            Transport = transport,
            TransportLabel = transportLabel,
            BatteryPercent = battery,
            MetaTemplate = metaTemplate,
            StateSince = stateSince,
            ConnectedSince = connectedSince,
            Detail = CreateGalaxyS24Detail(isUsb)
        };
    }

    public static AppSnapshot GetSnapshot(string fixtureName, bool isPinned = false, long revision = 1) {
        var normalized = (fixtureName ?? "empty").Trim().ToUpperInvariant();

        return normalized switch {
            "EMPTY" => CreateEmptySnapshot(isPinned, revision),
            "CONNECTED-USB" => CreateConnectedUsbSnapshot(isPinned, revision),
            "CONNECTED-WIFI" => CreateConnectedWifiSnapshot(isPinned, revision),
            "EXPANDED" => CreateExpandedSnapshot(isPinned, revision),
            "UNAUTHORIZED" => CreateUnauthorizedSnapshot(isPinned, revision),
            "OFFLINE" => CreateOfflineSnapshot(isPinned, revision),
            "RECOVERY" => CreateRecoverySnapshot(isPinned, revision),
            "BOOTLOADER" => CreateBootloaderSnapshot(isPinned, revision),
            "SIDELOAD" => CreateSideloadSnapshot(isPinned, revision),
            "UNKNOWN" => CreateUnknownSnapshot(isPinned, revision),
            "MULTI-DEVICE" => CreateMultiDeviceSnapshot(isPinned, revision),
            "PAIRING-OFFER" => CreatePairingOfferSnapshot(isPinned, revision),
            "PAIRING-CODE" => CreatePairingCodeSnapshot(isPinned, revision),
            "PAIRING-PROGRESS" => CreatePairingProgressSnapshot(isPinned, revision),
            "PAIRING-SUCCESS" => CreatePairingSuccessSnapshot(isPinned, revision),
            "PAIRING-FAILURE" => CreatePairingFailureSnapshot(isPinned, revision),
            "DISCOVERY-HEALTH" => CreateDiscoveryHealthSnapshot(isPinned, revision),
            "PAIRING-QR" => CreatePairingQrSnapshot(isPinned, revision),
            _ => CreateEmptySnapshot(isPinned, revision)
        };
    }

    public static IReadOnlyDictionary<string, AppSnapshot> GetAllSnapshots(bool isPinned = false, long revision = 1) {
        var dict = new Dictionary<string, AppSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in CanonicalFixtureNames) {
            dict[name] = GetSnapshot(name, isPinned, revision);
        }
        return dict;
    }

    private static AppSnapshot CreateEmptySnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Idle,
            TrayTooltip = "ADB View: No devices connected"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray<DeviceView>.Empty,
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    // Decision 0008: connected Wi-Fi device + zero services + healthy server; evidence from
    // a measured probe and environmental findings.
    private static AppSnapshot CreateDiscoveryHealthSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Connected,
            TrayTooltip = "Connected: Galaxy S24 (Wi-Fi)"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Connected,
                severity: SeverityKind.Positive,
                stateLabel: "Connected",
                transport: TransportKind.WiFi,
                transportLabel: "Wi-Fi",
                battery: 82,
                metaTemplate: "Connected {age}",
                ageOffset: TimeSpan.FromSeconds(46),
                connectedSince: T0 - TimeSpan.FromSeconds(46)
            )
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DiscoveryHealth = new DiscoveryHealthView {
            Title = Vocabulary.Discovery.BrokenTitle,
            Detail = Vocabulary.Discovery.BrokenDetail("Galaxy S24"),
            Evidence = ImmutableArray.Create(
                Vocabulary.Discovery.MeasuredEvidence(3),
                Vocabulary.Discovery.PortHeldEvidence("Microsoft Edge"),
                Vocabulary.Discovery.VirtualAdaptersNamedEvidence("Tailscale")
            ),
            ActionLabel = Vocabulary.Discovery.CheckAgainActionLabel,
            CanCheckAgain = true,
            Repairs = ImmutableArray.Create(
                new DiscoveryRepairCardAction {
                    Id = "restart-adb",
                    Label = Vocabulary.Discovery.RestartAdbLabel,
                    IsAdmin = false,
                    IsRunning = false,
                    RunningLabel = Vocabulary.Discovery.RestartAdbRunningLabel
                },
                new DiscoveryRepairCardAction {
                    Id = "stop-service:Tailscale",
                    Label = Vocabulary.Discovery.StopServiceLabel("Tailscale"),
                    IsAdmin = true,
                    IsRunning = false,
                    RunningLabel = Vocabulary.Discovery.StopServiceRunningLabel("Tailscale")
                }
            ),
            ResultLine = null
        },
        DeviceCountLabel = null
    };

    private static AppSnapshot CreateConnectedUsbSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Connected,
            TrayTooltip = "Connected: Galaxy S24 (USB)"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Connected,
                severity: SeverityKind.Positive,
                stateLabel: "Connected",
                transport: TransportKind.Usb,
                transportLabel: "USB",
                battery: 82,
                metaTemplate: "Connected {age}",
                ageOffset: TimeSpan.Zero,
                connectedSince: T0
            )
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreateConnectedWifiSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Connected,
            TrayTooltip = "Connected: Galaxy S24 (Wi-Fi)"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Connected,
                severity: SeverityKind.Positive,
                stateLabel: "Connected",
                transport: TransportKind.WiFi,
                transportLabel: "Wi-Fi",
                battery: 82,
                metaTemplate: "Connected {age}",
                ageOffset: TimeSpan.FromSeconds(46),
                connectedSince: T0 - TimeSpan.FromSeconds(46)
            )
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreateExpandedSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Connected,
            TrayTooltip = "Connected: Galaxy S24 (Wi-Fi)"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Connected,
                severity: SeverityKind.Positive,
                stateLabel: "Connected",
                transport: TransportKind.WiFi,
                transportLabel: "Wi-Fi",
                battery: 82,
                metaTemplate: "Connected {age}",
                ageOffset: TimeSpan.FromSeconds(120),
                connectedSince: T0 - TimeSpan.FromSeconds(120)
            )
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreateUnauthorizedSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Attention,
            TrayTooltip = "Unauthorized: Galaxy S24 (Wi-Fi)"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Unauthorized,
                severity: SeverityKind.Attention,
                stateLabel: "Unauthorized",
                transport: TransportKind.WiFi,
                transportLabel: "Wi-Fi",
                battery: null,
                metaTemplate: "{age}",
                ageOffset: TimeSpan.Zero,
                guidance: "Check the phone and allow USB debugging."
            )
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreateOfflineSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Idle,
            TrayTooltip = "Offline: Galaxy S24 (Wi-Fi)"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Offline,
                severity: SeverityKind.Neutral,
                stateLabel: "Offline",
                transport: TransportKind.WiFi,
                transportLabel: "Wi-Fi",
                battery: null,
                metaTemplate: "{age}",
                ageOffset: TimeSpan.FromSeconds(240)
            )
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreateRecoverySnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Special,
            TrayTooltip = "Recovery: Galaxy S24 (Wi-Fi)"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Recovery,
                severity: SeverityKind.Informational,
                stateLabel: "Recovery",
                transport: TransportKind.WiFi,
                transportLabel: "Wi-Fi",
                battery: null,
                metaTemplate: "{age}",
                ageOffset: TimeSpan.FromSeconds(480)
            )
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreateBootloaderSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Special,
            TrayTooltip = "Bootloader: Galaxy S24 (Wi-Fi)"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Bootloader,
                severity: SeverityKind.Informational,
                stateLabel: "Bootloader",
                transport: TransportKind.WiFi,
                transportLabel: "Wi-Fi",
                battery: null,
                metaTemplate: "{age}",
                ageOffset: TimeSpan.Zero
            )
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreateSideloadSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Special,
            TrayTooltip = "Sideload: Galaxy S24 (Wi-Fi)"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Sideload,
                severity: SeverityKind.Informational,
                stateLabel: "Sideload",
                transport: TransportKind.WiFi,
                transportLabel: "Wi-Fi",
                battery: null,
                metaTemplate: "{age}",
                ageOffset: TimeSpan.Zero
            )
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreateUnknownSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Special,
            TrayTooltip = "Unknown: Galaxy S24 (Wi-Fi)"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Unknown,
                severity: SeverityKind.Informational,
                stateLabel: "Unknown",
                transport: TransportKind.WiFi,
                transportLabel: "Wi-Fi",
                battery: null,
                metaTemplate: "{age}",
                ageOffset: TimeSpan.Zero
            )
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreateMultiDeviceSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Connected,
            TrayTooltip = "Connected: 6 devices"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray.Create(
            CreateGalaxyS24(
                state: DeviceStateKind.Connected,
                severity: SeverityKind.Positive,
                stateLabel: "Connected",
                transport: TransportKind.WiFi,
                transportLabel: "Wi-Fi",
                battery: 82,
                metaTemplate: "{age}",
                ageOffset: TimeSpan.FromSeconds(60),
                connectedSince: T0 - TimeSpan.FromSeconds(60)
            ),
            new DeviceView {
                Id = "d2",
                DisplayName = "Pixel 8",
                State = DeviceStateKind.Connected,
                Severity = SeverityKind.Positive,
                StateLabel = "Connected",
                StatusLine = "Connected · USB",
                Guidance = null,
                Transport = TransportKind.Usb,
                TransportLabel = "USB",
                BatteryPercent = 67,
                MetaTemplate = "{age}",
                StateSince = T0 - TimeSpan.FromSeconds(180),
                ConnectedSince = T0 - TimeSpan.FromSeconds(180),
                Detail = new DeviceDetailView {
                    Serial = "3928••••••••",
                    ExactModel = "Pixel 8",
                    AndroidVersion = "15",
                    Address = null,
                    CopyFields = ImmutableArray.Create(
                        new CopyFieldView { Key = "serial", Label = "Serial", Value = "3928••••••••" }
                    )
                }
            },
            new DeviceView {
                Id = "d3",
                DisplayName = "Pixel 5",
                State = DeviceStateKind.Unauthorized,
                Severity = SeverityKind.Attention,
                StateLabel = "Unauthorized",
                StatusLine = "Unauthorized · USB",
                Guidance = "Check the phone and allow USB debugging.",
                Transport = TransportKind.Usb,
                TransportLabel = "USB",
                BatteryPercent = null,
                MetaTemplate = "{age}",
                StateSince = T0,
                ConnectedSince = null,
                Detail = new DeviceDetailView {
                    Serial = "1924••••••••",
                    ExactModel = "Pixel 5",
                    AndroidVersion = "14",
                    Address = null,
                    CopyFields = ImmutableArray.Create(
                        new CopyFieldView { Key = "serial", Label = "Serial", Value = "1924••••••••" }
                    )
                }
            },
            new DeviceView {
                Id = "d4",
                DisplayName = "Galaxy Tab S10",
                State = DeviceStateKind.Recovery,
                Severity = SeverityKind.Informational,
                StateLabel = "Recovery",
                StatusLine = "Recovery · USB",
                Guidance = null,
                Transport = TransportKind.Usb,
                TransportLabel = "USB",
                BatteryPercent = null,
                MetaTemplate = "{age}",
                StateSince = T0 - TimeSpan.FromSeconds(600),
                ConnectedSince = null,
                Detail = new DeviceDetailView {
                    Serial = "R52X••••••••",
                    ExactModel = "SM-X920",
                    AndroidVersion = "15",
                    Address = null,
                    CopyFields = ImmutableArray.Create(
                        new CopyFieldView { Key = "serial", Label = "Serial", Value = "R52X••••••••" }
                    )
                }
            },
            new DeviceView {
                Id = "d5",
                DisplayName = "OnePlus 13",
                State = DeviceStateKind.Offline,
                Severity = SeverityKind.Neutral,
                StateLabel = "Offline",
                StatusLine = "Offline · Wi-Fi",
                Guidance = null,
                Transport = TransportKind.WiFi,
                TransportLabel = "Wi-Fi",
                BatteryPercent = null,
                MetaTemplate = "{age}",
                StateSince = T0 - TimeSpan.FromSeconds(1320),
                ConnectedSince = null,
                Detail = new DeviceDetailView {
                    Serial = "OP13••••••••",
                    ExactModel = "CPH2653",
                    AndroidVersion = "15",
                    Address = "192.168.1.189:41235",
                    CopyFields = ImmutableArray.Create(
                        new CopyFieldView { Key = "address", Label = "Address", Value = "192.168.1.189:41235" },
                        new CopyFieldView { Key = "serial", Label = "Serial", Value = "OP13••••••••" }
                    )
                }
            },
            new DeviceView {
                Id = "d6",
                DisplayName = "Nothing Phone",
                State = DeviceStateKind.Connected,
                Severity = SeverityKind.Positive,
                StateLabel = "Connected",
                StatusLine = "Connected · USB",
                Guidance = null,
                Transport = TransportKind.Usb,
                TransportLabel = "USB",
                BatteryPercent = 74,
                MetaTemplate = "{age}",
                StateSince = T0,
                ConnectedSince = T0,
                Detail = new DeviceDetailView {
                    Serial = "NP2A••••••••",
                    ExactModel = "A063",
                    AndroidVersion = "14",
                    Address = null,
                    CopyFields = ImmutableArray.Create(
                        new CopyFieldView { Key = "serial", Label = "Serial", Value = "NP2A••••••••" }
                    )
                }
            }
        ),
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = "6 devices"
    };

    private static AppSnapshot CreatePairingOfferSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.PairingAvailable,
            TrayTooltip = "Wireless pairing available: Galaxy S24"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray<DeviceView>.Empty,
        PairingOffers = ImmutableArray.Create(
            new PairingOfferView {
                Id = "offer-1",
                DisplayName = "Galaxy S24",
                Heading = "Wireless pairing available",
                Detail = "Waiting for a pairing code",
                ActionLabel = "Pair"
            }
        ),
        Pairing = null,
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreatePairingCodeSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Attention,
            TrayTooltip = "Pairing with Galaxy S24"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray<DeviceView>.Empty,
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = new PairingOperationView {
            OfferId = "offer-1",
            DisplayName = "Galaxy S24",
            Phase = PairingPhase.AwaitingCode,
            Title = "Pairing with Galaxy S24",
            Detail = "Enter the six-digit code shown on the phone.",
            CanSubmitCode = true,
            CanCancel = true,
            CanRetry = false,
            CodeLength = 6
        },
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreatePairingProgressSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Attention,
            TrayTooltip = "Pairing with Galaxy S24"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray<DeviceView>.Empty,
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = new PairingOperationView {
            OfferId = "offer-1",
            DisplayName = "Galaxy S24",
            Phase = PairingPhase.Pairing,
            Title = "Pairing with Galaxy S24…",
            Detail = "Keep the phone nearby while pairing completes.",
            CanSubmitCode = false,
            CanCancel = true,
            CanRetry = false,
            CodeLength = 6
        },
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreatePairingSuccessSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Connected,
            TrayTooltip = "Paired with Galaxy S24"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray<DeviceView>.Empty,
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = new PairingOperationView {
            OfferId = "offer-1",
            DisplayName = "Galaxy S24",
            Phase = PairingPhase.PairedWaitingForDevice,
            Title = "Paired with Galaxy S24",
            Detail = "Waiting for ADB connection…",
            CanSubmitCode = false,
            CanCancel = false,
            CanRetry = false,
            CodeLength = 6
        },
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    // Decision 0010: active QR session. The data URI is a placeholder rendering; the live
    // card displays real QRCoder output from the backend.
    private static AppSnapshot CreatePairingQrSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Idle,
            TrayTooltip = "ADB View: No devices connected"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray<DeviceView>.Empty,
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = null,
        PairingQr = new PairingQrView {
            SvgDataUri = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIyMjAiIGhlaWdodD0iMjIwIj48cmVjdCB3aWR0aD0iMjIwIiBoZWlnaHQ9IjIyMCIgZmlsbD0iI2ZmZmZmZiIvPjwvc3ZnPg==",
            Instructions = ImmutableArray.Create(
                Vocabulary.Pairing.QrInstructionPhone,
                Vocabulary.Pairing.QrInstructionScan
            ),
            CancelLabel = Vocabulary.Pairing.QrCancelLabel
        },
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };

    private static AppSnapshot CreatePairingFailureSnapshot(bool isPinned, long revision) => new() {
        Protocol = BridgeProtocol.Version,
        Revision = revision,
        GeneratedAt = T0,
        Health = new HealthView {
            Kind = GlobalHealthKind.Healthy,
            Title = null,
            Detail = null,
            Hint = null,
            Tray = TrayStatusKind.Attention,
            TrayTooltip = "Pairing failed with Galaxy S24"
        },
        Shell = CreateShell(isPinned),
        Devices = ImmutableArray<DeviceView>.Empty,
        PairingOffers = ImmutableArray<PairingOfferView>.Empty,
        Pairing = new PairingOperationView {
            OfferId = "offer-1",
            DisplayName = "Galaxy S24",
            Phase = PairingPhase.Failed,
            Title = "Pairing failed",
            Detail = "Check the code and try again.",
            CanSubmitCode = false,
            CanCancel = true,
            CanRetry = true,
            CodeLength = 6
        },
        Warnings = ImmutableArray<SubsystemWarningView>.Empty,
        DeviceCountLabel = null
    };
}
