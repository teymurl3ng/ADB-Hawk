# 0016 — Fallback discovery: QR-start query burst + instance-level merge

- **Date:** 2026-10-03
- **Supersedes (narrowly):** the whole-set effective rule of 0008/0011 ("adb list wins when
  non-empty"); everything else in those decisions stands.
- **Companion research:** research/2026-10-03-fallback-discovery-requery-and-merge.md

## Problem

QR pairing died silently on the owner's half-deaf network: adb's browse reports a non-empty
list (so the fallback is masked entirely), but no pairing row; meanwhile the fallback browser's
only query burst fires at browser start, and its empty-set requery floor is 60 s — longer than
every observed QR attempt. Additionally the native shell never rendered the offer-phase pairing
card, so even a discovered pairing offer would have been invisible natively.

## Contract changes

1. **Effective discovery set = instance-level union.** `EffectiveServices(adb, fallback)` is
   now `MdnsEffectiveSet.Merge`: adb rows win per `DiscoveryKey` (`serviceType \u001f
   instanceName`); fallback rows fill only keys adb does not report. Order is stable (adb rows
   first, then novel fallback rows). Because keys are exactly the offer/binding identity
   (0011), no duplicate offers or cards can arise from the merge.
2. **QR-session start triggers one fallback query burst.** `MdnsFallbackLifecycle.TriggerQuery()`
   is invoked by AppHost whenever a QR pairing session starts (user-initiated; rate limited by
   human behavior). No new periodic timer (R-05 intact).

## Non-changes

- adb remains authoritative per key (R-14 untouched; we never configure or restart adb).
- PairingOfferMapper, DiscoveryKey identity, decision 0014 withdrawal tolerance, and the
  double-card guards (0011 host-only second pass) are unchanged.
- Multicast egress interface pinning is explicitly deferred (research pass required).

## Acceptance

- Reducer: adb(connect-only) + fallback(pairing) → pairing offer exists; identical keys are
  never duplicated; adb-only and fallback-only sets behave exactly as before.
- QR session start produces one query burst within one loop slice.
- Native shell shows the offer-phase card for `PairingOffers[0]` when no operation is active,
  with Pair/Cancel raising the same verbs as the web (`pairStart`/`pairCancel`).
