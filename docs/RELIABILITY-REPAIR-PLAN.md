# Connection reliability repair plan — 2026-10-15

Owner authorization: the user requested a plan using the judgment hook and immediate execution once the review is satisfied. No additional plan approval is required.

## Judgment review

The installed `C:/Users/Teymur/.codex/hooks/independent_judgment.py` was inspected and executed. It emits the independent-judgment reminder; it is not a scoring service or an approval engine. Its policy is applied to the source evidence and this plan.

- The mixed-discovery merge exception, stale devices on server loss, missed internal discovery notifications, endpoint-selection mismatch, DNS correlation/expiry problems, timeout classification, and metadata mismatch have concrete reproductions or direct implementation evidence. Repair them with regression coverage.
- Audit finding 19 overstated the first-offer limitation: decision 0016 expressly authorizes `PairingOffers[0]`. Preserve it. Repair stale card operation identity, which is a separate defect.
- General automatic reconnection after every ordinary disconnection would expand product scope and interfere with shared ADB behavior. Preserve observation plus user-initiated pairing/connection. Fix recovery of this application's existing workers instead.
- A protocol version is not a server process identity. Improve recovery revalidation where an actual stream/power event exists; do not introduce constant extra polling or promise detection of an unobservable restart.
- Render existing global/local health states and existing frozen text. Add no design variants, settings, user-facing technical diagnostics, or arbitrary retry buttons. Keep DESIGN.md unchanged.
- Repair undo persistence needs a small lifetime clarification, documented before implementation. Preserve the existing closed, explicit repair action set; perform no real repair as a test.
- Real-device and overnight acceptance remain separate from fake-server and source verification. Do not mark them passed without observation.

Verdict: execute the bounded repairs below. Remaining environment-dependent concerns require explicit evidence and accurate coverage documentation, not speculative fixes.

## Execution sequence

1. **State correctness and worker containment.** Repair immutable merging; isolate failing state observers and make a terminal state-worker fault reject future work and become observable. Ensure authoritative stream loss starts the existing bounded grace once, and grace generations are monotonic. Add producer sequence ordering so wall-clock rollback cannot reject current stream truth. Use one selected transport/identity for card and metadata reconciliation.
2. **Discovery-driven connection and pairing.** Notify coordinators of accepted internal discovery changes independently of snapshot presentation. Select and record one endpoint consistently, advance eligible candidates within the existing policy, align request deadlines, distinguish user cancellation from operation deadlines, and release pairing invariants after exceptional failures.
3. **Discovery streams and DNS cache.** Emit empty CLI documents, continuously drain bounded diagnostic retention, observe CLI exit failures, and handle launch failures within owned backoff. Correlate records independent of order, apply addresses to every service sharing a host, resolve required missing records, retain independent record lifetimes, process withdrawals, invalidate network-scoped cache on rejoin, and refresh known short-lived records before expiry using the existing scheduler. Reject stale failed-source authority. Improve IPv6 address selection where the current transport supports it; document remaining network-family limits precisely.
4. **Server lifecycle and repairs.** Classify launch failures; wake the supervisor on resume/unlock and deliberately revalidate existing streams. Ensure every started repair has a terminal state, interpret exit codes correctly, use one timeout owner, and retain reversible-action bookkeeping until reversed or session end.
5. **Native presentation.** Render existing health/warning snapshots, suppress the healthy empty/QR presentation during global failure, and replace pairing cards when operation identity changes. Add view-model/fixture coverage and capture changed frozen states.
6. **Integration closure.** Add fake-server/lifecycle tests exercising the cross-subsystem failures, update ENGINEERING.md reliability rows and docs/DEVICE-TEST-MATRIX.md, run scripts/verify.ps1, launch the current Release executable from a non-smoke path, and inspect native captures. Re-run the isolated audit scenarios against the repaired assembly. Record unresolved real-device/soak limitations and ensure the running installed build is not mistaken for the newly built one.

## Completion evidence

Each repaired finding must have a source change, a meaningful regression or integration scenario, and a result in the completion record. The full gate must be green; DESIGN.md must retain its authority hash. No tests, assertions, or analyzer thresholds may be weakened to achieve that result. No new packages or projects are needed.
