// G0 host probe. Read-only facts about THIS machine, emitted as JSON.
//
// Purpose: turn every "(verify)" in ENGINEERING.md into an observed fact before the spec
// freezes. It never starts, kills, or reconfigures the ADB server (R-14): if nothing listens on
// the ADB port it reports that and moves on. It only spawns `adb version`, which does not
// contact the server.
//
// Build: see tools/verify.sh. Usage: probe_g0 [output.json]

#include <arpa/inet.h>
#include <ifaddrs.h>
#include <net/if.h>
#include <netinet/in.h>
#include <poll.h>
#include <pwd.h>
#include <sys/socket.h>
#include <sys/wait.h>
#include <unistd.h>

#include <gio/gio.h>

#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <set>
#include <sstream>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace {

// ---------------------------------------------------------------- tiny JSON builder

std::string jstr(std::string_view s) {
    std::string o = "\"";
    for (unsigned char c : s) {
        switch (c) {
            case '"': o += "\\\""; break;
            case '\\': o += "\\\\"; break;
            case '\n': o += "\\n"; break;
            case '\r': o += "\\r"; break;
            case '\t': o += "\\t"; break;
            default:
                if (c < 0x20 || c >= 0x7f) {
                    char b[8];
                    std::snprintf(b, sizeof b, "\\u%04x", static_cast<unsigned>(c));
                    o += b;
                } else {
                    o += static_cast<char>(c);
                }
        }
    }
    return o + "\"";
}

struct Obj {
    std::vector<std::pair<std::string, std::string>> kv;
    Obj& raw(std::string k, std::string v) {
        kv.emplace_back(std::move(k), std::move(v));
        return *this;
    }
    Obj& s(std::string k, std::string_view v) { return raw(std::move(k), jstr(v)); }
    Obj& n(std::string k, long long v) { return raw(std::move(k), std::to_string(v)); }
    Obj& b(std::string k, bool v) { return raw(std::move(k), v ? "true" : "false"); }
    std::string str() const {
        std::string o = "{";
        for (size_t i = 0; i < kv.size(); ++i) {
            if (i) o += ",";
            o += jstr(kv[i].first) + ":" + kv[i].second;
        }
        return o + "}";
    }
};

std::string arr(const std::vector<std::string>& items) {
    std::string o = "[";
    for (size_t i = 0; i < items.size(); ++i) {
        if (i) o += ",";
        o += items[i];
    }
    return o + "]";
}

std::string hex_of(const unsigned char* p, size_t n) {
    std::string o;
    char b[4];
    for (size_t i = 0; i < n; ++i) {
        std::snprintf(b, sizeof b, "%02x", static_cast<unsigned>(p[i]));
        o += b;
    }
    return o;
}

std::string uid_name(unsigned uid) {
    const passwd* pw = getpwuid(uid);
    return pw != nullptr ? pw->pw_name : "?";
}

std::string env_or(const char* k, const char* dflt = "") {
    const char* v = std::getenv(k);
    return v != nullptr ? v : dflt;
}

// ---------------------------------------------------------------- /proc/net sockets

struct ProcSock {
    std::string local;
    std::string state;
    unsigned uid = 0;
};

std::vector<ProcSock> proc_net(const char* path, unsigned port, const char* only_state) {
    std::vector<ProcSock> out;
    std::ifstream f(path);
    std::string line;
    std::getline(f, line);  // header
    while (std::getline(f, line)) {
        std::istringstream ss(line);
        std::string sl, local, rem, st, q, tr, rt;
        unsigned uid = 0;
        ss >> sl >> local >> rem >> st >> q >> tr >> rt >> uid;
        if (!ss) continue;
        const auto colon = local.rfind(':');
        if (colon == std::string::npos) continue;
        const unsigned long p = std::strtoul(local.c_str() + colon + 1, nullptr, 16);
        if (p != port) continue;
        if (only_state != nullptr && st != only_state) continue;
        out.push_back({local, st, uid});
    }
    return out;
}

std::string socks_json(const std::vector<ProcSock>& v) {
    std::vector<std::string> items;
    for (const auto& s : v)
        items.push_back(Obj().s("local", s.local).s("state", s.state).n("uid", s.uid)
                            .s("user", uid_name(s.uid)).str());
    return arr(items);
}

// ---------------------------------------------------------------- ADB host protocol

bool read_exact(int fd, void* buf, size_t n, int timeout_ms) {
    auto* p = static_cast<char*>(buf);
    while (n > 0) {
        pollfd pf{fd, POLLIN, 0};
        if (poll(&pf, 1, timeout_ms) <= 0) return false;
        const ssize_t k = recv(fd, p, n, 0);
        if (k <= 0) return false;
        p += k;
        n -= static_cast<size_t>(k);
    }
    return true;
}

bool parse_hex4(const char* s, size_t& out) {
    size_t v = 0;
    for (int i = 0; i < 4; ++i) {
        const char c = s[i];
        int d;
        if (c >= '0' && c <= '9') d = c - '0';
        else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
        else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
        else return false;
        v = v * 16 + static_cast<size_t>(d);
    }
    out = v;
    return true;
}

struct AdbReply {
    std::string status;  // "OKAY" | "FAIL" | ""
    std::string err;     // transport-level problem
    std::string reason;  // FAIL reason
    std::vector<unsigned char> payload;
    bool got_payload = false;
};

constexpr size_t kMaxPayload = 1u << 20;  // 1 MiB, same cap as ADB-Hawk

AdbReply adb_call(int port, const std::string& service, int frame_timeout_ms) {
    AdbReply r;
    const int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) { r.err = std::string("socket: ") + std::strerror(errno); return r; }
    sockaddr_in a{};
    a.sin_family = AF_INET;
    a.sin_port = htons(static_cast<uint16_t>(port));
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(fd, reinterpret_cast<sockaddr*>(&a), sizeof a) != 0) {
        r.err = std::string("connect: ") + std::strerror(errno);
        close(fd);
        return r;
    }
    char hdr[8];
    std::snprintf(hdr, sizeof hdr, "%04x", static_cast<unsigned>(service.size()));
    const std::string req = std::string(hdr) + service;
    if (send(fd, req.data(), req.size(), MSG_NOSIGNAL) != static_cast<ssize_t>(req.size())) {
        r.err = std::string("send: ") + std::strerror(errno);
        close(fd);
        return r;
    }
    char st[4];
    if (!read_exact(fd, st, 4, 3000)) { r.err = "no status word within 3s"; close(fd); return r; }
    r.status.assign(st, 4);
    if (r.status != "OKAY" && r.status != "FAIL") {
        r.err = "first reply was neither OKAY nor FAIL (not an adb server?)";
        close(fd);
        return r;
    }
    char len[4];
    size_t n = 0;
    if (read_exact(fd, len, 4, frame_timeout_ms) && parse_hex4(len, n) && n <= kMaxPayload) {
        std::vector<unsigned char> buf(n);
        if (n == 0 || read_exact(fd, buf.data(), n, 3000)) {
            if (r.status == "FAIL") r.reason.assign(buf.begin(), buf.end());
            else { r.payload = std::move(buf); r.got_payload = true; }
        }
    }
    close(fd);
    return r;
}

bool varint(const unsigned char*& p, const unsigned char* e, uint64_t& v) {
    v = 0;
    for (int shift = 0; shift < 64 && p < e; shift += 7) {
        const unsigned char b = *p++;
        v |= static_cast<uint64_t>(b & 0x7f) << shift;
        if ((b & 0x80) == 0) return true;
    }
    return false;
}

// Field numbers are taken from ADB-Hawk's AdbServerStatus.cs (3, 5, 7, 8, 12). This probe is
// what checks them against a real server; they are not assumed correct here.
void parse_server_status(const std::vector<unsigned char>& b, Obj& o) {
    const unsigned char* p = b.data();
    const unsigned char* e = p + b.size();
    while (p < e) {
        uint64_t tag;
        if (!varint(p, e, tag)) break;
        const unsigned f = static_cast<unsigned>(tag >> 3);
        const unsigned w = static_cast<unsigned>(tag & 7);
        if (w == 0) {
            uint64_t v;
            if (!varint(p, e, v)) break;
            if (f == 3) o.n("mdns_backend", static_cast<long long>(v));
            if (f == 12) o.b("mdns_enabled", v != 0);
        } else if (w == 2) {
            uint64_t l;
            if (!varint(p, e, l) || l > static_cast<uint64_t>(e - p)) break;
            const std::string s(reinterpret_cast<const char*>(p), static_cast<size_t>(l));
            p += l;
            if (f == 5) o.s("version", s);
            if (f == 7) o.s("executable_absolute_path", s);
            if (f == 8) o.s("log_absolute_path", s);
        } else if (w == 1) {
            if (e - p < 8) break;
            p += 8;
        } else if (w == 5) {
            if (e - p < 4) break;
            p += 4;
        } else {
            break;
        }
    }
}

std::string printable(const std::vector<unsigned char>& b, size_t cap) {
    std::string s;
    for (size_t i = 0; i < b.size() && i < cap; ++i)
        s += (b[i] >= 0x20 && b[i] < 0x7f) || b[i] == '\n' ? static_cast<char>(b[i]) : '.';
    return s;
}

std::string adb_probe(Obj& root) {
    int port = 5037;
    const std::string envport = env_or("ANDROID_ADB_SERVER_PORT");  // read-only, as adb does
    if (!envport.empty()) port = std::atoi(envport.c_str());
    root.n("port", port).s("ANDROID_ADB_SERVER_PORT", envport);

    const auto listeners = proc_net("/proc/net/tcp", static_cast<unsigned>(port), "0A");
    root.raw("listeners_proc_net_tcp", socks_json(listeners)).n("our_uid", getuid());
    bool foreign = false;
    for (const auto& l : listeners) foreign = foreign || l.uid != getuid();
    root.b("listener_owned_by_other_uid", foreign);

    struct Svc { const char* name; bool stream; };
    const Svc svcs[] = {
        {"host:version", false},
        {"host:server-status", false},
        {"host:track-devices-proto-binary", true},
        {"host:track-devices-l", true},
        {"host:track-mdns-services", true},
        {"host:mdns:check", false},
        {"host:mdns:services", false},
    };
    std::vector<std::string> results;
    for (const auto& s : svcs) {
        const AdbReply r = adb_call(port, s.name, s.stream ? 1500 : 3000);
        Obj o;
        o.s("service", s.name).s("status", r.status).s("transport_error", r.err);
        if (!r.reason.empty()) o.s("fail_reason", r.reason);
        if (r.got_payload) {
            o.n("payload_len", static_cast<long long>(r.payload.size()));
            const std::string svc = s.name;
            if (svc == "host:server-status") {
                parse_server_status(r.payload, o);
                o.s("payload_hex_head", hex_of(r.payload.data(), std::min<size_t>(r.payload.size(), 64)));
            } else if (svc.find("proto-binary") != std::string::npos) {
                o.s("payload_hex_head", hex_of(r.payload.data(), std::min<size_t>(r.payload.size(), 64)));
            } else {
                o.s("payload_text_head", printable(r.payload, 240));
            }
        }
        results.push_back(o.str());
        if (r.err.rfind("connect:", 0) == 0) break;  // no listener: stop, do NOT start a server
    }
    return arr(results);
}

// ---------------------------------------------------------------- mDNS socket facts

std::string mdns_probe() {
    Obj root;
    root.raw("udp5353_holders_v4", socks_json(proc_net("/proc/net/udp", 5353, nullptr)));
    root.raw("udp5353_holders_v6", socks_json(proc_net("/proc/net/udp6", 5353, nullptr)));

    const int s4 = socket(AF_INET, SOCK_DGRAM | SOCK_CLOEXEC, 0);
    const int one = 1;
    setsockopt(s4, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);  // SO_REUSEADDR only (see spec)
    setsockopt(s4, IPPROTO_IP, IP_MULTICAST_LOOP, &one, sizeof one);
    sockaddr_in any{};
    any.sin_family = AF_INET;
    any.sin_port = htons(5353);
    any.sin_addr.s_addr = htonl(INADDR_ANY);
    const bool bound = bind(s4, reinterpret_cast<sockaddr*>(&any), sizeof any) == 0;
    root.b("bind_5353_reuseaddr", bound);
    if (!bound) root.s("bind_error", std::strerror(errno));

    ifaddrs* ifa = nullptr;
    std::vector<std::string> ifs;
    std::set<unsigned> v6idx;
    if (getifaddrs(&ifa) == 0) {
        for (const ifaddrs* i = ifa; i != nullptr; i = i->ifa_next) {
            if (i->ifa_addr == nullptr) continue;
            const bool up = (i->ifa_flags & IFF_UP) != 0 && (i->ifa_flags & IFF_RUNNING) != 0;
            const bool mc = (i->ifa_flags & IFF_MULTICAST) != 0;
            const bool lo = (i->ifa_flags & IFF_LOOPBACK) != 0;
            const unsigned idx = if_nametoindex(i->ifa_name);
            const std::string virt_path = std::string("/sys/devices/virtual/net/") + i->ifa_name;
            if (i->ifa_addr->sa_family == AF_INET6 && up && mc && !lo) v6idx.insert(idx);
            if (i->ifa_addr->sa_family != AF_INET) continue;
            Obj o;
            char abuf[INET_ADDRSTRLEN];
            const auto* sin = reinterpret_cast<const sockaddr_in*>(i->ifa_addr);
            inet_ntop(AF_INET, &sin->sin_addr, abuf, sizeof abuf);
            o.s("name", i->ifa_name).n("ifindex", idx).s("addr", abuf)
                .b("up_running", up).b("multicast", mc).b("loopback", lo)
                .b("virtual_by_sysfs", access(virt_path.c_str(), F_OK) == 0);
            if (up && mc && !lo && bound) {
                ip_mreqn m{};
                inet_pton(AF_INET, "224.0.0.251", &m.imr_multiaddr);
                m.imr_address = sin->sin_addr;
                m.imr_ifindex = static_cast<int>(idx);
                const bool ok = setsockopt(s4, IPPROTO_IP, IP_ADD_MEMBERSHIP, &m, sizeof m) == 0;
                o.b("join_224_0_0_251", ok);
                if (!ok) o.s("join_error", std::strerror(errno));
            }
            ifs.push_back(o.str());
        }
        freeifaddrs(ifa);
    }
    root.raw("interfaces_v4", arr(ifs));
    close(s4);

    const int s6 = socket(AF_INET6, SOCK_DGRAM | SOCK_CLOEXEC, 0);
    setsockopt(s6, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
    setsockopt(s6, IPPROTO_IPV6, IPV6_V6ONLY, &one, sizeof one);
    sockaddr_in6 any6{};
    any6.sin6_family = AF_INET6;
    any6.sin6_port = htons(5353);
    any6.sin6_addr = in6addr_any;
    const bool bound6 = bind(s6, reinterpret_cast<sockaddr*>(&any6), sizeof any6) == 0;
    root.b("bind6_5353_reuseaddr", bound6);
    std::vector<std::string> j6;
    if (bound6) {
        for (const unsigned idx : v6idx) {
            ipv6_mreq m{};
            inet_pton(AF_INET6, "ff02::fb", &m.ipv6mr_multiaddr);
            m.ipv6mr_interface = idx;
            const bool ok = setsockopt(s6, IPPROTO_IPV6, IPV6_JOIN_GROUP, &m, sizeof m) == 0;
            j6.push_back(Obj().n("ifindex", idx).b("join_ff02_fb", ok).str());
        }
    }
    root.raw("joins_v6", arr(j6));
    close(s6);
    return root.str();
}

// ---------------------------------------------------------------- D-Bus / GSettings

std::string call_text(GDBusConnection* c, const char* name, const char* path, const char* iface,
                      const char* method, GVariant* params, bool& ok) {
    GError* err = nullptr;
    GVariant* r = g_dbus_connection_call_sync(c, name, path, iface, method, params, nullptr,
                                              G_DBUS_CALL_FLAGS_NONE, 2500, nullptr, &err);
    ok = r != nullptr;
    std::string out;
    if (r != nullptr) {
        gchar* t = g_variant_print(r, FALSE);
        out = t;
        g_free(t);
        g_variant_unref(r);
    } else {
        out = err != nullptr ? err->message : "unknown error";
        if (err != nullptr) g_error_free(err);
    }
    return out;
}

Obj dbus_call_obj(GDBusConnection* c, const char* name, const char* path, const char* iface,
                  const char* method, GVariant* params) {
    bool ok = false;
    const std::string t = call_text(c, name, path, iface, method, params, ok);
    Obj o;
    o.s("call", std::string(iface) + "." + method).b("ok", ok).s(ok ? "result" : "error", t);
    return o;
}

std::vector<std::string> names_matching(GDBusConnection* c, const char* method) {
    std::vector<std::string> out;
    GError* err = nullptr;
    GVariant* r = g_dbus_connection_call_sync(c, "org.freedesktop.DBus", "/org/freedesktop/DBus",
                                              "org.freedesktop.DBus", method, nullptr, nullptr,
                                              G_DBUS_CALL_FLAGS_NONE, 2500, nullptr, &err);
    if (r == nullptr) {
        if (err != nullptr) g_error_free(err);
        return out;
    }
    gchar** names = nullptr;
    g_variant_get(r, "(^as)", &names);
    for (gchar** p = names; p != nullptr && *p != nullptr; ++p) {
        const std::string n = *p;
        for (const char* needle : {"StatusNotifier", "Notifications", "portal", "ScreenSaver",
                                   "xapp", "login1", "Avahi", "resolve1"})
            if (n.find(needle) != std::string::npos) { out.push_back(n); break; }
    }
    g_strfreev(names);
    g_variant_unref(r);
    return out;
}

std::string gs_read(const char* schema, const char* key) {
    GSettingsSchemaSource* src = g_settings_schema_source_get_default();
    if (src == nullptr) return "no_schema_source";
    GSettingsSchema* sc = g_settings_schema_source_lookup(src, schema, TRUE);
    if (sc == nullptr) return "schema_missing";
    const bool has = g_settings_schema_has_key(sc, key) != FALSE;
    g_settings_schema_unref(sc);
    if (!has) return "key_missing";
    GSettings* s = g_settings_new(schema);
    GVariant* v = g_settings_get_value(s, key);
    gchar* t = g_variant_print(v, FALSE);
    std::string r = t;
    g_free(t);
    g_variant_unref(v);
    g_object_unref(s);
    return r;
}

std::string dbus_probe() {
    Obj root;
    GError* err = nullptr;
    GDBusConnection* sess = g_bus_get_sync(G_BUS_TYPE_SESSION, nullptr, &err);
    if (sess == nullptr) {
        root.b("session_bus", false).s("session_bus_error", err != nullptr ? err->message : "?");
        if (err != nullptr) g_error_free(err);
        err = nullptr;
    } else {
        root.b("session_bus", true);
        std::vector<std::string> own, act;
        for (const auto& n : names_matching(sess, "ListNames")) own.push_back(jstr(n));
        for (const auto& n : names_matching(sess, "ListActivatableNames")) act.push_back(jstr(n));
        root.raw("session_names_owned", arr(own)).raw("session_names_activatable", arr(act));

        std::vector<std::string> calls;
        calls.push_back(dbus_call_obj(sess, "org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher",
            "org.freedesktop.DBus.Properties", "Get",
            g_variant_new("(ss)", "org.kde.StatusNotifierWatcher", "IsStatusNotifierHostRegistered")).str());
        calls.push_back(dbus_call_obj(sess, "org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher",
            "org.freedesktop.DBus.Properties", "Get",
            g_variant_new("(ss)", "org.kde.StatusNotifierWatcher", "RegisteredStatusNotifierItems")).str());
        calls.push_back(dbus_call_obj(sess, "org.freedesktop.Notifications", "/org/freedesktop/Notifications",
            "org.freedesktop.Notifications", "GetCapabilities", nullptr).str());
        calls.push_back(dbus_call_obj(sess, "org.freedesktop.Notifications", "/org/freedesktop/Notifications",
            "org.freedesktop.Notifications", "GetServerInformation", nullptr).str());
        for (const char* key : {"color-scheme", "contrast", "high-contrast"})
            calls.push_back(dbus_call_obj(sess, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                "org.freedesktop.portal.Settings", "Read",
                g_variant_new("(ss)", "org.freedesktop.appearance", key)).str());
        root.raw("session_calls", arr(calls));
    }
    if (sess != nullptr) g_object_unref(sess);

    GDBusConnection* sys = g_bus_get_sync(G_BUS_TYPE_SYSTEM, nullptr, &err);
    if (sys == nullptr) {
        root.b("system_bus", false).s("system_bus_error", err != nullptr ? err->message : "?");
        if (err != nullptr) g_error_free(err);
        err = nullptr;
    } else {
        root.b("system_bus", true);
        std::vector<std::string> calls;
        calls.push_back(dbus_call_obj(sys, "org.freedesktop.login1", "/org/freedesktop/login1/session/auto",
            "org.freedesktop.DBus.Properties", "Get",
            g_variant_new("(ss)", "org.freedesktop.login1.Session", "LockedHint")).str());
        root.raw("system_calls", arr(calls));
        g_object_unref(sys);
    }

    Obj gs;
    gs.s("cinnamon.enable-animations", gs_read("org.cinnamon.desktop.interface", "enable-animations"))
        .s("cinnamon.gtk-theme", gs_read("org.cinnamon.desktop.interface", "gtk-theme"))
        .s("cinnamon.high-contrast", gs_read("org.cinnamon.desktop.interface", "high-contrast"))
        .s("gnome.color-scheme", gs_read("org.gnome.desktop.interface", "color-scheme"))
        .s("gnome.enable-animations", gs_read("org.gnome.desktop.interface", "enable-animations"))
        .s("xapp.left-click-activate-apps", gs_read("org.x.apps.statusicon", "left-click-activate-apps"));
    root.raw("gsettings", gs.str());
    return root.str();
}

// ---------------------------------------------------------------- toolchain facts

std::string run_text(std::vector<const char*> argv, int* exit_code = nullptr) {
    argv.push_back(nullptr);
    gchar* out = nullptr;
    gint status = 0;
    GError* err = nullptr;
    if (!g_spawn_sync(nullptr, const_cast<gchar**>(argv.data()), nullptr, G_SPAWN_SEARCH_PATH,
                      nullptr, nullptr, &out, nullptr, &status, &err)) {
        std::string m = err != nullptr ? err->message : "spawn failed";
        if (err != nullptr) g_error_free(err);
        if (exit_code != nullptr) *exit_code = -1;
        return m;
    }
    std::string s = out != nullptr ? out : "";
    g_free(out);
    if (exit_code != nullptr) *exit_code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    return s;
}

std::string toolchain_probe() {
    Obj o;
    std::vector<std::string> pk;
    for (const char* mod : {"gio-2.0", "gio-unix-2.0", "gtk+-3.0", "gtk4", "x11", "xrandr",
                            "gtk-layer-shell-0", "xapp", "libudev"}) {
        int code = 0;
        run_text({"pkg-config", "--exists", mod}, &code);
        pk.push_back(Obj().s("module", mod).b("dev_files_present", code == 0).str());
    }
    o.raw("pkg_config", arr(pk));
    // `adb version` does not contact the server. Never run anything that might start one.
    o.s("adb_version", run_text({"adb", "version"}));
    return o.str();
}

}  // namespace

int main(int argc, char** argv) {
    const char* outpath = argc > 1 ? argv[1] : nullptr;
    Obj env;
    for (const char* k : {"XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "DESKTOP_SESSION", "DISPLAY",
                          "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS",
                          "GDK_BACKEND", "ANDROID_HOME", "ANDROID_SDK_ROOT"})
        env.s(k, env_or(k, "<unset>"));

    Obj adb;
    const std::string services = adb_probe(adb);
    adb.raw("services", services);

    Obj root;
    root.s("kind", "adb-hawk-linux G0 probe")
        .raw("environment", env.str())
        .raw("adb", adb.str())
        .raw("mdns", mdns_probe())
        .raw("dbus", dbus_probe())
        .raw("toolchain", toolchain_probe());
    const std::string json = root.str() + "\n";
    std::fputs(json.c_str(), stdout);
    if (outpath != nullptr) {
        std::ofstream f(outpath);
        f << json;
    }
    return 0;
}
