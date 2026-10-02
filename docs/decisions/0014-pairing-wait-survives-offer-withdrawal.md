# 0014 — Pairing wait survives offer withdrawal after success

- **Date:** 2026-10-02
- **Status:** ACCEPTED — defect fix (live-session forensics, Phase 12 validation)
- **Phase:** post-Phase-12 (pairing reliability)

## Issue (forensic timeline, adbview-20261002.log 11:25–11:27)

Three wireless pairing attempts against a Samsung A06 (two six-digit, one QR; phone's previous
pairings forgotten; same Wi-Fi; no USB) all reached "Successfully paired" — the phone stored the
host key. None ever produced a device card.

Root cause: **Android closes the `_adb-tls-pairing._tcp` advertisement the moment pairing
completes.** Our reducer interpreted the disappearing pairing row as an offer withdrawal and —
123–269 ms after each success — set `Pairing = null`, **nulled `ActivePairingHost`**, cancelled
the 60-second wait (`GraceCommand.Cancel`), and `PairingSession.CancelIfOfferWithdrawn` cancelled
the pairing coordinator (bumping the generation). Consequences:

1. `PostPairConnectCoordinator` disarmed before the phone's `_adb-tls-connect._tcp` advertisement
   (a *different* row, announced only after pairing) ever entered the effective set —
   `host:connect` was never issued. Live probe hours later: the A06 still advertising
   `192.168.0.53:45533`, `adb devices -l` empty, serial absent from the entire log.
2. Attempt 3's in-flight `host:pair` result was dropped as stale-generation — no feedback at all.
3. The "designed 60 s wait" (ENGINEERING §6.8, row 13) never ran; the card cleared in ~130 ms.
   The one historically "working" session (S24, 01:45) had simply won the race — its connect row
   landed 127–172 ms after handoff, ahead of the ~290 ms withdrawal.

## Decision

**Offer withdrawal after pairing success is expected phone behavior and must not cancel the
pairing wait.** Concretely:

1. `StateReducer.ReduceOfferSource`: the offer-withdrawal branch skips clearing while the
   operation is in `Pairing` or `PairedWaitingForDevice` — the card stays ("Pairing with…" /
   "Waiting for ADB connection…"), `ActivePairingHost`/`ActivePairingOfferId`/wait generation
   stay armed (without this, a Pairing-phase withdrawal nulls `ActivePairingHost` and the later
   success arms a wait with no host — `host:connect` could still never fire).
2. `PairingSession.CancelIfOfferWithdrawn` skips the coordinator cancel while the operation is
   **in flight or already successful** (`Pairing` or `PairedWaitingForDevice`). Attempt 3's exact
   shape — withdrawal landing during the in-flight `host:pair` — is covered: the result now lands
   and renders instead of being dropped as stale-generation. The coordinator self-cleans after
   the result transitions the phase (failure lands → next withdrawal sweep frees it); parking is
   bounded by `HostPairClient`'s 30 s timeout.
3. The wait still ends exactly three ways: tracker arrival matching the pairing (existing
   `MatchesWaitingPairing` clear), the 60 s quiet expiry, or explicit user Cancel. Pre-success
   withdrawal (phone leaves pairing mode mid-entry, stale offer) cancels as today.
4. Tolerated withdrawals log once at INF: "pairing offer withdrawn after success; waiting for
   connection."

## Rejected alternatives

- Issuing `host:connect` at *pair* time toward the pairing port — wrong port; the phone closes it.
- Removing the withdrawal-cancel entirely — stale pre-success offers would pin a dead card.
- Logging-only diagnostics pass — the defect is stateful, not observability.

## Testing

- Reducer: success → effective set loses the pairing row → card/host/wait preserved; a later
  Connect row keeps PostPair state intact; pre-success withdrawal still cancels.
- Coordinator: withdrawal after success does NOT bump the generation — in-flight result lands
  (reproduces attempt 3); withdrawal before success still cancels.
- Existing suites stay green (offer withdrawal while awaiting *code* unchanged).
