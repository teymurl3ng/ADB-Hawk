#pragma once

#include <functional>
#include <memory>
#include <string>

#include "core/shell_placement.hpp"
#include "core/snapshot.hpp"
#include "platform/autostart.hpp"

namespace adbhawk::shell {

struct ShellCallbacks {
  std::function<void()> on_open_flyout;
  std::function<void()> on_pair_qr;
  std::function<void(std::string device_id)> on_disconnect_device;
  std::function<void(std::string offer_id,
                     std::string host,
                     int port,
                     std::string code,
                     std::string display_name)>
      on_pair_device;
  std::function<void()> on_restart_adb;
  std::function<void(bool pinned)> on_toggle_pin;
  std::function<void(bool show)> on_toggle_bubble;
  std::function<void(platform::ThemePreference theme)> on_select_theme;
  std::function<void(bool enabled)> on_toggle_autostart;
  std::function<void(bool suppress)> on_toggle_suppress_automount;
  std::function<void()> on_quit;
};

class IShell {
 public:
  virtual ~IShell() = default;

  virtual void ShowFlyout(core::ScreenPoint hint) = 0;
  virtual void HideFlyout() = 0;
  virtual void ToggleFlyout(core::ScreenPoint hint) = 0;
  virtual bool IsFlyoutVisible() const = 0;

  virtual void ShowBubble() = 0;
  virtual void HideBubble() = 0;
  virtual bool IsBubbleVisible() const = 0;

  virtual void ShowPairingPage() = 0;
  virtual void UpdateSnapshot(const core::AppSnapshot& snapshot) = 0;
  virtual void UpdateTheme(platform::ThemePreference theme) = 0;
};

}  // namespace adbhawk::shell
