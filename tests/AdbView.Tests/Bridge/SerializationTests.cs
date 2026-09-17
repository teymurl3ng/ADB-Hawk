namespace AdbView.Tests.Bridge;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using AdbView.Bridge;
using AdbView.State;
using Xunit;

public sealed class SerializationTests {
    private static readonly DateTimeOffset TestTimestamp = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private static AppSnapshot CreateSampleSnapshot(
        long revision = 1,
        int protocol = BridgeProtocol.Version,
        int? battery = 85,
        bool includePairing = true,
        bool includeNullables = true) {
        return new AppSnapshot {
            Protocol = protocol,
            Revision = revision,
            GeneratedAt = TestTimestamp,
            Health = new HealthView {
                Kind = GlobalHealthKind.Healthy,
                Title = includeNullables ? "All good" : null,
                Detail = includeNullables ? "Connected and ready" : null,
                Hint = includeNullables ? "No action required" : null,
                Tray = TrayStatusKind.Connected,
                TrayTooltip = "Connected: Pixel 8 (USB)",
            },
            Shell = new ShellView {
                Pinned = false,
                Theme = ThemeKind.Dark,
                TransparencyEnabled = true,
                ForcedColors = false,
                ReducedMotion = false,
            },
            Devices = ImmutableArray.Create(
                new DeviceView {
                    Id = "dev-pixel-8",
                    DisplayName = "Pixel 8",
                    State = DeviceStateKind.Connected,
                    Severity = SeverityKind.Positive,
                    StateLabel = "Connected",
                    StatusLine = "Connected · USB",
                    Guidance = includeNullables ? "Ready to use" : null,
                    Transport = TransportKind.Usb,
                    TransportLabel = "USB",
                    BatteryPercent = battery,
                    MetaTemplate = "Connected {age} ago",
                    StateSince = TestTimestamp.AddHours(-1),
                    ConnectedSince = TestTimestamp.AddHours(-1),
                    Detail = new DeviceDetailView {
                        Serial = "SERIAL12345",
                        ExactModel = includeNullables ? "Pixel 8 Pro" : null,
                        AndroidVersion = includeNullables ? "15" : null,
                        Address = null,
                        CopyFields = ImmutableArray.Create(
                            new CopyFieldView { Key = "serial", Label = "Serial", Value = "SERIAL12345" }),
                    },
                },
                new DeviceView {
                    Id = "dev-s24",
                    DisplayName = "Galaxy S24",
                    State = DeviceStateKind.Connected,
                    Severity = SeverityKind.Positive,
                    StateLabel = "Connected",
                    StatusLine = "Connected · Wi-Fi",
                    Guidance = null,
                    Transport = TransportKind.WiFi,
                    TransportLabel = "Wi-Fi",
                    BatteryPercent = 90,
                    MetaTemplate = "Connected {age} ago",
                    StateSince = TestTimestamp.AddMinutes(-30),
                    ConnectedSince = TestTimestamp.AddMinutes(-30),
                    Detail = new DeviceDetailView {
                        Serial = "192.168.1.50:5555",
                        ExactModel = "SM-S921B",
                        AndroidVersion = "14",
                        Address = "192.168.1.50:5555",
                        CopyFields = ImmutableArray.Create(
                            new CopyFieldView { Key = "serial", Label = "Serial", Value = "192.168.1.50:5555" },
                            new CopyFieldView { Key = "address", Label = "Wi-Fi address", Value = "192.168.1.50:5555" }),
                    },
                }),
            PairingOffers = ImmutableArray.Create(
                new PairingOfferView {
                    Id = "mdns-offer-1",
                    DisplayName = "Pixel Tablet",
                    Heading = "Wireless pairing available",
                    Detail = "Waiting for a pairing code",
                    ActionLabel = "Pair",
                }),
            Pairing = includePairing ? new PairingOperationView {
                OfferId = "mdns-offer-1",
                DisplayName = "Pixel Tablet",
                Phase = PairingPhase.AwaitingCode,
                Title = "Pairing with Pixel Tablet…",
                Detail = "Enter the six-digit code shown on the phone.",
                CanSubmitCode = true,
                CanCancel = true,
                CanRetry = false,
                CodeLength = 6,
            } : null,
            Warnings = ImmutableArray.Create(
                new SubsystemWarningView {
                    Subsystem = SubsystemKind.WirelessDiscovery,
                    Severity = SeverityKind.Informational,
                    Title = "Wireless discovery active",
                    Detail = "mDNS service tracker is monitoring for devices.",
                }),
            DeviceCountLabel = "2 devices",
        };
    }

    [Fact]
    public void RoundTripAppSnapshotPreservesAllFields() {
        var original = CreateSampleSnapshot();
        var json = BridgeSerializer.SerializeSnapshot(original);

        var restored = BridgeSerializer.DeserializeSnapshot(json);

        Assert.Equal(original.Protocol, restored.Protocol);
        Assert.Equal(original.Revision, restored.Revision);
        Assert.Equal(original.GeneratedAt, restored.GeneratedAt);
        Assert.Equal(original.Health.Kind, restored.Health.Kind);
        Assert.Equal(original.Health.Title, restored.Health.Title);
        Assert.Equal(original.Health.Detail, restored.Health.Detail);
        Assert.Equal(original.Health.Hint, restored.Health.Hint);
        Assert.Equal(original.Health.Tray, restored.Health.Tray);
        Assert.Equal(original.Health.TrayTooltip, restored.Health.TrayTooltip);
        Assert.Equal(original.Shell.Pinned, restored.Shell.Pinned);
        Assert.Equal(original.Shell.Theme, restored.Shell.Theme);
        Assert.Equal(original.Shell.TransparencyEnabled, restored.Shell.TransparencyEnabled);
        Assert.Equal(original.Shell.ForcedColors, restored.Shell.ForcedColors);
        Assert.Equal(original.Shell.ReducedMotion, restored.Shell.ReducedMotion);
        Assert.Equal(original.Devices.Length, restored.Devices.Length);
        Assert.Equal(original.Devices[0].Id, restored.Devices[0].Id);
        Assert.Equal(original.Devices[0].Transport, restored.Devices[0].Transport);
        Assert.Equal(original.Devices[1].Transport, restored.Devices[1].Transport);
        Assert.Equal(original.PairingOffers.Length, restored.PairingOffers.Length);
        Assert.NotNull(restored.Pairing);
        Assert.Equal(original.Pairing!.OfferId, restored.Pairing.OfferId);
        Assert.Equal(original.Pairing.CodeLength, restored.Pairing.CodeLength);
        Assert.Equal(original.Warnings.Length, restored.Warnings.Length);
        Assert.Equal(original.DeviceCountLabel, restored.DeviceCountLabel);
    }

    [Fact]
    public void CamelCaseNamingUsedExclusively() {
        var snapshot = CreateSampleSnapshot();
        var json = BridgeSerializer.SerializeSnapshot(snapshot);

        // Verify camelCase property names exist
        Assert.Contains("\"deviceCountLabel\":", json, StringComparison.Ordinal);
        Assert.Contains("\"pairingOffers\":", json, StringComparison.Ordinal);
        Assert.Contains("\"stateSince\":", json, StringComparison.Ordinal);
        Assert.Contains("\"transparencyEnabled\":", json, StringComparison.Ordinal);
        Assert.Contains("\"forcedColors\":", json, StringComparison.Ordinal);
        Assert.Contains("\"reducedMotion\":", json, StringComparison.Ordinal);
        Assert.Contains("\"batteryPercent\":", json, StringComparison.Ordinal);
        Assert.Contains("\"trayTooltip\":", json, StringComparison.Ordinal);

        // Verify string enums are camelCase
        Assert.Contains("\"healthy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"connected\"", json, StringComparison.Ordinal);
        Assert.Contains("\"wifi\"", json, StringComparison.Ordinal);
        Assert.Contains("\"usb\"", json, StringComparison.Ordinal);
        Assert.Contains("\"awaitingCode\"", json, StringComparison.Ordinal);
        Assert.Contains("\"wirelessDiscovery\"", json, StringComparison.Ordinal);

        // Verify no PascalCase keys appear
        Assert.DoesNotContain("\"DeviceCountLabel\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PairingOffers\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"StateSince\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NullHandlingPreservesNulls() {
        var snapshot = CreateSampleSnapshot(includePairing: false, includeNullables: false) with {
            DeviceCountLabel = null,
        };

        var json = BridgeSerializer.SerializeSnapshot(snapshot);
        var restored = BridgeSerializer.DeserializeSnapshot(json);

        Assert.Null(restored.Pairing);
        Assert.Null(restored.DeviceCountLabel);
        Assert.Null(restored.Health.Title);
        Assert.Null(restored.Health.Detail);
        Assert.Null(restored.Health.Hint);
        Assert.Null(restored.Devices[0].Guidance);
        Assert.Null(restored.Devices[0].Detail.ExactModel);
        Assert.Null(restored.Devices[0].Detail.AndroidVersion);
        Assert.Null(restored.Devices[0].Detail.Address);
    }

    [Fact]
    public void SafeIntegerBoundaryValidRevisionsSucceed() {
        var minRevision = CreateSampleSnapshot(revision: 1);
        var minJson = BridgeSerializer.SerializeSnapshot(minRevision);
        var restoredMin = BridgeSerializer.DeserializeSnapshot(minJson);
        Assert.Equal(1L, restoredMin.Revision);

        var maxRevision = CreateSampleSnapshot(revision: BridgeProtocol.MaxSafeRevision);
        var maxJson = BridgeSerializer.SerializeSnapshot(maxRevision);
        var restoredMax = BridgeSerializer.DeserializeSnapshot(maxJson);
        Assert.Equal(BridgeProtocol.MaxSafeRevision, restoredMax.Revision);
    }

    [Fact]
    public void SafeIntegerBoundaryInvalidRevisionsRejectedOnSerialization() {
        Assert.Throws<ArgumentOutOfRangeException>(() => {
            var zeroRevision = CreateSampleSnapshot(revision: 0);
            BridgeSerializer.SerializeSnapshot(zeroRevision);
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => {
            var negativeRevision = CreateSampleSnapshot(revision: -1);
            BridgeSerializer.SerializeSnapshot(negativeRevision);
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => {
            var overflowRevision = CreateSampleSnapshot(revision: BridgeProtocol.MaxSafeRevision + 1);
            BridgeSerializer.SerializeSnapshot(overflowRevision);
        });
    }

    [Fact]
    public void SafeIntegerBoundaryInvalidRevisionsRejectedOnDeserialization() {
        var baseJson = BridgeSerializer.SerializeSnapshot(CreateSampleSnapshot());

        var zeroJson = baseJson.Replace("\"revision\":1", "\"revision\":0", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => BridgeSerializer.DeserializeSnapshot(zeroJson));

        var negativeJson = baseJson.Replace("\"revision\":1", "\"revision\":-5", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => BridgeSerializer.DeserializeSnapshot(negativeJson));

        var overflowJson = baseJson.Replace("\"revision\":1", $"\"revision\":{BridgeProtocol.MaxSafeRevision + 1}", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => BridgeSerializer.DeserializeSnapshot(overflowJson));
    }

    [Fact]
    public void WebActionsAllEightTypesRoundTrip() {
        WebAction[] actions =
        [
            new ReadyAction { Protocol = 1 },
            new PinAction(),
            new UnpinAction(),
            new RequestDismissAction(),
            new ReportContentSizeAction { HeightEffectivePx = 420.5 },
            new PairStartAction { OfferId = "offer-123" },
            new PairSubmitAction { OfferId = "offer-123", Code = "654321" },
            new PairCancelAction { OfferId = "offer-123" },
        ];

        foreach (var action in actions) {
            var json = BridgeSerializer.SerializeAction(action);
            var restored = BridgeSerializer.DeserializeAction(json);

            Assert.Equal(action.GetType(), restored.GetType());

            switch (action) {
                case ReadyAction ready:
                    Assert.Equal(ready.Protocol, ((ReadyAction)restored).Protocol);
                    break;
                case ReportContentSizeAction report:
                    Assert.Equal(report.HeightEffectivePx, ((ReportContentSizeAction)restored).HeightEffectivePx);
                    break;
                case PairStartAction start:
                    Assert.Equal(start.OfferId, ((PairStartAction)restored).OfferId);
                    break;
                case PairSubmitAction submit:
                    Assert.Equal(submit.OfferId, ((PairSubmitAction)restored).OfferId);
                    Assert.Equal(submit.Code, ((PairSubmitAction)restored).Code);
                    break;
                case PairCancelAction cancel:
                    Assert.Equal(cancel.OfferId, ((PairCancelAction)restored).OfferId);
                    break;
            }
        }
    }

    [Fact]
    public void WebActionsUnknownActionRejected() {
        var unknownActionJson = "{\"type\":\"someFutureAction\",\"payload\":123}";
        Assert.Throws<JsonException>(() => BridgeSerializer.DeserializeAction(unknownActionJson));
    }

    [Fact]
    public void WebActionsExtraMemberAcceptedAndIgnored() {
        var extraPinJson = "{\"type\":\"pin\",\"futureField\":123}";
        var pinAction = BridgeSerializer.DeserializeAction(extraPinJson);
        Assert.IsType<PinAction>(pinAction);

        var extraReportJson = "{\"type\":\"reportContentSize\",\"heightEffectivePx\":400.0,\"futureField\":true}";
        var reportAction = BridgeSerializer.DeserializeAction(extraReportJson);
        var typedReport = Assert.IsType<ReportContentSizeAction>(reportAction);
        Assert.Equal(400.0, typedReport.HeightEffectivePx);
    }

    [Fact]
    public void WebActionsMissingOrInvalidRequiredFieldsRejected() {
        var missingFieldJson = "{\"type\":\"reportContentSize\"}";
        Assert.Throws<JsonException>(() => BridgeSerializer.DeserializeAction(missingFieldJson));

        var wrongTypeJson = "{\"type\":\"reportContentSize\",\"heightEffectivePx\":\"not-a-number\"}";
        Assert.Throws<JsonException>(() => BridgeSerializer.DeserializeAction(wrongTypeJson));

        var malformedJson = "{not-json";
        Assert.Throws<JsonException>(() => BridgeSerializer.DeserializeAction(malformedJson));
    }

    [Fact]
    public void EnumsInvalidValuesRejected() {
        var baseJson = BridgeSerializer.SerializeSnapshot(CreateSampleSnapshot());

        var badHealthJson = baseJson.Replace("\"healthy\"", "\"catastrophic\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => BridgeSerializer.DeserializeSnapshot(badHealthJson));

        var badTransportJson = baseJson.Replace("\"usb\"", "\"bluetooth\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => BridgeSerializer.DeserializeSnapshot(badTransportJson));

        var badStateJson = baseJson.Replace("\"connected\"", "\"flying\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => BridgeSerializer.DeserializeSnapshot(badStateJson));
    }

    [Fact]
    public void ForwardCompatibilityExtraSnapshotFieldsIgnored() {
        var baseJson = BridgeSerializer.SerializeSnapshot(CreateSampleSnapshot());
        var extendedJson = baseJson.Replace("\"protocol\":1", "\"protocol\":1,\"futureFeatureFlag\":true,\"extraDiagnosticData\":{\"foo\":1}", StringComparison.Ordinal);

        var restored = BridgeSerializer.DeserializeSnapshot(extendedJson);
        Assert.NotNull(restored);
        Assert.Equal(1, restored.Protocol);
    }

    [Fact]
    public void SnapshotEnvelopeSerializesAndDeserializesCorrectly() {
        var snapshot = CreateSampleSnapshot();
        var envelope = new SnapshotEnvelope { Snapshot = snapshot };

        var json = BridgeSerializer.SerializeSnapshotEnvelope(envelope);
        Assert.Contains("\"type\":\"snapshot\"", json, StringComparison.Ordinal);
        Assert.Contains("\"protocol\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"snapshot\":{", json, StringComparison.Ordinal);

        var restored = BridgeSerializer.DeserializeSnapshotEnvelope(json);
        Assert.Equal("snapshot", restored.Type);
        Assert.Equal(1, restored.Protocol);
        Assert.Equal(snapshot.Revision, restored.Snapshot.Revision);
    }

    [Fact]
    public void ExportCanonicalCanaryPayloads() {
        var usbSnapshot = new AppSnapshot {
            Protocol = BridgeProtocol.Version,
            Revision = 1,
            GeneratedAt = TestTimestamp,
            Health = new HealthView {
                Kind = GlobalHealthKind.Healthy,
                Title = "Connected",
                Detail = "USB device ready",
                Hint = null,
                Tray = TrayStatusKind.Connected,
                TrayTooltip = "Connected: Pixel 8 (USB)",
            },
            Shell = new ShellView {
                Pinned = false,
                Theme = ThemeKind.Dark,
                TransparencyEnabled = true,
                ForcedColors = false,
                ReducedMotion = false,
            },
            Devices = ImmutableArray.Create(new DeviceView {
                Id = "dev-pixel-8",
                DisplayName = "Pixel 8",
                State = DeviceStateKind.Connected,
                Severity = SeverityKind.Positive,
                StateLabel = "Connected",
                StatusLine = "Connected · USB",
                Guidance = null,
                Transport = TransportKind.Usb,
                TransportLabel = "USB",
                BatteryPercent = 85,
                MetaTemplate = "Connected {age} ago",
                StateSince = TestTimestamp.AddHours(-1),
                ConnectedSince = TestTimestamp.AddHours(-1),
                Detail = new DeviceDetailView {
                    Serial = "SERIAL-USB-1",
                    ExactModel = "Pixel 8",
                    AndroidVersion = "15",
                    Address = null,
                    CopyFields = ImmutableArray.Create(new CopyFieldView { Key = "serial", Label = "Serial", Value = "SERIAL-USB-1" }),
                },
            }),
            PairingOffers = ImmutableArray<PairingOfferView>.Empty,
            Pairing = null,
            Warnings = ImmutableArray<SubsystemWarningView>.Empty,
            DeviceCountLabel = null,
        };

        var wifiSnapshot = new AppSnapshot {
            Protocol = BridgeProtocol.Version,
            Revision = 2,
            GeneratedAt = TestTimestamp,
            Health = new HealthView {
                Kind = GlobalHealthKind.Healthy,
                Title = "Connected",
                Detail = "Wi-Fi device ready",
                Hint = null,
                Tray = TrayStatusKind.Connected,
                TrayTooltip = "Connected: Galaxy S24 (Wi-Fi)",
            },
            Shell = new ShellView {
                Pinned = false,
                Theme = ThemeKind.Dark,
                TransparencyEnabled = true,
                ForcedColors = false,
                ReducedMotion = false,
            },
            Devices = ImmutableArray.Create(new DeviceView {
                Id = "dev-galaxy-s24",
                DisplayName = "Galaxy S24",
                State = DeviceStateKind.Connected,
                Severity = SeverityKind.Positive,
                StateLabel = "Connected",
                StatusLine = "Connected · Wi-Fi",
                Guidance = null,
                Transport = TransportKind.WiFi,
                TransportLabel = "Wi-Fi",
                BatteryPercent = 90,
                MetaTemplate = "Connected {age} ago",
                StateSince = TestTimestamp.AddMinutes(-30),
                ConnectedSince = TestTimestamp.AddMinutes(-30),
                Detail = new DeviceDetailView {
                    Serial = "192.168.1.50:5555",
                    ExactModel = "SM-S921B",
                    AndroidVersion = "14",
                    Address = "192.168.1.50:5555",
                    CopyFields = ImmutableArray.Create(
                        new CopyFieldView { Key = "serial", Label = "Serial", Value = "192.168.1.50:5555" },
                        new CopyFieldView { Key = "address", Label = "Wi-Fi address", Value = "192.168.1.50:5555" }),
                },
            }),
            PairingOffers = ImmutableArray<PairingOfferView>.Empty,
            Pairing = null,
            Warnings = ImmutableArray<SubsystemWarningView>.Empty,
            DeviceCountLabel = null,
        };

        var multiDeviceSnapshot = new AppSnapshot {
            Protocol = BridgeProtocol.Version,
            Revision = 3,
            GeneratedAt = TestTimestamp,
            Health = new HealthView {
                Kind = GlobalHealthKind.Healthy,
                Title = "3 devices",
                Detail = "Active ADB connections",
                Hint = null,
                Tray = TrayStatusKind.Connected,
                TrayTooltip = "3 devices connected",
            },
            Shell = new ShellView {
                Pinned = false,
                Theme = ThemeKind.Dark,
                TransparencyEnabled = true,
                ForcedColors = false,
                ReducedMotion = false,
            },
            Devices = ImmutableArray.Create(
                new DeviceView {
                    Id = "dev-pixel-8",
                    DisplayName = "Pixel 8",
                    State = DeviceStateKind.Connected,
                    Severity = SeverityKind.Positive,
                    StateLabel = "Connected",
                    StatusLine = "Connected · USB",
                    Guidance = null,
                    Transport = TransportKind.Usb,
                    TransportLabel = "USB",
                    BatteryPercent = 85,
                    MetaTemplate = "Connected {age} ago",
                    StateSince = TestTimestamp.AddHours(-1),
                    ConnectedSince = TestTimestamp.AddHours(-1),
                    Detail = new DeviceDetailView {
                        Serial = "SERIAL-1",
                        ExactModel = "Pixel 8",
                        AndroidVersion = "15",
                        Address = null,
                        CopyFields = ImmutableArray.Create(new CopyFieldView { Key = "serial", Label = "Serial", Value = "SERIAL-1" }),
                    },
                },
                new DeviceView {
                    Id = "dev-galaxy-s24",
                    DisplayName = "Galaxy S24",
                    State = DeviceStateKind.Connected,
                    Severity = SeverityKind.Positive,
                    StateLabel = "Connected",
                    StatusLine = "Connected · Wi-Fi",
                    Guidance = null,
                    Transport = TransportKind.WiFi,
                    TransportLabel = "Wi-Fi",
                    BatteryPercent = 90,
                    MetaTemplate = "Connected {age} ago",
                    StateSince = TestTimestamp.AddMinutes(-30),
                    ConnectedSince = TestTimestamp.AddMinutes(-30),
                    Detail = new DeviceDetailView {
                        Serial = "192.168.1.50:5555",
                        ExactModel = null,
                        AndroidVersion = null,
                        Address = "192.168.1.50:5555",
                        CopyFields = ImmutableArray.Create(
                            new CopyFieldView { Key = "serial", Label = "Serial", Value = "192.168.1.50:5555" },
                            new CopyFieldView { Key = "address", Label = "Wi-Fi address", Value = "192.168.1.50:5555" }),
                    },
                },
                new DeviceView {
                    Id = "dev-nexus-5",
                    DisplayName = "Nexus 5",
                    State = DeviceStateKind.Unauthorized,
                    Severity = SeverityKind.Attention,
                    StateLabel = "Unauthorized",
                    StatusLine = "Unauthorized · USB",
                    Guidance = "Check the phone and allow USB debugging.",
                    Transport = TransportKind.Usb,
                    TransportLabel = "USB",
                    BatteryPercent = null,
                    MetaTemplate = "Detected {age} ago",
                    StateSince = TestTimestamp.AddMinutes(-10),
                    ConnectedSince = null,
                    Detail = new DeviceDetailView {
                        Serial = "SERIAL-UNAUTH",
                        ExactModel = null,
                        AndroidVersion = null,
                        Address = null,
                        CopyFields = ImmutableArray.Create(new CopyFieldView { Key = "serial", Label = "Serial", Value = "SERIAL-UNAUTH" }),
                    },
                }),
            PairingOffers = ImmutableArray<PairingOfferView>.Empty,
            Pairing = null,
            Warnings = ImmutableArray<SubsystemWarningView>.Empty,
            DeviceCountLabel = "3 devices",
        };

        var pairingSnapshot = new AppSnapshot {
            Protocol = BridgeProtocol.Version,
            Revision = 4,
            GeneratedAt = TestTimestamp,
            Health = new HealthView {
                Kind = GlobalHealthKind.Healthy,
                Title = "Pairing ready",
                Detail = "Wireless pairing service discovered",
                Hint = null,
                Tray = TrayStatusKind.PairingAvailable,
                TrayTooltip = "Wireless pairing available",
            },
            Shell = new ShellView {
                Pinned = false,
                Theme = ThemeKind.Dark,
                TransparencyEnabled = true,
                ForcedColors = false,
                ReducedMotion = false,
            },
            Devices = ImmutableArray<DeviceView>.Empty,
            PairingOffers = ImmutableArray.Create(
                new PairingOfferView {
                    Id = "mdns-1",
                    DisplayName = "Pixel Tablet",
                    Heading = "Wireless pairing available",
                    Detail = "Waiting for a pairing code",
                    ActionLabel = "Pair",
                },
                new PairingOfferView {
                    Id = "mdns-2",
                    DisplayName = "Galaxy Tab",
                    Heading = "Wireless pairing available",
                    Detail = "Waiting for a pairing code",
                    ActionLabel = "Pair",
                }),
            Pairing = new PairingOperationView {
                OfferId = "mdns-1",
                DisplayName = "Pixel Tablet",
                Phase = PairingPhase.AwaitingCode,
                Title = "Pairing with Pixel Tablet…",
                Detail = "Enter the six-digit code shown on the phone.",
                CanSubmitCode = true,
                CanCancel = true,
                CanRetry = false,
                CodeLength = 6,
            },
            Warnings = ImmutableArray<SubsystemWarningView>.Empty,
            DeviceCountLabel = null,
        };

        var nullablesSnapshot = new AppSnapshot {
            Protocol = BridgeProtocol.Version,
            Revision = 5,
            GeneratedAt = TestTimestamp,
            Health = new HealthView {
                Kind = GlobalHealthKind.Healthy,
                Title = null,
                Detail = null,
                Hint = null,
                Tray = TrayStatusKind.Idle,
                TrayTooltip = "ADB View",
            },
            Shell = new ShellView {
                Pinned = false,
                Theme = ThemeKind.Light,
                TransparencyEnabled = false,
                ForcedColors = false,
                ReducedMotion = false,
            },
            Devices = ImmutableArray.Create(new DeviceView {
                Id = "dev-bare",
                DisplayName = "Bare Device",
                State = DeviceStateKind.Connected,
                Severity = SeverityKind.Positive,
                StateLabel = "Connected",
                StatusLine = "Connected · USB",
                Guidance = null,
                Transport = TransportKind.Usb,
                TransportLabel = "USB",
                BatteryPercent = null,
                MetaTemplate = "Connected {age} ago",
                StateSince = TestTimestamp,
                ConnectedSince = null,
                Detail = new DeviceDetailView {
                    Serial = "BARE-SERIAL",
                    ExactModel = null,
                    AndroidVersion = null,
                    Address = null,
                    CopyFields = ImmutableArray.Create(new CopyFieldView { Key = "serial", Label = "Serial", Value = "BARE-SERIAL" }),
                },
            }),
            PairingOffers = ImmutableArray<PairingOfferView>.Empty,
            Pairing = null,
            Warnings = ImmutableArray<SubsystemWarningView>.Empty,
            DeviceCountLabel = null,
        };

        var snapshots = new[]
        {
            new { name = "connected-usb", envelope = new SnapshotEnvelope { Snapshot = usbSnapshot } },
            new { name = "connected-wifi", envelope = new SnapshotEnvelope { Snapshot = wifiSnapshot } },
            new { name = "multi-device", envelope = new SnapshotEnvelope { Snapshot = multiDeviceSnapshot } },
            new { name = "pairing", envelope = new SnapshotEnvelope { Snapshot = pairingSnapshot } },
            new { name = "nullables", envelope = new SnapshotEnvelope { Snapshot = nullablesSnapshot } },
            new { name = "max-revision", envelope = new SnapshotEnvelope { Snapshot = CreateSampleSnapshot(revision: BridgeProtocol.MaxSafeRevision) } },
        };

        var canaryDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "ui", "src", "contract", "__fixtures__");
        Directory.CreateDirectory(canaryDir);

        var dict = new Dictionary<string, object>();
        foreach (var s in snapshots) {
            var json = BridgeSerializer.SerializeSnapshotEnvelope(s.envelope);
            dict[s.name] = JsonDocument.Parse(json).RootElement;
        }

        var fullJson = JsonSerializer.Serialize(dict, IndentedJsonOptions);
        File.WriteAllText(Path.Combine(canaryDir, "canary.json"), fullJson.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }
}
