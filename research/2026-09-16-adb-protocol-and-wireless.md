# ADB access strategy: host protocol, device tracking, wireless discovery, pairing

- **Date:** 2026-09-16 (refreshed 2026-09-19 for Phase 5A)
- **Phase:** pre-Phase 0 (architecture); refreshed in Phases 4, 5A
- **Status:** current

## Problem

Decide, from first principles against the September 2026 Android tooling, how ADB View observes
ADB state: what to depend on, how to avoid polling, how to model wireless pairing versus wireless
connection, and how to get a trustworthy transport signal without parsing serial strings.

Phase 5A additionally requires an **immutable pin** of the `Devices` / `Device` protobuf schema
(field numbers and enums) and observed `host:track-devices-proto-binary` wire payloads.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| Android Debug Bridge (adb) — official guide | https://developer.android.com/tools/adb | last updated 2026-09-02 |
| AOSP adb host services documentation | https://android.googlesource.com/platform/packages/modules/adb/+/1cf2f017d312f73b3dc53bda85ef2610e35a80e9/docs/dev/services.md | commit `1cf2f017d312f73b3dc53bda85ef2610e35a80e9` |
| AOSP `adb_host.proto` (immutable pin) | https://android.googlesource.com/platform/packages/modules/adb/+/1cf2f017d312f73b3dc53bda85ef2610e35a80e9/proto/adb_host.proto | commit `1cf2f017d312f73b3dc53bda85ef2610e35a80e9` |
| SDK Platform Tools release notes | https://developer.android.com/tools/releases/platform-tools | platform-tools 37.0.1 (July 2026) |
| Live observation on this machine | `127.0.0.1:5037` host protocol against installed adb | platform-tools **37.0.1-15733141**, 2026-09-19 |

**Do not use `refs/heads/main` alone as schema authority.** The Devices schema for Phase 5A is pinned to
commit `1cf2f017d312f73b3dc53bda85ef2610e35a80e9` of `platform/packages/modules/adb`.
Content SHA-256 of that `adb_host.proto` blob (UTF-8, as fetched):
`fb6d382f03f705a46b04f6008a71571c124c826669296beff2039be4063f7b4c`.

## Version investigated

platform-tools **37.0.1**; adb Wi-Fi 2.0 (Android 17 + adb 37.x); Windows 11; .NET 10.

## Findings

1. **Host protocol.** The ADB server listens on TCP `127.0.0.1:5037`. A request is a 4-character
   hex length followed by the ASCII service name; the reply is `OKAY` or `FAIL` plus a
   length-prefixed reason. One-shot services close the connection; `track-*` services keep it open
   and push a new length-prefixed payload on every change.
2. **Event-driven device tracking exists in three variants:** `host:track-devices`,
   `host:track-devices-proto-binary`, `host:track-devices-proto-text`. Each payload is a complete
   device list, not a delta. After `OKAY`, updates are `[4 ASCII hex length][payload]`.
3. **Pinned `Devices` / `Device` schema** (commit `1cf2f017…`):

   ```
   enum ConnectionState {
     ANY=0, CONNECTING=1, AUTHORIZING=2, UNAUTHORIZED=3, NOPERMISSION=4,
     DETACHED=5, OFFLINE=6, BOOTLOADER=7, DEVICE=8, HOST=9,
     RECOVERY=10, SIDELOAD=11, RESCUE=12
   }
   enum ConnectionType { UNKNOWN=0, USB=1, SOCKET=2 }

   message Device {
     string serial = 1;
     ConnectionState state = 2;
     string bus_address = 3;          // ignored by ADB View
     string product = 4;
     string model = 5;
     string device = 6;
     ConnectionType connection_type = 7;
     int64 negotiated_speed = 8;      // ignored
     int64 max_speed = 9;             // ignored
     int64 transport_id = 10;         // ignored
   }
   message Devices { repeated Device device = 1; }
   ```

4. **`host:track-devices-l` text format (observed + services.md).** Same long listing as
   `host:devices-l`: lines of
   `SERIAL<whitespace>STATE[ product:… model:… device:… transport_id:… …]\n`.
   No `connection_type` field. Transport must not be invented by the text parser (Phase 5B identity).
5. **Live USB capture (2026-09-19)** — device `R8VXA02SNKH` (Samsung Galaxy A06 family):
   - proto-text: `state: DEVICE`, `connection_type: USB`, `product: a06xx`, `model: SM_A065F`, `device: a06`
   - proto-binary hex (43 bytes): `0A290A0B52385658413032534E4B481008220561303678782A08534D5F4130363546320361303638015002`
   - Field walk matches pin: Devices.device=1 → Device{serial=1, state=2 (8), product=4, model=5, device=6, connection_type=7 (1=USB), transport_id=10}
   - text-l: `R8VXA02SNKH            device product:a06xx model:SM_A065F device:a06 transport_id:2`
6. **Wi-Fi / SOCKET observation:** **unavailable** on this machine at capture time (only one USB
   device attached). Do not fabricate SOCKET payloads; FakeAdbServer scripts cover SOCKET in tests.
7. **Framing vs payload errors (Phase 5A contract):**
   - Valid frame + malformed protobuf → drop that update, keep stream (log ≤ once/minute).
   - Malformed ADB framing / mid-frame EOF → terminate session, reconnect with tracker backoff.
8. **Proto unsupported vs generic FAIL (Phase 5A):** AOSP `sockets.cpp` sends
   `unknown host service '<service>'` when a host service cannot be created. Only that
   diagnostic (matched via `AdbHostServiceDiagnostics.IsUnknownHostService`) caches
   generation-scoped text fallback. Any other `CommandFailed` FAIL keeps proto preferred,
   emits `DeviceTrackerFailed`, and reconnects with tracker backoff.
9. **Tracker reconnect backoff (ENGINEERING.md §6.5):** 0.5 s, 1 s, 2 s, 5 s, 10 s, 30 s cap, jittered;
   distinct from server-supervisor backoff (1/2/5/10/30/60).
10. **mDNS / pairing / Wi-Fi 2.0** findings from earlier revisions remain valid for Phases 6+ and are
   unchanged here.

## Chosen approach

Speak the host protocol directly from C#: `AdbHostConnection` framing, supervised
`host:track-devices-proto-binary` with per-generation fallback to `host:track-devices-l`,
`AdbProtoReader` for Devices (known fields only), raw `RawDevice` emission before identity/UI
normalization (Phase 5B/5C).

## Why

Removes polling, gives a documented transport signal when proto is available, keeps protocol
parsing fakeable, and isolates framing failures from protobuf body failures.

## Rejected approaches

- **Google.Protobuf + vendored `.proto`** — dependency and regeneration ritual for a handful of fields.
- **Shelling `adb devices -l` on a timer** — polling; forbidden.
- **Inferring Wi-Fi from `emulator-*` or colon serials inside the text parser** — belongs in
  `DeviceIdentity` (Phase 5B), not the raw tracker.

## Compatibility concerns

platform-tools &lt; 33 may lack proto tracking → text fallback, `connection_type` absent.
mDNS-blocked networks are Phase 6.

## Testing implications

- Unit: Devices protobuf parse, text-l parse, malformed body vs framing, generation-scoped proto
  fallback, tracker backoff 0.5…30, injected clock timestamps.
- Cross-check: committed USB binary fixture vs observed proto-text.
- Fake server: scripted track streams; no sleeps.
- Hardware: USB captured; Wi-Fi SOCKET capture still open (V-9).
- **Headed production acceptance (2026-09-19, Phase 5C/5D):** Release `AdbView.exe`
  (no fixture) on Samsung A06 `R8VXA02SNKH` → flyout `DeviceCard` `SM_A065F` /
  Connected / USB / id `usb:R8VXA02SNKH`; tray `connected` /
  `Connected: SM_A065F (USB)`; battery/Android version absent (Phase 5). Physical
  USB disconnect/reconnect not live-observed (OS blocked software disable);
  hermetic tests cover authoritative removal and 2 s grace. Recorded in
  `docs/DEVICE-TEST-MATRIX.md`.

## Open items

V-1 residual (mDNS host-service string), V-9 (Wi-Fi SOCKET binary capture when hardware available),
V-10 (Wi-Fi 2.0 pairing timing), V-7 (battery dumpsys).
