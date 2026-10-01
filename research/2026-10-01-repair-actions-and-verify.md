# Repair actions: user-approved ADB restart, allow-listed admin repairs, episode balloon

- **Date:** 2026-10-01
- **Author:** agent (Phase 12 Step 2; amends decision 0008)
- **Phase:** 12.2 (discovery health — repair actions)
- **Status:** current

## Problem

Decision 0008 shipped the broken-discovery evidence card (Step 1) with no repair affordance. The
user approved three additions: a **Restart ADB** button with verify-and-report, **UAC-elevated**
allow-listed admin repairs for the documented environmental causes, and **one balloon per broken
episode**. Each collides with an existing invariant — R-14 (never kill the ADB server), the
boundary tests that encode it, the per-user/no-admin install posture — so every mechanism needs
verification before design.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| Microsoft Learn — How User Account Control works | https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works | ms.date 2026-04-23; fetched 2026-10-01 |
| Microsoft Learn — ProcessStartInfo.Verb (.NET 10) | https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.verb?view=net-10.0 | updated 2026-08-13; fetched 2026-10-01 |
| Microsoft Learn — sc.exe config | https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-config | ms.date 2018-06-05 (page current 2026-02); fetched 2026-10-01 |
| Microsoft Learn — netsh interface (set/show) | https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-interface | fetched 2026-10-01 |
| In-repo exploration | AdbServerSupervisor/DeviceTrackerLifecycle/MdnsTrackerLifecycle/NotificationPolicy/NotificationCoordinator/AdbProcessRunner/BoundaryTests | 2026-10-01 read |

## Version investigated

Windows 11 `10.0.26100` · .NET 10.0.401 · platform-tools 37.0.1-15733141 · CsWin32 0.3.333.

## Findings

1. **Recovery after a kill is already fully owned by the supervisor.** `AdbServerSupervisor`
   probes every 5 s while Up; on failure it emits `Restoring` (first 10 s), calls
   `AdbProcessRunner.StartServerAsync` (argv `start-server`, 20 s timeout, process-tree kill on
   timeout, environment inherited unchanged), reprobes immediately, and emits `Up` with a bumped
   generation (`AdbServerSupervisor.cs:130-195`). Backoff 1/2/5/10/30/60 s ±20 % resets on
   recovery (`Backoff.cs`). Both tracker lifecycles hard-stop on any non-Up phase and
   stop-and-restart keyed on generation (`DeviceTrackerLifecycle.cs:49-129`; proven by
   `ChaosHermeticTests.Mut1`, `CompositionTests` generation rows, `ScenarioG` server-death row).
   **The repair therefore only needs to perform the kill; everything after is existing,
   tested machinery.** Live precedent: the Phase 6D operator follow-up did
   `adb kill-server` + `start-server` manually and the app's trackers reconnected cleanly.
2. **No kill path exists today, and the boundary tests encode its absence.**
   `BoundaryTests.cs:545-562` forbids the tokens `kill-server`/`host:kill` in ADB-core files, and
   `FakeAdbServer.AssertNoKillCommandSent` fails any scenario where the product sends a kill
   command. R-14 (AGENTS.md) states the law. The user-approved repair changes this contract for
   exactly one action, so **decision 0008 must be amended and the boundary list updated in the
   same change** — a contract change first, not a gate loosening.
3. **Kill mechanism: process spawn, not smartsocket.** `adb kill-server` as a spawned client
   command (pattern identical to the existing `start-server` invocation) avoids the
   `host:kill` socket-close race and keeps all ADB interaction in the Adb layer. In-flight work
   survives a restart: pairing is bounded by its 30 s timeout → `Failed` + user retry;
   metadata probes fail into value-omitted (2 s single retry, next cadence re-probes); devices
   are held 2 s by the tracker grace so cards do not flicker (`StateReducer.cs:12`).
4. **UAC mechanics.** Elevation prompts (consent for admins, credentials for standard users) are
   shown on the secure desktop; a per-user app with no `requireAdministrator`/`autoElevate`
   manifest can still request elevation **per operation** by launching a fixed executable with
   the ShellExecute `runas` verb (`ProcessStartInfo.UseShellExecute = true, Verb = "runas"`).
   Declining the prompt raises `Win32Exception` (canceled) — the app must treat that as a no-op.
   Constraint: `UseShellExecute = true` **cannot redirect stdio**, so elevated actions are
   fire-and-observe; correctness must come from observing system state afterwards (which is
   exactly the verify-and-report model).
5. **Allow-listed admin repairs (the two documented suspects on this host).**
   - Stop the Tailscale service: `sc.exe stop Tailscale` (reversible `sc.exe start Tailscale`;
     start-type untouched, so a reboot restores it regardless). Elevation required; absence of
     the service = action not offered.
   - Disable a virtual adapter: `netsh interface set interface name=<name> admin=disable`
     (reversible `admin=enable`); requires elevation; documented parameter is the interface
     **name** (related netsh contexts accept name-or-index). Adapter names are system-derived —
     they must be charset-validated (letters, digits, space, parentheses, hyphen, dot) before
     touching a command line, and passed via `ArgumentList` (no shell, no interpolation).
6. **Notification budget machinery supports "one per episode" with existing patterns.**
   `NotificationPolicy` is edge-triggered on snapshot transitions with hold-then-materialize
   (re-validated at expiry), cancel-on-withdraw, and latched one-shot outage notifications —
   `AdbUnavailableOutageNotified` is emitted once and cleared only when health leaves
   `AdbUnavailable` (`NotificationPolicy.cs:261-263`, cleared `:43-45`). A
   `DiscoveryBroken` intent can copy that latch exactly: latch on emit, clear when the card
   clears. Master switch (`SetNotificationsEnabled(false)` cancels pending and re-baselines),
   burst accounting, and startup/resume suppression all apply generically. Text flows through
   `Vocabulary.Notifications` → `BalloonText` (63/255 char truncation) → `TrayIcon.ShowBalloon`.
7. **Per-user installer posture preserved.** The app process itself never elevates; no manifest
   change (an `autoElevate`/`requireAdministrator` manifest would prompt at every launch and
   break `PrivilegesRequired=lowest`). Each admin repair is one UAC consent for one fixed,
   non-interpolated (except the validated adapter name) command line. R-13 "no user-supplied
   data on a command line" is honored: nothing the user types ever reaches argv.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| `host:kill` over the smartsocket | No new process spawn | Socket-close race on server exit; still trips the boundary token; less symmetric with `start-server` |
| `cmd.exe /c` wrappers for admin actions | One shell, multiple commands | Shell interpolation on a command line — R-13 adjacency; harder to reason about; rejected |
| `sc config … start= disabled` (persist) | Survives reboot | Changes system configuration beyond the repair session; not self-reversing; rejected — stop-only is transient |
| Kill/close third-party apps (msedge, ChatGPT) holding 5353 | Removes contention | User explicitly declined: data-loss risk; Edge re-binds on relaunch anyway |
| `autoElevate` manifest | One prompt | Prompts every launch; breaks per-user install posture; rejected |
| Parse elevated-command output (redirect) | Richer result | Impossible: `Verb=runas` forces `UseShellExecute=true`, which cannot redirect; observation-based verify instead |

## Chosen approach

1. **`Host/RepairCoordinator.cs`** owning a closed action set:
   `RestartAdb` (non-admin: locate adb.exe, spawn `kill-server`, supervisor recovers, then after
   the next `Up` generation + settle delay re-run the discovery measurement), and per-finding
   admin actions `StopService(Tailscale)` / `DisableAdapter(<validated name>)` offered only when
   the corresponding evidence exists (service present / adapter found). Every action is
   idempotent, user-initiated only, and followed by an automatic re-measure whose result replaces
   the card's evidence (restored → card clears; still broken → evidence refreshes, "still not
   working" is visible truth, never a fake success).
2. **Elevated execution:** one `Process.Start` per action with `UseShellExecute=true, Verb="runas"`,
   fixed FileName (`sc.exe` / `netsh.exe`), `ArgumentList`-built arguments, adapter names
   charset-validated; decline (`Win32Exception`) = logged no-op.
3. **R-14 amendment via decision 0008 revision:** the single sanctioned kill (`adb kill-server`
   spawned, never `host:kill`, never on a timer, only from the button), boundary-token list
   updated to carve out the one allowed site, `FakeAdbServer` gains a sanctioned kill scenario
   while `AssertNoKillCommandSent` stays default-strict elsewhere.
4. **Card UI:** the evidence card gains an action row — `Restart ADB` primary; admin repairs as
   labeled secondary actions with the system name shown (e.g. "Stop Tailscale service");
   per-action running/disabled state; a result line rendered by `Vocabulary` ("Discovery
   restored" clears the card via the trigger; "Still not working" keeps it). Copy decision
   recorded in the 0008 amendment.
5. **Balloon:** `NotificationKind.DiscoveryBroken`, edge-triggered when the card appears
   (hold 1.5 s like `PairingAvailable`, cancel-on-clear during hold), latched once per episode
   (`AdbUnavailableOutageNotified` pattern), body = card title/detail line from Vocabulary;
   cleared when the card clears so a new episode can notify again.

## Why

Every moving part after the kill is already tested machinery (supervisor, lifecycles, grace,
notification policy); the new code is one coordinator, one elevated-spawn helper, and one policy
intent — the smallest surface that honors the user's three decisions without auto-repair,
third-party process kills, or manifest changes.

## Rejected approaches

- Automatic repair on detection (no approval button) — violates the user decision and
  R-14's spirit.
- Restarting ADB on a schedule or on every flyout open — R-05 / citizenship.
- `sc config` persistent disabling of services or adapters — beyond the episode, not
  self-reversing.
- Redirecting elevated output — impossible with `runas`; observation replaces parsing.

## Compatibility concerns

- UAC declined → no-op with a visible "not run" state on the card; never retried automatically.
- Service/adapter absent on other machines → actions never offered (evidence-driven).
- `adb.exe` not locatable → `RestartAdb` disabled (supervisor's own unavailable path governs).
- Restart drops in-flight pairing → existing 30 s timeout + retry UX; the card notes nothing
  (pairing failure copy already covers it).
- Multiple monitors/UAC secure desktop focus loss: the flyout may deactivate under the secure
  desktop — light-dismiss may hide it; the card state survives in the snapshot and re-renders on
  reopen (level-triggered).

## Testing implications

- `AdbServerSupervisor`/lifecycle behavior: unchanged; already covered (ScenarioG, Mut1).
- New `RepairCoordinator` tests with a process-spawner seam: kill-spawn argv, decline no-op,
  charset validation rejections, action-offered gating, verify-and-report transitions.
- `FakeAdbServer`: sanctioned kill scenario for the new path; kill assertion stays strict
  everywhere else.
- Boundary tests: updated allow-list per the 0008 amendment (contract change, tested).
- `NotificationTimelineTests`: `DiscoveryBroken` once-per-episode (emit → clear → re-break →
  emits again; cancel-during-hold; master switch off; burst interactions).
- Vitest: card action states (running/disabled/result line); visual baseline update with reason
  (card grows an action row — decision 0008 amendment).
- Live: real `Restart ADB` on this host with trackers recovering and the card re-measuring;
  UAC stop-Tailscale + re-enable; adapter disable/enable (Hyper-V Default Switch) — matrix rows.

## Open items

- Whether "Restart ADB" should also be offered when discovery is *healthy* (e.g. general
  trouble-shooting) — default: no; the action lives on the broken-discovery card only.
- Exact settle delay between `Up` generation and the re-measure (tracker needs one complete
  mDNS frame first — event-driven: re-measure on the first empty frame *after* the generation
  bump rather than a fixed sleep, keeping R-05).
