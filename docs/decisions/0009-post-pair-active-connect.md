# 0009 — Post-pair active connect (`host:connect`)

- **Date:** 2026-10-01
- **Status:** ACCEPTED — explicit human contract change (Phase 12 Step 4)
- **Phase:** 12.4

## Issue

After a successful wireless pair the product waits (≤60 s, then quietly clears) for the device to
appear. Arrival depended on the ADB server's mDNS-driven auto-connect — which is broken exactly on
hosts where browse is broken (`research/2026-09-19-host-mdns-empty-while-tcp-works.md`: auto-connect
never fired; manual `adb connect` was needed). The prior product law "the product never issues
`host:connect`" kept Adb View observation-only but left the pairing flow's last mile dependent on
the broken path.

## Decision

The product issues **`host:connect:<host>:<port>`** under exactly these constraints:

1. **Single origin:** only after *our* pairing flow reaches `PairedWaitingForDevice`
   (the pair was explicitly user-initiated). Never scheduled, never on any other trigger.
2. **Single target:** the paired device's `_adb-tls-connect._tcp` row whose IP equals the
   pairing binding host (`ActivePairingHost`), TLS port from that row. Strict host equality —
   no heuristics, no arbitrary hosts, no CLI `adb connect`.
3. **One attempt per host:port per waiting episode**; a failed attempt is re-armed only by a
   fresh service appearance (row absent → present again). No retry storms.
4. **No state fabrication:** the tracker remains the sole source of device truth. A successful
   connect does not create a DeviceView; the existing tracker-arrival and 60 s quiet-clear paths
   are unchanged. No user-visible copy changes (`Waiting for ADB connection…` stays truthful).
5. Framing per `research/2026-10-01-post-pair-active-connect.md`: the server answers
   OKAY + hex-length + body on success **and** failure (`connect_service` always uses
   `SendProtocolString`); the client classifies by body prefix
   (`connected to` / `already connected` = success shapes).

Still forbidden: `adb disconnect`, `adb tcpip`, `adb root`, connects for devices not paired in
this session, `ADB_MDNS_AUTO_CONNECT` manipulation (R-14 unchanged except for this narrow grant).

## Testing

- `FakeAdbServer` `host:connect:` handler: connected / already-connected / failure body /
  unknown-service FAIL — each classified; attempt-once-per-episode proven; no attempt when the
  phase is not waiting; no attempt without a host-matching Connect row.
- Live matrix row: real pair → `connected to` logged → tracker arrival well under 60 s.

## Rejected alternatives

- Status quo (wait for auto-connect) — broken on the affected host.
- Spawning the `adb connect` CLI — same server service behind a child process.
- Connect-on-every-publish — request storm against a shared server.
- Treating connect success as arrival — violates the V-10 invariant (paired ≠ connected).
