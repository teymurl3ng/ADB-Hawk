# 0019 — Toast timing, and no toast while the flyout is on screen

- **Date:** 2026-10-03
- **Amends:** the notification timing rows of ENGINEERING.md §8.1/§8.2

## Problem

Owner report, two parts:

1. "The notifications are too slow — it takes 2-3 seconds to find out the connection is
   gone. It should be instant."
2. "The notification screen should not come when the tray is already on the screen — it
   disrupts it."

## Findings

**Latency.** Detection is not slow: `host:track-devices` is a persistent push stream, the
reducer publishes synchronously with no coalescing window, and the flyout and tray are
updated immediately on every snapshot. The delay was entirely in the toast:
`NotificationPolicy.HoldConnected` / `HoldDisconnected` were **2.5 s** — a deliberate flap
debounce, not a detection cost. (A separate 2 s grace applies only when the tracker
*stream* dies, e.g. an adb server restart, giving a ~4.5 s worst case; that one is required
for correctness and stays.)

**Suppression.** Nothing in the notification path knew whether the flyout was visible.
`NotificationCoordinator` had no visibility input at all.

## Decision

1. **Connection holds 2.5 s → 0.5 s** (`HoldConnected`, `HoldDisconnected`,
   `HoldDeviceProblem`). Half a second still swallows a sub-second cable blip — the flap
   debounce's purpose — while reading as immediate. The tracker-failure grace (2 s) and all
   anti-spam windows (60 s dedup, 10 s burst cap, startup/resume suppression) are unchanged.
2. **No delivery while the flyout is visible.** An optional `Func<bool>` predicate on
   `NotificationCoordinator` is evaluated in `EmitBatch` **before**
   `NotificationPolicy.ApplyBurstAndRecord`. Dropping later (at the sink) would still record
   the emit — latching the one-per-episode flags and consuming a burst slot — so a
   notification the user never saw would silently change dedup behaviour. Evaluated here,
   a suppressed notification consumes nothing and can still be delivered later.
3. **Drop, never defer.** A deferred toast would arrive after the fact and, for latched
   kinds, confusingly late; the codebase already establishes "drop pending, no backfill"
   (decision 0006, and the startup/resume absorb path). Edge-triggered kinds do not re-fire
   without a genuinely new transition, which is the correct behaviour.
4. **Coverage:** "flyout visible" also covers a pinned flyout (a pinned flyout never hides),
   so no separate pinned check is needed. `PairingResult` is suppressed too — the exempt
   rationale ("the user is staring at the UI waiting for it") is *satisfied* by the visible
   flyout, which is already showing the result. Tray-context-menu-open is out of scope: it
   is not observable through `ITrayBalloonHost`.

## Acceptance

- `N_SuppressedWhileTheFlyoutIsVisible` — nothing emitted while the predicate is true.
- `O_SuppressionConsumesNoBookkeepingSoTheNextTransitionStillEmits` — after the flyout
  closes, a new transition notifies normally (proving no episode latch or burst slot was
  consumed).
- Timing tests retargeted to the symbolic hold constant, so the value lives in one place.
- `ENGINEERING.md` §8.1/§8.2 updated (hold values + a visible-flyout suppression row).
