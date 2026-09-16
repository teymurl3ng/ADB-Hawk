using System;

namespace AdbView.App;

internal static class CommandLine {
    // Minimal parsing for Phase 1. Add args as needed.
    public static bool ShouldToggleAndExit(string[] args) {
        // Actually, SingleInstance already handles toggling the owner process.
        // We might not need explicit args for toggle yet, since second instance auto-toggles.
        return false;
    }
}
