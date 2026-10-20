#pragma once

#include <gio/gio.h>

#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "core/snapshot.hpp"
#include "platform/autostart.hpp"

namespace adbhawk::platform {

enum class SingleInstanceStatus {
  AcquiredPrimary,
  SecondaryForwarded,
  Failed,
};

struct DbusMenuCallbacks {
  std::function<void()> on_open_flyout;
  std::function<void()> on_pair_qr;
  std::function<void(bool show)> on_toggle_bubble;
  std::function<void(ThemePreference theme)> on_select_theme;
  std::function<void(bool enabled)> on_toggle_autostart;
  std::function<void(bool suppress)> on_toggle_suppress_automount;
  std::function<void()> on_quit;
};

struct DbusEventCallbacks {
  std::function<void(int32_t x, int32_t y)> on_tray_activate;
  std::function<void()> on_instance_toggle_received;
  std::function<void(uint32_t notification_id, std::string action_key)>
      on_notification_action;
  std::function<void(bool sleeping_or_locked)> on_sleep_or_lock_changed;
  std::function<void(core::ThemeKind theme, bool reduced_motion)>
      on_appearance_changed;
};

class SessionDbusManager {
 public:
  SessionDbusManager(DbusMenuCallbacks menu_cb, DbusEventCallbacks event_cb);
  ~SessionDbusManager();

  SessionDbusManager(const SessionDbusManager&) = delete;
  SessionDbusManager& operator=(const SessionDbusManager&) = delete;

  // Initializes D-Bus connections (session bus + optional system bus for login1).
  // In unit tests, an existing test connection can be injected.
  SingleInstanceStatus Initialize(GDBusConnection* injected_session_bus = nullptr,
                                 GDBusConnection* injected_system_bus = nullptr);

  void Stop();

  // Updates the StatusNotifierItem pixmap, tooltip, and status dot from `snapshot`.
  void UpdateTrayFromSnapshot(const core::AppSnapshot& snapshot,
                              bool is_dark_panel);

  // Updates DBusMenu checkmarks and radio states.
  void UpdateMenuState(const UserPreferences& prefs, bool autostart_enabled);

  // Sends an XDG desktop notification via `org.freedesktop.Notifications`.
  // Returns the notification ID (which can be passed as `replaces_id` to collapse updates).
  uint32_t SendNotification(std::string_view title,
                            std::string_view body,
                            uint32_t replaces_id = 0,
                            bool urgency_critical = false,
                            std::optional<std::string_view> action_label = std::nullopt);

  // Reads and writes whether opening file manager on USB connect is suppressed (via GSettings automount-open).
  static bool ReadAutomountSuppressed();
  static void WriteAutomountSuppressed(bool suppress);

  // Whether a StatusNotifierHost is currently active on the session bus.
  bool HasStatusNotifierHost() const;

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};

}  // namespace adbhawk::platform
