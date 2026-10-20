# adb-hawk-linux — Engineering

**Status:** Step 0 (G0 host probe) in progress. Nothing below is frozen until the open items in §5 are closed.

**Lineage:** a Linux re-implementation of the principles in Windows ADB-Hawk (`../ADB-Hawk`: `DESIGN.md`, `ENGINEERING.md`, `AGENTS.md`). Product contract and behavior come from there; this file records only what is *different or newly known* on Linux. Where this file is silent, ADB-Hawk's text is the reference for intent, not for mechanism.

Evidence labels used throughout: **[observed]** measured on this machine by a tool in this repo; **[source]** read from a file/binary; **[inferred]** reasoning from the above; **[unverified]** a hypothesis nobody has tested.

---

## 1. What G0 observed (2026-10-19, Linux Mint 22.3, Cinnamon 6.6.4, X11)

Raw data: `probe-report.json` (machine-specific, git-ignored). Re-run: `build/probe_g0 probe-report.json` from the real desktop session.

### ADB
- **[observed]** An adb server owned by our uid listens on `127.0.0.1:5037`; `host:version` → `0029` (protocol 41). Client is `34.0.4-debian` (`adb version` reports `1.0.41`).
- **[observed]** On this server `host:server-status`, `host:track-devices-proto-binary` and `host:track-mdns-services` all reply `FAIL unknown host service`.
  - **Consequence:** on distro adb the text paths are the *primary* path, not a rare fallback: `host:track-devices-l` (works), `host:mdns:services` (one-shot, works, empty at probe time), in-process mDNS browser for streaming discovery. `server-status` is unavailable, so the server's executable path, mDNS backend and `mdns_enabled` are **not obtainable from the server**.
- **[observed]** `host:track-devices-l` first frame was empty (no device attached during the probe) — so the *non-empty* line format on Linux (including `no permissions (...)`) is still **[unverified]**.
- **[observed, unexplained]** `host:mdns:check` returned `OKAY` + a zero-length payload. The CLI usually prints a daemon string. Do not build logic on this reply until understood.
- **[inferred]** `host:version` is the protocol number, not the platform-tools build. The mismatch guard can therefore only compare protocol numbers (server `0029` vs the `1.0.NN` suffix of `adb version`). That is also what adb itself compares **[unverified for newer clients]**. Whether a *newer* client against an older server kills it is **[unverified]**.

### mDNS (UDP 5353)
- **[observed]** `avahi-daemon` (uid 110) holds `0.0.0.0:5353` on v4 and v6. Our uid holds four more 5353 sockets (three bound to `224.0.0.251`, one wildcard); which processes is not known (uid only, by design — see F12).
- **[observed]** A second `SO_REUSEADDR`-only bind of `0.0.0.0:5353` (v4) and `[::]:5353` (v6) **succeeded** alongside avahi, and `IP_ADD_MEMBERSHIP` by `ifindex` / `IPV6_JOIN_GROUP ff02::fb` succeeded on `wlp2s0`.
- **[not tested]** That packets are actually *received* on that socket. Needs G5.
- **[observed]** Only one multicast-capable interface exists here (`wlp2s0`). Multi-interface behavior **cannot** be exercised on this machine; G5 must use network namespaces.

### Desktop session
- **[observed]** `org.kde.StatusNotifierWatcher` and `org.x.StatusNotifierWatcher` are both owned; `IsStatusNotifierHostRegistered` = true; three items registered (blueman, Antigravity, one xapp).
- **[observed]** Notification server: `cinnamon 6.6.4`, spec `1.2`, capabilities `actions, action-icons, body, body-markup, icon-static, persistence, sound`. Whether it honors the `transient` or `desktop-entry` hints is **[unverified]** (hints are not advertised as capabilities).
- **[observed]** `org.freedesktop.portal.Settings.Read` works: `org.freedesktop.appearance/color-scheme` → `2`, `contrast` → `0`. A `high-contrast` key does not exist there (expected; not a portal key).
- **[observed]** `org.cinnamon.desktop.interface` has `enable-animations` (true) and `high-contrast` (false). `org.gnome.desktop.interface color-scheme` reads `'default'` while the portal says `2` **[inferred: the xapp portal derives it from the GTK theme]**.
- **[observed]** `org.freedesktop.login1 .../session/auto` `LockedHint` is readable on the system bus. `org.freedesktop.ScreenSaver` is owned on the session bus.
- **[observed]** `/proc/sys/kernel/apparmor_restrict_unprivileged_userns` = `0` (network namespaces for G5 should work unprivileged **[unverified until G5 runs]**).

### Toolchain
- **[observed]** Dev files present: `gio-2.0`, `gio-unix-2.0`, `libudev`. **Absent:** `gtk+-3.0`, `gtk4`, `x11`, `xrandr`, `gtk-layer-shell-0`, `xapp`. Everything up to and including D-Bus can be built now; the GTK/X11 shell needs `sudo apt install libgtk-3-dev libx11-dev libxrandr-dev` (user decision pending).
- **[observed]** The agent sandbox has no session bus and only `lo`. D-Bus and real-network probes must be run unsandboxed (needs user approval each time).

---

## 2. Decisions (provisional until §5 closes)

| # | Decision | Basis |
|---|---|---|
| D1 | C++20 (`g++ 13`), no exceptions across C boundaries, ASan/UBSan in tests. | Most code is glue to C libraries; parsers isolated in `core/`. |
| D2 | `core/` is pure: no I/O, no GLib, no GTK. Parsers take `std::span`, return values. | Testable, fuzzable. |
| D3 | GLib **main thread is the single writer**; one `adb-io` thread owns blocking adb sockets; events return via mutex queue + eventfd GSource; **one snapshot per drain**. | Removes locks from state; matches "latest snapshot wins". |
| D4 | Text ADB paths are primary (§1). Proto services are probed once, used only if they answer `OKAY`; never inferred from a version string. | Observed on this server. |
| D5 | Tray: StatusNotifierItem + `com.canonical.dbusmenu`; `Status=Active` always; pixmap icon rendered by us; re-register on `NameOwnerChanged` of the watcher. `org.x.StatusIcon` only if G0-SNI shows SNI clicks are unusable. | Watcher present; click behavior still to be measured (§5). |
| D6 | Click coordinates are a **hint**. On X11 fall back to the pointer position; validate against monitor geometry. | SNI hosts differ; `(0,0)` on keyboard activation. |
| D7 | Single instance = D-Bus well-known name `org.adbhawk.Instance` (do-not-queue); second launch calls `Toggle` and exits. | Session bus is required anyway; no stale socket. |
| D8 | Shell toolkit: GTK 3.24 behind a small `shell/` interface. | GTK4 removed the X11 window-control calls; layer-shell on Mint is GTK3-only. |
| D9 | **No root repair in v1** (no `pkexec`). Non-root `Restart ADB` only, user-initiated. | pkexec prompt shows a path, not an action; low value here. |
| D10 | mDNS sockets use `SO_REUSEADDR` only; send queries from an ephemeral port; join per `ifindex`; rejoin on `NETLINK_ROUTE` events. | Observed sharing works with avahi. `SO_REUSEPORT` avoidance is a preference, not a gate. |
| D11 | Time for "N s ago" uses `CLOCK_BOOTIME`. | `MONOTONIC` pauses over suspend. |
| D12 | Linux adds a device state for `no permissions (...)` with its own jargon-free guidance; it is **not** mapped to `Unauthorized`. | Different cause, different fix. |

---

## 3. Differences from Windows ADB-Hawk worth remembering
- ADB binary search: `ANDROID_HOME` → `ANDROID_SDK_ROOT` → `PATH` (absolute entries only) → `~/Android/Sdk/platform-tools` → `/usr/lib/android-sdk/platform-tools` → `/opt/android-sdk/platform-tools`; require regular file + `X_OK`. An autostarted app may not see variables exported only in `~/.bashrc`.
- Diagnostics identify UDP 5353 holders by **uid** from `/proc/net/udp` (other users' `/proc/<pid>/fd` is unreadable); virtual interfaces by `/sys/devices/virtual/net/<if>` plus `IFF_*` flags, not name lists.
- Autostart: XDG `autostart/*.desktop` (read the checkbox back from the file, honoring `Hidden`/`X-GNOME-Autostart-enabled`), not a systemd user service.
- Honor `ANDROID_ADB_SERVER_PORT` **read-only**.
- Verify the listener on the ADB port belongs to our uid before showing its devices.

---

## 4. Corrections log (claims made earlier in planning that were wrong or overstated)
1. *"Cinnamon left-click behavior is settled by disassembly."* **Overstated.** I inferred what a flag in `xapp-sn-watcher` means; nothing was observed. Replaced by the G0-SNI test (§5).
2. *"proto services may exist on 34.0.4-debian."* **Wrong for this server** (observed `unknown host service`). Text paths are primary (D4).
3. *"SO_REUSEPORT must be banned by a static check."* **Overstated.** Dropped as a gate (D10).
4. *Claude's `host:version` vs `adb version` guard* — kept, but narrowed to protocol numbers (§1, inferred).

---

## 5. Open items (each must be closed by a probe result, not an argument)
1. **SNI click behavior on Cinnamon** — run `build/probe_sni` and `build/probe_sni --item-is-menu` in the desktop session, click left/right/middle; record which method fires and what x/y arrive. Decides D5/D6 and whether `org.x.StatusIcon` is needed.
2. Non-empty `track-devices-l` line formats on Linux, incl. `no permissions (...)` — needs a device and a deliberately unpermitted state (or a captured transcript).
3. What `host:mdns:check` really returns on this server.
4. Packets actually received on the shared 5353 socket (G5).
5. Whether Cinnamon honors notification hints `transient` / `desktop-entry`.
6. Cinnamon behavior for window type `UTILITY`/`NOTIFICATION` + `ABOVE` (G7, needs X11 dev headers).

---

## 6. Gates (`tools/verify.sh`) and build order

| Gate | What | State |
|---|---|---|
| G0 | host probes compile warning-free; live runs are deliberate (`build/probe_g0`, `build/probe_sni`) | probes written; `probe_g0` run once; `probe_sni` **not yet run** |
| G1 | build hygiene + static rules (no `ADB_*` env writes, `kill-server` only in one file, no `system`/`popen`) | rules written, **no `src/` yet** → SKIP |
| G2–G8 | unit/sanitizer/fake-adbd/mDNS-netns/D-Bus/X11/resource | not implemented → SKIP |

Order: P0 G0 → P1 `core/` → P2 `adb-io` + fake adbd + `--print-snapshots` → P3 D-Bus → P4 GTK3 shell → P5 mDNS/netlink/QR → P6 diagnostics/autostart/suspend → P7 bubble → P8 Wayland tiers.
