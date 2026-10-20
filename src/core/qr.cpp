#include "core/qr.hpp"

#include <algorithm>
#include <array>
#include <cstdlib>
#include <limits>
#include <string>
#include <vector>

namespace adbhawk::core {

namespace {

struct VersionSpec {
  int version;
  int size;
  int data_codewords;
  int ec_codewords_per_block;
  int num_blocks;
  int align_coord;  // 0 for version 1, otherwise second coordinate in [6, align_coord]
};

constexpr std::array<VersionSpec, 6> kVersionSpecs = {{
    {1, 21, 16, 10, 1, 0},
    {2, 25, 28, 16, 1, 18},
    {3, 29, 44, 26, 1, 22},
    {4, 33, 64, 18, 2, 26},
    {5, 37, 86, 24, 2, 30},
    {6, 41, 108, 16, 4, 34},
}};

uint8_t GfMul(uint8_t x, uint8_t y) {
  uint8_t z = 0;
  for (int i = 7; i >= 0; --i) {
    z = static_cast<uint8_t>((z << 1) ^ ((z >> 7) * 0x1Du));
    z ^= static_cast<uint8_t>(((y >> i) & 1u) * x);
  }
  return z;
}

std::vector<uint8_t> ComputeReedSolomonGenerator(int degree) {
  std::vector<uint8_t> gen(static_cast<size_t>(degree), 0);
  gen.back() = 1;
  uint8_t root = 1;
  for (int i = 0; i < degree; ++i) {
    for (int j = 0; j < degree; ++j) {
      size_t idx = static_cast<size_t>(j);
      gen[idx] = GfMul(gen[idx], root);
      if (j + 1 < degree) {
        gen[idx] ^= gen[idx + 1];
      }
    }
    root = GfMul(root, 0x02u);
  }
  return gen;
}

std::vector<uint8_t> ComputeReedSolomonRemainder(
    std::span<const uint8_t> data,
    std::span<const uint8_t> generator) {
  std::vector<uint8_t> result(generator.size(), 0);
  for (uint8_t b : data) {
    uint8_t factor = b ^ result.front();
    result.erase(result.begin());
    result.push_back(0);
    for (size_t i = 0; i < generator.size(); ++i) {
      result[i] ^= GfMul(generator[i], factor);
    }
  }
  return result;
}

void AppendBits(uint32_t val, int num_bits, std::vector<uint8_t>* bits) {
  for (int i = num_bits - 1; i >= 0; --i) {
    bits->push_back(static_cast<uint8_t>((val >> i) & 1u));
  }
}

struct GridBuilder {
  int size = 0;
  std::vector<uint8_t> modules;
  std::vector<uint8_t> is_function;

  explicit GridBuilder(int s)
      : size(s),
        modules(static_cast<size_t>(s * s), 0),
        is_function(static_cast<size_t>(s * s), 0) {}

  void SetFunctionModule(int x, int y, bool dark) {
    if (x < 0 || y < 0 || x >= size || y >= size) {
      return;
    }
    size_t idx = static_cast<size_t>(y * size + x);
    modules[idx] = dark ? 1u : 0u;
    is_function[idx] = 1u;
  }

  void DrawFinderPattern(int cx, int cy) {
    for (int dy = -4; dy <= 4; ++dy) {
      for (int dx = -4; dx <= 4; ++dx) {
        int dist = std::max(std::abs(dx), std::abs(dy));
        int xx = cx + dx;
        int yy = cy + dy;
        if (xx >= 0 && xx < size && yy >= 0 && yy < size) {
          SetFunctionModule(xx, yy, dist != 2 && dist != 4);
        }
      }
    }
  }

  void DrawAlignmentPattern(int cx, int cy) {
    for (int dy = -2; dy <= 2; ++dy) {
      for (int dx = -2; dx <= 2; ++dx) {
        int dist = std::max(std::abs(dx), std::abs(dy));
        SetFunctionModule(cx + dx, cy + dy, dist != 1);
      }
    }
  }

  void DrawFormatBits(int mask) {
    // ECC Level M format bits = 00; data = (00 << 3) | mask.
    int data = (0 << 3) | mask;
    int rem = data;
    for (int i = 0; i < 10; ++i) {
      rem = (rem << 1) ^ ((rem >> 9) * 0x537);
    }
    int bits = ((data << 10) | rem) ^ 0x5412;

    for (int i = 0; i <= 5; ++i) {
      SetFunctionModule(8, i, ((bits >> i) & 1) != 0);
    }
    SetFunctionModule(8, 7, ((bits >> 6) & 1) != 0);
    SetFunctionModule(8, 8, ((bits >> 7) & 1) != 0);
    SetFunctionModule(7, 8, ((bits >> 8) & 1) != 0);
    for (int i = 9; i < 15; ++i) {
      SetFunctionModule(14 - i, 8, ((bits >> i) & 1) != 0);
    }

    for (int i = 0; i < 8; ++i) {
      SetFunctionModule(size - 1 - i, 8, ((bits >> i) & 1) != 0);
    }
    for (int i = 8; i < 15; ++i) {
      SetFunctionModule(8, size - 15 + i, ((bits >> i) & 1) != 0);
    }
    SetFunctionModule(8, size - 8, true);
  }
};

bool MaskBit(int mask, int x, int y) {
  switch (mask) {
    case 0:
      return (x + y) % 2 == 0;
    case 1:
      return y % 2 == 0;
    case 2:
      return x % 3 == 0;
    case 3:
      return (x + y) % 3 == 0;
    case 4:
      return (x / 3 + y / 2) % 2 == 0;
    case 5:
      return (x * y) % 2 + (x * y) % 3 == 0;
    case 6:
      return ((x * y) % 2 + (x * y) % 3) % 2 == 0;
    case 7:
      return ((x + y) % 2 + (x * y) % 3) % 2 == 0;
    default:
      return false;
  }
}

int ScorePenalty(const GridBuilder& g) {
  int penalty = 0;
  auto get = [&](int x, int y) -> bool {
    return g.modules[static_cast<size_t>(y * g.size + x)] != 0;
  };
  for (int y = 0; y < g.size; ++y) {
    bool run_color = false;
    int run_len = 0;
    for (int x = 0; x < g.size; ++x) {
      bool c = get(x, y);
      if (x == 0 || c != run_color) {
        run_color = c;
        run_len = 1;
      } else {
        ++run_len;
        if (run_len == 5) {
          penalty += 3;
        } else if (run_len > 5) {
          penalty += 1;
        }
      }
    }
  }
  for (int x = 0; x < g.size; ++x) {
    bool run_color = false;
    int run_len = 0;
    for (int y = 0; y < g.size; ++y) {
      bool c = get(x, y);
      if (y == 0 || c != run_color) {
        run_color = c;
        run_len = 1;
      } else {
        ++run_len;
        if (run_len == 5) {
          penalty += 3;
        } else if (run_len > 5) {
          penalty += 1;
        }
      }
    }
  }
  for (int y = 0; y + 1 < g.size; ++y) {
    for (int x = 0; x + 1 < g.size; ++x) {
      bool c = get(x, y);
      if (c == get(x + 1, y) && c == get(x, y + 1) && c == get(x + 1, y + 1)) {
        penalty += 3;
      }
    }
  }
  return penalty;
}

std::string Base64Encode(std::string_view input) {
  constexpr char kTable[] =
      "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  std::string out;
  out.reserve(((input.size() + 2) / 3) * 4);
  size_t i = 0;
  while (i + 3 <= input.size()) {
    uint32_t n =
        (static_cast<uint32_t>(static_cast<uint8_t>(input[i])) << 16) |
        (static_cast<uint32_t>(static_cast<uint8_t>(input[i + 1])) << 8) |
        static_cast<uint32_t>(static_cast<uint8_t>(input[i + 2]));
    out.push_back(kTable[(n >> 18) & 0x3Fu]);
    out.push_back(kTable[(n >> 12) & 0x3Fu]);
    out.push_back(kTable[(n >> 6) & 0x3Fu]);
    out.push_back(kTable[n & 0x3Fu]);
    i += 3;
  }
  size_t rem = input.size() - i;
  if (rem == 1) {
    uint32_t n = static_cast<uint32_t>(static_cast<uint8_t>(input[i])) << 16;
    out.push_back(kTable[(n >> 18) & 0x3Fu]);
    out.push_back(kTable[(n >> 12) & 0x3Fu]);
    out.push_back('=');
    out.push_back('=');
  } else if (rem == 2) {
    uint32_t n =
        (static_cast<uint32_t>(static_cast<uint8_t>(input[i])) << 16) |
        (static_cast<uint32_t>(static_cast<uint8_t>(input[i + 1])) << 8);
    out.push_back(kTable[(n >> 18) & 0x3Fu]);
    out.push_back(kTable[(n >> 12) & 0x3Fu]);
    out.push_back(kTable[(n >> 6) & 0x3Fu]);
    out.push_back('=');
  }
  return out;
}

}  // namespace

std::optional<QrMatrix> EncodeQrCode(std::string_view payload) {
  if (payload.empty()) {
    return std::nullopt;
  }
  const VersionSpec* spec = nullptr;
  for (const auto& s : kVersionSpecs) {
    // Byte mode header = 4 bits mode (0100) + 8 bits length = 12 bits -> 2 bytes overhead.
    if (static_cast<int>(payload.size()) + 2 <= s.data_codewords) {
      spec = &s;
      break;
    }
  }
  if (spec == nullptr) {
    return std::nullopt;
  }

  std::vector<uint8_t> bits;
  bits.reserve(static_cast<size_t>(spec->data_codewords * 8));
  AppendBits(0x4u, 4, &bits);  // Byte mode indicator 0100
  AppendBits(static_cast<uint32_t>(payload.size()), 8, &bits);
  for (unsigned char c : payload) {
    AppendBits(c, 8, &bits);
  }
  size_t capacity_bits = static_cast<size_t>(spec->data_codewords * 8);
  size_t term = std::min<size_t>(4, capacity_bits - bits.size());
  AppendBits(0, static_cast<int>(term), &bits);
  while (bits.size() % 8 != 0) {
    bits.push_back(0);
  }

  std::vector<uint8_t> data_bytes(static_cast<size_t>(spec->data_codewords), 0);
  for (size_t i = 0; i < bits.size(); ++i) {
    data_bytes[i / 8] =
        static_cast<uint8_t>((data_bytes[i / 8] << 1) | bits[i]);
  }
  size_t filled_bytes = bits.size() / 8;
  for (size_t i = filled_bytes; i < data_bytes.size(); ++i) {
    data_bytes[i] = ((i - filled_bytes) % 2 == 0) ? 0xECu : 0x11u;
  }

  // Split into RS blocks and interleave.
  std::vector<uint8_t> rs_gen =
      ComputeReedSolomonGenerator(spec->ec_codewords_per_block);
  int block_data_len = spec->data_codewords / spec->num_blocks;
  std::vector<std::vector<uint8_t>> data_blocks(
      static_cast<size_t>(spec->num_blocks));
  std::vector<std::vector<uint8_t>> ec_blocks(
      static_cast<size_t>(spec->num_blocks));
  for (int b = 0; b < spec->num_blocks; ++b) {
    size_t idx = static_cast<size_t>(b);
    size_t offset = static_cast<size_t>(b * block_data_len);
    std::span<const uint8_t> slice(data_bytes.data() + offset,
                                   static_cast<size_t>(block_data_len));
    data_blocks[idx].assign(slice.begin(), slice.end());
    ec_blocks[idx] = ComputeReedSolomonRemainder(slice, rs_gen);
  }

  std::vector<uint8_t> all_codewords;
  for (int i = 0; i < block_data_len; ++i) {
    for (int b = 0; b < spec->num_blocks; ++b) {
      all_codewords.push_back(
          data_blocks[static_cast<size_t>(b)][static_cast<size_t>(i)]);
    }
  }
  for (int i = 0; i < spec->ec_codewords_per_block; ++i) {
    for (int b = 0; b < spec->num_blocks; ++b) {
      all_codewords.push_back(
          ec_blocks[static_cast<size_t>(b)][static_cast<size_t>(i)]);
    }
  }

  GridBuilder base(spec->size);
  for (int i = 0; i < spec->size; ++i) {
    base.SetFunctionModule(6, i, i % 2 == 0);
    base.SetFunctionModule(i, 6, i % 2 == 0);
  }
  base.DrawFinderPattern(3, 3);
  base.DrawFinderPattern(spec->size - 4, 3);
  base.DrawFinderPattern(3, spec->size - 4);
  if (spec->align_coord > 0) {
    base.DrawAlignmentPattern(spec->align_coord, spec->align_coord);
  }
  base.DrawFormatBits(0);

  // Place codewords in upward/downward 2-column zigzag.
  size_t bit_idx = 0;
  size_t total_bits = all_codewords.size() * 8;
  for (int right = spec->size - 1; right >= 1; right -= 2) {
    if (right == 6) {
      right = 5;
    }
    for (int vert = 0; vert < spec->size; ++vert) {
      for (int j = 0; j < 2; ++j) {
        int x = right - j;
        bool upward = ((right + 1) & 2) == 0;
        int y = upward ? (spec->size - 1 - vert) : vert;
        size_t m_idx = static_cast<size_t>(y * spec->size + x);
        if (base.is_function[m_idx] == 0 && bit_idx < total_bits) {
          uint8_t byte_val = all_codewords[bit_idx / 8];
          bool dark = ((byte_val >> (7 - (bit_idx % 8))) & 1u) != 0;
          base.modules[m_idx] = dark ? 1u : 0u;
          ++bit_idx;
        }
      }
    }
  }

  int best_mask = 0;
  int best_penalty = std::numeric_limits<int>::max();
  GridBuilder best_grid = base;

  for (int m = 0; m < 8; ++m) {
    GridBuilder candidate = base;
    for (int y = 0; y < candidate.size; ++y) {
      for (int x = 0; x < candidate.size; ++x) {
        size_t idx = static_cast<size_t>(y * candidate.size + x);
        if (candidate.is_function[idx] == 0 && MaskBit(m, x, y)) {
          candidate.modules[idx] ^= 1u;
        }
      }
    }
    candidate.DrawFormatBits(m);
    int pen = ScorePenalty(candidate);
    if (pen < best_penalty) {
      best_penalty = pen;
      best_mask = m;
      best_grid = std::move(candidate);
    }
  }
  (void)best_mask;

  return QrMatrix{
      .size = best_grid.size,
      .modules = std::move(best_grid.modules),
  };
}

std::string RenderQrSvg(const QrMatrix& matrix, int border_modules) {
  int total = matrix.size + 2 * border_modules;
  std::string svg;
  svg.reserve(4096);
  svg.append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ");
  svg.append(std::to_string(total));
  svg.push_back(' ');
  svg.append(std::to_string(total));
  svg.append("\" shape-rendering=\"crispEdges\">");
  svg.append("<rect width=\"100%\" height=\"100%\" fill=\"#FFFFFF\"/>");
  svg.append("<path fill=\"#000000\" d=\"");
  for (int y = 0; y < matrix.size; ++y) {
    for (int x = 0; x < matrix.size; ++x) {
      if (matrix.Get(x, y)) {
        svg.push_back('M');
        svg.append(std::to_string(x + border_modules));
        svg.push_back(',');
        svg.append(std::to_string(y + border_modules));
        svg.append("h1v1h-1z");
      }
    }
  }
  svg.append("\"/></svg>");
  return svg;
}

std::optional<std::string> RenderQrSvgDataUri(std::string_view payload) {
  auto matrix = EncodeQrCode(payload);
  if (!matrix.has_value()) {
    return std::nullopt;
  }
  std::string svg = RenderQrSvg(*matrix, 4);
  return "data:image/svg+xml;base64," + Base64Encode(svg);
}

std::string BuildAdbQrPairingPayload(std::string_view instance_name,
                                     std::string_view six_digit_secret) {
  return "WIFI:T:ADB;S:" + std::string(instance_name) +
         ";P:" + std::string(six_digit_secret) + ";;";
}

std::string RedactQrPairingSession(std::string_view instance_name) {
  return "adbview-session(" + std::string(instance_name) + ", secret=***)";
}

}  // namespace adbhawk::core
