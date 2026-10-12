# 0026 — Bounded QR queries, explicit egress and event-driven rejoin

- **Date:** 2026-10-12
- **Status:** Owner-requested PC-side discovery hardening
- **Amends:** 0016 query scheduling and its deferred egress work; 0008 UI remains unchanged
- **Research:** `research/2026-10-12-multicast-host-discovery.md`

## Problem

A single burst before the scan does not cover an advertisement that begins later.
Default-route egress and startup-only memberships leave interface selection and
network changes uncovered. None of these code observations proves the host's
actual failure cause or that Android responds to ephemeral-port queries.

## Contract

1. A QR attempt starts an immediate fallback burst and a 2 s fast-query window,
   bounded to 2 min using the injected clock in the existing 200 ms loop. Cancel,
   accepted QR clear/expiry and handoff stop it. Server-generation replacement
   discards it; repeated starts do not extend it. Outside it, TTL maintenance and
   the 60 s floor remain unchanged. There is no new timer or background fast mode.
2. Active ephemeral-port bursts select IP_MULTICAST_IF for each freshly enumerated
   Up, multicast-capable, non-loopback IPv4 address. The browser and reference
   probe share the candidate provider. An empty list falls back to default egress.
   Per-interface successes/failures are Debug; changed set/counts get one
   Information summary. A failing interface cannot prevent another from sending.
3. NetworkAddressChanged and resume only set a pending flag. After 1 s of quiet,
   the loop recreates the passive socket and rejoins current interfaces, then
   queries once. Failures retain the active path; disposal unsubscribes events.
4. The standalone A/B/C harness produces evidence before any shared-5353 QU or
   known-host unicast query is considered. Task 4 requires owner results showing
   A/B receive nothing while C receives the relevant service. No such evidence
   exists yet; that implementation is deferred.

## Scope and acceptance

No Android listener/app, server restart, environment changes, packages, manual
IP entry, UI strings, or DESIGN edit. Frozen-clock scheduling, termination and
floor checks; fake-socket interface sends and rejoin/failure checks; full verify
gate. Live A/B/C, delayed scan, pairing, roam and resume acceptance are explicitly
NOT EXERCISED until measured on the owner's host.
