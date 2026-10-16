# AGENTS.md — rules for coding agents working on ADB View

**Read this file, `DESIGN.md`, `ENGINEERING.md`, and the current phase in `PHASES.md` before writing any code.**

ADB View is a tiny Windows 11 tray utility that answers one question: *what is ADB doing right now?* Its value is that it is correct, quiet, and still working in six months. Your job is to implement the frozen design inside the frozen architecture — not to improve either.

**Authorized shell migration (2026-10-15, decision 0029):** the owner approved executing `docs/WINUI-REBUILD-RUNBOOK.md`. Its M00–M12 track and the explicit DESIGN §41 / ENGINEERING §23 amendments govern the replacement shell. The existing WPF application stays supported until the WinUI feasibility gate passes and cutover is verified. This authorization does not relax backend, security, failure-reporting or verification invariants.

Rules marked **[auto]** are enforced by `scripts/verify.ps1`. The others are enforced by review, and violating them is a defect regardless of whether the build passes.

---

## The rules

**R-01 · `DESIGN.md` is law.**
`DESIGN.md` is the product and visual authority; where any Stitch export disagrees, the written contract wins. Decision 0029 explicitly authorizes the shell migration contract in §41 and its deliberate hash update; it supersedes only the listed old shell mechanics. Other design changes require a decision and an explicit contract amendment before implementation. Never change the authority hash merely to make tests pass. **[auto: DESIGN.md hash check]**

**R-02 · No redesign.**
Implement the adopted shell contract: stable list/detail/pairing region, fixed outer dimensions while open, Back navigation and stationary bubble. No extra product features, compact/dense/mini/pinned card variants, settings/refresh/reconnect/help/hamburger in the header or `Retry ADB` button. Increased density scrolls. New app-owned motion uses DESIGN §41's named 0/83/167 ms recipes and live reduced motion; platform control behavior retains its native defaults. The legacy WPF shell retains its existing token/curve checks until replaced; no second WPF motion rewrite. **[auto: forbidden component names, migration/source checks]**

**R-03 · No product-scope expansion.**
Observe → explain → notify → pair. Nothing else. No shell, terminal, logcat, APK install, file transfer, screen mirroring, reboot, port forwarding, screenshots, process/app management, connection history, charts, analytics, dashboards, or settings screens. The tray context menu is **Open ADB View**, **Start with Windows**, **Notifications**, **Status bubble**, **Theme** (System / Light / Dark), and **Exit** — nothing else (see `DESIGN.md` §33 / decisions 0006, 0020).

**R-04 · Architecture boundaries are fixed.**
The migration target is three permanent projects: `src/AdbView.Core`, WinUI executable `src/AdbView`, and `tests/AdbView.Tests`. M02 permits a nonshipping `prototypes/WinUiShellProbe` outside the release solution; M04–M10 permit one temporary `src/AdbView.WinUiMigration` candidate. Keep the existing WPF/WinForms shell working until cutover, then remove it and the temporary candidate project. The two product windows remain flyout and bubble; native tray/context-menu plumbing is allowed. No DI container, generic host, message bus, third product surface or browser UI. Core has no WPF/WinForms/WinUI/native window dependency; no Windows API calls from State; no ADB knowledge in NativeUi; no product truth in Host. **[auto: source-boundary tests]**

**R-05 · No polling where an event or stream exists.**
Device state comes from a persistent `track-devices` stream; wireless discovery comes from a streaming mDNS service tracker. Do not add a timer to "refresh" either. The only sanctioned periodic work in the entire product is: the ADB server probe backoff, the 120 s metadata tick while pinned **and visible**, and the shell's 1 s age ticker while visible. A new timer requires an `ENGINEERING.md` edit and a `research/` note proving no event mechanism exists. **[auto: timer scan in `Adb/` and `State/`]**

**R-06 · Latest `AppSnapshot` wins.**
The backend produces one immutable snapshot with a monotonic revision; the shell renders the newest and nothing else. Never queue states behind an animation, never delay truth, never render optimistic local state for pin, pairing, or device data, and never keep a second copy of truth on either side. If a snapshot arrives mid-animation, the animation retargets.

**R-07 · The shell has no business logic.**
It must not execute adb, interpret adb output, decide device semantics, compute global health, query battery, map an enum to semantic state text, or implement any recovery policy. Dynamic semantic/state wording belongs in `State/Vocabulary.cs` so flyout, tooltip, and notifications cannot drift. Frozen static interface copy may live in the shell only when it is directly specified by `DESIGN.md` and does not encode backend meaning.

**R-08 · Never swallow a failure.**
No empty `catch`, no `catch { }` around I/O, no ignored exit codes, no "best effort" silence. Every failure either changes state, retries with its own owned backoff, or is logged with a subsystem and a reason — usually all three. Never solve a reliability problem by restarting everything on a timer: each subsystem owns its own detection, recovery, and backoff. **[auto: analyzer + scan]**

**R-09 · No silent dependencies.**
Adding any NuGet package requires, in the same change: a dated `research/` note, an exact version pin in `Directory.Packages.props`, and an entry in `ENGINEERING.md §15`. Never introduce a floating version. The avoid-list in §15 is binding. **[auto: dependency-diff check]**

**R-10 · Research before touching external integrations.**
Before changing anything about ADB, DWM, the tray, notifications, DPI/placement, or the build and packaging chain, write or refresh a dated note in `research/` using `research/TEMPLATE.md`, citing official sources and the exact versions investigated. Your training data is not a source; neither is a blog post that contradicts current official docs. Record rejected alternatives and compatibility concerns.

**R-11 · Tests are part of the change.**
New behaviour ships with unit tests. A new UI state ships with a fixture and a view-model test. A new failure mode ships with a `FakeAdbServer` scenario. A change to the reliability matrix (`ENGINEERING.md §11`) ships with a test that exercises the row. Real ADB integration is never considered proven by mocks — device-observable behaviour also gets a line in `docs/DEVICE-TEST-MATRIX.md`.

**R-12 · Run the gate, and be honest about it.**
`scripts/verify.ps1` must pass before you declare any task complete. Never claim success from a partial build or a subset of tests. Never disable a test, loosen a threshold, add a suppression, or delete an assertion to make the gate pass. The gate's Release publish smoke run exits before the shell is created, so it never proves the UI launches: launch the Release exe from a path that does not contain "smoke" before you claim the shell works. If a rendered shell genuinely must change, capture it with `scripts/capture-native.ps1 -Fixture <name>`, say so explicitly in your summary with the reason, and record the Windows build. If you cannot make the gate pass, stop and report exactly what fails.

**R-13 · Security invariants.**
The shell renders no web content: there is no browser surface, no host objects, no script bridge, and no remote assets. No DevTools. No user-supplied data on a command line — the pairing code goes over the socket or via stdin, never argv. No pairing code in a log, ever. Serials are redacted at Information level. No telemetry, no crash reporting, no network access; `System.Net.Http` must not be referenced. **[auto]**

**R-14 · ADB citizenship.**
The ADB server is shared with every other tool on the machine. Never kill it (`host:kill`, `adb kill-server`), never reconfigure it, never set `ADB_*` environment variables (including `ADB_MDNS_OPENSCREEN` — platform-tools 37.0.1 removed Openscreen and the variable has no effect), never bundle or download platform-tools, and never issue a device-mutating command. Pairing is the only write operation, and only on explicit user action. If adb is missing or degraded, *report* it in the words `DESIGN.md` specifies; do not fix the user's machine.

**R-15 · Vocabulary discipline.**
`Vocabulary` owns dynamic semantic/state wording shared across flyout, tooltip, and notifications. Frozen static interface copy may live in the shell when it comes directly from `DESIGN.md`. No technical jargon in any user-visible surface: no TLS, security token, daemon, mDNS, transport id, protocol, enum name, or exception text in the flyout, the tooltip, or a notification. Human-readable, sentence case, exactly as `DESIGN.md` writes it.

**R-16 · Small, verifiable increments.**
Work the authorized M00–M12 shell track in order, alongside explicitly open release acceptance. Do not start production migration before M02 passes, and do not mark the old real-device/soak/release criteria complete through a shell change. Each package's allowed scope and gate are binding. Keep increments reviewable; preserve all assertions through the migration map.

---

## Before you say "done"

1. `scripts/verify.ps1` is green (toolchain pin, format, build with warnings-as-errors, C# tests including source boundaries, Release publish smoke) — and the Release exe has been launched from a non-"smoke" path.
2. The current phase's acceptance criteria are each demonstrably met — name them in your summary.
3. No new dependency, timer, component variant, or user-visible string exists without its required paperwork.
4. `research/` reflects anything you learned about an external API.
5. Your summary states plainly what you did **not** do, and what you were unsure about.

## When you are unsure

Stop and ask, or write a `docs/decisions/` note. A paused phase costs a day. A silently redesigned component, an unpinned dependency, a swallowed exception, or a "helpful" extra feature costs the product its reason to exist.
