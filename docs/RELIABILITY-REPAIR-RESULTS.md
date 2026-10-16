# Connection reliability repair results — 2026-10-15

The owner authorized planning with the installed judgment hook and immediate execution. The hook was inspected and run; it supplies an independent-judgment reminder rather than a scoring or approval service. The resulting plan is docs/RELIABILITY-REPAIR-PLAN.md, with research and decision 0027 recorded before implementation.

## Implemented repairs

| Audit concern | Resolution | Verification |
|---|---|---|
| Mixed-source merge exception freezes the state loop | Safe immutable conversion; failing observers are isolated; terminal worker failure closes input and invalidates stale devices | Capacity cases and subsequent disconnect; observer-failure continuation; isolated reproduction |
| Server/stream stop retains connected cards | Existing two-second removal grace is armed regardless of the loss path; repeated failures do not prolong it; outage tokens are never reused | ServerLossArmsGraceOnceAndNewOutageGetsFreshToken; reproduction clears device after grace |
| Native health/warnings absent | Existing global unavailable/restoring and local wireless warnings are rendered; healthy empty/QR entry is suppressed during global failure | Native view-model tests, four new fixtures, four complete inspected captures |
| UTC rollback rejects valid device/discovery updates | Production lifecycle owners assign monotonic source sequences; legacy unsequenced events retain their ordering contract | Rollback and stale-sequence regression; updated production-shaped reproduction |
| New connect advertisement does not trigger connection | StateReconciled notifies coordinators independently of rendered snapshot publication | FakeAdbServer stream delivers late connect advertisement with unchanged snapshot revision and triggers one connect |
| Endpoint/port mismatch and alternate candidates ignored | One endpoint value is selected and recorded; eligible candidates advance after failure; episodes are disarmed/cancelled | Multi-port regression and reproduction; one attempt per continuous endpoint remains |
| Five-second inner deadline defeats ten-second connect budget | Host connection receives the same ten-second budget; transport timeout becomes a typed result | Client/connection regressions and full suite |
| Pairing timeout clears silently; unexpected exception leaves coordinator busy | Owner classifies deadline expiry separately from user cancellation; exceptional failures restore a retryable phase | Pairing regressions; real HostPairClient with isolated fake server reports Failed after deadline |
| Shared-host or address-before-service DNS records fail discovery | Record cache resolves every service sharing a hostname across packet order | Split/order-varied packet tests and both original probes |
| Missing DNS dependencies never resolved | Specific SRV/TXT/A/AAAA questions use the existing socket and maintenance loop with bounded retry | MissingRecordsProduceSpecificResolutionQuestions |
| Incorrect record TTLs and withdrawals retain stale endpoints | Independent record expiry, address goodbye/cache-flush handling, current-address preference | TTL, withdrawal, cache-flush and IPv6 projection regressions; expired endpoint probe becomes unusable |
| Short TTLs conflict with the populated-cache refresh floor | Known records refresh at 80/85/90/95 percent of TTL; sixty-second empty-cache floor stays | Exact short-TTL scheduling test; no new periodic timer |
| Old-network cache survives rejoin | Rejoin clears the old record cache before receiving current data | NetworkRejoinInvalidatesPreviousNetworkEndpoints; exact-tick clock harness |
| Failed ADB discovery overrides fresh fallback data forever | The existing unavailable expiry demotes stale authoritative data and reconciles offers/bindings against fallback | FailedDiscoveryExpiresOldAuthorityAndUsesFreshFallbackEndpoint; original failure probe |
| Empty CLI update suppressed; diagnostic pipe can block | Empty documents are emitted while the pipe remains open; drainage continues beyond bounded retained text; exit failures are observed | Open-pipe empty update and 256 KiB drain regressions; original CLI probe |
| Repeated discovery failures indefinitely postpone unavailable | Deadlines belong to an outage episode, not retries | Deadline-rescheduling regression and probe |
| Card and metadata identity differ | Both use the effective discovery set and the same selected transport | Fallback identity produces one eligible metadata target |
| First offline duplicate hides connected transport | Shared transport selection prefers a connected entry | Duplicate-transport regression and probe |
| Executable launch or worker failure stops recovery silently | Launch failures become observed results; supervisor iteration failures stay in owned backoff; CLI failures retry; fallback session failures have owned recovery | Missing executable and injected launch failure tests; all existing worker/lifecycle tests |
| Resume/unlock recovery waits on backoff; unchanged protocol hides status change | Existing delay is interruptible; power/session events revalidate; device-stream loss requests liveness/status refresh; status version/path changes establish a new generation | RequestProbeInterruptsLongBackoff and same-protocol revalidation tests |
| Pairing card retains old operation target/name | Different operation ID replaces the card; same-operation name refresh updates wording without discarding typed code | Retry-target and code-preservation regressions |
| Repair falsely completes or stays visibly running | Exit codes are evaluated; the process runner owns the single timeout; started actions receive terminal results after exceptions; async verification failure is logged | Repair failure regressions; fake commands only |
| Diagnostic card clear loses repair undo | Applied reversible actions retain bookkeeping for the application session | Undo remains offered after card clears; no live machine repair executed |

## Judgment corrections and limits

- The first-offer-only UI is explicitly authorized by decision 0016, so the audit's characterization of that choice as a defect was withdrawn. Active operation identity was a separate defect and was repaired.
- General remembered-device reconnection after every ordinary drop remains outside the observation/pairing product scope. Existing post-pair connection and worker recovery were repaired.
- IPv6 addresses are now retained from AAAA records arriving through supported discovery paths and can be selected by the connection coordinator. The fallback's multicast socket/egress remains IPv4; pure IPv6 multicast and guessed link-local scope are not claimed supported.
- An external same-binary server restart that produces no observable stream/power/status event cannot be identified reliably from its protocol number. Event-driven revalidation and status changes are covered; invisible restarts are not claimed detected.
- Reversible actions retain session bookkeeping, but no new undo surface was added. The existing undo actions remain available when the diagnostic repair card is shown; access outside that surface is a design-contract question.

## Verification

The complete scripts/verify.ps1 gate passed with **1,010 tests passed, zero failed or skipped**, pinned SDK 10.0.401, format verification, warnings-as-errors Release build, source/architecture boundaries, and Release publish smoke. The baseline audit gate had 972 tests; this repair adds 38 cases. DESIGN.md and docs/DESIGN.sha256 have no diff. No new package, application/test project, general polling timer, or automatic machine repair was added. Git diff --check passed.

Two verification failures were corrected without weakening assertions: the shared test log collector now snapshots a concurrent queue, avoiding enumeration during concurrent log writes; the deterministic DNS harness now converts decimal times to exact ticks, avoiding the verified one-tick difference between AddSeconds(1.4) and AddSeconds(0.4).AddSeconds(1). The fixture inventory assertion was expanded from 18 to 22 and explicitly asserts the four added health fixtures.

The isolated audit harness was rerun against the repaired Release assembly. It was updated for the new event constructor parameters and stamps production-shaped monotonic sequences; legacy timestamp behavior remains covered separately. Its results are recorded in artifacts/audit-2026-10-15/AFTER-REPAIR.md.

Native captures were performed through scripts/capture-native.ps1 using Windows PowerShell 5.1 and a temporary per-monitor DPI wrapper. Windows build: **26100**. These complete images were inspected:

- [ADB unavailable](</C:/Users/Teymur/Documents/Projects/ADB Hawk/artifacts/visual/phase-2/native-adb-unavailable.png>)
- [Restoring ADB](</C:/Users/Teymur/Documents/Projects/ADB Hawk/artifacts/visual/phase-2/native-adb-restoring.png>)
- [Wireless discovery unavailable with connected USB device](</C:/Users/Teymur/Documents/Projects/ADB Hawk/artifacts/visual/phase-2/native-wireless-unavailable.png>)
- [Restoring wireless discovery with connected USB device](</C:/Users/Teymur/Documents/Projects/ADB Hawk/artifacts/visual/phase-2/native-wireless-restoring.png>)

The rendered shell changed because these existing contract states were missing and could hide connection failures. The empty-state text was restored to the frozen DESIGN.md wording. Its view-model transitions are tested, but the old capture helper produced partial/black images for that fixture; that image is not accepted as visual evidence. No production capture API or shell DPI policy was changed to work around the capture limitation.

The rebuilt Release executable was launched from the non-smoke source output directory and remains running. Its log identifies `src/AdbView/bin/Release/net10.0-windows/AdbView.exe`, built on October 15. The old installed process was stopped only for fixture captures. The shared adb process remained PID 5888 throughout; no real kill-server, pairing, adapter disable, or service stop was issued as a test.

The installed copy/shortcut under AppData/Local/Programs/ADBView was not replaced. The current running process is the rebuilt source output; launching the installed shortcut after closing it would still use the older installed build. Installation/packaging is a separate operation from these source repairs.

## Remaining acceptance

Actual phone disconnection/reconnection, sleep/wake, network roaming, real repair/undo, pure IPv6 networking, mixed-DPI hardware, resource/performance budgets, and the 24-hour soak remain NOT EXERCISED. They are explicit rows in docs/DEVICE-TEST-MATRIX.md. Phase 11 release acceptance is not declared complete and no public release was created.
