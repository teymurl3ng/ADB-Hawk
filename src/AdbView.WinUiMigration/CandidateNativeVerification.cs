namespace AdbView.WinUiMigration;

using AdbView.NativeUi;
using AdbView.Notifications;
using AdbView.State;
using AdbView.WinUiMigration.Host.Native;
using AdbView.WinUiMigration.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

internal sealed partial class CandidateWindow {
    private Task HoldBubbleDeadline(TimeSpan delay, CancellationToken token) {
        Check("bubble-deadline-is-exactly-60-seconds", delay == StatusBubblePresenter.TransientLifetime && delay.TotalSeconds == 60);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _bubbleDeadlines.Add(completion); return completion.Task.WaitAsync(token);
    }
    private async Task VerifyNativeScenariosAsync() {
        NativeShellOwner native = _nativeOwner!;
        NativeTray tray = native.Tray;
        HWND receiver = native.Receiver.Handle;
        var bubble = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(native.Bubble));
        var flyout = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
        const uint testMessage = 0x8000 + 82;
        var inputComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inputHook = new WindowMessageHook(flyout, (message, command, parameter) => {
            if (message != testMessage) { return null; }
            switch (command.Value) {
                case 0:
                    return new LRESULT((AppWindow.IsVisible ? 1 : 0) | (native.Bubble.AppWindow.IsVisible ? 2 : 0)
                    | (_presenter.Snapshot!.Shell.Pinned ? 4 : 0) | (tray.MenuIsOpen ? 8 : 0) | (_presenter.DetailsId is not null ? 32 : 0));
                case 1: native.Hide(); break;
                case 2: native.Open(); break;
                case 3:
                case 4:
                    AppSnapshot snapshot = _presenter.Snapshot!;
                    if (!_mailbox.Publish(snapshot with { Revision = snapshot.Revision + 1, Shell = snapshot.Shell with { Pinned = command.Value == 3 } })) {
                        throw new InvalidOperationException("Native input fixture: pin snapshot was rejected.");
                    }
                    break;
                case 5: _presenter.OpenDevice(_presenter.Snapshot!.Devices[0].Id); break;
                case 99: inputComplete.TrySetResult(); break;
                default: throw new ArgumentOutOfRangeException(nameof(command));
            }
            return new LRESULT(0);
        });
        Check("receiver-is-hidden-top-level-without-parent", !PInvoke.IsWindowVisible(receiver) && PInvoke.GetParent(receiver).IsNull
            && PInvoke.GetAncestor(receiver, GET_ANCESTOR_FLAGS.GA_ROOT) == receiver);
        Check("native-tray-added-and-version-four-negotiated", tray.Registered && tray.AddCount == 1 && tray.VersionCount == 1 && tray.ModifyCount > 0);
        Check("tray-tooltip-is-snapshot-vocabulary", tray.Tooltip == _presenter.Snapshot!.Health.TrayTooltip);
        foreach (NotificationSeverity severity in Enum.GetValues<NotificationSeverity>()) {
            var intent = new NotificationIntent(new NotificationKey(NotificationKind.DeviceConnected, "fixture"), new string('a', 100), new string('b', 400), severity);
            var balloon = tray.BuildBalloon(intent);
            var expectedSeverity = severity switch {
                NotificationSeverity.Warning => NOTIFY_ICON_INFOTIP_FLAGS.NIIF_WARNING,
                NotificationSeverity.Error => NOTIFY_ICON_INFOTIP_FLAGS.NIIF_ERROR,
                _ => NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO
            };
            Check("balloon-payload-retains-severity-and-core-truncation-" + severity, balloon.hWnd == receiver
                && balloon.szInfoTitle.ToString() == AdbView.Host.BalloonText.TruncateTitle(intent.Title)
                && balloon.szInfo.ToString() == AdbView.Host.BalloonText.TruncateBody(intent.Body)
                && (balloon.dwInfoFlags & NOTIFY_ICON_INFOTIP_FLAGS.NIIF_ICON_MASK) == expectedSeverity);
        }
        VerifyNativeMenu(tray, false, true, false, AdbView.Host.ThemePreference.System, 0);
        tray.Select(2); tray.Select(3); tray.Select(4); tray.Select(12);
        VerifyNativeMenu(tray, true, false, true, AdbView.Host.ThemePreference.Dark, 2);
        Check("fixture-menu-does-not-optimistically-change-snapshot-theme", _presenter.Snapshot.Shell.Theme == DeviceFixtures.Snapshot("native-scenarios").Shell.Theme);
        tray.Select(2); tray.Select(3); tray.Select(10);
        VerifyNativeMenu(tray, false, true, true, AdbView.Host.ThemePreference.System, 0);
        tray.Select(11); VerifyNativeMenu(tray, false, true, true, AdbView.Host.ThemePreference.Light, 1); tray.Select(10);
        Check("flyout-visible-suppresses-bubble", !native.Bubble.AppWindow.IsVisible);
        native.Hide(); await DispatcherTurnAsync().ConfigureAwait(true);
        Check("connected-bubble-permanent-visible", native.Bubble.AppWindow.IsVisible && _bubbleDeadlines.Count == 0);
        double scale = PInvoke.GetDpiForWindow(flyout) / 96.0;
        Check("bubble-is-fixed-32-dip", native.Bubble.AppWindow.ClientSize.Width == (int)Math.Round(32 * scale)
            && native.Bubble.AppWindow.ClientSize.Height == (int)Math.Round(32 * scale));
        var initialSize = native.Bubble.AppWindow.Size; var initialPosition = native.Bubble.AppWindow.Position;
        Check("bubble-has-normal-snapshot-tooltip", ToolTipService.GetToolTip(native.Bubble.OpenButton) as string == _presenter.Snapshot.Health.TrayTooltip);
        Check("bubble-is-nonactivating-and-not-in-switchers", PInvoke.GetForegroundWindow() != bubble && !native.Bubble.AppWindow.IsShownInSwitchers
            && (PInvoke.GetWindowLongPtr(bubble, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) & (nint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE) != 0
            && !native.Bubble.OpenButton.IsTabStop && !native.Bubble.OpenButton.AllowFocusOnInteraction);
        Check("bubble-rejects-mouse-activation", PInvoke.SendMessage(bubble, PInvoke.WM_MOUSEACTIVATE, default, default).Value == (int)PInvoke.MA_NOACTIVATE);
        native.Bubble.Activate();
        Check("explicit-bubble-activation-is-vetoed", PInvoke.GetForegroundWindow() != bubble);
        native.SetFullScreenSuppressed(true); Check("full-screen-input-suppresses-bubble", !native.Bubble.AppWindow.IsVisible);
        native.SetFullScreenSuppressed(false); Check("full-screen-input-cleared-restores-bubble", native.Bubble.AppWindow.IsVisible);
        native.SetSuspended(true); Check("suspend-input-suppresses-bubble", !native.Bubble.AppWindow.IsVisible);
        native.SetSuspended(false); Check("resume-input-restores-bubble", native.Bubble.AppWindow.IsVisible);
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveClick() => clicked.TrySetResult();
        native.Bubble.OpenRequested += ObserveClick;
        try {
            await System.IO.File.WriteAllTextAsync(_options.ResultPath + ".input-ready.json", System.Text.Json.JsonSerializer.Serialize(new {
                ProcessId = Environment.ProcessId,
                BubbleHwnd = (long)(nint)bubble,
                FlyoutHwnd = (long)(nint)flyout,
                ReceiverHwnd = (long)(nint)receiver,
                TestMessage = testMessage,
                TrayCallback = NativeTray.CallbackMessage,
                Scale = scale,
            })).ConfigureAwait(true);
            Check("external-mouse-action-delivered-to-owned-bubble", await Task.WhenAny(clicked.Task, Task.Delay(8000)).ConfigureAwait(true) == clicked.Task);
        }
        finally { native.Bubble.OpenRequested -= ObserveClick; }
        await DispatcherTurnAsync().ConfigureAwait(true);
        Check("real-bubble-click-opens-and-activates-flyout", AppWindow.IsVisible && !native.Bubble.AppWindow.IsVisible && PInvoke.GetForegroundWindow() == flyout);
        Check("real-bubble-input-never-activated-bubble", native.BubbleForegroundActivations == 0);
        int nativeWritesBeforeInput = _windowController.NativeBoundsWrites;
        await System.IO.File.WriteAllTextAsync(_options.ResultPath + ".keyboard-ready", "ready").ConfigureAwait(true);
        Check("external-drag-and-keyboard-driver-completed", await Task.WhenAny(inputComplete.Task, Task.Delay(10000)).ConfigureAwait(true) == inputComplete.Task);
        Check("native-drag-does-not-add-application-bounds-writes", _windowController.NativeBoundsWrites == nativeWritesBeforeInput);
        Check("external-input-keeps-bubble-nonactivating", native.BubbleForegroundActivations == 0);
        native.Hide();
        await InvokeButtonAsync(native.Bubble.OpenButton).ConfigureAwait(true);
        Stage($"bubble open action: flyout {AppWindow.IsVisible}, bubble {native.Bubble.AppWindow.IsVisible}, foreground {(nint)PInvoke.GetForegroundWindow():X}, expected {(nint)flyout:X}");
        Check("bubble-action-opens-flyout-and-hides-bubble", AppWindow.IsVisible && !native.Bubble.AppWindow.IsVisible && PInvoke.GetForegroundWindow() == flyout);
        native.Escape(); Check("escape-at-list-hides-flyout", !AppWindow.IsVisible && native.Bubble.AppWindow.IsVisible);
        PInvoke.SendMessage(receiver, NativeTray.CallbackMessage, default, new LPARAM((1 << 16) | (int)NativeTray.KeyboardSelect));
        Check("version-four-keyboard-selection-opens", AppWindow.IsVisible && tray.SelectionCount == 1 && !native.Bubble.AppWindow.IsVisible);
        PInvoke.SendMessage(receiver, NativeTray.CallbackMessage, default, new LPARAM((1 << 16) | (int)PInvoke.WM_LBUTTONUP));
        Check("legacy-button-up-does-not-double-toggle", AppWindow.IsVisible && tray.SelectionCount == 1);
        PInvoke.SendMessage(receiver, NativeTray.CallbackMessage, default, new LPARAM((2 << 16) | (int)PInvoke.NIN_SELECT));
        Check("unowned-icon-callback-is-ignored", AppWindow.IsVisible && tray.SelectionCount == 1);
        PInvoke.SendMessage(receiver, NativeTray.CallbackMessage, default, new LPARAM((1 << 16) | (int)PInvoke.NIN_SELECT));
        Check("version-four-pointer-selection-toggles", !AppWindow.IsVisible && tray.SelectionCount == 2);
        tray.Select(1); tray.Select(1); Check("open-command-is-idempotent", AppWindow.IsVisible && !native.Bubble.AppWindow.IsVisible);
        _presenter.OpenDevice(_presenter.Snapshot.Devices[0].Id);
        native.Escape(); Check("escape-from-details-returns-without-hide", _presenter.DetailsId is null && AppWindow.IsVisible);
        var popup = new Popup { XamlRoot = Root.XamlRoot, Child = new Border { Width = 24, Height = 24 }, IsOpen = true };
        await DispatcherTurnAsync().ConfigureAwait(true);
        native.Escape(); Check("escape-closes-owned-popup-before-flyout", !popup.IsOpen && AppWindow.IsVisible);
        popup.Child = null;
        _presenter.OpenDevice(_presenter.Snapshot.Devices[0].Id);
        PInvoke.SendMessage(receiver, NativeTray.CallbackMessage, default, new LPARAM((1 << 16) | (int)PInvoke.NIN_BALLOONUSERCLICK));
        Check("balloon-click-opens-current-list", AppWindow.IsVisible && _presenter.DetailsId is null);
        tray.RemoveForRecreationTest();
        Check("recreation-control-removes-only-candidate-icon", !tray.Registered);
        PInvoke.SendMessage(receiver, tray.TaskbarCreatedMessage, default, default);
        Check("receiver-recreates-and-renegotiates-version", tray.Registered && tray.AddCount == 2 && tray.VersionCount == 2);
        AppSnapshot current = _presenter.Snapshot;
        await PublishAndDrainAsync(current with { Revision = 20, Shell = current.Shell with { Pinned = true } }).ConfigureAwait(true);
        Check("authoritative-pin-enables-native-caption-region", native.CaptionRegionCount == 1);
        native.Escape(); Check("escape-hides-pinned-without-unpinning", !AppWindow.IsVisible && _presenter.Snapshot!.Shell.Pinned);
        native.Open();
        AppSnapshot unpinned = _presenter.Snapshot! with { Revision = 21, Shell = current.Shell with { Pinned = false } };
        await PublishAndDrainAsync(unpinned).ConfigureAwait(true);
        Check("authoritative-unpin-clears-caption-region", native.CaptionRegionCount == 0);
        int writes = _windowController.NativeBoundsWrites, bubbleWrites = _windowController.BubbleBoundsWrites;
        PInvoke.SendMessage(receiver, PInvoke.WM_DISPLAYCHANGE, default, default);
        PInvoke.SendMessage(flyout, PInvoke.WM_SETTINGCHANGE, default, default);
        PInvoke.SendMessage(receiver, PInvoke.WM_SETTINGCHANGE, default, default);
        await DispatcherTurnAsync().ConfigureAwait(true);
        Check("environment-burst-is-one-settled-recomputation", native.EnvironmentRecomputations == 1
            && _windowController.NativeBoundsWrites == writes + 1 && _windowController.BubbleBoundsWrites == bubbleWrites + 1);
        Check("environment-does-not-start-motion", !_presenter.ContentMotion.IsActive);
        native.Hide();
        AppSnapshot failure = DeviceFixtures.Snapshot("adb-unavailable", 22);
        await PublishAndDrainAsync(failure).ConfigureAwait(true);
        Check("failure-starts-one-transient-and-bubble", _bubbleDeadlines.Count == 1 && native.Bubble.AppWindow.IsVisible);
        await PublishAndDrainAsync(failure with { Revision = 23 }).ConfigureAwait(true);
        Check("same-transient-does-not-rearm", _bubbleDeadlines.Count == 1);
        _bubbleDeadlines[0].SetResult(); await Task.Delay(30).ConfigureAwait(true); await DispatcherTurnAsync().ConfigureAwait(true);
        Check("elapsed-injected-60-second-deadline-hides", !native.Bubble.AppWindow.IsVisible);
        await PublishAndDrainAsync(DeviceFixtures.Snapshot("empty", 24)).ConfigureAwait(true);
        Check("idle-without-prior-connection-is-hidden", !native.Bubble.AppWindow.IsVisible);
        await PublishAndDrainAsync(DeviceFixtures.Snapshot("list", 25)).ConfigureAwait(true);
        Check("new-permanent-state-restores-bubble", native.Bubble.AppWindow.IsVisible);
        await PublishAndDrainAsync(DeviceFixtures.Snapshot("empty", 26)).ConfigureAwait(true);
        Check("disconnect-transient-reuses-last-connection-detail", _bubbleDeadlines.Count == 2 && native.Bubble.AppWindow.IsVisible
            && ToolTipService.GetToolTip(native.Bubble.OpenButton) as string == current.Health.TrayTooltip);
        await PublishAndDrainAsync(DeviceFixtures.Snapshot("unauthorized", 27)).ConfigureAwait(true);
        _bubbleDeadlines[1].SetResult(); await Task.Delay(30).ConfigureAwait(true); await DispatcherTurnAsync().ConfigureAwait(true);
        Check("permanent-state-survives-stale-expiry", native.Bubble.AppWindow.IsVisible);
        Check("semantic-change-does-not-move-or-resize-bubble", native.Bubble.AppWindow.Size == initialSize && native.Bubble.AppWindow.Position == initialPosition);
        tray.Select(4); Check("bubble-preference-disables-immediately", !native.Bubble.AppWindow.IsVisible);
        tray.Select(4); Check("bubble-preference-restores-current-state", native.Bubble.AppWindow.IsVisible);
        await PublishAndDrainAsync(DeviceFixtures.Snapshot("adb-unavailable", 28)).ConfigureAwait(true);
        Check("shutdown-control-has-pending-deadline", _bubbleDeadlines.Count == 3);
        PInvoke.SendMessage(receiver, PInvoke.WM_DISPLAYCHANGE, default, default);
        int settled = native.EnvironmentRecomputations;
        native.Dispose(); native.Dispose();
        _bubbleDeadlines[2].SetResult(); await Task.Delay(30).ConfigureAwait(true); await DispatcherTurnAsync().ConfigureAwait(true);
        Check("native-owner-disposal-removes-icon-and-both-owned-hwnds", native.Disposed && !tray.Registered && !PInvoke.IsWindow(receiver) && !PInvoke.IsWindow(bubble));
        Check("queued-environment-and-expiry-cannot-revive-disposed-owner", native.EnvironmentRecomputations == settled && native.CaptionRegionCount == 0);
        ShowSurface();
    }
    private void VerifyNativeMenu(NativeTray tray, bool startup, bool notifications, bool bubble, AdbView.Host.ThemePreference theme, int themeIndex) {
        HMENU menu = tray.CreateMenu();
        bool destroyed;
        try {
            string[] expected = ["Open ADB View", "", "Start with Windows", "Notifications", "Status bubble", "Theme", "", "Exit"];
            Check("native-menu-exact-entry-count", PInvoke.GetMenuItemCount(menu) == expected.Length);
            for (int index = 0; index < expected.Length; index++) { Check("native-menu-label-" + index, MenuText(menu, index) == expected[index]); }
            Check("native-menu-default-open-item", PInvoke.GetMenuDefaultItem(menu, 0, 0) == 1);
            Check("native-menu-startup-check", MenuChecked(menu, 2) == startup);
            Check("native-menu-notification-check", MenuChecked(menu, 3) == notifications);
            Check("native-menu-bubble-check", MenuChecked(menu, 4) == bubble);
            HMENU themes = PInvoke.GetSubMenu(menu, 5);
            Check("native-theme-menu-exact-entry-count", !themes.IsNull && PInvoke.GetMenuItemCount(themes) == 3);
            string[] names = ["System", "Light", "Dark"];
            for (int index = 0; index < 3; index++) {
                Check("native-theme-label-and-exclusive-check-" + index, MenuText(themes, index) == names[index] && MenuChecked(themes, index) == (index == themeIndex));
            }
            Check("native-theme-selection", tray.Theme == theme);
        }
        finally { destroyed = PInvoke.DestroyMenu(menu); if (!destroyed) { Serilog.Log.Warning("Native fixture: menu cleanup failed"); } }
        Check("native-menu-and-submenu-cleanup", destroyed);
    }
    private static bool MenuChecked(HMENU menu, int index) => (PInvoke.GetMenuState(menu, (uint)index, MENU_ITEM_FLAGS.MF_BYPOSITION) & (uint)MENU_ITEM_FLAGS.MF_CHECKED) != 0;
    private static unsafe string MenuText(HMENU menu, int index) {
        if ((PInvoke.GetMenuState(menu, (uint)index, MENU_ITEM_FLAGS.MF_BYPOSITION) & (uint)MENU_ITEM_FLAGS.MF_SEPARATOR) != 0) { return string.Empty; }
        char* text = stackalloc char[128];
        int length = PInvoke.GetMenuString(menu, (uint)index, new PWSTR(text), 128, MENU_ITEM_FLAGS.MF_BYPOSITION);
        if (length == 0) { throw new InvalidOperationException("Native fixture: menu label query failed."); }
        return new string(text, 0, length);
    }
}
