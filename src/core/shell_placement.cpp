#include "core/shell_placement.hpp"

#include <algorithm>
#include <cmath>
#include <cstdlib>

namespace adbhawk::core {

namespace {

ScreenRect IntersectRects(const ScreenRect& a, const ScreenRect& b) {
  int x1 = std::max(a.x, b.x);
  int y1 = std::max(a.y, b.y);
  int x2 = std::min(a.x + a.width, b.x + b.width);
  int y2 = std::min(a.y + a.height, b.y + b.height);
  if (x2 <= x1 || y2 <= y1) {
    return ScreenRect{0, 0, 0, 0};
  }
  return ScreenRect{x1, y1, x2 - x1, y2 - y1};
}

int FindContainingMonitorIndex(ScreenPoint pt,
                               std::span<const ScreenRect> monitors) {
  for (size_t i = 0; i < monitors.size(); ++i) {
    if (monitors[i].Contains(pt)) {
      return static_cast<int>(i);
    }
  }
  return -1;
}

}  // namespace

FlyoutPlacement ComputeFlyoutPlacement(const PlacementInputs& in) {
  FlyoutPlacement result{};

  std::vector<ScreenRect> monitors = in.monitors;
  if (monitors.empty()) {
    monitors.push_back(ScreenRect{0, 0, 1920, 1080});
  }

  // 1. Resolve Anchor Point (F3)
  ScreenPoint anchor = in.tray_hint;
  int monitor_idx = -1;

  // Reject (0, 0) hint or out-of-bounds hints; fallback to pointer
  if (anchor.x != 0 || anchor.y != 0) {
    monitor_idx = FindContainingMonitorIndex(anchor, monitors);
  }

  if (monitor_idx < 0) {
    if (in.pointer_valid) {
      anchor = in.pointer_position;
      monitor_idx = FindContainingMonitorIndex(anchor, monitors);
    }
  }

  // Final fallback if both tray hint and pointer are invalid/out-of-bounds:
  // default to the top-right corner of the primary monitor
  if (monitor_idx < 0) {
    monitor_idx = 0;
    anchor = ScreenPoint{monitors[0].x + monitors[0].width - 100,
                         monitors[0].y + 10};
  }

  result.target_monitor_index = monitor_idx;
  const ScreenRect& target_monitor = monitors[static_cast<size_t>(monitor_idx)];

  // 2. Usable Area (Intersect with Workarea if valid)
  ScreenRect usable = target_monitor;
  if (!in.workarea.IsEmpty()) {
    ScreenRect inter = IntersectRects(target_monitor, in.workarea);
    if (inter.width >= in.dimensions.width_dip &&
        inter.height >= in.dimensions.height_dip) {
      usable = inter;
    }
  }

  // 3. Panel Edge Inference
  // Distance to the 4 edges of the target monitor
  int dist_top = std::abs(anchor.y - target_monitor.y);
  int dist_bottom =
      std::abs((target_monitor.y + target_monitor.height) - anchor.y);
  int dist_left = std::abs(anchor.x - target_monitor.x);
  int dist_right =
      std::abs((target_monitor.x + target_monitor.width) - anchor.x);

  int min_dist = std::min({dist_top, dist_bottom, dist_left, dist_right});
  PanelEdge edge = PanelEdge::Top;
  if (min_dist == dist_bottom) {
    edge = PanelEdge::Bottom;
  } else if (min_dist == dist_left) {
    edge = PanelEdge::Left;
  } else if (min_dist == dist_right) {
    edge = PanelEdge::Right;
  }
  result.anchored_edge = edge;

  // 4. Calculate Logical (DIP) Position
  const int w = in.dimensions.width_dip;
  const int h = in.dimensions.height_dip;
  const int margin = in.dimensions.margin_dip;

  int x = 0;
  int y = 0;

  switch (edge) {
    case PanelEdge::Top:
      y = anchor.y + margin;
      x = anchor.x - (w / 2);
      break;
    case PanelEdge::Bottom:
      y = anchor.y - h - margin;
      x = anchor.x - (w / 2);
      break;
    case PanelEdge::Left:
      x = anchor.x + margin;
      y = anchor.y - (h / 2);
      break;
    case PanelEdge::Right:
      x = anchor.x - w - margin;
      y = anchor.y - (h / 2);
      break;
  }

  // 5. Clamp to Usable Screen Area
  int min_x = usable.x + margin;
  int max_x = usable.x + usable.width - w - margin;
  if (max_x >= min_x) {
    x = std::clamp(x, min_x, max_x);
  } else {
    x = usable.x;
  }

  int min_y = usable.y + margin;
  int max_y = usable.y + usable.height - h - margin;
  if (max_y >= min_y) {
    y = std::clamp(y, min_y, max_y);
  } else {
    y = usable.y;
  }

  result.logical_position = ScreenPoint{x, y};

  // 6. HiDPI Device-Pixel Scaling (F5)
  double scale = in.scale_factor > 0.0 ? in.scale_factor : 1.0;
  result.device_position = ScreenPoint{
      static_cast<int>(std::round(static_cast<double>(x) * scale)),
      static_cast<int>(std::round(static_cast<double>(y) * scale)),
  };

  return result;
}

bool ShouldDebounceTrayToggle(int64_t last_hide_deactivate_ms,
                              int64_t activate_timestamp_ms,
                              int64_t debounce_window_ms) {
  if (last_hide_deactivate_ms <= 0 || activate_timestamp_ms <= 0) {
    return false;
  }
  int64_t delta = activate_timestamp_ms - last_hide_deactivate_ms;
  return delta >= 0 && delta <= debounce_window_ms;
}

}  // namespace adbhawk::core
