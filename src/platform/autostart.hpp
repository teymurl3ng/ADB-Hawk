#pragma once

#include <string>
#include <string_view>

namespace adbhawk::platform {

enum class ThemePreference {
  System,
  Light,
  Dark,
};

struct UserPreferences {
  ThemePreference theme = ThemePreference::System;
  bool show_status_bubble = false;

  bool operator==(const UserPreferences&) const = default;
};

// Resolves `/proc/self/exe` as an absolute path.
std::string ResolveSelfExecutablePath();

// Quotes and escapes an absolute executable path for a Desktop Entry `Exec=` line
// (escapes `\`, `"`, `` ` ``, `$`, and doubles `%` -> `%%`).
std::string FormatDesktopExecValue(std::string_view abs_exec_path);

// Reads XDG autostart state from `<config_home>/autostart/adb-hawk.desktop`
// (falls back to legacy `adb-view.desktop` if present).
// Honors `Hidden=true` and `X-GNOME-Autostart-enabled=false` (written by DE settings tools).
bool ReadAutostartEnabled(std::string_view config_home_override = "");

// Atomically writes or updates `<config_home>/autostart/adb-hawk.desktop`
// using 0700 directory + 0600 temp file + fsync + rename + dir fsync.
bool WriteAutostartEnabled(bool enabled,
                           std::string_view abs_exec_path = "",
                           std::string_view config_home_override = "");

// Reads `<config_home>/adb-hawk/prefs.ini` (falls back to `adb-view/prefs.ini`).
// Returns default `UserPreferences` if absent.
UserPreferences ReadUserPreferences(std::string_view config_home_override = "");

// Atomically writes `<config_home>/adb-hawk/prefs.ini` (mode 0600 in 0700 dir, fsync + rename).
bool WriteUserPreferences(const UserPreferences& prefs,
                          std::string_view config_home_override = "");

}  // namespace adbhawk::platform
