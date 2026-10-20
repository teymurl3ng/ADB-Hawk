#pragma once

#include <atomic>
#include <cstdint>
#include <mutex>
#include <set>
#include <thread>
#include <vector>

#include "core/mdns_cache.hpp"
#include "core/reduce.hpp"

namespace adbhawk::platform {

struct MdnsBrowserConfig {
  uint16_t multicast_port = 5353;
  bool include_loopback = false;  // Set true in isolated G5 loopback/netns tests
  int64_t sweep_interval_ms = 2'000;
};

class MdnsBrowser {
 public:
  explicit MdnsBrowser(MdnsBrowserConfig config = {});
  ~MdnsBrowser();

  MdnsBrowser(const MdnsBrowser&) = delete;
  MdnsBrowser& operator=(const MdnsBrowser&) = delete;

  void Start();
  void Stop();

  // Eventfd signaled whenever a new `MdnsFallbackReceivedEvent` is queued.
  int NotifyFd() const { return notify_fd_; }

  std::vector<core::AdbViewEvent> DrainEvents();

  // Forces a multicast group re-join across all active interfaces (F10: resume from suspend)
  // and sends a fresh PTR query burst.
  void RejoinAndQuery();

  // Returns the number of valid ADB mDNS announcements currently in the cache.
  int CurrentAnnouncementCount() const;

 private:
  void ThreadMain();
  void RefreshMulticastMemberships(int rx4_fd, int rx6_fd, bool force_rejoin);
  void SendPtrQueriesOnAllInterfaces();
  void ProcessIncomingDatagram(int fd, int64_t now_ms);
  void EmitServicesIfChanged(int64_t now_ms, bool force_emit);

  MdnsBrowserConfig config_;
  int stop_fd_ = -1;
  int wakeup_fd_ = -1;
  int notify_fd_ = -1;
  bool running_ = false;
  std::thread thread_;

  mutable std::mutex mu_;
  bool stop_requested_ = false;
  bool rejoin_requested_ = false;
  std::vector<core::AdbViewEvent> pending_events_;
  core::MdnsRecordCache cache_;
  std::vector<core::RawMdnsService> last_emitted_;
  int64_t next_seq_ = 0;
  std::set<unsigned int> joined_v4_ifindices_;
  std::set<unsigned int> joined_v6_ifindices_;
  std::atomic<int> announcement_count_{0};
};

}  // namespace adbhawk::platform
