namespace AdbView.State;

using System.Globalization;
using AdbView.Adb.Model;

/// <summary>
/// Dynamic semantic/state wording shared across flyout, tooltip, and notifications.
/// Device copy follows DESIGN.md §12 / ENGINEERING.md §4.3.
/// Pairing / wireless copy follows DESIGN.md §14–§18.
/// </summary>
internal static class Vocabulary {
    internal static class Health {
        public const string AdbUnavailableTitle = "ADB unavailable";
        public const string AdbUnavailableDetail = "ADB View couldn't find or start ADB.";
        public const string AdbUnavailableHint = "Install Android Platform Tools or make adb.exe available to Windows.";
        public const string AdbUnavailableTrayTooltip = "ADB unavailable";

        public const string AdbRestoringTitle = "Restoring ADB…";
        public const string AdbRestoringDetail = "ADB View is reconnecting to the local ADB service.";

        public const string NoDevicesTrayTooltip = "No ADB devices";
    }

    internal static class Device {
        public const string ConnectedStateLabel = "Connected";
        public const string UnauthorizedStateLabel = "Authorization required";
        public const string OfflineStateLabel = "Device offline";
        public const string RecoveryStateLabel = "Recovery";
        public const string BootloaderStateLabel = "Bootloader";
        public const string SideloadStateLabel = "Sideload";
        public const string RescueStateLabel = "Rescue";
        public const string AuthorizingStateLabel = "Authorizing";
        public const string ConnectingStateLabel = "Connecting";
        public const string DetachedStateLabel = "Detached";
        public const string UnknownStateLabel = "Unknown state";

        public const string UnauthorizedGuidance = "Check the phone and allow USB debugging.";
        public const string OfflineGuidance = "ADB can see this device, but the connection isn’t responding.";

        /// <summary>
        /// The shell's age formatting substitutes <c>{age}</c> with a string that already includes
        /// "ago" / "Just now", so templates must not append a second "ago".
        /// </summary>
        public const string ConnectedMetaTemplate = "Connected {age}";
        public const string AgeOnlyMetaTemplate = "{age}";

        public const string TransportUsb = "USB";
        public const string TransportWifi = "Wi-Fi";
        public const string TransportUnknown = "Unknown";

        public const string CopySerialKey = "serial";
        public const string CopySerialLabel = "Serial";
        public const string CopyAddressKey = "address";
        public const string CopyAddressLabel = "Address";
    }

    internal static class Pairing {
        public const string OfferHeading = "Wireless pairing available";
        public const string OfferDetail = "Waiting for a pairing code";
        public const string ActionLabel = "Pair";
        public const string DefaultDeviceName = "Android device";

        public const string CodeEntryDetail = "Enter the six-digit code shown on the phone.";
        public const string ProgressDetail = "Keep the phone nearby while pairing completes.";
        public const string SuccessDetail = "Waiting for ADB connection…";
        public const string FailedTitle = "Pairing failed";
        public const string FailedDetail = "Check the code and try again.";

        /// <summary>
        /// Failure supporting line selected by cause (decision 0007, DESIGN.md §15).
        /// Any kind outside the fixed set keeps the frozen wrong-code line.
        /// </summary>
        public static string FailedDetailFor(PairingFailureKind kind) => kind switch {
            PairingFailureKind.Timeout => "The phone did not respond in time. Try again.",
            PairingFailureKind.RateLimited => "Too many attempts. Wait a moment and try again.",
            PairingFailureKind.InvalidRequest => "The pairing offer changed. Tap Pair and enter the new code.",
            _ => FailedDetail,
        };

        public static string CodeEntryTitle(string displayName) => "Pairing with " + displayName;
        public static string ProgressTitle(string displayName) => "Pairing with " + displayName + "…";
        public static string SuccessTitle(string displayName) => "Paired with " + displayName;

        // QR pairing (decision 0010).
        public const string QrInstructionPhone = "On the phone, open Developer options → Wireless debugging.";
        public const string QrInstructionScan = "Tap Pair device with QR code and point the camera at this code.";
        public const string QrCancelLabel = "Cancel";
    }

    internal static class Wireless {
        public const string UnavailableTitle = "Wireless discovery unavailable";
        public const string UnavailableDetail = "USB ADB is still working normally.";
        public const string RestoringTitle = "Restoring wireless discovery…";
        public const string RestoringDetail = "USB ADB is still working normally.";
    }

    /// <summary>
    /// Discovery-health card copy (decision 0008, DESIGN.md §18). Evidence lines describe
    /// findings only; never protocols, ports, or driver names (R-15).
    /// </summary>
    internal static class Discovery {
        public const string BrokenTitle = "Wireless discovery isn't working";
        public const string CheckAgainActionLabel = "Check again";
        public const string MeasuredNothingEvidence = "No announcements were measured just now.";
        public const string ProbeUnavailableEvidence = "ADB View couldn't measure the network just now.";
        public const string VirtualAdaptersEvidence = "Virtual network adapters are active";
        public const string PortHoldersEvidenceSuffix = "using the discovery port";

        public static string BrokenDetail(string displayName) =>
            displayName + " is connected over Wi-Fi, but ADB isn't receiving device announcements on this network.";

        public static string MeasuredEvidence(int count) => count switch {
            1 => "1 device announcement is visible on this network.",
            _ => count.ToString(CultureInfo.InvariantCulture) + " device announcements are visible on this network.",
        };

        public static string PortHeldEvidence(string processName) =>
            processName + " is using the discovery port.";

        public static string PortHoldersEvidence(string firstProcessName, int additionalCount) =>
            additionalCount switch {
                1 => firstProcessName + " and 1 other app are using the discovery port.",
                _ => firstProcessName + " and " + additionalCount.ToString(CultureInfo.InvariantCulture)
                    + " other apps are using the discovery port.",
            };

        public static string VirtualAdaptersNamedEvidence(string first, string second) =>
            VirtualAdaptersEvidence + " (" + first + ", " + second + ").";

        public static string VirtualAdaptersNamedEvidence(string only) =>
            VirtualAdaptersEvidence + " (" + only + ").";

        // ----- repair actions (decision 0008 amendment) -----

        public const string RestartAdbLabel = "Restart ADB";
        public const string RestartAdbRunningLabel = "Restarting ADB…";
        public const string ResultStillBroken = "Still not working after this action.";
        public const string ResultDeclined = "Approval was declined, so nothing was changed.";
        public const string ResultFailed = "The action couldn't be completed.";

        public static string StopServiceLabel(string service) => "Stop " + service + " service";
        public static string StartServiceLabel(string service) => "Start " + service + " service";
        public static string DisableAdapterLabel(string adapter) => "Disable " + adapter;
        public static string EnableAdapterLabel(string adapter) => "Re-enable " + adapter;

        public static string StopServiceRunningLabel(string service) => "Stopping " + service + "…";
        public static string StartServiceRunningLabel(string service) => "Starting " + service + "…";
        public static string DisableAdapterRunningLabel(string adapter) => "Disabling " + adapter + "…";
        public static string EnableAdapterRunningLabel(string adapter) => "Re-enabling " + adapter + "…";

        /// <summary>Renders a repair button label from the backend-owned action kind/target.</summary>
        public static string RepairLabel(DiscoveryRepairActionKind kind, string? target) => kind switch {
            DiscoveryRepairActionKind.RestartAdb => RestartAdbLabel,
            DiscoveryRepairActionKind.StopService => StopServiceLabel(target ?? string.Empty),
            DiscoveryRepairActionKind.StartService => StartServiceLabel(target ?? string.Empty),
            DiscoveryRepairActionKind.DisableAdapter => DisableAdapterLabel(target ?? string.Empty),
            DiscoveryRepairActionKind.EnableAdapter => EnableAdapterLabel(target ?? string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown repair kind."),
        };

        public static string RepairRunningLabel(DiscoveryRepairActionKind kind, string? target) => kind switch {
            DiscoveryRepairActionKind.RestartAdb => RestartAdbRunningLabel,
            DiscoveryRepairActionKind.StopService => StopServiceRunningLabel(target ?? string.Empty),
            DiscoveryRepairActionKind.StartService => StartServiceRunningLabel(target ?? string.Empty),
            DiscoveryRepairActionKind.DisableAdapter => DisableAdapterRunningLabel(target ?? string.Empty),
            DiscoveryRepairActionKind.EnableAdapter => EnableAdapterRunningLabel(target ?? string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown repair kind."),
        };

        public static string ResultLineFor(DiscoveryRepairOutcome outcome) => outcome switch {
            DiscoveryRepairOutcome.Completed => ResultStillBroken,
            DiscoveryRepairOutcome.Declined => ResultDeclined,
            DiscoveryRepairOutcome.Failed => ResultFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown repair outcome."),
        };
    }

    /// <summary>
    /// Notification titles/bodies — same canonical strings as flyout / tooltip surfaces.
    /// </summary>
    internal static class Notifications {
        public static string DeviceConnectedTitle(string displayName) => displayName + " connected";

        public static string DeviceConnectedBody(string transportLabel) =>
            "Connected over " + transportLabel;

        public static string DeviceDisconnectedTitle(string displayName) => displayName + " disconnected";

        public static string AuthorizationRequiredTitle(string displayName) =>
            displayName + " needs authorization";

        public static string AuthorizationRequiredBody => Device.UnauthorizedGuidance;

        public static string DeviceProblemTitle(string displayName) => displayName + " is offline";

        public static string DeviceProblemBody => Device.OfflineGuidance;

        public static string PairingAvailableTitle => Pairing.OfferHeading;

        public static string PairingAvailableBody(string displayName) =>
            displayName + " is waiting for a pairing code";

        public static string PairingSuccessTitle(string displayName) => Pairing.SuccessTitle(displayName);

        public static string PairingSuccessBody => Pairing.SuccessDetail;

        public static string PairingFailedTitle => Pairing.FailedTitle;

        public static string AdbUnavailableTitle => Health.AdbUnavailableTitle;

        public static string AdbUnavailableBody => Health.AdbUnavailableDetail;

        public static string DevicesChangedTitle(int count) => count + " devices changed";

        public static string DevicesChangedBody => string.Empty;
    }
}
