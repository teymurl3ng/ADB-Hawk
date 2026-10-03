# Stuck pairing slot and the QR handoff (live defect, 2026-10-03)

- **Date:** 2026-10-03
- **Author:** coding agent (audit-driven; owner-reported, log-evidenced)
- **Phase:** post-I2 hardening (decision 0017)
- **Status:** current

## Problem

The owner reported that after scanning the QR code the pairing card "mixes it up with code
pairing, then just fails", with Cancel and Pair unresponsive and a visually broken Retry
button — while typed-code pairing worked. An audit of the session log plus three code
deep-dives located a lifecycle defect that no UI change could have fixed.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| AOSP adb_wifi.md (pairing service lifecycle: advertisement closes on completion) | https://android.googlesource.com/platform/packages/modules/adb/+/main/docs/adb_wifi.md | current main, 2026-10 |
| Project decision 0014 (pairing wait survives offer withdrawal) | docs/decisions/0014-*.md | 2026-10-02 |
| Project decision 0010 (QR pairing) | docs/decisions/0010-qr-pairing.md | 2026-10-01 |
| Project research: QR pairing | research/2026-10-01-qr-pairing.md | 2026-10-01 |
| Owner session log | %LOCALAPPDATA%\ADBView\logs\adbview-20261003.log, lines 3174–3289 | 2026-10-03 15:10–15:16 |

## Version investigated

app main @ `f87e38b`; Windows 11 10.0.26100; platform-tools adb 37.x; Galaxy S24 (SM-S921W)
and Galaxy A06 (SM-A065F).

## Findings

Observed rather than documented (this is our own state machine):

- The reducer clears `AppSnapshot.Pairing` when a device matching the waited endpoint appears
  (`StateReducer` device-list reduction) or when the pairing-wait grace expires
  (`ReducePairingWaitExpired`). Both are reducer-local; neither informs `PairingCoordinator`.
- `PairingSession.CancelIfOfferWithdrawn` deliberately preserves an operation in
  `Pairing`/`PairedWaitingForDevice` (decision 0014, for Android closing its advertisement on
  completion). With no subsequent release, the coordinator's single-operation slot stays held
  for the app's lifetime.
- The owner's log shows 16 consecutive `kept in flight` lines and no release line, then four
  `pairStart rejected: pairing operation already active` — the successful code pairing at
  15:13:55 blocked the QR handoff at 15:14:16 and every click after it.
- `QrPairingSession`'s handoff logged `handoff complete` immediately after invoking the start
  delegates, without checking whether the start was accepted.
- `PairingCoordinator.Cancel(offerId)` requires an id match, so a UI Cancel for a different
  offer than the parked one is silently a no-op.
- Documented (RFC 6762 / AOSP) but relevant: each QR pairing code is single-use, so retrying a
  QR pairing must mint a fresh code rather than resubmit the old secret.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Release when the reducer's wait completes (chosen) | Uses the reducer's existing, tested completion signal; no new timer (R-05) | Needs a snapshot observation hook in the host |
| Add a timeout timer to the coordinator | Self-contained | New periodic work → R-05 violation and ENGINEERING.md edit |
| Only preempt on a new pairStart | One-line-ish | Leaves the slot held while the user does nothing; Cancel stays broken |
| Add an `origin` field to `PairingOperationView` for QR detection | Explicit; no heuristics | Changes the frozen snapshot/bridge contract (BridgeProtocol 1) |
| Detect QR origin by the app's own instance prefix (chosen) | No contract change | A prefix heuristic; documented, and the alternative is noted for later |

## Chosen approach

See decision 0017. Release on wait completion; preempt `PairedWaitingForDevice` / different-offer
`Failed`; make Cancel id-agnostic; verify the QR handoff and keep the QR card on refusal; present
QR operations without typed-code affordances and offer "Scan again" on failure.

## Why

The release is the only change that addresses the reported failure; the remainder removes the
ways the failure could hide and the ways the user could be trapped. No ADB interaction changes:
the same `host:pair` call with the same secret path (R-13 intact), and no new timer (R-05).
