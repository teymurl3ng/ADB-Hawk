# QR-code wireless pairing

- **Date:** 2026-10-01
- **Author:** agent (Phase 12 Step 5; decision 0010)
- **Phase:** 12.5
- **Status:** current

## Problem

Pairing currently requires the user to transcribe a six-digit code from the phone into the
flyout. The phone's own camera can carry that credential instead: Android 11+ Wireless debugging
has a native "Pair device with QR code" mode. Adding it removes the transcription failure class
(typos, code expiry, re-typing) — and is now actually viable on hosts with broken ADB browse
because the fallback browser (decision 0008 follow-through, Step 3) can see the phone's
post-scan advertisement even when the ADB server cannot.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| AOSP — Architecture of ADB Wifi (`adb_wifi.md`) | https://android.googlesource.com/platform/packages/modules/adb/+/HEAD/docs/dev/adb_wifi.md | main as of fetch 2026-10-01 |
| AOSP adb `services.cpp` (pair_service write-back; first-colon password split) | https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/services.cpp | main as of fetch 2026-10-01 |
| adbqr (reference implementation, bash) | https://github.com/kristjan/adbqr + `bin/adbqr` source | fetched 2026-10-01 |
| Android Developers — Pair devices over Wi-Fi (Studio QR flow) | https://developer.android.com/studio/run/device | fetched 2026-10-01 |
| QRCoder (NuGet) | https://www.nuget.org/packages/QRCoder | 1.8.0, fetched 2026-10-01 |
| In-repo pins | research/2026-09-16-adb-protocol-and-wireless.md (host:pair framing; first-colon split) | current |

## Version investigated

platform-tools 37.0.1-15733141 · Samsung A06 Android 16 (live validation pending) · .NET 10 · QRCoder 1.8.0.

## Findings

1. **Direction of the protocol (AOSP `adb_wifi.md`, verbatim semantics):** Studio "generates a
   QR code embedding a WPA3-format string"; the device "has a special Camera QR code handler
   which when it sees type T:ADB starts a Pairing Server with the requested instance name."
   Payload: `WIFI:T:ADB;S:<service-instance-name>;P:<password>;;` — `T:ADB` marks the ADB
   variant, `S:` requests the mDNS instance name for `_adb-tls-pairing._tcp`, `P:` is the
   pairing password ("the second shared secret"). Example from the doc:
   `WIFI:T:ADB;S:studio-g@<xeYnap/;P:(Aq+v9>Cx>!/;;`.
2. **Instance-name conventions.** Typed-code mode advertises `adb-<guid>`; QR mode (Studio)
   randomizes the suffix "so Studio can tell which phone just scanned the QR code"
   (`studio-<RANDOM-10>`). adbqr uses `adbqr-<8 hex>`. Any unique suffix works — the phone
   honors the requested name.
3. **Secret charset constraints (derived, load-bearing).** The password is embedded in the
   smartsocket service string `host:pair:<password>:<endpoint>` and split at the **first** colon
   after `pair:` (pinned research). The `WIFI:` format reserves `;`. Therefore the secret must
   avoid `:` and `;`; adbqr's choice — six decimal digits — is maximally compatible, matches
   `PairingCode.IsValid` in our codebase exactly, and keeps the existing pair path unchanged.
   Chosen: six-digit numeric secret, generated per session.
4. **Desktop stays a pure client.** No pairing server, no TLS listener, no SPAKE2 on our side —
   the phone's camera handler does the server side; we watch for the advertisement and run the
   existing `host:pair` flow with the pre-generated secret. Reference: adbqr is literally
   `adb pair $ADDR $CODE` after seeing the service over mDNS.
5. **QRCoder 1.8.0** (NuGet; MIT; pure C#, zero external dependencies; ISO/IEC 18004): exact
   pin for `Directory.Packages.props`. SVG output is built in (`QRCoder.SvgRenderer`-family).
   ECC level M (25 %) — adbqr's default, adequate for on-screen rendering.
6. **State machine reuse.** When the phone starts advertising after the scan, the advertisement
   flows through `MdnsTracker`/fallback → `PairingOfferMapper` → a normal pairing offer keyed by
   its DiscoveryKey. The QR session therefore needs no new pairing machinery: on seeing a
   Pairing-kind service whose instance name equals the requested one, the coordinator issues the
   existing `pairStart` + `pairSubmit(offerId, generatedSecret)` programmatically and the
   standard progress/success/failure cards take over (their wording, timers, and withdrawal
   logic unchanged). Discovery now has two sources (adb stream + fallback browser), so the
   post-scan advertisement is seen even with broken ADB browse.
7. **R-13 posture.** The secret never logs (same redaction discipline as `host:pair:`), never
   reaches argv. The QR image (which visually encodes the secret) is backend-rendered SVG
   delivered to the frontend for display only — per the human-approved choice, the payload
   string itself never enters bridge state or JS. Session secrets are single-use and expire
   with the session.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Hand-rolled QR encoder | Zero dependency (avoid-list culture) | Subtle encoder bugs produce silently unscannable output; test-vector burden; user chose pinned NuGet |
| `U:` (uid) field in payload | Matches some third-party tooling | Not in AOSP `adb_wifi.md`; unnecessary complexity |
| High-entropy alphanumeric secret | Longer secret | Breaks `PairingCode.IsValid` reuse; colon/semicolon hazards in the service string; adbqr proves 6 digits sufficient |
| Frontend renders QR from payload string | Simpler bridge | Secret in JS state — rejected by design authority |

## Chosen approach

1. `Adb/QrPairingRequest.cs`: per-session generation — secret = 6 decimal digits,
   instance name = `adbview-<8 hex>`; payload = `WIFI:T:ADB;S:<name>;P:<secret>;;`; `ToString`
   surfaces only redacted forms. QRCoder 1.8.0 renders the SVG (ECC M, quiet-zone default).
2. `Host/QrPairingSession.cs`: Start → generate request, publish `PairingQrView` (SVG + copy),
   arm the sanctioned session-expiry timer (StateEngine `GraceCommand` pattern — the same
   mechanism as the pairing-wait timer; decision 0010 sanctions it); watch snapshots for a
   Pairing-kind service whose InstanceName matches → issue `pairStart` + `pairSubmit` with the
   generated secret → the existing pairing cards take over; Cancel → clear quietly; expiry →
   clear quietly. One session at a time; a scan-pair failure keeps the standard failure card.
3. Frontend: `PairingQrCard` (DESIGN §40 list addition) with two data-driven states — idle
   entry ("Pair a device over Wi-Fi") and active (QR image + instructions + Cancel). The SVG is
   rendered as an `<img>` from a backend-provided data URI.
4. Copy (Vocabulary): entry label, instruction lines naming the exact phone screen
   (Developer options → Wireless debugging → Pair device with QR code), cancel label. No
   protocol jargon (R-15).

## Why

Removes the transcription failure class with a pure-client implementation that reuses the whole
existing pairing state machine; QRCoder is the minimal, dependency-clean renderer; the fallback
browser makes the flow work on hosts where the original design (Studio's) would fail.

## Rejected approaches

- Desktop-hosted pairing server / own SPAKE2 — the phone already implements it; huge surface for zero gain.
- Frontend-side QR rendering — secret in JS state; rejected by design authority.
- Manual IP:port entry as an alternative — long-rejected (invents offers outside discovery).
- Camera-on-desktop flows — no camera assumptions for a tray utility.

## Compatibility concerns

- Phone-side OEM variance: whether the requested instance name is honored and advertised must be
  live-verified on the Samsung A06 (and any future device) before calling this LIVE PASS —
  `DEVICE-TEST-MATRIX` row added.
- Advertisement reachability: adb browse broken on the affected host → the fallback browser
  must see the pairing advertisement; if neither source sees it, the session expires quietly.
- ADB server < 31 has no wireless pairing at all — offer path unchanged (no offers, no QR need).
- SVG rendering under DPI scaling: quiet zone + card width 348 epx validated in the visual
  baseline; scannability live-verified.

## Testing implications

- `QrPairingRequest`: charset/length invariants, payload shape, redacted `ToString`, unique
  across sessions; QRCoder SVG smoke (deterministic for fixed input).
- `QrPairingSession` (injected pairing-session seam): match → pairStart+pairSubmit once;
  non-matching instances ignored; cancel/expiry clear; second Start while active rejected.
- Reducer: `PairingQrView` in/out; expiry event; standard pairing cards unaffected.
- Vitest + visual baseline for the card's two states; contract regen.
- DEVICE-TEST-MATRIX rows: live scan on the A06 (acceptance), wrong-phone scan, scan-then-cancel
  on phone, expiry quiet clear.

## Open items

- Live: A06 honors requested instance name; secret accepted by `host:pair` on first try;
  scannability at card size under 100–200 % DPI.
- ENGINEERING §15 entry for QRCoder 1.8.0 (this note + pin are the R-09 paperwork).
