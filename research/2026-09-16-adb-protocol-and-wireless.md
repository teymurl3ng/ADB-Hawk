# ADB access strategy: host protocol, device tracking, wireless discovery, pairing

- **Date:** 2026-09-16 (refreshed 2026-09-19 for Phase 5A / 6A)
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
   - Valid frame + malformed protobuf → drop that update, keep stream (log â‰¤ once/minute).
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

## Phase 6A — mDNS wireless discovery (refreshed 2026-09-19)

### Immutable pins

| Item | Pin | Evidence |
|---|---|---|
| Service type constants (`adb`, `adb-tls-pairing`, `adb-tls-connect` → full `_…._tcp`) | AOSP `platform/packages/modules/adb` **`1cf2f017d312f73b3dc53bda85ef2610e35a80e9`** `adb_mdns.h` / `adb_mdns.cpp` | `ADB_FULL_MDNS_SERVICE_TYPE(atype) ("_" atype "._tcp")`; `kADBDNSServices[]` |
| Complete-list stream send logic | android-cuttlefish **`e412cd1a7f35`** `base/cvd/adb/client/mdns_tracker.cpp` (vendored AOSP adb client, Copyright 2025) | `list_mdns_services()` serializes full `MdnsServices`; `update_mdns_trackers()` pushes that complete payload to every tracker |
| `MdnsServices` / `MdnsService` protobuf schema | **Not on** `packages/modules/adb` `refs/heads/main` as of 2026-09-19 (no `mdns_tracker.cpp`, no `MdnsService` in published `adb_host.proto`) | Embedded FileDescriptorSet in platform-tools **37.0.1-15733141** `adb.exe` SHA-256 **`B4A6B455702684652CCCF7B46258B29E653538904359A58FD4931CF3EF286B3F`** |
| Live host/CLI behaviour | Installed platform-tools **37.0.1-15733141** on this machine | Non-mutating probes 2026-09-19 |

**Open AOSP gap (reported, not blocked):** the tracking implementation and `MdnsServices` schema have not landed on googlesource `packages/modules/adb` main under an immutable SHA we can fetch. Phase 6A treats the **37.0.1 binary FileDescriptor + live OKAY payloads + cuttlefish tracker send loop** as the authority for wire shape and complete-list semantics until AOSP publishes the matching blob.

### Verified host services (37.0.1)

| Service | Kind | Observed |
|---|---|---|
| `host:track-mdns-services` | **preferred streaming** | `OKAY` + length-prefixed **binary** `MdnsServices` frames (same framing as track-devices: `[4 ASCII hex len][payload]`) |
| `host:track-mdns-services-proto-binary` | alias | Identical binary payload to preferred (live) |
| `host:track-mdns-services-proto-text` | alias | Still binary on 37.0.1 (name does **not** switch encoding) |
| `host:mdns:services` | **one-shot last resort** | `OKAY` + text body `instance\tservice_type\thost:port\n` (IPv4 observed; optional Wi-Fi 2.0 fields absent) |
| `host:mdns:check` | diagnostic | `OKAY` + `mdns daemon version [adb discovery 0.0.0]` |

No separate ProtoMdnsTracking capability probe API exists; unsupported preferred tracking is detected only via AOSP `unknown host service '…'` FAIL (same classifier as Phase 5A).

### CLI fallback (verified)

- Exact argv: `mdns track-services --proto-text` against the discovered absolute `adb.exe`.
- Live stdout is protobuf **text** for message `Services` (`tcp` / `tls` / `pair` repeated groups).
- `mdns track-services` without flags → `adb.exe: mdns track-services takes two arguments`.
- `--proto-binary` on Windows stdout is **unsafe**: ADB emits `XXXX\r\n` then payload, and `0x0A` bytes inside the protobuf are corrupted to `0x0D 0x0A` through the console pipe. Do not use CLI binary mode on Windows.
- Help text omits `track-services`; the subcommand still works on 37.0.1.

### Stream semantics

**Complete current service list on every update** (not deltas).

Cuttlefish `mdns_tracker.cpp` builds a fresh `adb::proto::MdnsServices` from the entire discovered set and sends it; `update_mdns_trackers()` rebroadcasts that complete snapshot. Live idle observation: one initial frame, no second frame within 5 s when the set is unchanged. Withdrawal = absence from a newer complete list. No TTL/grace in the protocol.

### Wire schema (FileDescriptor + live field walk)

```
message MdnsServices {
  repeated ServiceAdbTcp tcp = 1;       // _adb._tcp
  repeated ServiceAdbTls tls = 2;       // _adb-tls-connect._tcp
  repeated ServiceAdbPairing pair = 3;  // _adb-tls-pairing._tcp
}
message ServiceAdbTcp { MdnsService service = 1; }
message ServiceAdbTls { MdnsService service = 1; bool known_device = 2; }
message ServiceAdbPairing { MdnsService service = 1; }
message MdnsService {
  string instance = 1;
  string service = 2;            // full type, e.g. "_adb._tcp"
  string domain = 3;
  optional string ipv4 = 4;
  repeated string ipv6 = 5;
  uint32 port = 6;
  optional string product_model = 7;
  optional string build_version_sdk_full = 8;
  optional string given_name = 9;
  optional string serial = 10;
  optional string mdns_service_version = 11;
  optional string hostname = 12;
}
```

Live Samsung A06 legacy service (`mdns_service_version: "1"`): instance, service, ipv4, three ipv6, port, product_model, build_version_sdk_full, mdns_service_version, hostname present; **given_name** and **serial** absent (Wi-Fi 2.0 / version `"2.0"` fields — keep optional).

### Service type constants (AOSP 1cf2f017)

- `_adb._tcp` — Legacy transport
- `_adb-tls-pairing._tcp` — Pairing
- `_adb-tls-connect._tcp` — Connect

Unknown types: cuttlefish logs and skips; ADB View classifies as `Unknown` and does not treat them as pairing offers.

### Deduplication identity

Stable raw key: **`(ServiceType, InstanceName)`** — matches AOSP registries keyed by instance within a service-type bucket. Not display name. PairingOffer Id remains unfrozen until Phase 6B/6C.

### Fallback order (frozen for implementation)

1. Preferred: `host:track-mdns-services` (binary complete lists).
2. Generation-scoped fallback after **explicit** unknown-host-service FAIL: CLI `adb.exe mdns track-services --proto-text`.
3. Last resort one-shot: `host:mdns:services` — **not** owned by the tracker loop; composition may call a one-shot query on flyout-open later. Tracker must not know "flyout open".

Generic FAIL → Failed + reconnect backoff; **do not** cache unsupported. New server generation retries preferred.

### Backoff (ENGINEERING §11 row 8)

mDNS reconnect: **1 s, 2 s, 5 s, 15 s, 30 s** cap, ±20 % jitter (same discipline as device tracker; different base sequence). Reset after a successful parsed snapshot.

**60 s degraded (row 9):** applies to the *Unavailable* warning path when discovery is disabled/empty-forever at the product layer — **not** encoded inside `MdnsTracker` reconnect delays in 6A. Re-probe on resume/network change remains composition work.

### Real non-mutating smoke (2026-09-19, Samsung A06)

- `adb mdns check` → enabled (`adb discovery 0.0.0` / libadbmdns).
- `adb mdns services` / `host:mdns:services` → one **legacy** `_adb._tcp` for `adb-R8VXA02SNKH` at `192.168.18.248:5555`.
- `host:track-mdns-services` / CLI `--proto-text` → same service with IPv4 + IPv6 + `product_model: SM-A065F`, `hostname: Android-5.local`, `mdns_service_version: "1"`.
- **No** `_adb-tls-pairing` / `_adb-tls-connect` observed (wireless pairing screen not open; not fabricated).

### Phase 6D host-browse regression (same day, later)

Same workstation later returned **empty** `host:track-mdns-services` / `adb mdns services` while unicast `adb pair` / `adb connect` still worked. Audited as **host mDNS browse failure**, not Adb View offer mapping — see `research/2026-09-19-host-mdns-empty-while-tcp-works.md`.

**Operator follow-ups tried (still empty):** Tailscale service Stopped + tunnel down; `adb kill-server` / `start-server` restored `adb` bind on UDP 5353 (`mdns_enabled: true`, `LIBADBMDNS`); Edge/ChatGPT intermittently also bound 5353; Hyper-V `vEthernet (Default Switch)` remained Up (disable required elevation). Google empty-track diagnostic still applies.

### Malformed handling

Same as Phase 5A: valid frame + bad body → drop update, rate-limit diagnostic, keep stream; framing corruption / mid-frame EOF → terminate session, Failed, reconnect. Malformed ≠ unsupported.

## Phase 6B — host:pair + pairing coordinator (2026-09-19)

### Immutable pins

| Item | Pin | Evidence |
|---|---|---|
| CLI constructs `host:pair:<password>:<HOST[:PORT]>` | AOSP `1cf2f017d312f73b3dc53bda85ef2610e35a80e9` `client/commandline.cpp` | `StringPrintf("host:pair:%s:%s", password.c_str(), argv[1])`; code from argv[2] or stdin |
| Server splits password at **first** `:` after `pair:` | same commit `services.cpp` | `ConsumePrefix(&name, "pair:")` then `strchr(name, ':')` → password / host remainder |
| Success vs failure writeback | same commit `services.cpp` `pair_service` | Success if response starts with `"Successful"` → `SendProtocolString`; else `SendFail` |
| Pairing work + messages | same commit `client/adb_wifi.cpp` | `Successfully paired to … [guid=…]`; `Failed: Wrong password…`; `Failed: Unable to start pairing client.`; parse via `ParseNetAddress` |
| Live framing on 37.0.1-15733141 | this machine | After request: initial `OKAY` (service accepted), then later `FAIL`+hex+reason **or** hex-length + success text (no second OKAY on success path) |

**Endpoint formatting for the second `%s`:** IPv4 `a.b.c.d:port`; IPv6 **`[addr]:port`** so the first-colon password split remains unambiguous. Port 1..65535.

### Request / response framing

1. Client sends standard length-prefixed `host:pair:<code>:<endpoint>`.
2. Server replies `OKAY` when the pair service thread is created (or `FAIL` + reason if the host service cannot be created, e.g. unknown service).
3. When pairing finishes, server writes either:
   - **Success:** four-hex length + UTF-8/ASCII body starting with `Successful`
   - **Failure:** `FAIL` + four-hex length + reason string

`ExecuteOneShotAsync` (OKAY → length → body) is **not** sufficient alone: a failure result after the ack begins with `FAIL`, not hex. Phase 6B uses a dedicated pair reader.

### CLI fallback (ENGINEERING §6.2)

`adb.exe pair <endpoint>` with the six-digit code on **stdin** when `argc==2`. Never place the code in argv when using the stdin form. Absolute `adb.exe` path; inherited environment; UseShellExecute=false. Used only when the host service is explicitly unknown/unsupported — not for Wrong password / Unable to start.

### V-10 (pair return vs tracker race) — domain closure

ADB server may call `adb_secure_connect_by_service_name` after a successful pair (`adb_wifi.cpp`). That is **server** auto-connect, not an ADB View `connect` call. Domain success → `PairedWaitingForDevice` only. **No DeviceView.** Tracker appearance (6C+) clears waiting; pairing success must not fabricate a connected device.

### Non-mutating live probe (2026-09-19)

`host:pair:000000:127.0.0.1:1` → `OKAY` then `FAIL` / `Failed: Unable to start pairing client.` (no phone pairing screen; no real code). IPv6 form `host:pair:000000:[::1]:1` accepted at the service layer. **Real six-digit pair against Samsung: not exercised.**
