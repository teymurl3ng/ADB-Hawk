using System;
using Microsoft.Win32;
using Serilog;

namespace AdbView.Host;

internal sealed class PowerAndSession : IDisposable {
    public event EventHandler? Suspend;
    public event EventHandler? Resume;
    public event EventHandler? SessionSwitch;
    public event EventHandler? DisplaySettingsChanged;

    public PowerAndSession() {
        // SystemEvents requires Microsoft.Win32.SystemEvents package.
        // Wait, the project doesn't have it yet. The user says: 
        // "If Microsoft.Win32.SystemEvents requires an explicit NuGet dependency... 
        // confirm the requirement, record it in research, amend ENGINEERING.md dependency policy if necessary, pin the exact version.
        // Current stable package version is 10.0.12, but only add it if actually required."
        // Actually, in .NET 10 windows targeted (net10.0-windows), SystemEvents is included in the Desktop framework.
        // Let's verify if we can use it without NuGet.

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
        SessionSwitch?.Invoke(this, EventArgs.Empty);
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
