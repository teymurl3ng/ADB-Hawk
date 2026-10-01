# 0010 — QR-code wireless pairing

- **Date:** 2026-10-01
- **Status:** ACCEPTED — explicit human design-authority change (Phase 12 Step 5)
- **Phase:** 12.5

## Issue

Pairing requires transcribing a six-digit code from the phone into the flyout — a failure class
(typos, code expiry, re-typing) the phone's own camera can eliminate. Android 11+ Wireless
debugging natively supports "Pair device with QR code". DESIGN.md had no QR surface and the
Phase 6 out-of-scope line excluded it; research (`research/2026-10-01-qr-pairing.md`) shows the
flow is pure-client on the desktop (the phone hosts the pairing server) and is viable on
browse-broken hosts thanks to the fallback browser (0008 follow-through).

## Decision

A new pairing surface, **PairingQrCard** (DESIGN §40), with two data-driven states:

### Entry (idle)

A quiet action, `Pair a device over Wi-Fi`, visible **whenever no pairing operation is
active** — inside the empty state and below the device list otherwise (design authority:
"always when idle", 2026-10-01). Tapping it starts a QR session.

### Active (session)

- The QR code (backend-rendered SVG delivered as an image; the payload string never enters
  bridge state or JS — R-13 posture, design authority 2026-10-01).
- Instruction lines naming the exact phone screen:
  `On the phone, open Developer options → Wireless debugging.` /
  `Tap Pair device with QR code and point the camera at this code.`
- `Cancel`.

Placement: where pairing cards live — after pairing offers, before device cards.

### Session semantics

- Backend generates a **six-digit numeric secret** and instance name `adbview-<8 hex>`;
  payload `WIFI:T:ADB;S:<name>;P:<secret>;;` (secret charset constrained by the
  `host:pair:<password>:<endpoint>` first-colon split and the `WIFI:` `;` separator;
  six digits reuse `PairingCode.IsValid` and the unchanged pair path).
- When a Pairing-kind service whose instance name equals the requested one appears on the
  effective discovery set (adb stream or fallback browser), the coordinator issues the existing
  `pairStart` + `pairSubmit(offerId, secret)` programmatically — the standard pairing
  progress/success/failure cards take over unchanged.
- **The unscanned session self-clears after 2 minutes** (design authority 2026-10-01) via the
  StateEngine timer-command pattern (the same sanctioned mechanism as the pairing-wait timer;
  R-05 paperwork: ENGINEERING §11 row 28). A new session regenerates the secret — each QR is
  single-use. Cancel clears quietly; expiry clears quietly (no failure card).
- One session at a time. Each QR is single-use: a second scan of a stale code fails at the
  phone (pairing server gone) and never pairs.

### Security posture (R-13)

The secret is never logged, never in argv, redacted everywhere `host:pair:` is. The QR image —
which visually encodes the secret — is backend-rendered SVG displayed by the frontend; the
payload string never crosses the bridge. Secrets are single-session and expire with it.

## Contract changes

- `AppSnapshot.PairingQr: PairingQrView | null` (`{ svgDataUri, instructions[], cancelLabel }`).
- Bridge actions `pairQrStart` / `pairQrCancel`.
- DESIGN.md §14–15 gain the surface copy; §40 gains `PairingQrCard`; the Phase 6 out-of-scope
  line's QR exclusion is superseded by this decision (PHASES.md Phase 12.5 activated).
- QRCoder 1.8.0 pinned (R-09: this note + `research/2026-10-01-qr-pairing.md` + ENGINEERING §15).

## Rejected alternatives

- Frontend QR rendering — secret in JS state; rejected by design authority.
- High-entropy alphanumeric secrets — break `PairingCode.IsValid` reuse; colon/semicolon hazards.
- Session lives until cancelled — stale codes stay scannable; rejected (2-minute self-clear chosen).
- Empty-state-only entry — rejected; "always when idle" chosen.

## Testing

- `QrPairingRequest` invariants (secret charset/length, payload shape, redacted strings,
  per-session uniqueness); QRCoder SVG smoke.
- `QrPairingSession` (injected pairing-actions seam): instance-name match → pairStart+pairSubmit
  once; non-matching rows ignored; cancel/expiry clear; second Start rejected.
- Reducer: view in/out, expiry generation guard; StateEngine timer slot.
- Vitest + visual fixture `pairing-qr`; live matrix: scan on the A06 (acceptance), wrong-phone
  scan, phone-side cancel, expiry quiet clear.
