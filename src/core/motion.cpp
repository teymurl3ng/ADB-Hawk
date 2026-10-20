#include "core/motion.hpp"

#include <algorithm>
#include <cmath>

namespace adbhawk::core {

namespace {

double SampleCurve(double a, double b, double c, double u) {
  return ((a * u + b) * u + c) * u;
}

double SampleCurveDerivative(double a, double b, double c, double u) {
  return (3.0 * a * u + 2.0 * b) * u + c;
}

}  // namespace

int64_t MotionDurationMs(MotionRecipeKind recipe, bool reduced_motion) {
  if (reduced_motion) {
    return 0;
  }
  switch (recipe) {
    case MotionRecipeKind::Instant:
      return 0;
    case MotionRecipeKind::FastFade:
      return 83;
    case MotionRecipeKind::ShortSpatial:
      return 167;
  }
  return 0;
}

double EvaluateCubicBezier(double x1,
                           double y1,
                           double x2,
                           double y2,
                           double t) {
  if (t <= 0.0) {
    return 0.0;
  }
  if (t >= 1.0) {
    return 1.0;
  }
  if (x1 == y1 && x2 == y2) {
    return t;
  }

  double cx = 3.0 * x1;
  double bx = 3.0 * (x2 - x1) - cx;
  double ax = 1.0 - cx - bx;

  double cy = 3.0 * y1;
  double by = 3.0 * (y2 - y1) - cy;
  double ay = 1.0 - cy - by;

  double u = t;
  for (int i = 0; i < 8; ++i) {
    double x_err = SampleCurve(ax, bx, cx, u) - t;
    if (std::abs(x_err) < 1e-7) {
      return std::clamp(SampleCurve(ay, by, cy, u), 0.0, 1.0);
    }
    double dx = SampleCurveDerivative(ax, bx, cx, u);
    if (std::abs(dx) < 1e-6) {
      break;
    }
    u -= x_err / dx;
  }

  double lo = 0.0;
  double hi = 1.0;
  u = t;
  for (int i = 0; i < 20; ++i) {
    double x_val = SampleCurve(ax, bx, cx, u);
    if (std::abs(x_val - t) < 1e-7) {
      break;
    }
    if (t > x_val) {
      lo = u;
    } else {
      hi = u;
    }
    u = 0.5 * (lo + hi);
  }
  return std::clamp(SampleCurve(ay, by, cy, u), 0.0, 1.0);
}

double EvaluateShortSpatialEasing(double t) {
  return EvaluateCubicBezier(0.0, 0.0, 0.0, 1.0, t);
}

}  // namespace adbhawk::core
