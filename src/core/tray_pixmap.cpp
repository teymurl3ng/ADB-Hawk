#include "core/tray_pixmap.hpp"

#include <algorithm>
#include <cmath>

namespace adbhawk::core {

namespace {

void BlendPixel(std::vector<uint8_t>* buf,
                int width,
                int x,
                int y,
                RgbaColor src,
                double coverage) {
  if (coverage <= 0.0 || src.a == 0) {
    return;
  }
  coverage = std::clamp(coverage, 0.0, 1.0);
  double src_a = (static_cast<double>(src.a) / 255.0) * coverage;
  size_t idx = static_cast<size_t>((y * width + x) * 4);
  double dst_a = static_cast<double>((*buf)[idx]) / 255.0;
  double dst_r = static_cast<double>((*buf)[idx + 1]) / 255.0;
  double dst_g = static_cast<double>((*buf)[idx + 2]) / 255.0;
  double dst_b = static_cast<double>((*buf)[idx + 3]) / 255.0;

  double out_a = src_a + dst_a * (1.0 - src_a);
  if (out_a <= 1e-6) {
    (*buf)[idx] = 0;
    (*buf)[idx + 1] = 0;
    (*buf)[idx + 2] = 0;
    (*buf)[idx + 3] = 0;
    return;
  }
  double out_r =
      ((static_cast<double>(src.r) / 255.0) * src_a + dst_r * dst_a * (1.0 - src_a)) /
      out_a;
  double out_g =
      ((static_cast<double>(src.g) / 255.0) * src_a + dst_g * dst_a * (1.0 - src_a)) /
      out_a;
  double out_b =
      ((static_cast<double>(src.b) / 255.0) * src_a + dst_b * dst_a * (1.0 - src_a)) /
      out_a;

  (*buf)[idx] = static_cast<uint8_t>(std::clamp(std::lround(out_a * 255.0), 0L, 255L));
  (*buf)[idx + 1] =
      static_cast<uint8_t>(std::clamp(std::lround(out_r * 255.0), 0L, 255L));
  (*buf)[idx + 2] =
      static_cast<uint8_t>(std::clamp(std::lround(out_g * 255.0), 0L, 255L));
  (*buf)[idx + 3] =
      static_cast<uint8_t>(std::clamp(std::lround(out_b * 255.0), 0L, 255L));
}

// Signed distance to a rounded rectangle centered at (cx, cy) with half-extents (hx, hy) and radius r.
double SdfRoundedRect(double px,
                      double py,
                      double cx,
                      double cy,
                      double hx,
                      double hy,
                      double r) {
  double qx = std::abs(px - cx) - (hx - r);
  double qy = std::abs(py - cy) - (hy - r);
  double out_dist = std::hypot(std::max(qx, 0.0), std::max(qy, 0.0));
  double in_dist = std::min(std::max(qx, qy), 0.0);
  return out_dist + in_dist - r;
}

}  // namespace

RgbaColor TrayMarkerColor(TrayStatusKind status, ThemeKind theme) {
  bool dark = (theme == ThemeKind::Dark);
  switch (status) {
    case TrayStatusKind::Connected:
      return dark ? RgbaColor{0x6C, 0xCB, 0x5F, 0xFF}
                  : RgbaColor{0x0F, 0x7B, 0x0F, 0xFF};
    case TrayStatusKind::Attention:
      return dark ? RgbaColor{0xFC, 0xE1, 0x00, 0xFF}
                  : RgbaColor{0x9D, 0x5D, 0x00, 0xFF};
    case TrayStatusKind::Failure:
      return dark ? RgbaColor{0xFF, 0x99, 0xA4, 0xFF}
                  : RgbaColor{0xC4, 0x2B, 0x1C, 0xFF};
    case TrayStatusKind::Special:
    case TrayStatusKind::PairingAvailable:
      return dark ? RgbaColor{0x60, 0xCD, 0xFF, 0xFF}
                  : RgbaColor{0x00, 0x5F, 0xB8, 0xFF};
    case TrayStatusKind::Idle:
      return dark ? RgbaColor{0x9A, 0x9A, 0x9A, 0xFF}
                  : RgbaColor{0x61, 0x61, 0x61, 0xFF};
  }
  return RgbaColor{0x9A, 0x9A, 0x9A, 0xFF};
}

TrayPixmap RenderTrayPixmap(int size_px, TrayStatusKind status) {
  size_px = std::clamp(size_px, 8, 128);
  TrayPixmap out;
  out.width = size_px;
  out.height = size_px;
  out.argb_network_bytes.assign(static_cast<size_t>(size_px * size_px * 4), 0);

  double s = static_cast<double>(size_px);
  double cx = s * 0.46;
  double cy = s * 0.50;
  double hx = s * 0.25;
  double hy = s * 0.36;
  double r = s * 0.08;
  double stroke_half = std::max(0.75, s * 0.045);
  double halo_half = stroke_half + std::max(0.75, s * 0.04);

  RgbaColor halo_col{0x12, 0x12, 0x14, 0xCC};
  RgbaColor glyph_col{0xF3, 0xF3, 0xF5, 0xFF};

  // Draw the outlined phone silhouette so it reads on both dark and light panels (DESIGN.md / F2).
  for (int y = 0; y < size_px; ++y) {
    for (int x = 0; x < size_px; ++x) {
      double px = static_cast<double>(x) + 0.5;
      double py = static_cast<double>(y) + 0.5;
      double d_rect = SdfRoundedRect(px, py, cx, cy, hx, hy, r);
      double d_stroke = std::abs(d_rect) - stroke_half;
      double d_halo = std::abs(d_rect) - halo_half;

      double cov_halo = std::clamp(0.5 - d_halo, 0.0, 1.0);
      if (cov_halo > 0.0) {
        BlendPixel(&out.argb_network_bytes, size_px, x, y, halo_col, cov_halo);
      }
      double cov_stroke = std::clamp(0.5 - d_stroke, 0.0, 1.0);
      if (cov_stroke > 0.0) {
        BlendPixel(&out.argb_network_bytes, size_px, x, y, glyph_col,
                   cov_stroke);
      }
    }
  }

  // Overlay semantic status dot in the bottom-right corner.
  double dot_cx = s * 0.76;
  double dot_cy = s * 0.76;
  double dot_r = std::max(2.2, s * 0.17);
  double dot_halo_r = dot_r + std::max(0.9, s * 0.055);

  RgbaColor dot_col = TrayMarkerColor(status, ThemeKind::Dark);
  if (status == TrayStatusKind::Attention) {
    dot_col = RgbaColor{0xFC, 0xE1, 0x00, 0xFF};
  } else if (status == TrayStatusKind::Failure) {
    dot_col = RgbaColor{0xFF, 0x4D, 0x5A, 0xFF};
  }

  RgbaColor dot_halo{0x10, 0x10, 0x12, 0xEE};
  for (int y = 0; y < size_px; ++y) {
    for (int x = 0; x < size_px; ++x) {
      double px = static_cast<double>(x) + 0.5;
      double py = static_cast<double>(y) + 0.5;
      double dist = std::hypot(px - dot_cx, py - dot_cy);

      double cov_halo = std::clamp(0.5 - (dist - dot_halo_r), 0.0, 1.0);
      if (cov_halo > 0.0) {
        BlendPixel(&out.argb_network_bytes, size_px, x, y, dot_halo, cov_halo);
      }
      double cov_dot = std::clamp(0.5 - (dist - dot_r), 0.0, 1.0);
      if (cov_dot > 0.0) {
        BlendPixel(&out.argb_network_bytes, size_px, x, y, dot_col, cov_dot);
      }
    }
  }

  return out;
}

std::vector<TrayPixmap> RenderStandardTrayPixmaps(TrayStatusKind status) {
  return {
      RenderTrayPixmap(16, status),
      RenderTrayPixmap(22, status),
      RenderTrayPixmap(24, status),
      RenderTrayPixmap(32, status),
  };
}

}  // namespace adbhawk::core
