# ADB View — Implementation Phases (authoritative plan)

**Date:** 2026-09-16 · **Authorities:** `DESIGN.md` (product/visual, frozen) · `ENGINEERING.md` (engineering) · `AGENTS.md` (agent rules)

## How to use this file

- Work phases **in order**. Do not start a phase until the previous phase's acceptance criteria are all met and `scripts/verify.ps1` is green.
- Every phase starts with its **research step**: create or refresh the named `research/` note using `research/TEMPLATE.md`, with official sources and the exact versions investigated. Model memory is not a source.
- Every phase ends with: `scripts/verify.ps1` green, the phase's acceptance criteria demonstrably met, and `PHASES.md` updated with a one-line completion record (date + commit).
- "Out of scope" lists are binding. Work found to be necessary but out of scope is recorded in `docs/decisions/` and scheduled, not smuggled in.

---

## Phase 0 — Repository, gates, and guard rails

**Research:** `research/<date>-toolchain-and-gates.md` — confirm .NET SDK 10.0.x current patch, xUnit v3 version and Microsoft Testing Platform behaviour under `dotnet test` on the .NET 10 SDK, Node LTS for Vite 8 (20.19+/22.12+), exact pinned versions for every dependency in `ENGINEERING.md §15`.

**Scope:** An empty repository that already enforces the architecture.

**Files/subsystems:** `ADBView.sln`, `global.json`, `Directory.Build.props` (TFM `net10.0-windows`, `Nullable=enable`, `TreatWarningsAsErrors=true`, `EnableNETAnalyzers`, `AnalysisLevel=latest-all` with a curated suppression list, version), `Directory.Packages.props` (exact pins), `.editorconfig`, `.gitignore`, `src/AdbView` skeleton (`Program.cs` writing one log line and exiting), `NativeMethods.txt` (empty), `app.manifest` (PerMonitorV2, no elevation), `tests/AdbView.Tests` with the source-boundary tests, `ui/` skeleton (Vite + React + TS + Tailwind, one placeholder page), `scripts/{build,test,verify,visual,gen-contract,package}.ps1`, `.github/workflows/ci.yml`, `DESIGN.md` copied verbatim + hash recorded, `ENGINEERING.md`, `AGENTS.md`, `PHASES.md`, `README.md`, `research/TEMPLATE.md`, PR template with the phase checklist.

**Tests:** Source-boundary tests that fail loudly if violated — no WinForms control types outside `Host/`; no `System.Net.Http`; no `DateTime.Now`/`UtcNow` outside `SystemClock`; no `postMessage` outside `ui/src/bridge/actions.ts`; forbidden component identifiers (`CompactDeviceCard`, `DenseDeviceCard`, `MultiDeviceCard`, `MiniDeviceCard`, `PinnedDeviceCard`); `DESIGN.md` hash unchanged; dependency manifest matches `ENGINEERING.md §15`; `ui/dist` contains no absolute `http(s)` URL.

**Acceptance:** Clean clone → `scripts/verify.ps1` passes end to end (format, build, both test suites, boundaries) in under three minutes. CI green on `windows-latest`. Every gate demonstrably fails when deliberately violated (a "break it on purpose" commit is recorded in the PR description, not merged).

**Out of scope:** Any product behaviour, any window, any tray icon, any ADB code, any real UI.

---

## Phase 1 — Windows shell (first milestone: it looks and behaves like a Windows flyout)

**Research:** `research/<date>-windows-host-integration.md` — DWM backdrop attributes and their requirements on frameless windows; `Shell_NotifyIconGetRect` / `NOTIFYICONIDENTIFIER`; `SHAppBarMessage(ABM_GETTASKBARPOS)`; per-monitor DPI APIs; WebView2 transparent background; `IsNonClientRegionSupported` + `app-region: drag`; `SystemEvents` power/session/display; named-pipe single instance.

**Scope:** Tray icon, borderless acrylic flyout hosting WebView2 on a **static local page** (no product UI, no fixtures yet), placement, pin, drag, light dismiss, single instance, logging, power/session/display handling, WebView recovery.

**Files/subsystems:** `App/{AppHost,SingleInstance,CommandLine,SystemClock}`, `Host/{TrayIcon,FlyoutWindow,FlyoutPlacement,WindowBackdrop,WebViewHost,SystemTheme,PowerAndSession,Interop/*}`, `Diagnostics/Log`, `Resources/` (one placeholder multi-resolution icon), `NativeMethods.txt` populated.

**Tests:** Unit — placement math over synthetic rects for all four taskbar edges, work-area clamping, mixed DPI scales (1.0/1.25/1.5/1.75/2.0), 1366×768, secondary monitors, anchor-source fallback chain; backoff sequences; theme/transparency detection mapping. Integration (headed) — show/hide, light dismiss on deactivation, pinned window survives deactivation, `TopMost` toggling, second-instance toggle, forced `ProcessFailed` recovery, WebView2 profile reset path, tray icon survives an Explorer restart.

**Acceptance:**

1. Left click toggles a 376-effective-px-wide flyout adjacent to the tray icon; right click offers only **Exit**.
2. Desktop Acrylic is visibly correct while unpinned **and** while pinned and unfocused (V-2 closed); if unavailable, the host reports `transparencyEnabled=false` and logs why.
3. No taskbar button, no Alt-Tab entry, no app chrome.
4. Manual matrix passes with no clipping, overlap, offscreen placement, or blur: 100/125/150/175/200 %, taskbar on all four edges, auto-hide taskbar, icon in the overflow flyout, two monitors with different DPI, 1366×768, 4K.
5. Pinned window drags from the header only (V-4 closed) and never from a card.
6. Second launch toggles the existing window and exits 0.
7. Suspend/resume and lock/unlock leave the window and tray correct; the log shows the events.

**Out of scope:** Any real UI, any fixture data, any ADB, notifications, icon _states_ (one placeholder icon is enough), content-driven height (fixed test height is fine).

---

## Phase 2 — The frozen UI, from fixtures, in the browser

**Research:** `research/<date>-frontend-stack-and-visual-testing.md` — pinned React/Vite/Tailwind/Vitest/Playwright versions; `grid-template-rows: 0fr→1fr`, `@starting-style`, `transition-behavior` support in the current WebView2/Chromium; Playwright `deviceScaleFactor` + `emulateMedia` for `forcedColors`, `colorScheme`, `prefers-reduced-motion`; Segoe UI Variable family names.

**Scope:** Every state in `DESIGN.md`, built once as canonical components and driven entirely by fixture snapshots in `ui/harness/`. No bridge, no ADB.

**Files/subsystems:** `ui/src/contract/snapshot.ts` (hand-written this phase, generated from Phase 3), `ui/src/styles/{tokens.css,app.css}`, `ui/src/components/*` (exactly the seven canonical components plus primitives), `ui/src/icons/*` (vendored SVGs from the approved exports), `ui/src/lib/{age.ts,fixtures/*}`, `ui/harness/*`.

**Tests:** Vitest over every fixture listed in `ENGINEERING.md §16.3` (all device states, empty, global states, subsystem warnings, all pairing phases, 1/2/3/8 devices, pairing + devices, pinned, light/dark/opaque/forced-colors, reduced motion, long names, absent battery/model/version, all age formats, only-one-expanded, revision guard). Playwright visual baselines for the curated fixture set × themes × 5 scales.

**Acceptance:**

1. Side-by-side review against Pass B.1 (geometry/components), C.1 (state semantics), C.3 (density/scroll), D.1 (theme tokens), F (icon system) with a written sign-off in the PR; B.1 wins wherever exports disagree.
2. Hard geometry verified in code review: 376 px width, 40 px header, 40×40 pin hit area, 32×32 device glyph, 15 px name, 12 px metadata, 12 px card padding, 8 px radii, 13.5 px pairing heading, 14 px pairing padding, 4 px grid spacing.
3. Exactly one `DeviceCard` implementation; no compact variant anywhere; density scrolls with a fixed header and no horizontal scroll.
4. Only the four motion durations appear in CSS; reduced motion zeroes them; no pulsing, spring, bounce, or overshoot.
5. Expansion animates without hard-coded heights and stays correct at 200 % (V-8 closed).
6. Forced-colors mode uses system color roles only; every state remains readable with color removed.
7. Keyboard-only operation and visible focus throughout; `aria-live` announces state changes only.

**Out of scope:** Any native integration, any real data, any action wiring beyond harness stubs, tray icon states.

---

## Phase 3 — The bridge

**Research:** `research/<date>-webview2-bridge.md` — `PostWebMessageAsJson`/`WebMessageReceived` semantics and ordering; `SetVirtualHostNameToFolderMapping` + `DenyCors`; navigation/resource/permission/download filters; `System.Text.Json` source generation with `UnmappedMemberHandling.Disallow`; clipboard permission behaviour (V-3).

**Scope:** The typed contract in both directions, the `ready` handshake, coalescing, hardening, and fixture-driven snapshot injection from native. Still no ADB.

**Files/subsystems:** `State/AppSnapshot.cs` (contract only), `Bridge/{BridgeProtocol,WebActions,BridgeSerializer,ActionRateLimiter}`, `Host/WebViewHost` hardening + transport, `ui/src/bridge/{useSnapshot,actions}`, `scripts/gen-contract.ps1` (C# → TS, committed output), `--fixtures <name>` startup flag.

**Tests:** Unit — serialization round-trip for every snapshot shape; rejection of unknown type/member/protocol, bad code format, unknown `offerId`, out-of-range height; rate limiting; coalescing (100 revisions → ≤ N posts, final state correct); contract regeneration produces no diff. Integration — `ready` → snapshot → action → snapshot; reload mid-session re-pushes current snapshot; `ProcessFailed` recovery re-pushes; protocol mismatch yields read-only mode; navigation to any external URL is blocked; DevTools absent in Release.

**Acceptance:** Pin/unpin, Esc-dismiss, and content-driven height work end to end through the bridge with the window now sized by `reportContentSize` (120–560 effective px, clamped natively). All eight actions and exactly one native→web message type exist; nothing else can cross. Copy works via `navigator.clipboard` with inline `Copied` feedback and no bridge traffic.

**Out of scope:** ADB, notifications, pairing behaviour beyond message plumbing, persistence.

---

## Phase 4 — ADB protocol core (fake server only)

**Research:** `research/<date>-adb-protocol-and-wireless.md` (create) — AOSP `docs/dev/services.md`, `adb.1.md`, platform-tools release notes; confirm host-service strings, `-proto-binary` framing, `server-status` fields, mDNS backends and defaults for the installed version; capture transcripts with `adb mdns track-services --proto-text` and the binary variant (V-1, V-9).

**Scope:** `AdbHostConnection`, framing, proto reader, executable discovery, server supervisor with backoff, capability probe — all exercised against `FakeAdbServer`. No real device required.

**Files/subsystems:** `Adb/Protocol/*`, `Adb/{AdbExecutableLocator,AdbServerSupervisor,AdbCapabilities,Backoff}`, `Adb/Model/*`, `tests/.../Fakes/FakeAdbServer.cs`, `scripts/capture-fixtures.ps1`, `State/StateEngine` + `Reducer` skeleton consuming `AdbServerStateChanged` only.

**Tests:** Framing and payload-cap tests; `OKAY`/`FAIL` handling with reason text; truncated/garbage frames; cancellation and disposal with no leaked sockets (socket-count assertion); proto reader over captured fixtures with binary-vs-text cross-check; malformed-proto resilience; executable discovery precedence including a hostile `adb.exe` in the working directory (must be ignored); supervisor phases including `AdbRestoring` vs `AdbUnavailable` at the 10 s boundary; backoff sequence and jitter bounds; `host:kill` never sent (scan + fake-server assertion).

**Acceptance:** With `FakeAdbServer`, §11 rows 1–4 behave exactly as specified, deterministically, in under a second each. The capability probe result is logged once per server generation. No timer exists outside `Backoff`.

**Out of scope:** Device tracking, mDNS, pairing, metadata, UI wiring.

---

## Phase 5 — Device tracking (first real hardware)

**Research:** refresh the ADB note with observed `track-devices-proto-binary` payloads for USB and wireless devices, `connection_type` values, and every state literal seen on real hardware.

**Scope:** `DeviceTracker`, identity resolution, the full reducer for devices, health and tray aggregation, `Vocabulary`, snapshots reaching the real UI.

**Files/subsystems:** `Adb/{DeviceTracker,DeviceIdentity}`, `State/{Reducer,SnapshotBuilder,HealthAggregator,TrayStatusAggregator,Vocabulary}`, `Resources/` tray icons for all six states, `Host/TrayIcon` state application.

**Tests:** Identity cases (`ENGINEERING.md §16.1`); every state literal → `DeviceStateKind`; unrecognized literal → `Unknown` + one log line; out-of-order lists ignored; tracker-down 2 s grace; stream reconnect (rows 5–7); ordering stability across revisions; tray priority and tooltip golden strings; `Vocabulary` dynamic-semantic golden strings quoting `DESIGN.md`; frozen static frontend copy covered by component/visual tests.

**Acceptance:** A real USB device appears within one second of plugging in, with correct name, state, transport and age; authorization prompts render the amber state with the exact guidance text; unplug removes the card; two devices render two cards; `adb kill-server` shows `Restoring ADB…` and recovers without flicker; tray icon and tooltip match the aggregate rules. Transcripts committed as fixtures.

**Out of scope:** Battery/model/version (no metadata yet), wireless discovery, pairing, notifications.

---

## Phase 6 — Wireless discovery and pairing

**Research:** refresh the ADB note with observed streaming-mDNS payloads for `_adb-tls-pairing`/`_adb-tls-connect`/`_adb._tcp` on Android ≤ 16 and Android 17 (Wi-Fi 2.0 fields, `mdns_service_version`), plus pairing request/response behaviour and timing including auto-connect (V-1, V-10).

**Scope:** `MdnsTracker`, offer modelling, address/name enrichment of connected wireless devices, `PairingCoordinator` and its full state machine, the wireless-discovery subsystem warning.

**Files/subsystems:** `Adb/{MdnsTracker,PairingCoordinator}`, reducer/pairing sections, `State/Vocabulary` pairing strings, `Bridge` pair actions wired end to end.

**Tests:** Fake-server scenarios — offer appears/withdraws; connect-service-only (must not create a card); pairing success; wrong code; refusal; hang → 30 s timeout; success then device appears (clears operation); success then 60 s silence (clears quietly); cancel mid-pairing then late success (dropped); mDNS unsupported/disabled → warning card while USB stays healthy; tracker reconnect (rows 8–9). Unit — offer id stability across re-advertisements; IPv6 records; enrichment matching by address.

**Acceptance:** Real pairing works on both an Android ≤ 16 and an Android 17 device using the exact `DESIGN.md §15` copy at every phase; **successful pairing never shows a connected device** — the card appears only when tracking says so; a wrong code shows `Pairing failed` / `Check the code and try again.` with immediate retry; on an mDNS-blocked network the warning card appears and USB devices are unaffected; no technical jargon (TLS, daemon, mDNS, token) appears anywhere in the UI.

**Out of scope:** QR-code pairing (not in `DESIGN.md`), `adb connect` for unpaired devices, remembering paired devices, metadata, notifications.

---

## Phase 7 — Metadata

**Research:** short note on `dumpsys battery` output across available OEM devices and Doze behaviour, and `getprop` keys for model/version (V-7).

**Scope:** `MetadataProbe` and the visibility-aware refresh policy; expanded-card fields complete.

**Files/subsystems:** `Adb/MetadataProbe`, reducer metadata handling, `Host` visibility signalling into the engine.

**Tests:** Trigger matrix (connect, flyout open with 30 s staleness, pinned 120 s tick, never while hidden); single in-flight per device; timeout → one retry → omit; unknown device no-op; parse tests over captured `dumpsys` outputs including odd OEM formats; **idle-cost test**: with the flyout hidden for 60 s and a connected device, zero device-directed requests are issued.

**Acceptance:** Battery matches the phone within one refresh; exact model and Android version appear in the expanded card; copy controls work for serial and address; hidden-window traffic is zero; opening the flyout refreshes stale values without a visible delay.

**Out of scope:** Any additional device telemetry (storage, CPU, screen state), charging state, battery history, graphs.

---

## Phase 8 — Notifications

**Research:** short note validating tray balloon presentation on the current Windows 11 build — banner appearance, app naming, Action Center persistence, Do-Not-Disturb behaviour (V-5); record the decision to keep balloons or swap the sink.

**Scope:** `NotificationPolicy`, `NotifierState`, `BalloonNotificationSink`, suppression/debounce/dedup/burst rules.

**Files/subsystems:** `Notifications/*`, `NotifierState` inside `AppState`, `Host/TrayIcon` balloon delivery.

**Tests:** Timeline-driven policy tests with a virtual clock for every rule in `ENGINEERING.md §8.2`: startup baseline silence, 5 s launch suppression, 2.5 s/1.5 s holds, 60 s dedup, 3-per-10 s burst collapse, resume suppression and re-baseline, pairing-result exemption, global failure once per outage, no notification for metadata/`AdbRestoring`/wireless degradation/special modes. Vocabulary parity test: dynamic notification text and dynamic flyout text for the same condition come from the same `Vocabulary` members.

**Acceptance:** Launching with three connected devices produces zero notifications; plugging in a device produces exactly one; jiggling a cable for ten seconds produces at most one; killing ADB for a minute produces exactly one; resume with devices attached produces none; pairing results always notify. Every string matches the flyout's wording.

**Out of scope:** Actionable/interactive notifications, notification settings, sounds, Action Center management, Windows App SDK migration (unless V-5 forces it, in which case it is a scoped change with its own research note).

---

## Phase 9 — Reliability hardening

**Research:** short note recording exactly how each failure is injected (fake-server scripts, Explorer restart, WebView process kill, network toggle, suspend/resume) and what "recovered" means for each.

**Scope:** Deliberate exercise and repair of every row in `ENGINEERING.md §11`; removal of any incidental timer or blanket retry that crept in.

**Files/subsystems:** `tests/.../Integration/Chaos*`, small fixes across `Adb/`, `Host/`, `State/`; no new subsystems.

**Tests:** Automated chaos suite for rows 1, 3, 5, 8, 11, 16, 17, 19, 24, 26 (fake server + real Explorer restart + forced WebView process failure + event-channel saturation). Long-run soak: 12 hours with periodic scripted device churn, asserting no handle/socket/memory growth beyond a fixed budget and no unhandled exception in the log. Audit tests: no `catch {}`; no `Task.Delay` loop outside `Backoff` and the sanctioned ticks; no `lock` in `State/`.

**Acceptance:** Every §11 row demonstrated (automated or with a recorded manual procedure), a clean soak run, and a log review showing that every failure produced a single explanatory line rather than a repeated storm.

**Out of scope:** New features, new UI states, performance micro-optimisation beyond the stated budget.

---

## Phase 10 — Packaging

**Research:** note on single-file self-contained publish with the WebView2 loader on clean x64/arm64 Windows 11, Evergreen runtime detection and the official bootstrapper, Inno Setup per-user install patterns and `HKCU\...\Run` handling (V-6).

**Scope:** Reproducible builds, installer, portable zip, fresh-machine verification.

**Files/subsystems:** `scripts/{build,package}.ps1`, `installer/AdbView.iss`, `docs/RELEASE.md`, `.github/workflows/release.yml`.

**Tests:** Build-fails-without-`wwwroot` test; no-absolute-URL asset check; installer smoke script (silent install → binary present → `Run` value only if selected → uninstall leaves no files, no registry value, no WebView2 profile).

**Acceptance:** On a fresh Windows 11 VM with no .NET, no Node, and platform-tools absent: install without admin → launch → tray icon appears → flyout shows `ADB unavailable` with the install hint → install platform-tools → app self-heals with no restart → connect a device → pair wirelessly → reboot with "start at sign-in" enabled and the app returns → uninstall is clean. Repeated for arm64.

**Out of scope:** Code signing purchase, Store submission, auto-update, MSIX.

---

## Phase 11 — Release hardening and v1.0.0

**Research:** final refresh of all `research/` notes with the versions actually shipped (Windows build, WebView2 runtime, platform-tools, device OS builds) so the release is reproducible in a year.

**Scope:** Real-device matrix, docs, performance verification, log review, tagging.

**Files/subsystems:** `docs/{DEVICE-TEST-MATRIX,TROUBLESHOOTING,RELEASE}.md`, `README.md`, `CHANGELOG.md`.

**Tests:** Full `ENGINEERING.md §16.6` matrix signed off on real hardware (≥ 1 device on Android ≤ 16, ≥ 1 on Android 17), all at 100 % and 200 % scaling with a mixed-DPI setup; the DPI/display matrix from `DESIGN.md §35`; performance budget verified (idle CPU ≈ 0 % over 10 minutes, zero hidden-window device traffic, flyout visible < 120 ms, state change visible < 250 ms, working set < 120 MB).

**Acceptance:** `DESIGN.md §38` acceptance criteria demonstrably satisfied; matrix signed; no unexplained visual diffs; a 24-hour pinned-mode run with no drift, no leak, and no spurious notification; v1.0.0 tagged with installer and zip artifacts plus SHA-256 hashes.

**Out of scope:** Everything the product boundary excludes, and every idea parked during Phases 0–10 (each already recorded in `docs/decisions/` for a future version).

---

## Completion record

| Phase | Completed (date) | Commit  | Notes                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| ----- | ---------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 0     | 2026-09-16       | d7c5cd6 | Implemented and verified locally: `scripts/verify.ps1` green (clean clone 105 s; warm ≈25–31 s); all 10 boundary invariants plus format/build/lint/typecheck/vitest/prettier/toolchain-pin gates deliberately violated once and observed failing (17 break tests). CI green on `d7c5cd6` (run #3, 35038876484) after two checkout-line-ending fixes: `e939c66` (DESIGN.md authority hash over LF-normalized content) and `d7c5cd6` (`.gitattributes` `eol=lf` + explicit prettier `endOfLine: "lf"`) — findings recorded in `research/2026-09-16-toolchain-and-gates.md` |
| 1     | 2026-09-16       | 6b730db | Manual headed validation passed. verify.ps1 GREEN. CI GREEN. No explicit environmental items remain unverified.                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| 2     | 2026-09-17       | aaf74e7 | Complete frozen UI implemented and verified locally across 16 fixtures; scripts/verify.ps1 green (27 C# tests, ESLint, Prettier, tsc, 3 Vitest, 16 Playwright visual tests, Release smoke); scripts/audit-phase2.ps1 green (417/417 checks passed across all fixtures and user interactions); 11 mutation proofs verified; native Acrylic backdrop, shell placement, and drag/pin interactions accepted.                                                                                                                                                                 |
| 3     | 2026-09-18       | 24cc717 | Generated typed C#/TypeScript bridge contract; runtime snapshot validation; monotonic revision/latest-wins semantics; real WebView2 typed transport; native-authoritative pin/dismiss/resize state; native FixtureSnapshotProvider for all 16 canonical fixtures; production UI driven by AppSnapshot; deterministic fixture ages; recovery/resend behavior; scripts/verify.ps1 green (91 C# tests, ESLint, Prettier, tsc, 36 Vitest, 16 visual fixtures, Release smoke); scripts/test-wave2.ps1 green (13/13); scripts/audit-phase2.ps1 green (417/417).                |
| 4     | 2026-09-18       | d844986 | Phase 4 complete (Waves 1–3 + StateEngine composition + Phase 4D closure): ADB framing/host connection, FakeAdbServer, capabilities/proto reader, executable discovery, server supervisor + backoff, StateEngine/StateReducer/Vocabulary for AdbServerStateChanged; reducer purity, revision exhaustion fail-closed, graceful FIFO drain, tray tooltip vocabulary (`No ADB devices` / `ADB unavailable`), hermetic composition tests. Phase 4D audit issues closed; `scripts/verify.ps1` green on CI.                                                                                                                                                                                                                                                                                                                                 |
| 5     | 2026-09-19       | a64ae7e | Phase 5 complete (Waves 1–3 + 5D closure): continuous `track-devices` (proto-binary preferred, text-l fallback), `DeviceIdentity` normalization, complete-list replacement with out-of-order protection, StateSince/ConnectedSince, 2 s tracker-failure retention, live `AppSnapshot.Devices` + DeviceCountLabel + tray aggregation/tooltip, fixture isolation, headed Samsung A06 USB proof (`SM_A065F` / Connected / USB / `usb:R8VXA02SNKH`). Waves: `2a62887` / `c37402c` / `8841cee`. Physical USB disconnect/reconnect not live-observed (hermetically covered). Next: Phase 6 wireless discovery/pairing. |
| 6     |                  |         |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| 7     |                  |         |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| 8     |                  |         |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| 9     |                  |         |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| 10    |                  |         |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| 11    |                  |         |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
