# Host mDNS empty while ADB TCP/pair still works

- **Date:** 2026-09-19
- **Author:** agent (Phase 6D hardware acceptance)
- **Phase:** 6D
- **Status:** current

## Problem

During Phase 6D live pairing on this workstation, Adb View showed no
`PairingOffer` while the human could still pair with the phone’s on-screen
IP:port + six-digit code. The question: is that an Adb View bug, or a host /
network / platform-tools discovery failure?

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| Android Developers — `adb` (wireless + mDNS backends + empty-track diagnostic) | https://developer.android.com/tools/adb | Fetched 2026-09-19 (docs describe platform-tools ≥ 37.0.0) |
| AOSP adb man page (`adb.1.md`) — `mdns check/services`, `ADB_MDNS_*` | https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/docs/user/adb.1.md | `refs/heads/main` as of fetch 2026-09-19 |
| LineageOS mirror of AOSP `docs/dev/adb_wifi.md` (service types + host is consumer) | https://github.com/LineageOS/android_packages_modules_adb/blob/lineage-23.2/docs/dev/adb_wifi.md | lineage-23.2 |
| Prior pin in-repo | `research/2026-09-16-adb-protocol-and-wireless.md` § Phase 6A | Live smoke earlier **same day** on same Samsung A06 |
| Arduino `mdns-discovery` Windows virtual-adapter analysis | https://github.com/arduino/mdns-discovery/pull/93 | PR discussion (not ADB code; Windows multicast behaviour) |

## Version investigated

| Component | Version |
|---|---|
| OS | Windows 11 `10.0.26100` |
| platform-tools / adb | **37.0.1-15733141** (`adb version` 1.0.41) |
| `adb server-status` | `mdns_enabled: true`, `mdns_backend: LIBADBMDNS`, `usb_backend: LIBADBUSB` |
| Device | Samsung A06 · serial `R8VXA02SNKH` · model `SM_A065F` · LAN `192.168.18.248` |
| Host Wi-Fi | `192.168.18.242` (same /24) |
| Extra adapters present | Tailscale (`100.120.142.111`, service **Running**), Hyper-V `vEthernet (Default Switch)` `172.23.176.1` |

## Isolation audit (facts observed 2026-09-19)

### A. Adb View is not the empty-list producer

| Probe | Result |
|---|---|
| Production `MdnsTrackerLifecycle` started | Logged `MdnsTracker started for server generation 1` |
| App snapshot while pairing IP:port reachable | `offerCount=0`, `warnings=[]` (healthy discovery path with empty set) |
| Independent of Adb View: `adb mdns check` | `mdns daemon version [adb discovery 0.0.0]` |
| Independent: `adb mdns services` | Header only — **no rows** |
| Independent: raw `host:mdns:services` | `OKAY` + **`frame_len=0`** |
| Independent: raw `host:track-mdns-services` first frame | `OKAY` + **`track_frame_len=0`** |
| Same probes with Adb View process stopped | Still empty |

Conclusion: the ADB server’s discovery registry is empty. Adb View correctly maps an empty complete-list to zero offers. That is **not** a PairingOfferMapper / reducer defect.

### B. Unicast ADB path still works

| Action | Result |
|---|---|
| TCP to phone pairing port (human-provided) | Open |
| `adb pair <ip>:<pairPort>` with code on **stdin** (not argv) | `Successfully paired … [guid=adb-R8VXA02SNKH-…]` |
| Auto-connect after pair | **Did not** produce a tracker device within ~45 s |
| Manual `adb connect 192.168.18.248:5555` (observation only; product never issues `host:connect`) | Connected |
| `track-devices` → Adb View | Wi-Fi `DeviceView` appeared (`SM_A065F` / Connected / Wi-Fi) |
| Pairing codes in `%LOCALAPPDATA%\ADBView\logs` | **0** hits for the codes used this session |

So: **pair + connect unicast ≠ mDNS browse**. Google’s own wireless flow documents both; Studio uses mDNS for offers, CLI can use the on-screen IP:port without mDNS (`adb_wifi.md`).

### C. Contrast with earlier same-day success

Phase 6A live smoke (same platform-tools, same phone, same research note) recorded:

- `adb mdns services` → `adb-R8VXA02SNKH _adb._tcp 192.168.18.248:5555`
- `host:track-mdns-services` non-empty binary / proto-text with `product_model: SM-A065F`

That proves this LAN **can** deliver ADB mDNS to this host. Tonight’s empty list is a **regression of host discovery reception**, not “Samsung never advertises.”

### D. Host environment suspects (correlated, not singly proven)

1. **Virtual adapters + multicast**  
   Active: Tailscale tunnel, Hyper-V `vEthernet`.  
   `adb.log` (older server start lines) shows `SetMulticastOutboundInterface` for multiple interface indices including Hyper-V-class adapters. Third-party Windows mDNS work (Arduino mdns-discovery) documents the failure mode: responses arrive on the physical NIC but the process’s multicast socket is joined on a virtual adapter → **silent empty discovery**.

2. **Shared UDP 5353**  
   Live: `adb.exe` **and** `msedge.exe` both bound `0.0.0.0:5353` / `[::]:5353`. Not proof of breakage alone (SO_REUSEADDR is common), but contention is real on this host.

3. **Official empty-track diagnostic**  
   Android Developers (`adb` docs): if `adb mdns track-services --proto-text` is empty, treat as **“your network does not support mDNS”** (from the workstation’s point of view). Our empty complete-list matches that diagnostic exactly, even though ICMP/TCP unicast to the phone succeeds.

4. **Not** `mdns_enabled: false`  
   `adb server-status` shows `mdns_enabled: true` and recommended `mdns_backend: LIBADBMDNS`. This is not the “ADB_MDNS disabled” class of failure.

5. **Leftover CLI client**  
   A hung `adb mdns track-services --proto-text` client was observed beside the fork-server. Killing **only that client** did not refill the service list. Not the root cause.

### E. What Adb View must / must not do (product law)

Per `AGENTS.md` R-14 / prior Phase 6A research:

- Do **not** `adb kill-server` / `host:kill`.
- Do **not** set `ADB_*` (including `ADB_MDNS_OPENSCREEN`) to “fix” the user’s machine.
- Do **not** invent PairingOffers from a human-typed IP:port (discovery remains mDNS).
- Do **report** wireless-discovery degradation with frozen Vocabulary when the product’s warning policy fires; empty-but-healthy discovery with no offers is allowed and must not fabricate UI.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Treat as Adb View bug and “fix” parsing | Fast blame | Contradicted by empty host frames outside the app |
| Product sets `ADB_MDNS*` / restarts server | Might revive browse on some PCs | Forbidden citizenship; masks host config |
| Product falls back to manual IP:port offer entry | Would unblock flyout without mDNS | Out of frozen DESIGN scope (QR/manual IP pairing not in contract) |
| Document host/network mDNS failure; keep hermetic offer tests | Honest; matches Google’s empty-track guidance | Live PairingOffer acceptance blocked until host browse works |
| Ask operator to disable Tailscale / vSwitch / Edge mDNS briefly for 6D | May restore browse for acceptance | Environmental; not a code change |

## Chosen approach

1. **Classify tonight’s failure as host-side mDNS browse failure**, not an Adb View PairingOffer bug.
2. Keep product behaviour: consume ADB complete-lists only; zero services → zero offers.
3. For Phase 6D matrix: record LIVE pair/connect/Wi-Fi card where observed; mark flyout discovery / offer / inline pair UI **NOT EXERCISED** when `adb mdns services` is empty.
4. Optional operator experiment (manual, outside the product): temporarily remove virtual-adapter / UDP-5353 contention and re-check `adb mdns services` — if rows return, re-run offer acceptance. Do **not** encode that experiment into Adb View.

## Why

Adb View’s value is correct observation of ADB. When the shared ADB server’s discovery registry is empty, inventing offers or mutating the server would violate citizenship and the frozen contract. Hermetic tests already cover offer mapping when services exist.

## Rejected approaches

- **Synthesizing PairingOffers from the pairing dialog IP:port** — redesign / scope expansion.
- **Restarting or reconfiguring the ADB server from the app** — R-14.
- **Calling this an Adb View defect without an independent non-empty host list** — false positive.

## Compatibility concerns

- Windows hosts with Tailscale / Hyper-V / Docker / multiple mDNS binders are a known difficult class for multicast DNS.
- platform-tools 37.x default `LIBADBMDNS` can report enabled while still returning empty lists if multicast never arrives.
- Auto-connect after `host:pair` depends on browse/`_adb-tls-connect` (and `ADB_MDNS_AUTO_CONNECT` policy). Unicast `adb connect` can still attach a device when browse fails — as observed.

## Testing implications

- Hermetic: unchanged — fake non-empty pairing services must still produce one offer; empty list must produce zero.
- Hardware matrix: require `adb mdns services` non-empty **before** claiming LIVE PairingOffer / flyout pair.
- Do not count CLI `adb pair` + manual `adb connect` as LIVE proof of the in-app offer → Pair → code path.

## Open items

- V-1 / network: confirm whether disabling Tailscale or Hyper-V default switch restores `_adb._tcp` / `_adb-tls-pairing` on this host (operator experiment).
- Capture a fresh `adb.log` section from a server start **while** services are empty vs when Phase 6A saw services (interface-index join list).
- Wi-Fi 2.0 auto-connect timing (V-10) still separate; tonight auto-connect did not fire even after successful pair.

## Phase 6D operator follow-up (same evening)

| Step | Result |
|---|---|
| Tailscale service Stopped | Confirmed; Tailscale adapter gone |
| `adb kill-server` + `adb start-server` | Server came back; `adb` rebound UDP 5353 (`::` sockets); `mdns_enabled: true` / `LIBADBMDNS` |
| `adb mdns services` / track frame after restart | Still **empty** |
| `adb connect 192.168.18.248:5555` | Wi-Fi device restored |
| Disable Hyper-V Default Switch | Failed without elevation; adapter remained Up |
| Competing 5353 binders after restart | `msedge` / `ChatGPT` intermittently shared `0.0.0.0:5353` with adb |

Conclusion unchanged: host browse failure. Adb View must not invent offers or reconfigure ADB.
