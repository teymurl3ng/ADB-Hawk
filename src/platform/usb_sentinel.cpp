#include "platform/usb_sentinel.hpp"

#include <dirent.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

#include <algorithm>
#include <cctype>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace adbhawk::platform {

namespace {

std::string ReadSmallFileTrimmed(const std::string& path) {
  int fd = ::open(path.c_str(), O_RDONLY | O_CLOEXEC);
  if (fd < 0) {
    return {};
  }
  char buf[256];
  ssize_t n = ::read(fd, buf, sizeof(buf) - 1);
  ::close(fd);
  if (n <= 0) {
    return {};
  }
  buf[n] = '\0';
  std::string_view sv(buf, static_cast<size_t>(n));
  while (!sv.empty() &&
         std::isspace(static_cast<unsigned char>(sv.front())) != 0) {
    sv.remove_prefix(1);
  }
  while (!sv.empty() &&
         std::isspace(static_cast<unsigned char>(sv.back())) != 0) {
    sv.remove_suffix(1);
  }
  return std::string(sv);
}

bool IsAdbInterfaceDirectory(const std::string& iface_path) {
  std::string cls = ReadSmallFileTrimmed(iface_path + "/bInterfaceClass");
  std::string sub = ReadSmallFileTrimmed(iface_path + "/bInterfaceSubClass");
  std::string prt = ReadSmallFileTrimmed(iface_path + "/bInterfaceProtocol");

  // ADB USB Interface specification:
  // Class: 0xff (255, Vendor Specific)
  // SubClass: 0x42 (66, ADB)
  // Protocol: 0x01 (1, ADB)
  bool cls_ok = (cls == "ff" || cls == "FF" || cls == "255");
  bool sub_ok = (sub == "42" || sub == "66");
  bool prt_ok = (prt == "01" || prt == "1");

  return cls_ok && sub_ok && prt_ok;
}

std::optional<UsbAdbDevice> InspectDeviceDirectory(const std::string& dev_dir,
                                                   const std::string& bus_id) {
  UsbAdbDevice dev{};
  dev.sysfs_device_path = dev_dir;
  dev.bus_id = bus_id;
  dev.id_vendor = ReadSmallFileTrimmed(dev_dir + "/idVendor");
  dev.id_product = ReadSmallFileTrimmed(dev_dir + "/idProduct");
  dev.product = ReadSmallFileTrimmed(dev_dir + "/product");
  dev.manufacturer = ReadSmallFileTrimmed(dev_dir + "/manufacturer");
  dev.serial = ReadSmallFileTrimmed(dev_dir + "/serial");

  if (dev.id_vendor.empty() && dev.id_product.empty() && dev.product.empty()) {
    return std::nullopt;
  }
  return dev;
}

}  // namespace

std::vector<UsbAdbDevice> ScanSysfsUsbAdbDevices(const std::string& sysfs_root) {
  std::vector<UsbAdbDevice> devices;
  std::unordered_set<std::string> seen_bus_ids;

  DIR* dir = ::opendir(sysfs_root.c_str());
  if (dir == nullptr) {
    return devices;
  }

  struct dirent* de = nullptr;
  while ((de = ::readdir(dir)) != nullptr) {
    std::string name = de->d_name;
    if (name == "." || name == "..") {
      continue;
    }

    // Pattern 1: Flat interface entry in sysfs_root containing a colon, e.g. "1-4:1.3"
    size_t colon_pos = name.find(':');
    if (colon_pos != std::string::npos && colon_pos > 0) {
      std::string iface_path = sysfs_root + "/" + name;
      if (IsAdbInterfaceDirectory(iface_path)) {
        std::string bus_id = name.substr(0, colon_pos);
        if (!seen_bus_ids.contains(bus_id)) {
          std::string dev_dir = sysfs_root + "/" + bus_id;
          auto dev = InspectDeviceDirectory(dev_dir, bus_id);
          if (dev.has_value()) {
            seen_bus_ids.insert(bus_id);
            devices.push_back(std::move(*dev));
          }
        }
      }
      continue;
    }

    // Pattern 2: Device directory, e.g. "1-4", containing child interface directories "1-4:1.3"
    std::string dev_dir = sysfs_root + "/" + name;
    DIR* dev_sub = ::opendir(dev_dir.c_str());
    if (dev_sub != nullptr) {
      struct dirent* sub_de = nullptr;
      while ((sub_de = ::readdir(dev_sub)) != nullptr) {
        std::string sub_name = sub_de->d_name;
        if (sub_name.find(':') != std::string::npos) {
          std::string sub_iface = dev_dir + "/" + sub_name;
          if (IsAdbInterfaceDirectory(sub_iface)) {
            if (!seen_bus_ids.contains(name)) {
              auto dev = InspectDeviceDirectory(dev_dir, name);
              if (dev.has_value()) {
                seen_bus_ids.insert(name);
                devices.push_back(std::move(*dev));
              }
            }
            break;
          }
        }
      }
      ::closedir(dev_sub);
    }
  }

  ::closedir(dir);

  std::sort(devices.begin(), devices.end(),
            [](const UsbAdbDevice& a, const UsbAdbDevice& b) {
              return a.bus_id < b.bus_id;
            });
  return devices;
}

}  // namespace adbhawk::platform
