namespace AdbView.WinUiMigration.Host;

using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

internal static class NativeClipboard {
    public static bool Copy(string value) {
        try {
            var data = new DataPackage();
            data.SetText(value);
            Clipboard.SetContent(data);
            return true;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException) {
            // Never log values/serials; a failed clipboard write must not show Copied.
            Console.Error.WriteLine("Clipboard: text write failed: " + exception.GetType().Name);
            return false;
        }
    }
}
