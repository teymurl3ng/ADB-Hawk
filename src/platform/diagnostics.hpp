#pragma once

#include <sys/types.h>

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#include "core/snapshot.hpp"

namespace adbhawk::platform {

struct MulticastInterfaceInfo {
  unsigned int ifindex = 0;
  std::string name;
  bool is_virtual = false;
  std::vector<uint32_t> ipv4_addrs_be;  // network byte order

  bool operator==(const MulticastInterfaceInfo&) const = default;
};

// Parses `/proc/net/udp` and `/proc/net/udp6` text tables to identify UIDs
// holding UDP port 5353 (0x14E9) other than `self_uid`, resolved via `getpwuid_r` (F12).
std::vector<std::string> ParseUdp5353HoldersFromProc(
    std::string_view proc_net_udp,
    std::string_view proc_net_udp6,
    uid_t self_uid);

// Enumerates active multicast-capable non-loopback interfaces (`IFF_UP | IFF_RUNNING | IFF_MULTICAST`).
// Marks `is_virtual = true` if `/sys/devices/virtual/net/<name>` exists (F12).
std::vector<MulticastInterfaceInfo> EnumerateMulticastInterfaces();

// Collects a read-only `DiscoveryDiagnosis` snapshot combining the mDNS probe outcome,
// UDP 5353 port holders from `/proc/net/udp{,6}`, and active virtual interfaces.
core::DiscoveryDiagnosis CollectDiscoveryDiagnosis(
    core::DiscoveryProbeOutcome probe_outcome,
    int announcement_count);

}  // namespace adbhawk::platform
