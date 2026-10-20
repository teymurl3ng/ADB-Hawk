#include "platform/repair_restart_adb.hpp"

#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/wait.h>
#include <unistd.h>

#include <cstring>
#include <string>

#include "core/adb_wire.hpp"
#include "platform/adb_locator.hpp"

namespace adbhawk::platform {

namespace {

bool SendHostKillWire(uint16_t port) {
  int fd = ::socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
  if (fd < 0) {
    return false;
  }

  sockaddr_in addr{};
  addr.sin_family = AF_INET;
  addr.sin_port = htons(port);
  addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

  int rc = ::connect(fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr));
  if (rc != 0) {
    struct pollfd pfd {};
    pfd.fd = fd;
    pfd.events = POLLOUT;
    if (::poll(&pfd, 1, 1000) <= 0) {
      ::close(fd);
      return false;
    }
    int err = 0;
    socklen_t len = sizeof(err);
    if (::getsockopt(fd, SOL_SOCKET, SO_ERROR, &err, &len) != 0 || err != 0) {
      ::close(fd);
      return false;
    }
  }

  auto frame = core::EncodeAdbFrame("host:kill");
  if (!frame.has_value()) {
    ::close(fd);
    return false;
  }
  ssize_t sent = ::send(fd, frame->data(), frame->size(), MSG_NOSIGNAL);
  if (sent != static_cast<ssize_t>(frame->size())) {
    ::close(fd);
    return false;
  }

  struct pollfd rfd {};
  rfd.fd = fd;
  rfd.events = POLLIN;
  if (::poll(&rfd, 1, 1000) > 0) {
    char status[4];
    (void)::recv(fd, status, sizeof(status), 0);
  }
  ::close(fd);
  return true;
}

bool SpawnAdbStartServer(const std::string& adb_path) {
  pid_t pid = ::fork();
  if (pid < 0) {
    return false;
  }
  if (pid == 0) {
    int devnull = ::open("/dev/null", O_RDWR | O_CLOEXEC);
    if (devnull >= 0) {
      ::dup2(devnull, STDIN_FILENO);
      ::dup2(devnull, STDOUT_FILENO);
      ::dup2(devnull, STDERR_FILENO);
      if (devnull > 2) {
        ::close(devnull);
      }
    }
    char arg0[] = "adb";
    char arg1[] = "start-server";
    char* const argv[] = {arg0, arg1, nullptr};
    ::execv(adb_path.c_str(), argv);
    ::_exit(127);
  }
  int status = 0;
  if (::waitpid(pid, &status, 0) < 0) {
    return false;
  }
  return WIFEXITED(status) && WEXITSTATUS(status) == 0;
}

}  // namespace

core::DiscoveryRepairOutcome ExecuteUserInitiatedRestartAdb(
    uint16_t port,
    const std::optional<std::string>& adb_path) {
  auto owner = VerifyLocalAdbServerOwner(port, ::geteuid());
  if (owner == ListenerOwnerStatus::OwnedByOtherUid) {
    return core::DiscoveryRepairOutcome::Failed;
  }
  if (owner == ListenerOwnerStatus::OwnedByExpectedUid) {
    (void)SendHostKillWire(port);
  }
  if (!adb_path.has_value() || adb_path->empty()) {
    return core::DiscoveryRepairOutcome::Failed;
  }
  if (!SpawnAdbStartServer(*adb_path)) {
    return core::DiscoveryRepairOutcome::Failed;
  }
  return core::DiscoveryRepairOutcome::Completed;
}

}  // namespace adbhawk::platform
