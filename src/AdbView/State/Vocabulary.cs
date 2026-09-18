namespace AdbView.State;

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
}
