#include <cassert>
#include <cstdio>
#include <cstdlib>
#include <vector>

#include "core/shell_placement.hpp"

#define CHECK(cond)                                                          \
  do {                                                                       \
    if (!(cond)) {                                                           \
      std::fprintf(stderr, "FAIL: %s (%s:%d)\n", #cond, __FILE__, __LINE__); \
      std::exit(1);                                                          \
    }                                                                        \
  } while (0)

using namespace adbhawk::core;

void TestTopBarPanelPlacement() {
  PlacementInputs in{};
  in.monitors = {ScreenRect{0, 0, 1920, 1080}};
  in.workarea = ScreenRect{0, 40, 1920, 1040};  // 40px top bar
  in.tray_hint = ScreenPoint{1850, 20};          // Tray near top-right
  in.dimensions = FlyoutDimensions{376, 480, 8};
  in.scale_factor = 1.0;

  FlyoutPlacement p = ComputeFlyoutPlacement(in);
  CHECK(p.target_monitor_index == 0);
  CHECK(p.anchored_edge == PanelEdge::Top);
  // Clamped to workarea top (40 + 8 = 48) and right (1920 - 376 - 8 = 1536)
  CHECK(p.logical_position.x == 1536);
  CHECK(p.logical_position.y >= 48);
  CHECK(p.device_position.x == 1536);
  CHECK(p.device_position.y == p.logical_position.y);
}

void TestBottomBarPanelPlacement() {
  PlacementInputs in{};
  in.monitors = {ScreenRect{0, 0, 1920, 1080}};
  in.workarea = ScreenRect{0, 0, 1920, 1032};  // 48px bottom taskbar
  in.tray_hint = ScreenPoint{1850, 1056};       // Tray in bottom bar
  in.dimensions = FlyoutDimensions{376, 480, 8};
  in.scale_factor = 1.0;

  FlyoutPlacement p = ComputeFlyoutPlacement(in);
  CHECK(p.target_monitor_index == 0);
  CHECK(p.anchored_edge == PanelEdge::Bottom);
  CHECK(p.logical_position.x == 1536);
  // Clamped to workarea bottom: 1032 - 480 - 8 = 544
  CHECK(p.logical_position.y <= 544);
}

void TestVerticalBarPlacement() {
  PlacementInputs in_left{};
  in_left.monitors = {ScreenRect{0, 0, 1920, 1080}};
  in_left.workarea = ScreenRect{48, 0, 1872, 1080};  // Left dock
  in_left.tray_hint = ScreenPoint{24, 200};
  in_left.dimensions = FlyoutDimensions{376, 480, 8};
  auto p_left = ComputeFlyoutPlacement(in_left);
  CHECK(p_left.anchored_edge == PanelEdge::Left);
  CHECK(p_left.logical_position.x >= 56);

  PlacementInputs in_right{};
  in_right.monitors = {ScreenRect{0, 0, 1920, 1080}};
  in_right.workarea = ScreenRect{0, 0, 1872, 1080};  // Right dock
  in_right.tray_hint = ScreenPoint{1900, 200};
  in_right.dimensions = FlyoutDimensions{376, 480, 8};
  auto p_right = ComputeFlyoutPlacement(in_right);
  CHECK(p_right.anchored_edge == PanelEdge::Right);
  CHECK(p_right.logical_position.x <= 1872 - 376 - 8);
}

void TestTrayHintFallbackToPointer() {
  PlacementInputs in{};
  in.monitors = {ScreenRect{0, 0, 1920, 1080}};
  in.tray_hint = ScreenPoint{0, 0};  // Invalid / missing hint
  in.pointer_valid = true;
  in.pointer_position = ScreenPoint{1800, 30};
  in.dimensions = FlyoutDimensions{376, 480, 8};

  FlyoutPlacement p = ComputeFlyoutPlacement(in);
  CHECK(p.target_monitor_index == 0);
  CHECK(p.anchored_edge == PanelEdge::Top);
  // Centered horizontally around pointer (1800 - 376/2 = 1612), clamped to right bound 1536
  CHECK(p.logical_position.x == 1536);
}

void TestMultiMonitorWithNegativeOrigins() {
  PlacementInputs in{};
  // Secondary monitor to the left: -1920..0
  // Primary monitor: 0..1920
  in.monitors = {ScreenRect{0, 0, 1920, 1080}, ScreenRect{-1920, 0, 1920, 1080}};
  in.tray_hint = ScreenPoint{-50, 20};  // Top-right of secondary monitor
  in.dimensions = FlyoutDimensions{376, 480, 8};
  in.scale_factor = 1.0;

  FlyoutPlacement p = ComputeFlyoutPlacement(in);
  CHECK(p.target_monitor_index == 1);
  CHECK(p.anchored_edge == PanelEdge::Top);
  // Within Monitor 1: x in [-1920 + 8, 0 - 376 - 8] -> [-1912, -384]
  CHECK(p.logical_position.x <= -384);
  CHECK(p.logical_position.x >= -1912);
  CHECK(p.logical_position.y >= 8);
}

void TestHiDpiScaling() {
  PlacementInputs in{};
  in.monitors = {ScreenRect{0, 0, 1920, 1080}};
  in.tray_hint = ScreenPoint{1000, 20};
  in.dimensions = FlyoutDimensions{376, 480, 8};
  in.scale_factor = 2.0;

  FlyoutPlacement p = ComputeFlyoutPlacement(in);
  CHECK(p.device_position.x == p.logical_position.x * 2);
  CHECK(p.device_position.y == p.logical_position.y * 2);
}

void TestDismissDebounceRace() {
  int64_t hide_ts = 5000;

  // Signal arrives 100ms later -> inside 250ms window -> ignore/debounce
  CHECK(ShouldDebounceTrayToggle(hide_ts, 5100, 250) == true);
  // Signal arrives exactly at 250ms -> ignore/debounce
  CHECK(ShouldDebounceTrayToggle(hide_ts, 5250, 250) == true);
  // Signal arrives 300ms later -> outside window -> allow toggle
  CHECK(ShouldDebounceTrayToggle(hide_ts, 5300, 250) == false);
  // Invalid timestamp or earlier event -> allow toggle
  CHECK(ShouldDebounceTrayToggle(0, 5100, 250) == false);
  CHECK(ShouldDebounceTrayToggle(hide_ts, 4900, 250) == false);
}

int main() {
  TestTopBarPanelPlacement();
  TestBottomBarPanelPlacement();
  TestVerticalBarPlacement();
  TestTrayHintFallbackToPointer();
  TestMultiMonitorWithNegativeOrigins();
  TestHiDpiScaling();
  TestDismissDebounceRace();
  std::printf("OK shell placement and dismiss race tests passed\n");
  return 0;
}
