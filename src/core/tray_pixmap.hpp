#pragma once

#include <cstdint>
#include <vector>

#include "core/snapshot.hpp"

namespace adbhawk::core {

struct TrayPixmap {
  int width = 0;
  int height = 0;
  // ARGB32 in network byte order (big-endian: [A, R, G, B] per pixel),
  // matching `org.kde.StatusNotifierItem` `IconPixmap` `a(iiay)` specification.
  std::vector<uint8_t> argb_network_bytes;
};

struct RgbaColor {
  uint8_t r = 0;
  uint8_t g = 0;
  uint8_t b = 0;
  uint8_t a = 255;
};

// Returns the semantic marker color for the given tray status and theme.
RgbaColor TrayMarkerColor(TrayStatusKind status, ThemeKind theme);

// Renders a single square tray icon pixmap of `size_px × size_px` in network byte order ARGB32.
TrayPixmap RenderTrayPixmap(int size_px, TrayStatusKind status);

// Renders the standard multi-resolution tray icon set (16, 22, 24, 32 px).
std::vector<TrayPixmap> RenderStandardTrayPixmaps(TrayStatusKind status);

}  // namespace adbhawk::core
