# 0007 — Pairing failure detail selected by cause

- **Date:** 2026-09-28
- **Status:** ACCEPTED — explicit human design-authority change
- **Phase:** post-Phase-10 product hardening (pairing reliability follow-up)

## Issue

Every failed pairing attempt rendered the same supporting line,
`Check the code and try again.`, regardless of why the pair failed. Field
diagnosis (2026-09-28) showed this misleads: a wrong code, an unreachable or
unresponsive phone, too many rapid attempts, and a stale/changed pairing offer
all look identical, so users retype a correct code and conclude pairing is
broken. The backend already classifies every failure (`PairingFailureKind`)
but the classification never reached a user-visible surface.

## Decision

The failure **title stays frozen** (`Pairing failed`). The **supporting line
under it is selected from a fixed, closed set by the failure cause** — no
technical jargon, no exception text, same wording on the flyout card and the
notification body:

| Cause (domain kind)                                  | Supporting line                                        |
|------------------------------------------------------|--------------------------------------------------------|
| Code wrong or refused (`Rejected`)                    | `Check the code and try again.`                        |
| Phone did not respond in time (`Timeout`)             | `The phone did not respond in time. Try again.`        |
| Too many attempts (`RateLimited`)                     | `Too many attempts. Wait a moment and try again.`      |
| Offer changed or withdrawn (`InvalidRequest`)         | `The pairing offer changed. Tap Pair and enter the new code.` |

Any other failure kind (`ProtocolFailure`, `MalformedResponse`) keeps the
frozen wrong-code line; `Cancelled` never shows a card. The frontend renders
the backend-provided detail when present and falls back to the frozen
wrong-code line otherwise (fixture rendering unchanged).

## Follow-ups (recorded, not in this change)

- `HostPairClient.ClassifyFailReason` maps every post-ACK server FAIL to
  `Rejected`; a distinct classification for
  `Unable to start pairing client` (phone-side pairing server refused) would
  allow a more precise line later.
- An invalid (non-6-digit) code submitted at the API surface is classified
  `InvalidRequest` but is unreachable from the shipped UI (the input gates
  submission at six digits).

## Rejected alternatives

- One generic line for all failures — the reported field confusion.
- Free-form backend reason strings on the card — violates R-15 vocabulary
  discipline and DESIGN §14–15 (no protocol internals in user surfaces).
- Per-cause titles in addition to per-cause details — larger visual/contract
  change than the problem requires.

## Testing

- `PairingOperationMapperTests`: every failure kind maps to the decision's
  line; non-failed phases unchanged.
- `NotificationTimelineTests` pairing-failure assertions updated to the
  cause-selected body.
- Vitest: `PairingCard` renders the provided detail and keeps the frozen
  default when none is provided.
