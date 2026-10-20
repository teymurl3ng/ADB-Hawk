#pragma once

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace adbhawk::core {

struct QrMatrix {
  int size = 0;  // modules per side (21..41)
  std::vector<uint8_t> modules;  // size * size, 1 = dark, 0 = light

  bool Get(int x, int y) const {
    if (x < 0 || y < 0 || x >= size || y >= size) {
      return false;
    }
    return modules[static_cast<size_t>(y * size + x)] != 0;
  }
};

// Encodes `payload` into a QR Code Model 2 (Byte mode, ECC Level M, Version 1..6).
// Returns std::nullopt if payload is empty or exceeds 106 bytes.
std::optional<QrMatrix> EncodeQrCode(std::string_view payload);

// Renders a `QrMatrix` into an SVG document string with `border_modules` quiet zone.
std::string RenderQrSvg(const QrMatrix& matrix, int border_modules = 4);

// Renders `payload` into a `data:image/svg+xml;base64,...` URI for `PairingQrView`.
std::optional<std::string> RenderQrSvgDataUri(std::string_view payload);

// Builds the AOSP Wireless Debugging QR payload: `WIFI:T:ADB;S:<instance>;P:<secret>;;`.
std::string BuildAdbQrPairingPayload(std::string_view instance_name,
                                     std::string_view six_digit_secret);

// Redacted diagnostic description (`adbview-session(<instance>, secret=***)`), per R-11.
std::string RedactQrPairingSession(std::string_view instance_name);

}  // namespace adbhawk::core
