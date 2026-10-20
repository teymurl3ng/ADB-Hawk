#include <gio/gio.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>

#include "core/snapshot.hpp"
#include "platform/autostart.hpp"
#include "platform/dbus/session_dbus.hpp"

namespace {

using namespace adbhawk::core;
using namespace adbhawk::platform;

#define REQUIRE(cond)                                                       \
  do {                                                                      \
    if (!(cond)) {                                                          \
      std::fprintf(stderr, "FAIL: %s:%d: %s\n", __FILE__, __LINE__, #cond); \
      std::exit(1);                                                         \
    }                                                                       \
  } while (0)

void TestDbusAdaptersWithGTestDbus() {
  GTestDBus* test_bus = g_test_dbus_new(G_TEST_DBUS_NONE);
  g_test_dbus_up(test_bus);

  GMainLoop* loop = g_main_loop_new(nullptr, FALSE);
  std::thread loop_thread([&]() { g_main_loop_run(loop); });

  GError* err = nullptr;
  GDBusConnection* conn = g_dbus_connection_new_for_address_sync(
      g_test_dbus_get_bus_address(test_bus),
      static_cast<GDBusConnectionFlags>(
          G_DBUS_CONNECTION_FLAGS_AUTHENTICATION_CLIENT |
          G_DBUS_CONNECTION_FLAGS_MESSAGE_BUS_CONNECTION),
      nullptr, nullptr, &err);
  REQUIRE(conn != nullptr);
  REQUIRE(err == nullptr);

  bool saw_toggle = false;
  bool saw_open_flyout = false;
  bool saw_bubble_toggle = false;
  bool saw_pair_wifi = false;
  int32_t activate_x = -1;
  int32_t activate_y = -1;

  DbusMenuCallbacks menu_cb{};
  menu_cb.on_open_flyout = [&]() { saw_open_flyout = true; };
  menu_cb.on_pair_qr = [&]() { saw_pair_wifi = true; };
  menu_cb.on_toggle_bubble = [&](bool) { saw_bubble_toggle = true; };

  DbusEventCallbacks event_cb{};
  event_cb.on_instance_toggle_received = [&]() { saw_toggle = true; };
  event_cb.on_tray_activate = [&](int32_t x, int32_t y) {
    activate_x = x;
    activate_y = y;
  };

  SessionDbusManager mgr(std::move(menu_cb), std::move(event_cb));
  auto st = mgr.Initialize(conn, nullptr);
  REQUIRE(st == SingleInstanceStatus::AcquiredPrimary);

  const gchar* self_name = g_dbus_connection_get_unique_name(conn);

  // 1. Test org.adbhawk.Instance -> Toggle()
  GVariant* reply = g_dbus_connection_call_sync(
      conn, self_name, "/org/adbhawk/Instance", "org.adbhawk.Instance", "Toggle",
      nullptr, nullptr, G_DBUS_CALL_FLAGS_NONE, 1000, nullptr, &err);
  if (err != nullptr) {
    std::fprintf(stderr, "Toggle error: %s\n", err->message);
  }
  REQUIRE(reply != nullptr);
  REQUIRE(err == nullptr);
  REQUIRE(saw_toggle);
  g_variant_unref(reply);

  // 2. Test org.kde.StatusNotifierItem properties & Activate
  GVariant* prop_status = g_dbus_connection_call_sync(
      conn, self_name, "/StatusNotifierItem", "org.freedesktop.DBus.Properties",
      "Get",
      g_variant_new("(ss)", "org.kde.StatusNotifierItem", "Status"),
      G_VARIANT_TYPE("(v)"), G_DBUS_CALL_FLAGS_NONE, 1000, nullptr, &err);
  REQUIRE(prop_status != nullptr);
  GVariant* inner_val = nullptr;
  g_variant_get(prop_status, "(v)", &inner_val);
  REQUIRE(std::strcmp(g_variant_get_string(inner_val, nullptr), "Active") == 0);
  g_variant_unref(inner_val);
  g_variant_unref(prop_status);

  GVariant* act_reply = g_dbus_connection_call_sync(
      conn, self_name, "/StatusNotifierItem", "org.kde.StatusNotifierItem",
      "Activate", g_variant_new("(ii)", 450, 12), nullptr,
      G_DBUS_CALL_FLAGS_NONE, 1000, nullptr, &err);
  REQUIRE(act_reply != nullptr);
  REQUIRE(activate_x == 450);
  REQUIRE(activate_y == 12);
  g_variant_unref(act_reply);

  // 3. Test com.canonical.dbusmenu GetLayout and Click events
  const char* empty_strv[] = {nullptr};
  GVariant* layout_reply = g_dbus_connection_call_sync(
      conn, self_name, "/MenuBar", "com.canonical.dbusmenu", "GetLayout",
      g_variant_new("(ii^as)", 0, 1, empty_strv),
      G_VARIANT_TYPE("(u(ia{sv}av))"), G_DBUS_CALL_FLAGS_NONE, 1000, nullptr,
      &err);
  REQUIRE(layout_reply != nullptr);
  guint32 rev = 0;
  g_variant_get(layout_reply, "(u@(ia{sv}av))", &rev, nullptr);
  REQUIRE(rev >= 1);
  g_variant_unref(layout_reply);

  // Click on "Open ADB Hawk" (id 1)
  GVariant* ev_reply = g_dbus_connection_call_sync(
      conn, self_name, "/MenuBar", "com.canonical.dbusmenu", "Event",
      g_variant_new("(isvu)", 1, "clicked", g_variant_new_int32(0), 100),
      nullptr, G_DBUS_CALL_FLAGS_NONE, 1000, nullptr, &err);
  REQUIRE(ev_reply != nullptr);
  REQUIRE(saw_open_flyout);
  g_variant_unref(ev_reply);

  // Click on "Show status bubble" (id 3)
  GVariant* ev_bubble_reply = g_dbus_connection_call_sync(
      conn, self_name, "/MenuBar", "com.canonical.dbusmenu", "Event",
      g_variant_new("(isvu)", 3, "clicked", g_variant_new_int32(0), 101),
      nullptr, G_DBUS_CALL_FLAGS_NONE, 1000, nullptr, &err);
  REQUIRE(ev_bubble_reply != nullptr);
  REQUIRE(saw_bubble_toggle);
  g_variant_unref(ev_bubble_reply);

  // Click on "Pair device with Wi-Fi" (id 2) via EventGroup
  GVariantBuilder eg_b;
  g_variant_builder_init(&eg_b, G_VARIANT_TYPE("a(isvu)"));
  g_variant_builder_add(&eg_b, "(isvu)", 2, "clicked", g_variant_new_string(""), 102);
  GVariant* ev_group_reply = g_dbus_connection_call_sync(
      conn, self_name, "/MenuBar", "com.canonical.dbusmenu", "EventGroup",
      g_variant_new("(@a(isvu))", g_variant_builder_end(&eg_b)),
      G_VARIANT_TYPE("(ai)"), G_DBUS_CALL_FLAGS_NONE, 1000, nullptr, &err);
  REQUIRE(ev_group_reply != nullptr);
  REQUIRE(saw_pair_wifi);
  g_variant_unref(ev_group_reply);

  // Test GetGroupProperties
  GVariantBuilder ids_b;
  g_variant_builder_init(&ids_b, G_VARIANT_TYPE("ai"));
  g_variant_builder_add(&ids_b, "i", 1);
  GVariant* group_props = g_dbus_connection_call_sync(
      conn, self_name, "/MenuBar", "com.canonical.dbusmenu",
      "GetGroupProperties",
      g_variant_new("(@ai^as)", g_variant_builder_end(&ids_b), empty_strv),
      G_VARIANT_TYPE("(a(ia{sv}))"), G_DBUS_CALL_FLAGS_NONE, 1000, nullptr,
      &err);
  REQUIRE(group_props != nullptr);
  g_variant_unref(group_props);

  mgr.Stop();
  g_main_loop_quit(loop);
  loop_thread.join();
  g_main_loop_unref(loop);

  g_object_unref(conn);
  g_test_dbus_down(test_bus);
  g_object_unref(test_bus);
}

}  // namespace

int main() {
  TestDbusAdaptersWithGTestDbus();
  std::printf("OK D-Bus single-instance, SNI, and dbusmenu tests passed\n");
  return 0;
}
