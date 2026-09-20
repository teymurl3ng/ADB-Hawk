namespace AdbView.State;

using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

internal sealed record AppSnapshot {
    public required int Protocol { get; init; }              // == BridgeProtocol.Version (1)
    [JsonConverter(typeof(AdbView.Bridge.SafeRevisionConverter))]
    public required long Revision { get; init; }             // monotonic, never reused
    public required DateTimeOffset GeneratedAt { get; init; }// UTC; frontend clock-skew safe
    public required HealthView Health { get; init; }
    public required ShellView Shell { get; init; }
    public required ImmutableArray<DeviceView> Devices { get; init; }      // display order = stable
    public required ImmutableArray<PairingOfferView> PairingOffers { get; init; }
    public PairingOperationView? Pairing { get; init; }                    // at most one at a time
    public required ImmutableArray<SubsystemWarningView> Warnings { get; init; }
    public required string? DeviceCountLabel { get; init; }   // "3 devices" or null (<2 devices)
}

// ---------- global health ----------
internal enum GlobalHealthKind { Healthy, AdbUnavailable, AdbRestoring }

internal sealed record HealthView {
    public required GlobalHealthKind Kind { get; init; }
    public required string? Title { get; init; }       // "ADB unavailable" | "Restoring ADB…" | null
    public required string? Detail { get; init; }      // "ADB View couldn't find or start ADB."
    public required string? Hint { get; init; }        // "Install Android Platform Tools or make adb.exe available to Windows."
    public required TrayStatusKind Tray { get; init; }
    public required string TrayTooltip { get; init; }  // "Connected: Galaxy S24 (Wi-Fi)"
}

internal enum TrayStatusKind { Idle, Connected, Attention, Failure, Special, PairingAvailable }

// ---------- shell / environment ----------
internal enum ThemeKind { Dark, Light }

internal sealed record ShellView {
    public required bool Pinned { get; init; }
    public required ThemeKind Theme { get; init; }
    public required bool TransparencyEnabled { get; init; }  // false => opaque token set
    public required bool ForcedColors { get; init; }         // Windows contrast theme active
    public required bool ReducedMotion { get; init; }
}

// ---------- devices ----------
internal enum DeviceStateKind {
    Connected, Unauthorized, Offline, Recovery, Bootloader, Sideload,
    Rescue, Authorizing, Connecting, Detached, Unknown,
}

internal enum SeverityKind { Positive, Attention, Failure, Informational, Neutral }

internal enum TransportKind {
    Usb,
    [JsonStringEnumMemberName("wifi")]
    WiFi,
    Unknown,
}

internal sealed record DeviceView {
    public required string Id { get; init; }            // stable internal id (§6.7)
    public required string DisplayName { get; init; }    // "Galaxy S24"
    public required DeviceStateKind State { get; init; } // for icon/marker selection only
    public required SeverityKind Severity { get; init; }
    public required string StateLabel { get; init; }     // "Connected" | "Authorization required"
    public required string StatusLine { get; init; }     // "Connected · Wi-Fi"
    public string? Guidance { get; init; }               // "Check the phone and allow USB debugging."
    public required TransportKind Transport { get; init; }
    public required string TransportLabel { get; init; } // "USB" | "Wi-Fi"
    public int? BatteryPercent { get; init; }            // null => omit battery entirely
    public required string MetaTemplate { get; init; }    // "Connected {age} ago" (frontend fills {age})
    public required DateTimeOffset StateSince { get; init; }
    public DateTimeOffset? ConnectedSince { get; init; }     // only when a meaningful connected instant exists
    public required DeviceDetailView Detail { get; init; } // expanded card payload
}

internal sealed record DeviceDetailView {
    /// <summary>
    /// User-visible hardware serial. Null when only a transport endpoint is known (Wi-Fi host:port).
    /// Does not affect internal DeviceId or AdbSerial transport addressing.
    /// </summary>
    public string? Serial { get; init; }
    public string? ExactModel { get; init; }             // "SM-S921B"
    public string? AndroidVersion { get; init; }         // "15"
    public string? Address { get; init; }                // "192.168.1.42:37895" (Wi-Fi only)
    public required ImmutableArray<CopyFieldView> CopyFields { get; init; } // serial, address
}

internal sealed record CopyFieldView {
    public required string Key { get; init; }     // "serial" | "address"
    public required string Label { get; init; }   // "Serial"
    public required string Value { get; init; }
}

// ---------- pairing (independent of devices) ----------
internal sealed record PairingOfferView {
    public required string Id { get; init; }          // stable per mDNS instance
    public required string DisplayName { get; init; } // PairingDisplayName mapper (never raw adb-* instance)
    public required string Heading { get; init; }     // "Wireless pairing available"
    public required string Detail { get; init; }      // "Waiting for a pairing code"
    public required string ActionLabel { get; init; } // "Pair"
}

internal enum PairingPhase { AwaitingCode, Pairing, PairedWaitingForDevice, Failed }

internal sealed record PairingOperationView {
    public required string OfferId { get; init; }
    public required string DisplayName { get; init; }
    public required PairingPhase Phase { get; init; }
    public required string Title { get; init; }   // "Pairing with Galaxy S24…" | "Paired with Galaxy S24"
    public string? Detail { get; init; }          // "Enter the six-digit code shown on the phone."
    public required bool CanSubmitCode { get; init; }
    public required bool CanCancel { get; init; }
    public required bool CanRetry { get; init; }
    public required int CodeLength { get; init; } // 6
}

// ---------- local subsystem warnings (never global) ----------
internal enum SubsystemKind { WirelessDiscovery }

internal sealed record SubsystemWarningView {
    public required SubsystemKind Subsystem { get; init; }
    public required SeverityKind Severity { get; init; }  // Attention or Informational
    public required string Title { get; init; }  // "Wireless discovery unavailable" | "Restoring wireless discovery…"
    public required string Detail { get; init; } // "USB ADB is still working normally."
}
