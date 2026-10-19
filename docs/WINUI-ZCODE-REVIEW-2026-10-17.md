# Zcode review and recovery — 2026-10-17

Reviewed against the native M08 checkpoint `cb097ff`. The owner requested an independent assessment and offered reversal of unsatisfactory work. Review covers committed live composition `3177d28` and the subsequent staged/uncommitted preference increment. No M09 work or unrelated repair was undertaken.

## Progress actually made

Zcode committed the previously verified work as `cb097ff`, then added an explicit `--live` candidate path in `3177d28`: backend/service composition, snapshot/action/notification wiring, a distinct live mutex/tray identity and tray-only startup. It subsequently added persisted preference wiring, four coordinator tests, and byte-identical moves of startup/preferences adapters into Core. The preference increment was not committed or fully qualified. Its retained log shows 1,072 passing C# cases, but ends before headed verification/publish; that is not a full-gate pass.

Useful work is present: fixture/live option separation, reuse of BackendRuntime and existing actions, distinct live identity, explicit shutdown logging and honest recording of incomplete diagnosis/environment work. No dependency, renderer, backend protocol or existing fixture threshold was changed. The live increment's recorded build/fixture evidence is useful, but does not verify the new live wiring: existing fixtures never enter that branch, and the added automated live test covers options only.

## Confirmed findings

1. **P1 — unrelated preference commands can replace production autostart.** `TrayPreferenceCoordinator.Reconcile` writes all four preferences on every tray preference event (archived source lines 44–61), including unconditional `Enable`/`Disable` at line 55. `LiveShell` constructs the real shared `StartupRegistration`. With autostart enabled, merely selecting Light/Dark, Notifications or Status bubble calls `Enable`, which writes the candidate's `Environment.ProcessPath` into the installed app's `ADB View` Run value. That command appends `--startup`; the candidate parser accepts `--live` or fixture arguments, and rejects `--startup`. The result would be broken autostart. This is broader than the README warning about explicitly enabling startup. No real toggle was executed during this audit. Read-only inspection still showed the correct installed executable registration.

2. **P1 — real ages use a fixture clock.** The new live branch passes `FixtureSnapshotProvider.T0 + _fixtureClock.Elapsed` into SnapshotPresenter (archived CandidateWindow line 84). T0 is 2026-09-17 12:00 UTC; live state timestamps use actual SystemClock. The existing visible age ticker therefore supplies a time earlier than current device state. AgeFormatter clamps negative deltas to zero, displaying “Just now” at each tick instead of real elapsed time. This fixture clock was correct for fixtures; extending that wiring to live mode made it incorrect.

3. **P1 — live snapshots queue ahead of the one-slot mailbox.** `LiveShell.OnSnapshotPublished` marshals one delegate per snapshot to the dispatcher (archived lines 111–115); only that delegate calls CandidateWindow.PublishLiveSnapshot/`_mailbox.Publish`. Thus a busy dispatcher retains a FIFO of immutable snapshots before they reach the coalescing slot. Its drain can render an older snapshot while newer truth remains queued upstream. The same marshal adapter discards `DispatcherQueue.TryEnqueue`'s bool result. This bypasses the existing mailbox's thread-safe publication and explicit rejection behavior.

4. **P1 — saved Notifications=off is not applied on startup.** LiveShell creates NotificationCoordinator with `notificationsEnabled: true` (archived line 59). AttachTray loads saved values only into menu fields; it does not update the coordinator. The only `SetNotificationsEnabled` is in the later preference-change handler (line 136). A saved off check can therefore coexist with enabled notification delivery until another preference is selected. Refresh-on-menu-open also reloads fields without synchronizing notification/theme/bubble effects.

5. **P2 — host adapters were moved across the frozen boundary.** ENGINEERING §23.1 explicitly keeps preferences/startup in executable Host. Moving the complete Registry-backed adapters into Core was byte-identical, and did not break existing tests, but contradicts that ownership contract. Passing the desktop-reference guard does not authorize the move. Share or link the host code through an explicit migration mechanism, retaining pure seams only where appropriate.

## Evidence limits

The live log contains two subclass-removal warnings and a receiver-destruction warning during the bubble-enabled shutdown. This confirms an unqualified lifetime path, not the root cause. That native owner predates Zcode; the warnings cannot fairly be attributed solely to its new preference logic. Physical DPI, Explorer restart, system theme, power/session/full-screen sources, forwarding and full diagnosis/repair composition remained explicitly incomplete. The known M07 resource variability also predates Zcode and remains deferred to M09. No attempt was made to fix any of these during the review.

## Recovery

The implementation was restored exactly to `cb097ff` for `src/`, `tests/`, `scripts/`, DESIGN, ENGINEERING and dependency pins. The two untracked preference additions were moved into an artifact quarantine. Original Git commits remain; history was not rewritten and no recovery commit was created. The inverse code changes appear staged relative to `3177d28`.

Recoverable material is in `artifacts/winui-rebuild/m08/zcode-audit-2026-10-17/`: exact pre-rollback source copies, staged/unstaged/checkpoint binary patches, `history.bundle`, quarantine, before-status and read-only startup/install hashes. Zcode's old status text is preserved there. No registry value, installed executable, production process or shared ADB setting was changed.

Next implementation remains a fresh, narrowly bounded **M08 live composition** item. Reuse suitable archived pieces after correcting clock selection, direct mailbox publication and failure handling; provide tests that execute the composition seam. Preferences should be a separate increment with candidate identities, per-command writes and startup round-trip tests. This review does not mark M08 complete or start that redo.

Verification of the restored checkpoint is recorded in the current rebuild status and `verify-restored-checkpoint.log` under the audit artifacts. A partial build/test run must not be reported as a complete gate.
