#pragma once

#include <cstdint>

namespace adbhawk::core {

enum class MotionRecipeKind {
  Instant,       // 0 ms (state changes, pin toggle)
  FastFade,      // 83 ms linear (hover/focus opacity)
  ShortSpatial,  // 167 ms cubic-bezier(0, 0, 0, 1) (flyout open/close, detail navigation)
};

// Returns effective duration in milliseconds (collapses to 0 when `reduced_motion` is true).
int64_t MotionDurationMs(MotionRecipeKind recipe, bool reduced_motion);

// Evaluates `cubic-bezier(x1, y1, x2, y2)` at normalized time `t` in [0, 1].
double EvaluateCubicBezier(double x1,
                           double y1,
                           double x2,
                           double y2,
                           double t);

// Evaluates the `DESIGN.md §41` short spatial easing `cubic-bezier(0, 0, 0, 1)` at `t` in [0, 1].
double EvaluateShortSpatialEasing(double t);

}  // namespace adbhawk::core
