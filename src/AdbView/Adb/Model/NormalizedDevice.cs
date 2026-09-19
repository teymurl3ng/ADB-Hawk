namespace AdbView.Adb.Model;

/// <summary>
/// Semantic ADB device state after Phase 5B normalization (ENGINEERING.md §6.5).
/// Domain-only; Phase 5C maps into snapshot device views later.
/// </summary>
internal enum DeviceSemanticState {
    Connected,
    Unauthorized,
    Offline,
    Recovery,
    Bootloader,
    Sideload,
    Rescue,
    Authorizing,
    Connecting,
    Detached,
    Unknown,
}

/// <summary>Transport after Phase 5B normalization (ENGINEERING.md §6.7).</summary>
internal enum DeviceTransport {
    Usb,
    WiFi,
    Unknown,
}

/// <summary>
/// Stateless normalized device-domain record. No UI labels, severity, ages, or metadata.
/// </summary>
internal sealed record NormalizedDevice {
    public required string Id { get; init; }
    public required string HardwareSerial { get; init; }
    public required DeviceTransport Transport { get; init; }
    public required DeviceSemanticState SemanticState { get; init; }
    public required string DisplayName { get; init; }
    public string? Product { get; init; }
    public string? Model { get; init; }
    public string? Device { get; init; }
}
