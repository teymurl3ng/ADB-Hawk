using System;
using Microsoft.Win32;
using Serilog;

namespace AdbView.Host;

internal sealed class PowerAndSession : IDisposable {
    public event EventHandler? Suspend;
    public event EventHandler? Resume;
    public event EventHandler<SessionSwitchEventArgs>? SessionSwitch;
    public event EventHandler? DisplaySettingsChanged;

    public PowerAndSession() {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) {
        Log.Information("Power mode changed: {Mode}", e.Mode);
        if (e.Mode == PowerModes.Suspend) Suspend?.Invoke(this, EventArgs.Empty);
        else if (e.Mode == PowerModes.Resume) Resume?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) {
        Log.Information("Session switched: {Reason}", e.Reason);
        SessionSwitch?.Invoke(this, e);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) {
        Log.Information("Display settings changed.");
        DisplaySettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
