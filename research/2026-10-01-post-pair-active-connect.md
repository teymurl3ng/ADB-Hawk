# Post-pair active connect (`host:connect`)

- **Date:** 2026-10-01
- **Author:** agent (Phase 12 Step 4)
- **Phase:** 12.4 (discovery health)
- **Status:** current

## Problem

After a successful wireless pair, the product shows `Paired with … / Waiting for ADB connection…`
and waits up to 60 s for the device to appear. Arrival depends on the ADB server's own
auto-connect (`adb_secure_connect_by_service_name`), which is mDNS-browse-dependent — and on the
affected host the server's browse is broken (`research/2026-09-19-host-mdns-empty-while-tcp-works.md`):
the observed behavior was **auto-connect never fired** and a manual `adb connect` was needed
(matrix row: "ADB auto-connect after pair (no manual connect) | not exercised"). The fix: after
*our* pairing flow succeeds, issue `host:connect` once toward the paired device when its
`_adb-tls-connect._tcp` service is visible, so arrival no longer depends on the broken browse.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| AOSP adb `services.cpp` (connect_service, pair_service write-back) | https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/services.cpp | main as of fetch 2026-10-01 |
| Installed adb 37.0.1 help + live probe | this machine | 2026-10-01 |
| In-repo pins | research/2026-09-16-adb-protocol-and-wireless.md (framing, V-10, product law) | 2026-09-16/19, current |
| In-repo evidence | research/2026-09-19-host-mdns-empty-while-tcp-works.md (auto-connect did not fire) | current |

## Version investigated

platform-tools 37.0.1-15733141 (installed, live-probed) · AOSP adb main 2026-10-01 · Windows 11 26100.

## Findings

1. **Service string & routing.** Host service `connect:<host>[:<port>]` (default port 5555 when
   issued via CLI; the smartsocket service itself takes the full `host:port` — our caller always
   supplies the mDNS-advertised TLS port). The server runs it in a service thread
   (`create_service_thread("connect", …)`), so the ack path matches every one-shot host service.
2. **Framing — critical difference from `host:pair`.** `connect_service` writes its result with
   `SendProtocolString` **unconditionally**: `OKAY` + four-hex-length + UTF-8 body, whether the
   connect succeeded or failed. There is no `FAIL`+reason branch (that exists only for
   `pair_service` and unknown host services). Therefore the existing `ExecuteOneShotAsync`
   (OKAY → length → body) is sufficient; the client classifies by body text:
   - success: body starts with `connected to` (AOSP `connect_device`)
   - success-equivalent: body starts with `already connected`
   - failure: anything else — observed live on 37.0.1: `cannot resolve host '--help' and port 5555: No such host is known.`; AOSP `connect_device` failures are `failed to connect to '<addr>'`-shaped.
3. **No server-side timeout.** The service has none; the client bounds the exchange (we use the
   standard Adb host-connection connect timeout plus a request timeout of 10 s).
4. **Auto-connect policy.** `ADB_MDNS_AUTO_CONNECT` defaults to `adb-tls-connect` (live help
   output) — i.e. the server auto-connects only what its (broken) browse tells it about. Our
   active connect is complementary and does not touch that policy (R-14: no env changes).
5. **Contract reversal.** The product law "product never issues `host:connect`"
   (`research/2026-09-19…` §E) existed to keep the product observation-only. Decision 0009
   reverses it narrowly: exactly one user-initiated origin (the completed pairing flow), toward
   the paired device, once per connect-service appearance, never scheduled, never for arbitrary
   hosts. `adb connect` as a CLI surface, `adb disconnect`, `adb tcpip`, and pairing-time
   connects remain forbidden. The `host:connect` token is not in any boundary-forbidden list.
6. **Endpoint resolution.** The connect target is the paired device's `_adb-tls-connect._tcp`
   row (host + TLS port) — **not** the pairing offer's port (pairing port closes after pairing).
   Host match against the pairing binding host (`ActivePairingHost`); TLS port from the row.
   mDNS hostname forms (`adb-<guid>.local`) never match an IP host — IP rows only (our rows
   carry `Ipv4`; strict host equality, no heuristics; a mismatch is logged and the attempt is
   not made).
7. **Client CLI parity.** `adb connect HOST[:PORT]` remains available to humans; we never spawn
   the CLI for this (the socket path needs no fallback — the `connect:` host service exists in
   every server shipped with wireless debugging, Android 11+).

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Keep waiting on server auto-connect only (status quo) | Zero code | Broken exactly where it matters (browse-dependent); 60 s wait then quiet clear with no device |
| Spawn `adb.exe connect` (CLI) | Reuses tested process path | Pointless: same server service behind it; child-process + parsing for a socket call our protocol layer already speaks |
| Connect on every snapshot publish while waiting | Aggressive arrival | Request storm against a shared server; rejected — once per appearance |
| Extend pairing CLI fallback to connect | Symmetry with pair | No old-server gap exists for connect; unnecessary |

## Chosen approach

1. `src/AdbView/Adb/HostConnectClient.cs`: one-shot `host:connect:<endpoint>` via
   `ExecuteOneShotAsync` (OKAY → 4-hex → body), 10 s request timeout; classify
   `Connected` (`connected to`/`already connected` prefix) vs `Failed(reason)` vs protocol
   failures — same exception taxonomy as the pair path. No CLI fallback.
2. `src/AdbView/Host/PostPairConnectCoordinator.cs`: watches accepted snapshots; armed while
   `snapshot.Pairing?.Phase == PairedWaitingForDevice`; on each publish, if the effective
   service set contains a **Connect**-kind row with `Ipv4 == ActivePairingHost` and not yet
   attempted for that host:port, issue one connect (skipped when the tracker already shows a
   Wi-Fi device for that host/serial — the wait clears anyway). One attempt per appearance;
   failures are logged and re-armed only by a fresh appearance. The existing 60 s quiet-clear
   and tracker-arrival clear paths are unchanged.
3. Decision 0009 records the reversal (scope above) and the vocabulary check (no new user
   strings; the waiting copy stays truthful).

## Why

Removes the last arrival dependency on the broken browse for a flow we just completed
user-initiated; one bounded, idempotent, read-only-in-effect call; no timers, no CLI, no env
changes; the tracker remains the sole source of device truth (connect success does not
fabricate a DeviceView — the tracker's arrival clears the wait exactly as today).

## Rejected approaches

- Auto-connect for devices we did not pair in this session — outside the user action scope.
- Retrying on failure — the device re-announces periodically; each appearance re-arms exactly
  one attempt; a failure loop would mask a real network problem.
- Treating connect success as device arrival — the tracker is authoritative (V-10 invariant).

## Compatibility concerns

- Servers without `connect:` host service (pre-Android-11-era platform-tools pairing is already
  unsupported) → unknown host service FAIL → logged as unsupported, wait continues as today.
- Phone moved to a new IP between pair and announcement: strict host match finds nothing;
  behavior degrades to today's wait-and-clear (logged at Information).
- The 60 s quiet clear remains the upper bound; active connect typically lands within seconds.

## Testing implications

- FakeAdbServer gains a `host:connect:` handler: success body, `already connected` body,
  failure body, unknown-service FAIL — each classified correctly; attempt-once-per-appearance
  proven; no attempt when the tracker already shows the device.
- Reducer untouched (no state changes); coordinator tests use the existing snapshot-publish seam.
- Live matrix: pair (real device) → observe `connected to` in logs → tracker arrival < 60 s;
  the "ADB auto-connect after pair (no manual connect)" matrix row becomes exercisable.

## Open items

- Exact AOSP `connect_device` failure string set is version-dependent; classification keeps the
  prefix match for successes and treats every other body as failure-with-reason (logged verbatim,
  minus user-identifying detail per R-13 — reason strings contain host:port only).
