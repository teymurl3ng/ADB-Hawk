# Metadata host:transport + classic shell framing (V-7)

- **Date:** 2026-09-19
- **Author:** agent (Phase 7A)
- **Phase:** 7
- **Status:** current

## Problem

Phase 7 must enrich Connected devices with battery / exact model / Android
version over the local ADB smart socket. `AdbHostConnection.ExecuteOneShotAsync`
assumes host-style hex4-prefixed payloads after `OKAY`. Device `shell:` after
`host:transport:` may not use that framing. Framing, connection lifetime,
timeouts, and parsers must be pinned before any StateEngine integration (7B).

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| AOSP adb services catalog | https://android.googlesource.com/platform/packages/modules/adb/+/1cf2f017d312f73b3dc53bda85ef2610e35a80e9/docs/dev/services.md | commit `1cf2f017d312f73b3dc53bda85ef2610e35a80e9` (main @ 2026-09-19) |
| AOSP `adb.cpp` host `transport:` switch | https://android.googlesource.com/platform/packages/modules/adb/+/1cf2f017d312f73b3dc53bda85ef2610e35a80e9/adb.cpp | same commit |
| AOSP daemon shell service (classic vs `shell,v2`) | https://android.googlesource.com/platform/packages/modules/adb/+/1cf2f017d312f73b3dc53bda85ef2610e35a80e9/daemon/services.cpp | same tree |
| AOSP `BatteryService` dumpsys `level:` | https://android.googlesource.com/platform/frameworks/base/+/1cdfff555f4a21f71ccc978290e2e212e2f8b168/services/core/java/com/android/server/BatteryService.java | commit `1cdfff555f4a21f71ccc978290e2e212e2f8b168` |
| Platform Tools live | local `adb.exe` | **37.0.1-15733141** |
| Samsung A06 live USB+Wi-Fi | `R8VXA02SNKH`, `192.168.18.248:5555` | Android 16 / SDK 36, model `SM-A065F` |

## Version investigated

- Platform Tools: 37.0.1-15733141
- Host OS: Windows 11 10.0.26100
- Devices: Samsung SM-A065F (USB serial `R8VXA02SNKH`, Wi-Fi `192.168.18.248:5555`)

## Findings

### A. Host transport selection

Documented and observed:

1. Client connects to `127.0.0.1:5037`.
2. Sends smart-socket request `host:transport:<serial>` (hex4 length + ASCII).
3. Server replies `OKAY` on success, or `FAIL` + hex4 + reason.
4. After `OKAY`, subsequent requests on the **same** TCP connection are forwarded
   to that device’s adbd (`docs/dev/services.md`; `adb.cpp` `transport:` path →
   `HostRequestResult::SwitchedTransport`).

`host:tport:serial:` (modern client) also works but returns an extra 8-byte
transport id after `OKAY`. ENGINEERING freezes **`host:transport:<serial>`**;
live 37.0.1 accepts it for both USB and `host:port` Wi-Fi serials.

### B–C. Shell after transport — framing

For classic **`shell:<command>`** (not `shell,v2:`):

1. Client sends `shell:dumpsys battery` / `shell:getprop …` as a second
   smart-socket request on the switched connection.
2. Server/device replies **`OKAY`**.
3. Body is **raw stdout/stderr bytes until TCP EOF** — **not** host hex4
   frames, **not** shell-protocol v2 packets.

Live hex heads (USB A06, PT 37.0.1):

| Service | Shell status | Bytes | Hex head | Text |
|---|---|---|---|---|
| `shell:getprop ro.product.model` | OKAY | 9 | `53 4D 2D 41 30 36 35 46 0A` | `SM-A065F\n` |
| `shell:getprop ro.build.version.release` | OKAY | 3 | `31 36 0A` | `16\n` |
| `shell:dumpsys battery` | OKAY | ~11708 | starts `43 75 72 72…` (`Current Battery…`) | includes `  level: 97` |

`shell,v2:` is rejected for Phase 7: ENGINEERING lists classic `shell:…` only,
and v2 would require packet parsing ADB View does not need.

### D. Existing `AdbHostConnection` is incompatible

`ExecuteOneShotAsync` after `OKAY` always reads a hex4 length then that many
bytes, then closes. After transport+shell `OKAY`, the next bytes are payload
(e.g. `S` of `SM-A065F`), not hex digits → would throw `MalformedLength`.

**Chosen:** dedicated `MetadataDeviceShellClient` (same pattern as
`HostPairClient`): connect → transport → OKAY → shell → OKAY → read raw to
EOF with a metadata-specific byte cap.

### E. Connection lifetime

One classic `shell:` service **consumes** the connection (EOF ends the
stream). Do **not** reuse the socket for a second shell command.

**Phase 7A policy:** fresh TCP connection per metadata command.

### F. Battery / properties

- `BatteryService` dump prints `  level: ` + `batteryLevel` (and separately
  `  Capacity level: `). Samsung A06 also appends OEM log lines containing
  `level:N,` inside `ACTION_BATTERY_CHANGED` text.
- Parser must match **line** `^\s*level:\s*(-?\d+)\s*$` only — never
  `Capacity level:` and never log-line `level:`.
- `getprop` returns the property value plus a trailing newline; trim
  surrounding whitespace; empty → null. Preserve preview/dev release strings
  as returned (no SDK→version mapping). Live A06: model `SM-A065F`, release
  `16` (SDK 36 observed separately; not used as version).

### Timeout / retry ambiguity (ENGINEERING §6.9)

§6.9: *“Each probe: `host:transport:<serial>` → `shell:dumpsys battery` …,
5 s timeout, single retry after 2 s”* diagrams **battery**. The Connected
trigger also fetches model/version once per generation. §6.1 one-shot timeout
is 5 s per request. Partial success is required for truthful fields.

**Phase 7A freeze (explicit, not silent):**

| Rule | Freeze |
|---|---|
| Timeout scope | **Per individual command attempt** (each fresh connection) = **5 s** |
| Retry scope | **Per failed command**: exactly **one** retry after injected **2 s** |
| Three-field probe | Run three commands independently; keep successful fields if another fails |
| Battery-only refresh (7B) | Same per-command timeout/retry |

If product owners later require atomic all-or-nothing or a shared 5 s budget
across three commands, that needs an ENGINEERING clarification before 7B.

### Output bound

ENGINEERING §6.1 generic cap is 1 MiB. Metadata dumpsys on A06 was ~12 KiB.
**Phase 7A metadata cap: 64 KiB** per shell body. Overflow → local probe
failure (no unbounded growth).

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Reuse `ExecuteOneShotAsync` | Less code | Wrong framing; broken |
| `shell,v2:` | Exit codes | Extra protocol; not in ENGINEERING |
| `adb.exe shell` spawn | Easy | Forbidden (R-14 / §12.1); not socket citizenship |
| `cmd battery get level` | Smaller output | V-7 fallback only; dumpsys works on A06 |

## Chosen approach

Dedicated device-shell client + `MetadataProbe` with closed `MetadataCommand`
enum, three compile-time command strings, per-command 5 s / one 2 s retry,
64 KiB raw EOF read, strict battery line parser, getprop trim-only parsers.

## Why

Matches AOSP docs + PT 37.0.1 live evidence, preserves ENGINEERING command
list, keeps failure local, enables hermetic FakeAdb targeting tests, and
avoids inventing a generic shell facility.

## Rejected approaches

- Generic public shell API / user-supplied commands
- Clamping out-of-range battery
- SDK-derived Android version
- Tracker `model` as ExactModel (7A source is getprop only)
- Socket reuse after shell EOF

## Compatibility concerns

- Very old adbd without `shell:` would FAIL at shell OKAY — local failure.
- OEM dumpsys extras are ignored if the AOSP `level:` line remains present
  (verified Samsung A06).
- Emulator serials (`emulator-5554`) use the same transport+shell path.

## Testing implications

Hermetic FakeAdb multi-device transport selection; parser fixtures including
Samsung-like dumps with `Capacity level:` and ACTION log lines; injected
timeout/retry; live observe-only smoke when A06 attached.

## Open items

- 7B: StateEngine generation validation at reducer boundary; null semantics
  for `MetadataReceived` (Battery=null clears; Model/AndroidVersion null
  preserve cache); bump generation when `AdbSerial` changes.
- V-7 optional: additional OEM dumpsys samples if dumpsys ever lacks `level:`
  on some devices (`cmd battery get level` fallback only after research).
