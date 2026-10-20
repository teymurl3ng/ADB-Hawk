// G0 SNI probe: registers ONE temporary StatusNotifierItem and logs every method call the panel
// makes on it. Settles, by observation, what Cinnamon's xapp-sn-watcher does on left-click,
// right-click and middle-click, and what x/y arrive.
//
// Interactive: you click the tray icon. It exits after --seconds (default 45) or --max-events
// (default 6). The item disappears when the process exits. No ADB access, no files written.
//
// Usage: probe_sni [--item-is-menu] [--seconds N] [--max-events N]
//   default           ItemIsMenu=false, Menu="/NO_DBUSMENU"  (what we intend to ship)
//   --item-is-menu    ItemIsMenu=true  (the other branch inside xapp-sn-watcher)

#include <gio/gio.h>
#include <unistd.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

namespace {

constexpr const char* kIface = "org.kde.StatusNotifierItem";
constexpr const char* kPath = "/StatusNotifierItem";

const char* kXml = R"XML(
<node>
  <interface name="org.kde.StatusNotifierItem">
    <property name="Category" type="s" access="read"/>
    <property name="Id" type="s" access="read"/>
    <property name="Title" type="s" access="read"/>
    <property name="Status" type="s" access="read"/>
    <property name="WindowId" type="u" access="read"/>
    <property name="IconName" type="s" access="read"/>
    <property name="IconPixmap" type="a(iiay)" access="read"/>
    <property name="OverlayIconName" type="s" access="read"/>
    <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
    <property name="AttentionIconName" type="s" access="read"/>
    <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
    <property name="AttentionMovieName" type="s" access="read"/>
    <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
    <property name="ItemIsMenu" type="b" access="read"/>
    <property name="Menu" type="o" access="read"/>
    <method name="ContextMenu"><arg type="i" name="x" direction="in"/><arg type="i" name="y" direction="in"/></method>
    <method name="Activate"><arg type="i" name="x" direction="in"/><arg type="i" name="y" direction="in"/></method>
    <method name="SecondaryActivate"><arg type="i" name="x" direction="in"/><arg type="i" name="y" direction="in"/></method>
    <method name="Scroll"><arg type="i" name="delta" direction="in"/><arg type="s" name="orientation" direction="in"/></method>
    <signal name="NewIcon"/>
    <signal name="NewStatus"><arg type="s" name="status"/></signal>
  </interface>
</node>)XML";

struct State {
    GMainLoop* loop = nullptr;
    bool item_is_menu = false;
    int max_events = 6;
    int events = 0;
    gint64 t0 = 0;
    std::string service;
};

State g;

double since_start() { return static_cast<double>(g_get_monotonic_time() - g.t0) / 1e6; }

// 22x22 solid-ish green square, ARGB32 in network byte order (A,R,G,B per pixel).
GVariant* pixmap_variant() {
    constexpr int kSize = 22;
    guchar px[kSize * kSize * 4];
    for (int i = 0; i < kSize * kSize; ++i) {
        const int x = i % kSize;
        const int y = i / kSize;
        const bool edge = x == 0 || y == 0 || x == kSize - 1 || y == kSize - 1;
        px[i * 4 + 0] = 0xFF;
        px[i * 4 + 1] = edge ? 0x10 : 0x4A;
        px[i * 4 + 2] = edge ? 0x30 : 0xDE;
        px[i * 4 + 3] = edge ? 0x10 : 0x80;
    }
    GVariant* bytes = g_variant_new_fixed_array(G_VARIANT_TYPE_BYTE, px, sizeof px, 1);
    GVariant* one = g_variant_new("(ii@ay)", kSize, kSize, bytes);
    return g_variant_new_array(G_VARIANT_TYPE("(iiay)"), &one, 1);
}

GVariant* empty_pixmaps() { return g_variant_new_array(G_VARIANT_TYPE("(iiay)"), nullptr, 0); }

GVariant* get_property(GDBusConnection*, const gchar*, const gchar*, const gchar*,
                       const gchar* name, GError**, gpointer) {
    const std::string n = name;
    if (n == "Category") return g_variant_new_string("ApplicationStatus");
    if (n == "Id") return g_variant_new_string("adbhawk-g0-probe");
    if (n == "Title") return g_variant_new_string("ADB Hawk G0 probe");
    if (n == "Status") return g_variant_new_string("Active");
    if (n == "WindowId") return g_variant_new_uint32(0);
    if (n == "IconName") return g_variant_new_string("");  // force the pixmap path
    if (n == "IconPixmap") return pixmap_variant();
    if (n == "OverlayIconName" || n == "AttentionIconName" || n == "AttentionMovieName")
        return g_variant_new_string("");
    if (n == "OverlayIconPixmap" || n == "AttentionIconPixmap") return empty_pixmaps();
    if (n == "ToolTip")
        return g_variant_new("(s@a(iiay)ss)", "", empty_pixmaps(), "ADB Hawk G0 probe",
                             "Click me: left, right, middle");
    if (n == "ItemIsMenu") return g_variant_new_boolean(g.item_is_menu);
    if (n == "Menu") return g_variant_new_object_path("/NO_DBUSMENU");
    return nullptr;
}

void method_call(GDBusConnection*, const gchar* sender, const gchar*, const gchar*,
                 const gchar* method, GVariant* params, GDBusMethodInvocation* inv, gpointer) {
    gchar* p = g_variant_print(params, FALSE);
    std::printf("EVENT t=%.2fs sender=%s method=%s params=%s\n", since_start(), sender, method, p);
    std::fflush(stdout);
    g_free(p);
    g_dbus_method_invocation_return_value(inv, nullptr);
    if (++g.events >= g.max_events) g_main_loop_quit(g.loop);
}

void on_watcher_owner(GDBusConnection*, const gchar*, const gchar*, const gchar*, const gchar*,
                      GVariant* params, gpointer) {
    const gchar *name = nullptr, *oldo = nullptr, *newo = nullptr;
    g_variant_get(params, "(&s&s&s)", &name, &oldo, &newo);
    if (std::strcmp(name, "org.kde.StatusNotifierWatcher") == 0) {
        std::printf("NOTE t=%.2fs StatusNotifierWatcher owner changed old='%s' new='%s'\n",
                    since_start(), oldo, newo);
        std::fflush(stdout);
    }
}

void register_with_watcher(GDBusConnection* c) {
    GError* err = nullptr;
    GVariant* r = g_dbus_connection_call_sync(
        c, "org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.kde.StatusNotifierWatcher",
        "RegisterStatusNotifierItem", g_variant_new("(s)", g.service.c_str()), nullptr,
        G_DBUS_CALL_FLAGS_NONE, 3000, nullptr, &err);
    if (r == nullptr) {
        std::printf("FAIL RegisterStatusNotifierItem: %s\n", err != nullptr ? err->message : "?");
        if (err != nullptr) g_error_free(err);
        g_main_loop_quit(g.loop);
        return;
    }
    g_variant_unref(r);
    std::printf("OK registered '%s' with watcher at t=%.2fs. ItemIsMenu=%s. CLICK the new green icon "
                "in the panel: left, then right, then middle.\n",
                g.service.c_str(), since_start(), g.item_is_menu ? "true" : "false");
    std::fflush(stdout);
}

void name_acquired(GDBusConnection* c, const gchar*, gpointer) { register_with_watcher(c); }

void name_lost(GDBusConnection*, const gchar* name, gpointer) {
    std::printf("FAIL could not own bus name %s\n", name);
    g_main_loop_quit(g.loop);
}

gboolean on_timeout(gpointer) {
    std::printf("TIMEOUT t=%.2fs events=%d\n", since_start(), g.events);
    g_main_loop_quit(g.loop);
    return G_SOURCE_REMOVE;
}

}  // namespace

int main(int argc, char** argv) {
    int seconds = 45;
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--item-is-menu") == 0) g.item_is_menu = true;
        else if (std::strcmp(argv[i], "--seconds") == 0 && i + 1 < argc) seconds = std::atoi(argv[++i]);
        else if (std::strcmp(argv[i], "--max-events") == 0 && i + 1 < argc) g.max_events = std::atoi(argv[++i]);
    }
    g.t0 = g_get_monotonic_time();
    g.service = "org.freedesktop.StatusNotifierItem-" + std::to_string(getpid()) + "-1";

    GError* err = nullptr;
    GDBusConnection* c = g_bus_get_sync(G_BUS_TYPE_SESSION, nullptr, &err);
    if (c == nullptr) {
        std::printf("FAIL session bus: %s\n", err != nullptr ? err->message : "?");
        return 2;
    }
    GDBusNodeInfo* node = g_dbus_node_info_new_for_xml(kXml, &err);
    if (node == nullptr) {
        std::printf("FAIL introspection xml: %s\n", err != nullptr ? err->message : "?");
        return 2;
    }
    GDBusInterfaceVTable vt{method_call, get_property, nullptr, {}};
    if (g_dbus_connection_register_object(c, kPath, node->interfaces[0], &vt, nullptr, nullptr, &err) == 0) {
        std::printf("FAIL register_object: %s\n", err != nullptr ? err->message : "?");
        return 2;
    }
    g_dbus_connection_signal_subscribe(c, "org.freedesktop.DBus", "org.freedesktop.DBus",
                                       "NameOwnerChanged", "/org/freedesktop/DBus", nullptr,
                                       G_DBUS_SIGNAL_FLAGS_NONE, on_watcher_owner, nullptr, nullptr);

    g.loop = g_main_loop_new(nullptr, FALSE);
    const guint owner = g_bus_own_name_on_connection(c, g.service.c_str(), G_BUS_NAME_OWNER_FLAGS_NONE,
                                                     name_acquired, name_lost, nullptr, nullptr);
    g_timeout_add_seconds(static_cast<guint>(seconds), on_timeout, nullptr);
    g_main_loop_run(g.loop);

    std::printf("DONE events=%d\n", g.events);
    g_bus_unown_name(owner);
    g_dbus_node_info_unref(node);
    g_main_loop_unref(g.loop);
    g_object_unref(c);
    return 0;
}
