#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace adbhawk::platform {

struct UsbAdbDevice {
  std::string sysfs_device_path;
  std::string bus_id;
  std::string id_vendor;
  std::string id_product;
  std::string product;
  std::string manufacturer;
  std::string serial;

  bool operator==(const UsbAdbDevice&) const = default;
};

// Scans Linux sysfs (`sysfs_root`, defaults to "/sys/bus/usb/devices") for USB
// devices presenting the Android Debug Bridge interface (Class 0xff, Subclass 0x42, Protocol 0x01).
// Completely non-intrusive (read-only filesystem reads, zero USB claims, zero root required).
std::vector<UsbAdbDevice> ScanSysfsUsbAdbDevices(
    const std::string& sysfs_root = "/sys/bus/usb/devices");

}  // namespace adbhawk::platform
