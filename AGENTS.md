# AGENTS.md — rules for coding agents working on ADB View

**Read this file, `DESIGN.md`, `ENGINEERING.md`, and the current phase in `PHASES.md` before writing any code.**

ADB View is a tiny Windows 11 tray utility that answers one question: *what is ADB doing right now?* Its value is that it is correct, quiet, and still working in six months. Your job is to implement the frozen design inside the frozen architecture — not to improve either.

Rules marked **[auto]** are enforced by `scripts/verify.ps1`. The others are enforced by review, and violating them is a defect regardless of whether the build passes.

---

## The rules

**R-01 · `DESIGN.md` is law.**
Never edit `DESIGN.md`. It is the frozen product and visual authority; where any Stitch export disagrees with it, `DESIGN.md` wins, and Pass B.1 is the visual master among the exports. If implementation reveals a genuinely missing state or a real usability problem: stop, write `docs/decisions/NNNN-<slug>.md` describing the issue and the proposed contract change, get the design contract updated, and only then implement. **[auto: DESIGN.md hash check]**

**R-02 · No redesign.**
No new component variants, no compact/dense/mini/pinned card, no alternate layout for multiple devices, no substituted visual language, no "tidied" spacing, no extra chrome, no settings/refresh/reconnect/close/help/hamburger in the header, no `Retry ADB` button. When density increases, **scroll**. When state changes, change semantic content — not component architecture. Only the four approved motion durations (0 / 83 / 167 / 333 ms) may appear in CSS. Structural easing is `cubic-bezier(0.55, 0.55, 0, 1)`. **[auto: forbidden component names, motion-token scan]**

**R-03 · No product-scope expansion.**
Observe → explain → notify → pair. Nothing else. No shell, terminal, logcat, APK install, file transfer, screen mirroring, reboot, port forwarding, screenshots, process/app management, connection history, charts, analytics, dashboards, or settings screens. The tray context menu is **Open ADB View**, **Start with Windows**, **Notifications**, **Status bubble**, **Theme** (System / Light / Dark), and **Exit** — nothing else (see `DESIGN.md` §33 / decisions 0006, 0020).

**R-04 · Architecture boundaries are fixed.**
One application project (`src/AdbView`), one C# test project (`tests/AdbView.Tests`), one frontend (`ui/`). Do not add projects. Do not add a DI container, a generic host, or a message-bus library. WinForms exists only as the message loop and the tray icon. The product's two windows — the flyout and the status bubble — are WPF and live in `src/AdbView/NativeUi/`. No other control renders product UI, and no third surface may be added without a `docs/decisions/` note (`DESIGN.md` §4). No product state in `Host/`. No Windows API calls from `State/`. No ADB knowledge in `Bridge/` or in the frontend. Code belongs in the folder that owns the concern (`ENGINEERING.md §3.1`). **[auto: source-boundary tests]**

**R-05 · No polling where an event or stream exists.**
Device state comes from a persistent `track-devices` stream; wireless discovery comes from a streaming mDNS service tracker. Do not add a timer to "refresh" either. The only sanctioned periodic work in the entire product is: the ADB server probe backoff, the 120 s metadata tick while pinned **and visible**, and the frontend's 1 s age ticker while visible. A new timer requires an `ENGINEERING.md` edit and a `research/` note proving no event mechanism exists. **[auto: timer scan in `Adb/` and `State/`]**

**R-06 · Latest `AppSnapshot` wins.**
The backend produces one immutable snapshot with a monotonic revision; the frontend renders the newest and nothing else. Never queue states behind an animation, never delay truth, never render optimistic local state for pin, pairing, or device data, and never keep a second copy of truth on either side. If a snapshot arrives mid-animation, the animation retargets.

**R-07 · The frontend has no business logic.**
It must not execute adb, interpret adb output, decide device semantics, compute global health, query battery, map an enum to semantic state text, or implement any recovery policy. Dynamic semantic/state wording belongs in `State/Vocabulary.cs` so flyout, tooltip, and notifications cannot drift. Frozen static interface copy may live in frontend components only when it is directly specified by `DESIGN.md` and does not encode backend meaning.

**R-08 · Never swallow a failure.**
No empty `catch`, no `catch { }` around I/O, no ignored exit codes, no "best effort" silence. Every failure either changes state, retries with its own owned backoff, or is logged with a subsystem and a reason — usually all three. Never solve a reliability problem by restarting everything on a timer: each subsystem owns its own detection, recovery, and backoff. **[auto: analyzer + scan]**

**R-09 · No silent dependencies.**
Adding any NuGet or npm package requires, in the same change: a dated `research/` note, an exact version pin (`Directory.Packages.props` / `package.json` + lockfile), and an entry in `ENGINEERING.md §15`. Never introduce a floating version. The avoid-list in §15 is binding. **[auto: dependency-diff check]**

**R-10 · Research before touching external integrations.**
Before changing anything about ADB, WebView2, DWM, the tray, notifications, DPI/placement, or the build and packaging chain, write or refresh a dated note in `research/` using `research/TEMPLATE.md`, citing official sources and the exact versions investigated. Your training data is not a source; neither is a blog post that contradicts current official docs. Record rejected alternatives and compatibility concerns.

**R-11 · Tests are part of the change.**
New behaviour ships with unit tests. A new UI state ships with a fixture, a Vitest test, and a visual baseline. A new failure mode ships with a `FakeAdbServer` scenario. A change to the reliability matrix (`ENGINEERING.md §11`) ships with a test that exercises the row. Real ADB integration is never considered proven by mocks — device-observable behaviour also gets a line in `docs/DEVICE-TEST-MATRIX.md`.

**R-12 · Run the gate, and be honest about it.**
`scripts/verify.ps1` must pass before you declare any task complete. Never claim success from a partial build or a subset of tests. Never disable a test, loosen a threshold, add a suppression, or delete an assertion to make the gate pass. If a visual baseline genuinely must change, update it with `scripts/visual.ps1 -Accept` and say so explicitly in your summary, with the reason and the WebView2 runtime version. If you cannot make the gate pass, stop and report exactly what fails.

**R-13 · Security invariants.**
No host objects in WebView2 (`AreHostObjectsAllowed = false`, never `AddHostObjectToScript`). No remote content: production assets are local, served through the virtual host mapping, with navigation, new-window, download, and resource filters active and all permissions denied by default. No DevTools in Release. No `ExecuteScriptAsync` for state. No user-supplied data on a command line — the pairing code goes over the socket or via stdin, never argv. No pairing code in a log, ever. Serials are redacted at Information level. No telemetry, no crash reporting, no network access; `System.Net.Http` must not be referenced. **[auto]**

**R-14 · ADB citizenship.**
The ADB server is shared with every other tool on the machine. Never kill it (`host:kill`, `adb kill-server`), never reconfigure it, never set `ADB_*` environment variables (including `ADB_MDNS_OPENSCREEN` — platform-tools 37.0.1 removed Openscreen and the variable has no effect), never bundle or download platform-tools, and never issue a device-mutating command. Pairing is the only write operation, and only on explicit user action. If adb is missing or degraded, *report* it in the words `DESIGN.md` specifies; do not fix the user's machine.

**R-15 · Vocabulary discipline.**
`Vocabulary` owns dynamic semantic/state wording shared across flyout, tooltip, and notifications. Frozen static interface copy may live in the frontend when it comes directly from `DESIGN.md`. No technical jargon in any user-visible surface: no TLS, security token, daemon, mDNS, transport id, protocol, enum name, or exception text in the flyout, the tooltip, or a notification. Human-readable, sentence case, exactly as `DESIGN.md` writes it.

**R-16 · Small, verifiable increments.**
Work the phases in `PHASES.md` order. Do not start a phase whose predecessor's acceptance criteria are unmet. Do not refactor outside the declared scope of the current phase. Prefer the smallest change that satisfies the phase, and leave the architecture smaller than the temptation.

---

## Before you say "done"

1. `scripts/verify.ps1` is green (format, build with warnings-as-errors, C# tests, contract regeneration check, lint, tsc, Vitest, Playwright visual, source boundaries, Release publish smoke).
2. The current phase's acceptance criteria are each demonstrably met — name them in your summary.
3. No new dependency, timer, component variant, or user-visible string exists without its required paperwork.
4. `research/` reflects anything you learned about an external API.
5. Your summary states plainly what you did **not** do, and what you were unsure about.

## When you are unsure

Stop and ask, or write a `docs/decisions/` note. A paused phase costs a day. A silently redesigned component, an unpinned dependency, a swallowed exception, or a "helpful" extra feature costs the product its reason to exist.
