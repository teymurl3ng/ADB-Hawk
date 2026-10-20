#include "core/protobuf_lite.hpp"

#include <string>
#include <unordered_set>

namespace adbhawk::core {

namespace {

constexpr size_t kMaxNestedBytes = 64 * 1024;

class ProtoReader {
 public:
  explicit ProtoReader(std::span<const uint8_t> buf) : buf_(buf) {}

  bool AtEnd() const { return pos_ >= buf_.size(); }

  bool TryReadVarint(uint64_t* out) {
    uint64_t result = 0;
    int shift = 0;
    for (int i = 0; i < 10; ++i) {
      if (pos_ >= buf_.size()) {
        return false;
      }
      uint8_t b = buf_[pos_++];
      if (i == 9 && (b & 0xFEu) != 0) {
        return false;  // 64-bit overflow on 10th byte
      }
      result |= static_cast<uint64_t>(b & 0x7Fu) << shift;
      if ((b & 0x80u) == 0) {
        *out = result;
        return true;
      }
      shift += 7;
    }
    return false;
  }

  bool TryReadTag(uint32_t* out_field_number, uint32_t* out_wire_type) {
    if (AtEnd()) {
      return false;
    }
    uint64_t tag = 0;
    if (!TryReadVarint(&tag) || tag == 0 || (tag >> 3) > 0x1FFFFFFFu) {
      return false;
    }
    *out_field_number = static_cast<uint32_t>(tag >> 3);
    *out_wire_type = static_cast<uint32_t>(tag & 0x7u);
    return true;
  }

  bool TryReadInt32(int* out) {
    uint64_t v = 0;
    if (!TryReadVarint(&v)) {
      return false;
    }
    *out = static_cast<int>(static_cast<int32_t>(static_cast<uint32_t>(v)));
    return true;
  }

  bool TryReadBool(bool* out) {
    uint64_t v = 0;
    if (!TryReadVarint(&v)) {
      return false;
    }
    *out = (v != 0);
    return true;
  }

  bool TryReadLengthDelimited(std::span<const uint8_t>* out) {
    uint64_t len = 0;
    if (!TryReadVarint(&len)) {
      return false;
    }
    if (len > buf_.size() - pos_) {
      return false;
    }
    size_t sz = static_cast<size_t>(len);
    *out = buf_.subspan(pos_, sz);
    pos_ += sz;
    return true;
  }

  bool TryReadString(std::string* out) {
    std::span<const uint8_t> slice;
    if (!TryReadLengthDelimited(&slice)) {
      return false;
    }
    out->assign(reinterpret_cast<const char*>(slice.data()), slice.size());
    return true;
  }

  bool TrySkipField(uint32_t wire_type) {
    switch (wire_type) {
      case 0: {  // Varint
        uint64_t dummy = 0;
        return TryReadVarint(&dummy);
      }
      case 1: {  // 64-bit
        if (buf_.size() - pos_ < 8) {
          return false;
        }
        pos_ += 8;
        return true;
      }
      case 2: {  // Length-delimited
        std::span<const uint8_t> dummy;
        return TryReadLengthDelimited(&dummy);
      }
      case 5: {  // 32-bit
        if (buf_.size() - pos_ < 4) {
          return false;
        }
        pos_ += 4;
        return true;
      }
      default:
        return false;
    }
  }

 private:
  std::span<const uint8_t> buf_;
  size_t pos_ = 0;
};

MdnsServiceKind ClassifyServiceType(std::string_view service_type) {
  if (service_type == "_adb-tls-connect._tcp") {
    return MdnsServiceKind::Connect;
  }
  if (service_type == "_adb-tls-pairing._tcp") {
    return MdnsServiceKind::Pairing;
  }
  if (service_type == "_adb._tcp") {
    return MdnsServiceKind::LegacyTcp;
  }
  return MdnsServiceKind::Unknown;
}

bool TryParseSingleDeviceProto(std::span<const uint8_t> bytes, RawDevice* out) {
  ProtoReader reader(bytes);
  RawDevice dev;
  dev.is_proto_binary = true;

  while (!reader.AtEnd()) {
    uint32_t field = 0;
    uint32_t wire = 0;
    if (!reader.TryReadTag(&field, &wire)) {
      return false;
    }
    switch (field) {
      case 1:  // serial
        if (wire != 2 || !reader.TryReadString(&dev.serial)) {
          return false;
        }
        break;
      case 2: {  // state
        int st = 0;
        if (wire != 0 || !reader.TryReadInt32(&st)) {
          return false;
        }
        dev.proto_state = st;
        break;
      }
      case 4: {  // product
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        dev.product = std::move(s);
        break;
      }
      case 5: {  // model
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        dev.model = std::move(s);
        break;
      }
      case 6: {  // device
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        dev.device = std::move(s);
        break;
      }
      case 7: {  // connection_type
        int ct = 0;
        if (wire != 0 || !reader.TryReadInt32(&ct)) {
          return false;
        }
        dev.connection_type = ct;
        break;
      }
      default:
        if (!reader.TrySkipField(wire)) {
          return false;
        }
        break;
    }
  }

  *out = std::move(dev);
  return true;
}

bool TryParseInnerMdnsService(std::span<const uint8_t> bytes,
                              std::optional<bool> known_device,
                              RawMdnsService* out) {
  ProtoReader reader(bytes);
  RawMdnsService svc;
  svc.known_device = known_device;

  while (!reader.AtEnd()) {
    uint32_t field = 0;
    uint32_t wire = 0;
    if (!reader.TryReadTag(&field, &wire)) {
      return false;
    }
    switch (field) {
      case 1:
        if (wire != 2 || !reader.TryReadString(&svc.instance_name)) {
          return false;
        }
        break;
      case 2:
        if (wire != 2 || !reader.TryReadString(&svc.service_type)) {
          return false;
        }
        break;
      case 3: {
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        svc.domain = std::move(s);
        break;
      }
      case 4: {
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        svc.ipv4 = std::move(s);
        break;
      }
      case 5: {
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        svc.ipv6.push_back(std::move(s));
        break;
      }
      case 6:
        if (wire != 0 || !reader.TryReadInt32(&svc.port)) {
          return false;
        }
        break;
      case 7: {
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        svc.product_model = std::move(s);
        break;
      }
      case 8: {
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        svc.build_version_sdk_full = std::move(s);
        break;
      }
      case 9: {
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        svc.given_name = std::move(s);
        break;
      }
      case 10: {
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        svc.serial = std::move(s);
        break;
      }
      case 11: {
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        svc.mdns_service_version = std::move(s);
        break;
      }
      case 12: {
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        svc.hostname = std::move(s);
        break;
      }
      default:
        if (!reader.TrySkipField(wire)) {
          return false;
        }
        break;
    }
  }

  svc.kind = ClassifyServiceType(svc.service_type);
  *out = std::move(svc);
  return true;
}

bool TryParseWrappedMdnsService(std::span<const uint8_t> bytes,
                                bool is_tls,
                                std::optional<RawMdnsService>* out) {
  ProtoReader reader(bytes);
  std::span<const uint8_t> service_bytes;
  bool has_service = false;
  std::optional<bool> known_device;

  while (!reader.AtEnd()) {
    uint32_t field = 0;
    uint32_t wire = 0;
    if (!reader.TryReadTag(&field, &wire)) {
      return false;
    }
    if (field == 1) {
      if (wire != 2 || !reader.TryReadLengthDelimited(&service_bytes)) {
        return false;
      }
      has_service = true;
    } else if (is_tls && field == 2) {
      bool kd = false;
      if (wire != 0 || !reader.TryReadBool(&kd)) {
        return false;
      }
      known_device = kd;
    } else {
      if (!reader.TrySkipField(wire)) {
        return false;
      }
    }
  }

  if (!has_service) {
    *out = std::nullopt;
    return true;
  }
  RawMdnsService svc;
  if (!TryParseInnerMdnsService(service_bytes, known_device, &svc)) {
    return false;
  }
  *out = std::move(svc);
  return true;
}

}  // namespace

bool TryParseAdbServerStatusProto(std::span<const uint8_t> bytes,
                                  AdbServerStatus* out_status) {
  if (out_status == nullptr || bytes.size() > kMaxAdbFrameBytes) {
    return false;
  }
  ProtoReader reader(bytes);
  AdbServerStatus status;

  while (!reader.AtEnd()) {
    uint32_t field = 0;
    uint32_t wire = 0;
    if (!reader.TryReadTag(&field, &wire)) {
      return false;
    }
    switch (field) {
      case 3: {  // mdns_backend
        int val = 0;
        if (wire != 0 || !reader.TryReadInt32(&val)) {
          return false;
        }
        switch (val) {
          case 1:
            status.mdns_backend = AdbMdnsBackend::Bonjour;
            break;
          case 2:
            status.mdns_backend = AdbMdnsBackend::Openscreen;
            break;
          case 3:
            status.mdns_backend = AdbMdnsBackend::Libadbmdns;
            break;
          default:
            status.mdns_backend = AdbMdnsBackend::Unknown;
            break;
        }
        break;
      }
      case 5:  // version
        if (wire != 2 || !reader.TryReadString(&status.version)) {
          return false;
        }
        break;
      case 7: {  // executable_absolute_path
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        status.executable_path = std::move(s);
        break;
      }
      case 8: {  // log_absolute_path
        std::string s;
        if (wire != 2 || !reader.TryReadString(&s)) {
          return false;
        }
        status.log_path = std::move(s);
        break;
      }
      case 12:  // mdns_enabled
        if (wire != 0 || !reader.TryReadBool(&status.mdns_enabled)) {
          return false;
        }
        break;
      default:
        if (!reader.TrySkipField(wire)) {
          return false;
        }
        break;
    }
  }

  *out_status = std::move(status);
  return true;
}

bool TryParseDevicesProto(std::span<const uint8_t> bytes,
                          std::vector<RawDevice>* out_devices) {
  if (out_devices == nullptr || bytes.size() > kMaxAdbFrameBytes) {
    return false;
  }
  out_devices->clear();
  if (bytes.empty()) {
    return true;
  }

  ProtoReader reader(bytes);
  while (!reader.AtEnd()) {
    uint32_t field = 0;
    uint32_t wire = 0;
    if (!reader.TryReadTag(&field, &wire)) {
      return false;
    }
    if (field == 1) {
      std::span<const uint8_t> nested;
      if (wire != 2 || !reader.TryReadLengthDelimited(&nested) ||
          nested.size() > kMaxNestedBytes) {
        return false;
      }
      RawDevice dev;
      if (!TryParseSingleDeviceProto(nested, &dev)) {
        return false;
      }
      out_devices->push_back(std::move(dev));
    } else {
      if (!reader.TrySkipField(wire)) {
        return false;
      }
    }
  }
  return true;
}

bool TryParseMdnsServicesProto(std::span<const uint8_t> bytes,
                               std::vector<RawMdnsService>* out_services) {
  if (out_services == nullptr || bytes.size() > kMaxAdbFrameBytes) {
    return false;
  }
  out_services->clear();
  if (bytes.empty()) {
    return true;
  }

  std::unordered_set<std::string> seen;
  ProtoReader reader(bytes);
  while (!reader.AtEnd()) {
    uint32_t field = 0;
    uint32_t wire = 0;
    if (!reader.TryReadTag(&field, &wire)) {
      return false;
    }
    if (field == 1 || field == 2 || field == 3) {
      std::span<const uint8_t> nested;
      if (wire != 2 || !reader.TryReadLengthDelimited(&nested) ||
          nested.size() > kMaxNestedBytes) {
        return false;
      }
      std::optional<RawMdnsService> svc;
      if (!TryParseWrappedMdnsService(nested, field == 2, &svc)) {
        return false;
      }
      if (svc.has_value() &&
          (!svc->instance_name.empty() || !svc->service_type.empty())) {
        if (seen.insert(svc->DiscoveryKey()).second) {
          out_services->push_back(std::move(*svc));
        }
      }
    } else {
      if (!reader.TrySkipField(wire)) {
        return false;
      }
    }
  }
  return true;
}

}  // namespace adbhawk::core
