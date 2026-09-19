namespace AdbView.State;

/// <summary>
/// Dynamic semantic/state wording shared across flyout, tooltip, and notifications.
/// Device copy follows DESIGN.md §12 / ENGINEERING.md §4.3.
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
}
