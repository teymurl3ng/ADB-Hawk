# Reliability repair lifetimes

- **Date:** 2026-10-15
- **Owner authorization:** plan and execute the audited reliability repairs, with independent judgment, in the current session.
- **Research:** research/2026-10-15-connection-reliability.md

## Decision

Existing server, device-stream, discovery, pairing, and repair owners retain their responsibilities. This work repairs their failure and reconciliation behavior and does not advance a product phase or change DESIGN.md.

An outage deadline belongs to the outage episode, not to each retry. A recovered stream cancels its deadline; a new outage receives a new monotonic generation. Published device truth is invalidated by loss of its authoritative stream through the existing two-second grace.

Internal discovery reconciliation must notify connection/QR coordinators even when no rendered snapshot field changes. Snapshot revision remains a presentation-truth revision, not a general-purpose backend event counter.

The first pairing offer remains the native offer specified by decision 0016. Active operation identity determines card identity. A card for a different operation cannot retain the previous operation's action target or QR classification.

Applied reversible repair actions remain tracked for the application session until reversed. Clearing the diagnostic card does not undo the system change and therefore does not erase the bookkeeping required by the existing undo actions. No new repair action or autonomous repair is introduced.

The browser retains its existing scheduler and empty-set query floor. Known records and incomplete resolution are scheduled from their actual validity, rather than a floor that exceeds their TTL. Network rejoin invalidates prior network-scoped data. Required follow-up DNS questions use the existing socket/query mechanism.

## Acceptance

Regression tests exercise each repaired lifetime boundary and the full verification gate passes. Existing health/warning fixtures are rendered natively with frozen text; DESIGN.md and its authority hash remain unchanged. Real-device and soak rows remain explicitly unverified until observed.
