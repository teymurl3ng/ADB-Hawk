#pragma once

#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include "core/reduce.hpp"

namespace adbhawk::platform {

// Returns monotonic milliseconds since boot (`CLOCK_BOOTTIME`, D11).
int64_t NowBoottimeMs();

struct AdbIoConfig {
  uint16_t port = 5037;
  bool auto_port_discovery = true;
  std::optional<std::string> adb_executable_path;
  bool verify_listener_uid = true;
  bool allow_start_server = true;
  int64_t unavailable_timeout_ms = 10'000;
  int64_t reconnect_initial_backoff_ms = 150;
  int64_t reconnect_max_backoff_ms = 2'000;
};

class AdbIoWorker {
 public:
  explicit AdbIoWorker(AdbIoConfig config);
  ~AdbIoWorker();

  AdbIoWorker(const AdbIoWorker&) = delete;
  AdbIoWorker& operator=(const AdbIoWorker&) = delete;

  void Start();
  void Stop();

  // Returns current active ADB port in use.
  uint16_t ActivePort() const;

  // File descriptor signaled (readable uint64_t via eventfd) whenever new events are queued.
  // Can be attached to a GLib GSource on the main thread or polled in headless/test mode.
  int NotifyFd() const { return notify_fd_; }

  // Drains all pending events accumulated since the last drain (D3, R-05: one snapshot per drain).
  std::vector<core::AdbViewEvent> DrainEvents();

  // Enqueues a metadata probe (`getprop` + `dumpsys battery`) for a Connected device.
  void RequestMetadataProbe(std::string device_id, std::string adb_serial);

  // Enqueues a pairing request (`host:pair` wire service). Secret is zeroed on completion (R-11).
  void RequestPairing(std::string offer_id,
                      std::string host,
                      int port,
                      std::string six_digit_code,
                      std::string display_name);

  // Enqueues a `host:connect:<host>:<port>` request after pairing succeeds.
  void RequestConnect(std::string host, int port);

 private:
  struct MetadataTask {
    std::string device_id;
    std::string adb_serial;
  };

  struct PairTask {
    std::string offer_id;
    std::string host;
    int port = 0;
    std::string code;
    std::string display_name;
  };

  struct ConnectTask {
    std::string host;
    int port = 0;
  };

  void ThreadMain();
  void PushEvent(core::AdbViewEvent evt);
  void WakeWorker();
  void RunPendingTasks();
  void ExecuteMetadataTask(const MetadataTask& task);
  void ExecutePairTask(PairTask task);
  void ExecuteConnectTask(const ConnectTask& task);

  AdbIoConfig config_;
  uint16_t active_port_ = 5037;
  int stop_fd_ = -1;
  int wakeup_fd_ = -1;
  int notify_fd_ = -1;
  bool running_ = false;
  std::thread thread_;

  std::mutex mu_;
  bool stop_requested_ = false;
  std::vector<core::AdbViewEvent> pending_events_;
  std::vector<MetadataTask> metadata_tasks_;
  std::vector<PairTask> pair_tasks_;
  std::vector<ConnectTask> connect_tasks_;
  int64_t next_device_seq_ = 0;
  int64_t next_mdns_seq_ = 0;
};

}  // namespace adbhawk::platform
