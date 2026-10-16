namespace AdbView.WinUiMigration;

using System.Diagnostics;
using System.Text.Json;
using AdbView.WinUiMigration.NativeUi.Motion;

internal sealed partial class CandidateWindow {
    private async Task RecordMotionAsync() {
        // Test-only handshake and timing. No recording or frame callbacks in product presentation.
        string prefix = _options.ResultPath!;
        _motionPolicy.SetSystemEnabled(true);
        string id = _presenter.Snapshot!.Devices[0].Id;
        _presenter.OpenDevice(id); _presenter.ContentMotion.Settle(); _presenter.Back(); _presenter.ContentMotion.Settle();
        Root.UpdateLayout();
        await File.WriteAllTextAsync(prefix + ".ready.json", JsonSerializer.Serialize(new {
            Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this).ToInt64(),
            ProcessId = Environment.ProcessId,
            Width = AppWindow.Size.Width,
            Height = AppWindow.Size.Height,
        })).ConfigureAwait(true);
        var waiting = Stopwatch.StartNew();
        while (!File.Exists(prefix + ".capture-ready")) {
            if (waiting.Elapsed > TimeSpan.FromSeconds(15)) { throw new InvalidOperationException("Motion recorder handshake timed out."); }
            await Task.Delay(25).ConfigureAwait(true);
        }
        await Task.Delay(200).ConfigureAwait(true);
        var events = new List<object>();
        for (int index = 0; index < 12; index++) {
            long before = Stopwatch.GetTimestamp();
            if (index % 2 == 0) { _presenter.OpenDevice(id); }
            else { _presenter.Back(); }
            events.Add(new { Index = index, Command = index % 2 == 0 ? 5 : 8, InputQpc = before, ReturnedQpc = Stopwatch.GetTimestamp() });
            await Task.Delay(300).ConfigureAwait(true);
        }
        var interruptions = new List<object>();
        foreach (int milliseconds in new[] { 42, 83, 150 }) {
            long before = Stopwatch.GetTimestamp();
            _presenter.OpenDevice(id);
            await Task.Delay(milliseconds).ConfigureAwait(true);
            long replaced = Stopwatch.GetTimestamp();
            // Same actual incoming pixels: replacing from an endpoint would visibly jump.
            _presenter.ContentMotion.Navigate("record-retarget:" + milliseconds, _presenter.Details!.Scroll);
            await Task.Delay(300).ConfigureAwait(true);
            interruptions.Add(new { InputQpc = before, ReplacedQpc = replaced, Milliseconds = milliseconds });
            _presenter.Back(); _presenter.ContentMotion.Settle();
        }
        await File.WriteAllTextAsync(prefix + ".events.json", JsonSerializer.Serialize(events)).ConfigureAwait(true);
        await File.WriteAllTextAsync(prefix + ".interruptions.json", JsonSerializer.Serialize(interruptions)).ConfigureAwait(true);
        Check("recorded-twelve-real-navigation-commands", events.Count == 12);
        Check("recording-completes-with-no-content-animation-handles", !_presenter.ContentMotion.IsActive && _presenter.ContentMotion.OwnedAnimationHandles == 0);
        // Keep the same visible native extent until the independent desktop observer ends.
        waiting.Restart();
        while (!File.Exists(prefix + ".capture-complete")) {
            if (waiting.Elapsed > TimeSpan.FromSeconds(10)) { throw new InvalidOperationException("Motion recorder completion timed out."); }
            await Task.Delay(25).ConfigureAwait(true);
        }
    }
}
