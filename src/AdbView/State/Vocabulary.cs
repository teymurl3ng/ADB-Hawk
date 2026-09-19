namespace AdbView.State;

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
        /// Frontend <c>formatAge</c> substitutes <c>{age}</c> with a string that already includes
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

        public static string CodeEntryTitle(string displayName) => "Pairing with " + displayName;
        public static string ProgressTitle(string displayName) => "Pairing with " + displayName + "…";
        public static string SuccessTitle(string displayName) => "Paired with " + displayName;
    }

    internal static class Wireless {
        public const string UnavailableTitle = "Wireless discovery unavailable";
        public const string UnavailableDetail = "USB ADB is still working normally.";
        public const string RestoringTitle = "Restoring wireless discovery…";
        public const string RestoringDetail = "USB ADB is still working normally.";
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

        public static string PairingFailedBody => Pairing.FailedDetail;

        public static string AdbUnavailableTitle => Health.AdbUnavailableTitle;

        public static string AdbUnavailableBody => Health.AdbUnavailableDetail;

        public static string DevicesChangedTitle(int count) => count + " devices changed";

        public static string DevicesChangedBody => string.Empty;
    }
}
