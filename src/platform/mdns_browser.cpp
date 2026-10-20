#include "platform/mdns_browser.hpp"

#include <arpa/inet.h>
#include <linux/netlink.h>
#include <linux/rtnetlink.h>
#include <net/if.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/eventfd.h>
#include <sys/socket.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstring>
#include <string_view>
#include <vector>

#include "core/mdns_wire.hpp"
#include "platform/adb_io.hpp"
#include "platform/diagnostics.hpp"

namespace adbhawk::platform {

namespace {

constexpr std::string_view kAdbServiceDomains[] = {
    "_adb-tls-pairing._tcp.local",
    "_adb-tls-connect._tcp.local",
    "_adb._tcp.local",
};

void DrainEventFd(int fd) {
  uint64_t val = 0;
  while (::read(fd, &val, sizeof(val)) > 0) {
  }
}

void SignalEventFd(int fd) {
  if (fd < 0) {
    return;
  }
  uint64_t val = 1;
  if (::write(fd, &val, sizeof(val)) < 0) {
    // Ignore EAGAIN / EBADF on eventfd.
  }
}

int OpenPassiveMdnsIpv4Socket(uint16_t port) {
  int fd = ::socket(AF_INET, SOCK_DGRAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
  if (fd < 0) {
    return -1;
  }
  int one = 1;
  (void)::setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
  unsigned char loop = 1;
  (void)::setsockopt(fd, IPPROTO_IP, IP_MULTICAST_LOOP, &loop, sizeof(loop));

  struct sockaddr_in sin {};
  sin.sin_family = AF_INET;
  sin.sin_addr.s_addr = htonl(INADDR_ANY);
  sin.sin_port = htons(port);
  if (::bind(fd, reinterpret_cast<struct sockaddr*>(&sin), sizeof(sin)) != 0) {
    ::close(fd);
    return -1;
  }
  return fd;
}

int OpenPassiveMdnsIpv6Socket(uint16_t port) {
  int fd = ::socket(AF_INET6, SOCK_DGRAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
  if (fd < 0) {
    return -1;
  }
  int one = 1;
  (void)::setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
  (void)::setsockopt(fd, IPPROTO_IPV6, IPV6_V6ONLY, &one, sizeof(one));
  unsigned int loop6 = 1;
  (void)::setsockopt(fd, IPPROTO_IPV6, IPV6_MULTICAST_LOOP, &loop6,
                     sizeof(loop6));

  struct sockaddr_in6 sin6 {};
  sin6.sin6_family = AF_INET6;
  sin6.sin6_addr = in6addr_any;
  sin6.sin6_port = htons(port);
  if (::bind(fd, reinterpret_cast<struct sockaddr*>(&sin6), sizeof(sin6)) !=
      0) {
    ::close(fd);
    return -1;
  }
  return fd;
}

int OpenRouteNetlinkSocket() {
  int fd = ::socket(AF_NETLINK, SOCK_RAW | SOCK_NONBLOCK | SOCK_CLOEXEC,
                    NETLINK_ROUTE);
  if (fd < 0) {
    return -1;
  }
  struct sockaddr_nl sa {};
  sa.nl_family = AF_NETLINK;
  sa.nl_groups = RTMGRP_LINK | RTMGRP_IPV4_IFADDR | RTMGRP_IPV6_IFADDR;
  if (::bind(fd, reinterpret_cast<struct sockaddr*>(&sa), sizeof(sa)) != 0) {
    ::close(fd);
    return -1;
  }
  return fd;
}

bool JoinIpv4MulticastOnIfIndex(int fd, unsigned int ifindex) {
  if (fd < 0 || ifindex == 0) {
    return false;
  }
  struct ip_mreqn mreq {};
  (void)::inet_pton(AF_INET, "224.0.0.251", &mreq.imr_multiaddr);
  mreq.imr_address.s_addr = htonl(INADDR_ANY);
  mreq.imr_ifindex = static_cast<int>(ifindex);
  if (::setsockopt(fd, IPPROTO_IP, IP_ADD_MEMBERSHIP, &mreq, sizeof(mreq)) ==
      0) {
    return true;
  }
  return errno == EADDRINUSE;
}

bool DropIpv4MulticastOnIfIndex(int fd, unsigned int ifindex) {
  if (fd < 0 || ifindex == 0) {
    return false;
  }
  struct ip_mreqn mreq {};
  (void)::inet_pton(AF_INET, "224.0.0.251", &mreq.imr_multiaddr);
  mreq.imr_address.s_addr = htonl(INADDR_ANY);
  mreq.imr_ifindex = static_cast<int>(ifindex);
  return ::setsockopt(fd, IPPROTO_IP, IP_DROP_MEMBERSHIP, &mreq,
                      sizeof(mreq)) == 0;
}

bool JoinIpv6MulticastOnIfIndex(int fd, unsigned int ifindex) {
  if (fd < 0 || ifindex == 0) {
    return false;
  }
  struct ipv6_mreq mreq6 {};
  (void)::inet_pton(AF_INET6, "ff02::fb", &mreq6.ipv6mr_multiaddr);
  mreq6.ipv6mr_interface = ifindex;
  if (::setsockopt(fd, IPPROTO_IPV6, IPV6_JOIN_GROUP, &mreq6, sizeof(mreq6)) ==
      0) {
    return true;
  }
  return errno == EADDRINUSE;
}

bool DropIpv6MulticastOnIfIndex(int fd, unsigned int ifindex) {
  if (fd < 0 || ifindex == 0) {
    return false;
  }
  struct ipv6_mreq mreq6 {};
  (void)::inet_pton(AF_INET6, "ff02::fb", &mreq6.ipv6mr_multiaddr);
  mreq6.ipv6mr_interface = ifindex;
  return ::setsockopt(fd, IPPROTO_IPV6, IPV6_LEAVE_GROUP, &mreq6,
                      sizeof(mreq6)) == 0;
}

void SendIpv4PacketOnIfIndex(unsigned int ifindex,
                             uint16_t dest_port,
                             const std::vector<uint8_t>& pkt) {
  if (pkt.empty() || ifindex == 0) {
    return;
  }
  int fd = ::socket(AF_INET, SOCK_DGRAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
  if (fd < 0) {
    return;
  }
  unsigned char ttl = 255;
  (void)::setsockopt(fd, IPPROTO_IP, IP_MULTICAST_TTL, &ttl, sizeof(ttl));
  unsigned char loop = 1;
  (void)::setsockopt(fd, IPPROTO_IP, IP_MULTICAST_LOOP, &loop, sizeof(loop));

  struct ip_mreqn mreq {};
  mreq.imr_ifindex = static_cast<int>(ifindex);
  (void)::setsockopt(fd, IPPROTO_IP, IP_MULTICAST_IF, &mreq, sizeof(mreq));

  struct sockaddr_in dst {};
  dst.sin_family = AF_INET;
  (void)::inet_pton(AF_INET, "224.0.0.251", &dst.sin_addr);
  dst.sin_port = htons(dest_port);

  (void)::sendto(fd, pkt.data(), pkt.size(), MSG_DONTWAIT | MSG_NOSIGNAL,
                 reinterpret_cast<const struct sockaddr*>(&dst), sizeof(dst));
  ::close(fd);
}

}  // namespace

MdnsBrowser::MdnsBrowser(MdnsBrowserConfig config) : config_(config) {
  stop_fd_ = ::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
  wakeup_fd_ = ::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
  notify_fd_ = ::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
}

MdnsBrowser::~MdnsBrowser() {
  Stop();
  if (stop_fd_ >= 0) {
    ::close(stop_fd_);
  }
  if (wakeup_fd_ >= 0) {
    ::close(wakeup_fd_);
  }
  if (notify_fd_ >= 0) {
    ::close(notify_fd_);
  }
}

void MdnsBrowser::Start() {
  if (running_) {
    return;
  }
  DrainEventFd(stop_fd_);
  {
    std::lock_guard<std::mutex> lock(mu_);
    stop_requested_ = false;
    rejoin_requested_ = false;
  }
  running_ = true;
  thread_ = std::thread(&MdnsBrowser::ThreadMain, this);
}

void MdnsBrowser::Stop() {
  if (!running_) {
    return;
  }
  {
    std::lock_guard<std::mutex> lock(mu_);
    stop_requested_ = true;
  }
  SignalEventFd(stop_fd_);
  SignalEventFd(wakeup_fd_);
  if (thread_.joinable()) {
    thread_.join();
  }
  running_ = false;
}

std::vector<core::AdbViewEvent> MdnsBrowser::DrainEvents() {
  DrainEventFd(notify_fd_);
  std::lock_guard<std::mutex> lock(mu_);
  std::vector<core::AdbViewEvent> out;
  out.swap(pending_events_);
  return out;
}

void MdnsBrowser::RejoinAndQuery() {
  {
    std::lock_guard<std::mutex> lock(mu_);
    rejoin_requested_ = true;
  }
  SignalEventFd(wakeup_fd_);
}

int MdnsBrowser::CurrentAnnouncementCount() const {
  return announcement_count_.load();
}

void MdnsBrowser::RefreshMulticastMemberships(int rx4_fd,
                                              int rx6_fd,
                                              bool force_rejoin) {
  auto ifaces = EnumerateMulticastInterfaces();
  std::set<unsigned int> target_ifindices;
  for (const auto& iface : ifaces) {
    target_ifindices.insert(iface.ifindex);
  }
  if (config_.include_loopback) {
    unsigned int lo_idx = ::if_nametoindex("lo");
    if (lo_idx != 0) {
      target_ifindices.insert(lo_idx);
    }
  }

  std::lock_guard<std::mutex> lock(mu_);
  if (force_rejoin) {
    for (unsigned int idx : joined_v4_ifindices_) {
      (void)DropIpv4MulticastOnIfIndex(rx4_fd, idx);
    }
    for (unsigned int idx : joined_v6_ifindices_) {
      (void)DropIpv6MulticastOnIfIndex(rx6_fd, idx);
    }
    joined_v4_ifindices_.clear();
    joined_v6_ifindices_.clear();
  }

  for (unsigned int idx : target_ifindices) {
    if (!joined_v4_ifindices_.contains(idx)) {
      if (JoinIpv4MulticastOnIfIndex(rx4_fd, idx)) {
        joined_v4_ifindices_.insert(idx);
      }
    }
    if (!joined_v6_ifindices_.contains(idx)) {
      if (JoinIpv6MulticastOnIfIndex(rx6_fd, idx)) {
        joined_v6_ifindices_.insert(idx);
      }
    }
  }
}

void MdnsBrowser::SendPtrQueriesOnAllInterfaces() {
  std::set<unsigned int> indices;
  {
    std::lock_guard<std::mutex> lock(mu_);
    indices = joined_v4_ifindices_;
  }
  for (std::string_view domain : kAdbServiceDomains) {
    auto pkt = core::BuildMdnsQuery(0, domain, core::kDnsTypePtr);
    for (unsigned int idx : indices) {
      SendIpv4PacketOnIfIndex(idx, config_.multicast_port, pkt);
    }
  }
}

void MdnsBrowser::ProcessIncomingDatagram(int fd, int64_t now_ms) {
  uint8_t buf[9000];
  while (true) {
    ssize_t n = ::recv(fd, buf, sizeof(buf), MSG_DONTWAIT);
    if (n <= 0) {
      break;
    }
    std::span<const uint8_t> packet(buf, static_cast<size_t>(n));
    std::vector<core::DnsRecord> records;
    if (!core::TryParseMdnsRecords(packet, &records) || records.empty()) {
      continue;
    }

    bool changed = false;
    std::vector<std::pair<std::string, uint16_t>> followups;
    std::set<unsigned int> indices;
    {
      std::lock_guard<std::mutex> lock(mu_);
      changed = cache_.Absorb(records, now_ms);
      followups = cache_.TakeDueQuestions(now_ms);
      indices = joined_v4_ifindices_;
      announcement_count_.store(static_cast<int>(cache_.Services().size()));
    }

    for (const auto& [qname, qtype] : followups) {
      auto qpkt = core::BuildMdnsQuery(0, qname, qtype);
      for (unsigned int idx : indices) {
        SendIpv4PacketOnIfIndex(idx, config_.multicast_port, qpkt);
      }
    }

    if (changed) {
      EmitServicesIfChanged(now_ms, /*force_emit=*/false);
    }
  }
}

void MdnsBrowser::EmitServicesIfChanged(int64_t now_ms, bool force_emit) {
  bool should_signal = false;
  {
    std::lock_guard<std::mutex> lock(mu_);
    const auto& current = cache_.Services();
    announcement_count_.store(static_cast<int>(current.size()));
    if (force_emit || current != last_emitted_) {
      last_emitted_ = current;
      pending_events_.push_back(core::MdnsFallbackReceivedEvent{
          .sequence = ++next_seq_,
          .at_ms = now_ms,
          .services = current,
      });
      should_signal = true;
    }
  }
  if (should_signal) {
    SignalEventFd(notify_fd_);
  }
}

void MdnsBrowser::ThreadMain() {
  int rx4_fd = OpenPassiveMdnsIpv4Socket(config_.multicast_port);
  int rx6_fd = OpenPassiveMdnsIpv6Socket(config_.multicast_port);
  int nl_fd = OpenRouteNetlinkSocket();

  RefreshMulticastMemberships(rx4_fd, rx6_fd, /*force_rejoin=*/false);
  SendPtrQueriesOnAllInterfaces();

  while (true) {
    {
      std::lock_guard<std::mutex> lock(mu_);
      if (stop_requested_) {
        break;
      }
    }

    struct pollfd pfds[5]{};
    nfds_t nfds = 0;

    pfds[nfds].fd = stop_fd_;
    pfds[nfds].events = POLLIN;
    nfds_t stop_slot = nfds++;

    pfds[nfds].fd = wakeup_fd_;
    pfds[nfds].events = POLLIN;
    nfds_t wake_slot = nfds++;

    nfds_t rx4_slot = 99;
    if (rx4_fd >= 0) {
      rx4_slot = nfds;
      pfds[nfds].fd = rx4_fd;
      pfds[nfds].events = POLLIN;
      ++nfds;
    }

    nfds_t rx6_slot = 99;
    if (rx6_fd >= 0) {
      rx6_slot = nfds;
      pfds[nfds].fd = rx6_fd;
      pfds[nfds].events = POLLIN;
      ++nfds;
    }

    nfds_t nl_slot = 99;
    if (nl_fd >= 0) {
      nl_slot = nfds;
      pfds[nfds].fd = nl_fd;
      pfds[nfds].events = POLLIN;
      ++nfds;
    }

    int pr = ::poll(pfds, nfds, static_cast<int>(config_.sweep_interval_ms));
    if (pr < 0) {
      if (errno == EINTR) {
        continue;
      }
      break;
    }

    if ((pfds[stop_slot].revents & POLLIN) != 0) {
      break;
    }

    int64_t now_ms = NowBoottimeMs();

    if ((pfds[wake_slot].revents & POLLIN) != 0) {
      DrainEventFd(wakeup_fd_);
      bool do_rejoin = false;
      {
        std::lock_guard<std::mutex> lock(mu_);
        if (stop_requested_) {
          break;
        }
        do_rejoin = rejoin_requested_;
        rejoin_requested_ = false;
      }
      if (do_rejoin) {
        RefreshMulticastMemberships(rx4_fd, rx6_fd, /*force_rejoin=*/true);
        SendPtrQueriesOnAllInterfaces();
      }
    }

    if (nl_slot < nfds && (pfds[nl_slot].revents & POLLIN) != 0) {
      char nl_buf[4096];
      while (::recv(nl_fd, nl_buf, sizeof(nl_buf), MSG_DONTWAIT) > 0) {
      }
      RefreshMulticastMemberships(rx4_fd, rx6_fd, /*force_rejoin=*/false);
      SendPtrQueriesOnAllInterfaces();
    }

    if (rx4_slot < nfds && (pfds[rx4_slot].revents & POLLIN) != 0) {
      ProcessIncomingDatagram(rx4_fd, now_ms);
    }

    if (rx6_slot < nfds && (pfds[rx6_slot].revents & POLLIN) != 0) {
      ProcessIncomingDatagram(rx6_fd, now_ms);
    }

    bool swept_changed = false;
    {
      std::lock_guard<std::mutex> lock(mu_);
      swept_changed = cache_.Sweep(now_ms);
    }
    if (swept_changed) {
      EmitServicesIfChanged(now_ms, /*force_emit=*/false);
    }
  }

  if (nl_fd >= 0) {
    ::close(nl_fd);
  }
  if (rx6_fd >= 0) {
    ::close(rx6_fd);
  }
  if (rx4_fd >= 0) {
    ::close(rx4_fd);
  }
}

}  // namespace adbhawk::platform
