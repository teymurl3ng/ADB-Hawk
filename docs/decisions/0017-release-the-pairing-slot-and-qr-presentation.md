# 0017 — Release the pairing slot; QR operations are not typed-code operations

- **Date:** 2026-10-03
- **Amends:** 0014 (its keep-in-flight rule becomes "keep in flight until the wait completes")
- **Companion research:** research/2026-10-03-stuck-pairing-slot-and-qr-handoff.md
- **Live evidence:** the owner's session log, 2026-10-03 15:13:55–15:15:50

## Problem

A *successful* pairing left the pair coordinator parked in `PairedWaitingForDevice`
indefinitely. The reducer correctly cleared `AppSnapshot.Pairing` once the paired device
appeared (or the wait expired), so the UI looked finished — but nothing told the coordinator,
and the coordinator's single-operation slot rejects every later `pairStart` with
`pairing operation already active`. Net effect: **after any successful pairing, no further
pairing was possible until the app was restarted.**

The owner hit exactly this. Log sequence:

```
15:13:55.860  PairingResult: Paired with Android device          (code pairing succeeded)
15:13:55→15:15:50  "pairing offer withdrawn while pairing/waiting; operation kept in flight" ×16
15:14:16.155  QR pairing: phone scanned; starting pair for adbview-f34ffd13
15:14:16.155  pairStart rejected: pairing operation already active
15:14:16.155  QR pairing: handoff complete                        (logged as success anyway)
15:14:22.986 / 15:14:27.147 / 15:14:29.980  pairStart rejected: pairing operation already active
```

No `pairing operation released: offer withdrawn from discovery` line exists in the whole
session. The three later rejections are the owner's Cancel/Pair/Retry clicks — each one
silently refused, which is why the buttons "did nothing".

Three secondary defects compounded it:
1. `QrPairingSession` cleared the QR card and logged success unconditionally, even when the
   start had been refused — hiding the failure and removing the only affordance (rescan).
2. `PairingSession.Cancel(offerId)` required the clicked id to match the active operation, so
   Cancel was inert whenever the card showed a different offer than the parked one.
3. The pairing card rendered the typed-code affordances (six digit slots, a Pair button gated
   on six typed digits) for a QR operation that supplies its own code, and laid its Pair and
   Retry buttons out in the same grid cell with an explicit `Style` that suppressed the app's
   button template (raw Aero2 chrome — the "glued with spit" Retry button).

## Contract changes

1. **Release a fulfilled wait.** `PairingSession.ReconcileWithSnapshot(snapshot)` releases the
   operation when `PairedWaitingForDevice` and the snapshot no longer carries it (the reducer's
   completion signal: device appeared or wait expired), or when a Connected Wi-Fi device matches
   the active endpoint's host. Emits `Cleared` with no failure reason, so the card simply goes.
2. **Preempt a parked operation.** A new `pairStart` releases a parked operation first when the
   phase is `PairedWaitingForDevice` (its purpose is fulfilled) or `Failed` for a *different*
   offer (QR sessions mint a fresh instance name every time, so same-id retry can never apply).
   A genuinely in-flight operation (`AwaitingCode` / `Pairing`) is never preempted.
3. **Cancel always cancels.** `PairingCoordinator.CancelActive()` clears the active operation
   regardless of the clicked offer id; `PairingSession.Cancel` uses it. The user can never be
   trapped by an id mismatch again.
4. **The QR handoff verifies itself.** `QrPairingSession` asks whether the start took effect
   (`PairingSession.IsActive`); on refusal it logs a warning, submits nothing, and keeps the QR
   card live so the code can be scanned again.
5. **QR operations are not typed-code operations.** An operation whose offer id carries this
   app's own QR instance prefix (`QrPairingRequest.InstancePrefix`, `adbview-`) renders no digit
   slots, no typed-code title, and no "Pair" button; while in flight it shows progress with a
   quiet Cancel, and on failure it offers **"Scan again"**, which mints a fresh code (each secret
   is single-use). Detection is by prefix because the snapshot contract is frozen
   (`BridgeProtocol` 1 / `AppSnapshot`) and carries no origin field; an additive field is the
   cleaner long-term form and is noted in the research note.

## Non-changes

- No new ADB call, no change to how the secret reaches adb (host:pair service string / stdin
  only) — R-13 and R-14 are untouched, and the secret still never reaches a log or argv.
- No new timer (R-05): the release is event-driven from the existing snapshot publication.
- The reducer's completion rules, `PairingOfferMapper`, decision 0011 identity, and the
  decision 0016 discovery merge are unchanged.

## Acceptance

- After a successful pairing whose device appears, a later `pairStart` is accepted
  (`FulfilledWaitIsReleased_SoTheNextPairIsAccepted`).
- A parked `Failed` operation for another offer does not block a new pair
  (`NewPairStartPreemptsAFailedOperationForAnotherOffer`).
- A genuine in-flight operation is undisturbed by reconciliation
  (`ReconcileLeavesAGenuineInFlightOperationAlone`).
- Cancel clears a parked operation even when the clicked id differs
  (`CancelClearsAParkedOperationEvenWhenTheClickedIdDiffers`).
- A refused QR handoff keeps the session and submits nothing; a later scan hands off exactly
  once (`RejectedHandoffKeepsTheQrSessionForARescan`).
- QR-origin cards hide the slots and offer "Scan again"; typed-code cards keep slots and
  Pair/Retry (`QrOperationHidesSlotsAndShowsProgress`, `QrFailureOffersScanAgain`,
  `TypedCodeCardKeepsSlotsAndPair`, `TypedCodeFailureOffersRetryNotScanAgain`).
