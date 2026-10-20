#include <gio/gio.h>
#include <glib-unix.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

#include "core/notify_policy.hpp"
#include "core/reduce.hpp"
#include "core/snapshot.hpp"
#include "core/vocabulary.hpp"
#include "platform/adb_io.hpp"
#include "platform/adb_locator.hpp"
#include "platform/autostart.hpp"
#include "platform/dbus/session_dbus.hpp"
#include "platform/diagnostics.hpp"
#include "platform/mdns_browser.hpp"
#include "platform/repair_restart_adb.hpp"
#include "platform/usb_sentinel.hpp"
#include "shell/gtk_shell.hpp"

namespace {

using namespace adbhawk::core;
using namespace adbhawk::platform;
using namespace adbhawk::shell;

struct AppConfig {
  bool print_snapshots = false;
  std::optional<uint16_t> port_override;
  int64_t duration_ms = 0;
};

bool EqualsIgnoreCase(std::string_view a, std::string_view b) {
  if (a.size() != b.size()) return false;
  for (size_t i = 0; i < a.size(); ++i) {
    if (std::tolower(static_cast<unsigned char>(a[i])) !=
        std::tolower(static_cast<unsigned char>(b[i]))) {
      return false;
    }
  }
  return true;
}

void PrintUsage() {
  std::printf(
      "Usage: adb-hawk [options]\n"
      "Options:\n"
      "  --print-snapshots       Print reduced snapshots as JSON lines to stdout\n"
      "  --port=<port>           Override ADB server port (defaults to 5037)\n"
      "  --duration-ms=<ms>      Run for <ms> milliseconds then exit (0 = infinite)\n"
      "  -h, --help              Show this help message\n");
}

AppConfig ParseCommandLine(int argc, char** argv) {
  AppConfig cfg{};
  for (int i = 1; i < argc; ++i) {
    std::string_view arg(argv[i]);
    if (arg == "--print-snapshots") {
      cfg.print_snapshots = true;
    } else if (arg.starts_with("--port=")) {
      cfg.port_override = static_cast<uint16_t>(
          std::atoi(std::string(arg.substr(7)).c_str()));
    } else if (arg.starts_with("--duration-ms=")) {
      cfg.duration_ms = std::atoll(std::string(arg.substr(14)).c_str());
    } else if (arg == "-h" || arg == "--help") {
      PrintUsage();
      std::exit(0);
    }
  }
  return cfg;
}

gboolean OnSignalTerm(gpointer user_data) {
  auto* loop = static_cast<GMainLoop*>(user_data);
  if (loop != nullptr && g_main_loop_is_running(loop)) {
    g_main_loop_quit(loop);
  }
  return G_SOURCE_REMOVE;
}

gboolean OnDurationTimeout(gpointer user_data) {
  auto* loop = static_cast<GMainLoop*>(user_data);
  if (loop != nullptr && g_main_loop_is_running(loop)) {
    g_main_loop_quit(loop);
  }
  return G_SOURCE_REMOVE;
}

}  // namespace

int main(int argc, char** argv) {
  AppConfig app_cfg = ParseCommandLine(argc, argv);

  AdbLocatorEnv loc_env = ReadCurrentLocatorEnv();
  auto adb_path = LocateAdbExecutable(loc_env);

  uint16_t adb_port = 5037;
  bool explicit_port = false;
  if (app_cfg.port_override.has_value()) {
    adb_port = *app_cfg.port_override;
    explicit_port = true;
  } else {
    adb_port = ResolveAdbServerPort();
  }

  GMainLoop* loop = g_main_loop_new(nullptr, FALSE);
  g_unix_signal_add(SIGINT, OnSignalTerm, loop);
  g_unix_signal_add(SIGTERM, OnSignalTerm, loop);

  if (app_cfg.duration_ms > 0) {
    g_timeout_add(static_cast<guint>(app_cfg.duration_ms), OnDurationTimeout,
                  loop);
  }

  // Set up ADB I/O Worker
  AdbIoConfig io_cfg{};
  io_cfg.port = adb_port;
  io_cfg.auto_port_discovery = !explicit_port;
  io_cfg.adb_executable_path = adb_path;
  io_cfg.verify_listener_uid = true;
  io_cfg.allow_start_server = true;

  AdbIoWorker adb_worker(io_cfg);
  adb_worker.Start();

  // Set up mDNS Browser
  MdnsBrowserConfig mdns_cfg{};
  mdns_cfg.multicast_port = 5353;
  MdnsBrowser mdns_browser(mdns_cfg);
  mdns_browser.Start();

  // Core reducer and notify policy state
  int64_t now_ms = NowBoottimeMs();
  std::optional<AppSnapshot> prev_snapshot = std::nullopt;
  AppSnapshot current_snapshot = MakeInitialSnapshot(now_ms);
  DeviceEngineState engine_state{};
  NotifierState notifier_state = NotificationPolicy::MakeInitialState(now_ms);
  std::unordered_map<std::string, int64_t> metadata_probed_generation;
  std::string armed_post_pair_host;
  std::string armed_post_pair_offer;
  std::unordered_set<std::string> attempted_post_pair_endpoints;

  // D-Bus Manager and GTK Shell (initialized in GUI mode)
  std::unique_ptr<SessionDbusManager> dbus_mgr;
  std::unique_ptr<IShell> gui_shell;

  std::vector<PendingHold> active_holds;

  if (!app_cfg.print_snapshots) {
    ShellCallbacks shell_cb{};
    shell_cb.on_restart_adb = [&]() {
      (void)ExecuteUserInitiatedRestartAdb(adb_worker.ActivePort(), adb_path);
    };
    shell_cb.on_disconnect_device = [&](std::string dev_id) {
      for (const auto& d : current_snapshot.devices) {
        if (d.id == dev_id && d.detail.address.has_value()) {
          break;
        }
      }
    };
    shell_cb.on_pair_device = [&](std::string offer_id, std::string host, int port,
                                  std::string code, std::string display_name) {
      adb_worker.RequestPairing(std::move(offer_id), std::move(host), port,
                                std::move(code), std::move(display_name));
    };
    shell_cb.on_pair_qr = [&gui_shell]() {
      if (gui_shell) {
        gui_shell->ShowFlyout(ScreenPoint{0, 0});
        gui_shell->ShowPairingPage();
      }
    };
    shell_cb.on_toggle_pin = [&](bool pinned) {
      current_snapshot.shell.pinned = pinned;
    };
    shell_cb.on_toggle_bubble = [&](bool show) {
      if (gui_shell) {
        if (show) gui_shell->ShowBubble(); else gui_shell->HideBubble();
      }
    };
    shell_cb.on_select_theme = [&](ThemePreference theme) {
      if (gui_shell) gui_shell->UpdateTheme(theme);
    };
    shell_cb.on_toggle_autostart = [](bool enabled) {
      (void)WriteAutostartEnabled(enabled);
    };
    shell_cb.on_toggle_suppress_automount = [](bool suppress) {
      SessionDbusManager::WriteAutomountSuppressed(suppress);
    };
    shell_cb.on_quit = [loop]() {
      if (loop != nullptr) g_main_loop_quit(loop);
    };

    gui_shell = CreateGtkShell(std::move(shell_cb), &argc, &argv);

    DbusMenuCallbacks menu_cb{};
    menu_cb.on_open_flyout = [&gui_shell]() {
      if (gui_shell) gui_shell->ShowFlyout(ScreenPoint{0, 0});
    };
    menu_cb.on_pair_qr = [&gui_shell]() {
      if (gui_shell) {
        gui_shell->ShowFlyout(ScreenPoint{0, 0});
        gui_shell->ShowPairingPage();
      }
    };
    menu_cb.on_toggle_bubble = [&gui_shell](bool show) {
      if (gui_shell) {
        if (show) gui_shell->ShowBubble(); else gui_shell->HideBubble();
      }
    };
    menu_cb.on_select_theme = [&gui_shell](ThemePreference theme) {
      if (gui_shell) gui_shell->UpdateTheme(theme);
    };
    menu_cb.on_toggle_autostart = [](bool enabled) {
      (void)WriteAutostartEnabled(enabled);
    };
    menu_cb.on_toggle_suppress_automount = [](bool suppress) {
      SessionDbusManager::WriteAutomountSuppressed(suppress);
    };
    menu_cb.on_quit = [loop]() {
      if (loop != nullptr) g_main_loop_quit(loop);
    };

    DbusEventCallbacks event_cb{};
    event_cb.on_instance_toggle_received = [&gui_shell]() {
      if (gui_shell) gui_shell->ToggleFlyout(ScreenPoint{0, 0});
    };
    event_cb.on_tray_activate = [&gui_shell](int32_t x, int32_t y) {
      if (gui_shell) gui_shell->ToggleFlyout(ScreenPoint{x, y});
    };

    dbus_mgr = std::make_unique<SessionDbusManager>(std::move(menu_cb),
                                                    std::move(event_cb));
    auto init_st = dbus_mgr->Initialize();
    if (init_st == SingleInstanceStatus::SecondaryForwarded) {
      // An existing instance is already running and was toggled; exit cleanly (F8, D7).
      adb_worker.Stop();
      mdns_browser.Stop();
      g_main_loop_unref(loop);
      return 0;
    }

    if (gui_shell) {
      UserPreferences user_prefs = ReadUserPreferences();
      gui_shell->UpdateTheme(user_prefs.theme);
      if (user_prefs.show_status_bubble) {
        gui_shell->ShowBubble();
      }
      gui_shell->UpdateSnapshot(current_snapshot);
    }
  }

  auto drain_and_reduce = [&]() {
    int64_t at_ms = NowBoottimeMs();
    auto adb_events = adb_worker.DrainEvents();
    auto mdns_events = mdns_browser.DrainEvents();

    bool changed = false;
    for (const auto& ev : adb_events) {
      auto res = ReduceWithDeviceState(current_snapshot, engine_state, ev, at_ms);
      current_snapshot = std::move(res.snapshot);
      engine_state = std::move(res.device_state);
      changed = true;
    }
    for (const auto& ev : mdns_events) {
      auto res = ReduceWithDeviceState(current_snapshot, engine_state, ev, at_ms);
      current_snapshot = std::move(res.snapshot);
      engine_state = std::move(res.device_state);
      changed = true;
    }

    // Trigger metadata probes for newly connected devices or devices with renewed generations
    for (const auto& [dev_id, sess] : engine_state.metadata_sessions) {
      if (sess.connected_eligible) {
        auto it = metadata_probed_generation.find(dev_id);
        if (it == metadata_probed_generation.end() || it->second != sess.generation) {
          metadata_probed_generation[dev_id] = sess.generation;
          adb_worker.RequestMetadataProbe(dev_id, sess.adb_serial);
        }
      }
    }
    for (auto it = metadata_probed_generation.begin(); it != metadata_probed_generation.end(); ) {
      if (!engine_state.metadata_sessions.contains(it->first)) {
        it = metadata_probed_generation.erase(it);
      } else {
        ++it;
      }
    }

    // Post-pair auto-connect coordinator (Decision 0009):
    if (current_snapshot.pairing.has_value() &&
        current_snapshot.pairing->phase == PairingPhase::PairedWaitingForDevice &&
        engine_state.active_pairing_host.has_value() &&
        !engine_state.active_pairing_host->empty()) {
      const std::string& host = *engine_state.active_pairing_host;
      if (armed_post_pair_host != host ||
          armed_post_pair_offer != current_snapshot.pairing->offer_id) {
        attempted_post_pair_endpoints.clear();
        armed_post_pair_host = host;
        armed_post_pair_offer = current_snapshot.pairing->offer_id;
      }

      // Decision 0011 guard: skip connect if already connected over Wi-Fi for this host
      bool already_connected_wifi = false;
      for (const auto& dev : current_snapshot.devices) {
        if (dev.transport == TransportKind::WiFi &&
            dev.state == DeviceStateKind::Connected &&
            dev.detail.address.has_value()) {
          std::string d_host;
          int d_port = 0;
          if (TrySplitHostPort(*dev.detail.address, &d_host, &d_port) &&
              EqualsIgnoreCase(d_host, host)) {
            already_connected_wifi = true;
            break;
          }
        }
      }

      if (!already_connected_wifi) {
        auto effective = MergeEffectiveMdnsServices(
            engine_state.latest_mdns_services,
            engine_state.latest_fallback_services);
        for (const auto& svc : effective) {
          if (svc.kind != MdnsServiceKind::Connect ||
              svc.service_type != "_adb-tls-connect._tcp" ||
              svc.port <= 0 || svc.port > 65535) {
            continue;
          }
          bool host_matches = false;
          if (svc.ipv4.has_value() &&
              EqualsIgnoreCase(*svc.ipv4, host)) {
            host_matches = true;
          }
          if (!host_matches) {
            for (const auto& ip6 : svc.ipv6) {
              if (EqualsIgnoreCase(ip6, host)) {
                host_matches = true;
                break;
              }
            }
          }
          if (host_matches) {
            std::string endpoint = host + ":" + std::to_string(svc.port);
            if (attempted_post_pair_endpoints.insert(endpoint).second) {
              adb_worker.RequestConnect(host, svc.port);
            }
          }
        }
      }
    } else {
      if (!armed_post_pair_host.empty()) {
        armed_post_pair_host.clear();
        armed_post_pair_offer.clear();
        attempted_post_pair_endpoints.clear();
      }
    }

    if (current_snapshot.devices.empty() &&
        current_snapshot.health.kind == GlobalHealthKind::Healthy) {
      auto usb_devs = ScanSysfsUsbAdbDevices();
      if (!usb_devs.empty()) {
        const auto& d = usb_devs.front();
        std::string name = !d.product.empty()
                               ? d.product
                               : (!d.manufacturer.empty() ? d.manufacturer
                                                          : d.bus_id);
        current_snapshot.health.hint =
            "USB phone detected (" + name +
            "), tracking on ADB port :" +
            std::to_string(adb_worker.ActivePort()) + "…";
        current_snapshot.health.tray = TrayStatusKind::Attention;
        current_snapshot.health.tray_tooltip = "USB device detected: " + name;
      }
    }

    if (changed) {
      if (app_cfg.print_snapshots) {
        std::printf("%s\n", SnapshotToJson(current_snapshot).c_str());
        std::fflush(stdout);
      } else {
        if (gui_shell) {
          gui_shell->UpdateSnapshot(current_snapshot);
        }
        if (dbus_mgr) {
          dbus_mgr->UpdateTrayFromSnapshot(current_snapshot, /*is_dark_panel=*/true);
          auto decision = NotificationPolicy::Evaluate(
              prev_snapshot, current_snapshot, notifier_state, at_ms);
          notifier_state = std::move(decision.next_state);
          prev_snapshot = current_snapshot;

          for (const auto& cancel_key : decision.cancel_holds) {
            active_holds.erase(
                std::remove_if(active_holds.begin(), active_holds.end(),
                               [&](const PendingHold& h) {
                                 return h.key == cancel_key;
                               }),
                active_holds.end());
          }

          for (const auto& new_hold : decision.arm_holds) {
            active_holds.erase(
                std::remove_if(active_holds.begin(), active_holds.end(),
                               [&](const PendingHold& h) {
                                 return h.key == new_hold.key;
                               }),
                active_holds.end());
            active_holds.push_back(new_hold);
          }

          if (!decision.immediate_intents.empty()) {
            auto [to_send, new_st] = NotificationPolicy::ApplyBurstAndRecord(
                decision.immediate_intents, notifier_state, at_ms);
            notifier_state = std::move(new_st);
            for (const auto& intent : to_send) {
              dbus_mgr->SendNotification(intent.title, intent.body);
            }
          }
        }
      }
    }
  };

  auto process_holds = [&]() {
    if (!dbus_mgr || active_holds.empty()) {
      return;
    }
    int64_t hold_now_ms = NowBoottimeMs();
    std::vector<NotificationIntent> candidates;
    std::vector<PendingHold> remaining;

    for (const auto& hold : active_holds) {
      if (hold_now_ms >= hold.due_at_ms) {
        auto intent = NotificationPolicy::TryMaterializeHold(
            hold, current_snapshot, notifier_state, hold_now_ms);
        if (intent.has_value()) {
          candidates.push_back(std::move(*intent));
        }
      } else {
        remaining.push_back(hold);
      }
    }
    active_holds = std::move(remaining);

    if (!candidates.empty()) {
      auto [to_send, new_st] = NotificationPolicy::ApplyBurstAndRecord(
          candidates, notifier_state, hold_now_ms);
      notifier_state = std::move(new_st);
      for (const auto& intent : to_send) {
        dbus_mgr->SendNotification(intent.title, intent.body);
      }
    }
  };

  guint hold_timer_id = 0;
  if (!app_cfg.print_snapshots) {
    hold_timer_id = g_timeout_add(
        200,
        [](gpointer data) -> gboolean {
          auto* fn = static_cast<decltype(process_holds)*>(data);
          (*fn)();
          return G_SOURCE_CONTINUE;
        },
        &process_holds);
  }

  // Attach eventfd channels to GLib main context
  GIOChannel* adb_chan = g_io_channel_unix_new(adb_worker.NotifyFd());
  g_io_channel_set_encoding(adb_chan, nullptr, nullptr);
  g_io_add_watch(
      adb_chan, G_IO_IN,
      [](GIOChannel*, GIOCondition, gpointer data) -> gboolean {
        auto* fn = static_cast<decltype(drain_and_reduce)*>(data);
        (*fn)();
        return G_SOURCE_CONTINUE;
      },
      &drain_and_reduce);

  GIOChannel* mdns_chan = g_io_channel_unix_new(mdns_browser.NotifyFd());
  g_io_channel_set_encoding(mdns_chan, nullptr, nullptr);
  g_io_add_watch(
      mdns_chan, G_IO_IN,
      [](GIOChannel*, GIOCondition, gpointer data) -> gboolean {
        auto* fn = static_cast<decltype(drain_and_reduce)*>(data);
        (*fn)();
        return G_SOURCE_CONTINUE;
      },
      &drain_and_reduce);

  // Print initial snapshot if headless
  if (app_cfg.print_snapshots) {
    std::printf("%s\n", SnapshotToJson(current_snapshot).c_str());
    std::fflush(stdout);
  }

  g_main_loop_run(loop);

  if (hold_timer_id != 0) {
    g_source_remove(hold_timer_id);
  }

  g_io_channel_unref(adb_chan);
  g_io_channel_unref(mdns_chan);
  if (dbus_mgr) {
    dbus_mgr->Stop();
  }
  mdns_browser.Stop();
  adb_worker.Stop();
  g_main_loop_unref(loop);

  return 0;
}
