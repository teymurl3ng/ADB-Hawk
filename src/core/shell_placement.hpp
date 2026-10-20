#pragma once

#include <cmath>
#include <cstdint>
#include <optional>
#include <span>
#include <vector>

namespace adbhawk::core {

struct ScreenPoint {
  int x = 0;
  int y = 0;

  bool operator==(const ScreenPoint&) const = default;
};

struct ScreenRect {
  int x = 0;
  int y = 0;
  int width = 0;
  int height = 0;

  bool Contains(ScreenPoint p) const {
    return p.x >= x && p.x < (x + width) && p.y >= y && p.y < (y + height);
  }

  bool IsEmpty() const { return width <= 0 || height <= 0; }

  bool operator==(const ScreenRect&) const = default;
};

enum class PanelEdge {
  Top,
  Bottom,
  Left,
  Right,
};

struct FlyoutDimensions {
  int width_dip = 240;
  int height_dip = 130;
  int margin_dip = 8;
};

struct PlacementInputs {
  ScreenPoint tray_hint{0, 0};
  ScreenPoint pointer_position{0, 0};
  bool pointer_valid = false;
  std::vector<ScreenRect> monitors;
  ScreenRect workarea{};  // Optional _NET_WORKAREA rectangle
  double scale_factor = 1.0;  // HiDPI scale factor (e.g. 1.0, 2.0)
  FlyoutDimensions dimensions{};
};

struct FlyoutPlacement {
  ScreenPoint logical_position{0, 0};
  ScreenPoint device_position{0, 0};
  PanelEdge anchored_edge = PanelEdge::Top;
  int target_monitor_index = 0;

  bool operator==(const FlyoutPlacement&) const = default;
};

// Computes the optimal (x, y) coordinates for the 376×480 DIP flyout panel.
// Validates tray_hint against monitors; if invalid or (0,0), falls back to pointer.
// Infers panel edge and clamps to the target monitor's bounds with margin.
FlyoutPlacement ComputeFlyoutPlacement(const PlacementInputs& in);

// Solves F4: Checks if a tray toggle should be debounced / ignored.
// If an activate/toggle signal arrives within `debounce_window_ms` (default 250ms)
// of a focus-loss deactivation hide, returns true (ignore).
bool ShouldDebounceTrayToggle(int64_t last_hide_deactivate_ms,
                              int64_t activate_timestamp_ms,
                              int64_t debounce_window_ms = 250);

}  // namespace adbhawk::core
