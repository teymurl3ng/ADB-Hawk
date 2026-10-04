# ADB View — Engineering Architecture (v1)

**Status:** Proposed for implementation · **Date:** 2026-09-16
**Authority:** `DESIGN.md` is the frozen product/visual authority. This document is the frozen *engineering* authority. Where this document and `DESIGN.md` disagree about presentation, `DESIGN.md` wins. Where `DESIGN.md` is silent about mechanism, this document decides.
**Companions:** `AGENTS.md` (enforceable agent rules), `PHASES.md` (implementation plan), `research/` (dated external-dependency findings).

Research basis for every version and API claim below: `research/2026-09-16-dotnet-webview2-frontend-stack.md`, `research/2026-09-16-adb-protocol-and-wireless.md`, `research/2026-09-16-windows-host-integration.md`.

---

## 1. Executive Architecture

One process. One C# project. One frontend. One state loop. Three long-lived sockets.

```
Single .NET 10 process (net10.0-windows, WinForms message loop, no WinForms UI controls)
├── Windows host layer      — tray icon, borderless flyout window, DWM acrylic, placement, DPI,
│                             pinning, drag, light dismiss, single instance, power/session events,
│                             WebView2 lifecycle + recovery, shell balloon notifications
├── State engine            — single-threaded serialized reducer; owns AppState; publishes immutable
│                             AppSnapshot with a monotonic revision; sole source of semantics/vocabulary
├── ADB layer               — in-house ADB *host protocol* client over TCP 127.0.0.1:5037:
│                             server supervisor, device tracker (track-devices-proto-binary),
│                             mDNS tracker (streaming service discovery), pairing, metadata probes
└── WebView2 frontend       — React 19 + TypeScript + Vite 8 + Tailwind 4, fully local, renders the
                              latest AppSnapshot and nothing else
```

**The ten decisions that define this architecture:**

1. **.NET 10 LTS, single executable project.** WinForms is used *only* as a message pump, a `NotifyIcon`, and a borderless `Form` that hosts WebView2. Zero WinForms controls render product UI. `Microsoft.Windows.CsWin32` generates the handful of Win32 calls needed (DWM, tray rect, window placement) at build time with no shipped assembly.
2. **No third-party ADB library.** ADB View speaks the ADB host protocol directly (4-hex length framing over `127.0.0.1:5037`). This is ~400 lines, fully fakeable in tests, and is the *only* way to reach the modern event-driven services (`host:track-devices-proto-binary`, streaming mDNS tracking, `server-status`) that the design requires. `adb.exe` is launched only to *start the server* and as a documented fallback for pairing/mDNS/status — never to read device state, never with user-supplied argv.
3. **Transport comes from ADB, not from string shapes.** `track-devices-proto-binary` reports `connection_type` (USB / SOCKET) per device. Serial-string heuristics exist only as a fallback for pre-37 platform-tools and are isolated in one documented function.
4. **Pairing discovery is modeled independently of devices.** mDNS `_adb-tls-pairing._tcp` services become *pairing offers*. `_adb-tls-connect._tcp` services are metadata enrichment (address, model, friendly name), never cards. A successful pair produces `PairedWaitingForDevice`, which only the device tracker can clear.
5. **One authoritative snapshot, single writer.** Every external event (socket message, host event, probe result, timer) is enqueued to one `Channel<AdbViewEvent>` consumed by one loop. Nothing else mutates state. Snapshots are immutable records with `Revision`; the newest always wins on both sides of the bridge.
6. **Semantic wording has one owner.** `Vocabulary` owns dynamic semantic/state wording that must stay consistent across the flyout, tray tooltip, and notifications: state labels, guidance, global/subsystem health, pairing status, tray tooltips, and notification text. The frontend may contain frozen static interface copy from `DESIGN.md` (for example `ADB View`, `No ADB devices`, field labels, button labels, and inline `Copied` feedback), but it never maps backend enums to semantic text. Dynamic meaning cannot drift across surfaces.
7. **Native window, native drag, v1 opaque surface.** The flyout keeps `WS_CAPTION` while collapsing its non-client area via `WM_NCCALCSIZE`, plus `WM_NCACTIVATE(TRUE)` for pinned focus behavior. **v1 does not use live Desktop Acrylic** (`DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW`) under WebView2: measured diagnosis showed multi-second stale presentation with a transparent WebView child, and composition hosting is unavailable on the frozen WinForms stack (`docs/decisions/0004-…`). v1 always paints the intentional opaque dark/light surfaces (`DESIGN.md` §22), with matching WebView2 `DefaultBackgroundColor` and frontend `data-opaque`. Rounded corners (`DWMWCP_ROUNDSMALL`) remain. No CSS `backdrop-filter` in production. Live Acrylic is deferred architecture work.
8. **Notifications are derived, debounced, and vocabulary-shared.** A pure function over (previous snapshot, next snapshot, notifier state, clock) produces notification intents; a policy layer applies startup suppression, flap debounce, and per-device/kind cooldowns. v1 delivers them through the tray icon's own shell balloon channel — zero added dependency, and the tooltip remains a second channel that survives WebView failure.
9. **Self-healing by explicit supervision, never by blanket timers.** Each of the three long-lived streams (device tracker, mDNS tracker, server probe) has its own owner, its own failure detection, and its own jittered exponential backoff. A dead mDNS tracker degrades exactly one local warning card; it never turns the app red.
10. **v1 ships no updater, no telemetry, and no settings screen.** Pin resets to unpinned each launch (deterministic without storage). A minimal typed preference store under `HKCU\Software\ADBView` holds NotificationsEnabled, Theme and StatusBubbleEnabled only. Start-with-Windows is the shared `HKCU\...\Run` value `ADB View` (installer checkbox and tray toggle; quoted path + `--startup`).

**Deliberately excluded** (each was considered and rejected; see §15): AdvancedSharpAdbClient, Google.Protobuf, Windows App SDK, a DI container, Redux/Zustand, a router, an animation library, an icon package, a tray-icon library, a mocking library, a second process, an IPC service, SQLite/history, auto-update, telemetry, CSS acrylic.

---

## 2. Technology Decision Record

Every entry: choice · status as of 2026-09-16 · alternatives · reason · risk.

### 2.1 Runtime and language

| | |
|---|---|
| **Choice** | .NET 10 (`net10.0-windows`), C# 14, `WindowsDesktop` SDK with `UseWindowsForms=true` |
| **Status** | .NET 10 is the current LTS (released Nov 2025), latest patch **10.0.12** (2026-09-08), supported through **14 Nov 2028**. .NET 8 and .NET 9 both reach end of support **10 Nov 2026** — before this app would finish shipping. .NET 11 is in preview, STS, releases Nov 2026. |
| **Alternatives** | .NET 8 LTS (dies in two months); .NET 11 preview (unsupported, STS = 2-year window); C++/Win32 (no benefit here, worse agent maintainability); Rust + webview (ecosystem mismatch with the required C# host). |
| **Reason** | Longest support runway, no migration debt, LTS discipline matches a background utility that must keep working untouched for years. |
| **Risk** | Low. Pin the SDK with `global.json` (`rollForward: latestFeature`) so agents cannot silently jump to .NET 11 previews. |

### 2.2 Windows host shell

| | |
|---|---|
| **Choice** | WinForms as *host plumbing only*: `ApplicationConfiguration` + `NotifyIcon` + one borderless `Form` (`FlyoutWindow`) whose entire client area is a WebView2. All product UI is web. |
| **Status** | Supported for the life of .NET 10. WinForms `NotifyIcon` handles the `TaskbarCreated` broadcast internally (`WmTaskbarCreated`), which gives Explorer-restart recovery for free — a requirement we would otherwise hand-roll. |
| **Alternatives** | **Raw Win32 + CsWin32 only:** ~600 extra lines re-implementing message loop, tray registration, Explorer-restart handling, DPI plumbing — more surface, no gain. **WinUI 3 / Windows App SDK:** large dependency, runtime bootstrapper or ~40 MB self-contained payload, awkward for a frameless tray flyout, and the design is explicitly not WinUI. **WPF:** heavier than needed and buys nothing once all UI is web. **Electron:** forbidden by the brief and wrong for a tray utility. |
| **Reason** | Smallest reliable host that Windows already knows how to keep alive. WinForms here is infrastructure, not a UI framework. |
| **Risk** | Agents may be tempted to "just add a WinForms control." `AGENTS.md` R-04 forbids it; an analyzer rule and a source-boundary test enforce it. |

### 2.3 WebView2

| | |
|---|---|
| **Choice** | `Microsoft.Web.WebView2` **1.0.4191.47** (released 2026-08-28), Evergreen runtime, `WebView2` WinForms control (not the composition control). |
| **Status** | Actively released on a ~4-week SDK cadence; runtime follows Edge stable (2-week cadence). |
| **Alternatives** | Fixed-version runtime (+180 MB, manual CVE patching — rejected); `WebView2CompositionControl` (needed only to layer native controls over web content, which we never do); CEF/Chromium embed (enormous). |
| **Reason** | Native, evergreen, already present on Windows 11; transparent background support; virtual-host mapping for offline assets; process-failure events we can supervise. |
| **Risk** | Evergreen runtime updates can change rendering. Mitigation: visual-regression baselines are regenerated deliberately (never auto-accepted) and the runtime version is recorded in every log header and visual-test run. |

### 2.4 Win32 interop

| | |
|---|---|
| **Choice** | `Microsoft.Windows.CsWin32` **0.3.333** (2026-09-03), `PrivateAssets=all` — build-time source generator, nothing shipped. |
| **Alternatives** | Hand-written `DllImport` (error-prone struct/marshalling bugs in exactly the places that matter: `NOTIFYICONIDENTIFIER`, `APPBARDATA`, `DWMWINDOWATTRIBUTE`); PInvoke.Windows.* prebuilt assemblies (extra runtime assemblies). |
| **Reason** | Correct signatures generated from official Win32 metadata, zero runtime footprint, allow-list file (`NativeMethods.txt`) doubles as an auditable inventory of every native call the app can make. |
| **Risk** | Still 0.x versioning. Pinned exactly; used only for well-established APIs. |

### 2.5 ADB access

| | |
|---|---|
| **Choice** | In-house `AdbHostClient` speaking the documented ADB host protocol over `127.0.0.1:5037`, plus a minimal forward-compatible protobuf field reader for the `-proto-binary` services. `adb.exe` used only for `start-server` and documented fallbacks. |
| **Status** | Protocol documented in AOSP `docs/dev/services.md` / `SERVICES.TXT`; platform-tools **37.0.1** (July 2026) is current. It removed the deprecated Openscreen mDNS backend entirely: `libadbmdns` is now the only backend and `ADB_MDNS_OPENSCREEN` has no effect. `host:track-devices-proto-binary`, `host:track-devices-proto-text`, streaming mDNS service tracking (`adb mdns track-services --proto-binary`), and `server-status` (`AdbServerStatus` in `adb_host.proto`) exist in current tooling. |
| **Alternatives** | **AdvancedSharpAdbClient 3.6.16:** actively published **2026-01-13** and compatible with .NET 10. It remains a viable general-purpose ADB client, but the package's documented high-level API does not provide the exact modern surfaces this design is built around — structured proto device tracking with `connection_type`, streaming mDNS service tracking, and `server-status` — so ADB View would still need custom host-protocol code for its critical path. Using both would increase surface area without removing that work. **Shelling `adb.exe devices -l` on a timer:** polling plus fragile text parsing — forbidden by the brief and by this architecture. **Reimplementing the device-side ADB protocol (bypassing the server):** would fight every other tool on the machine for the USB device; unacceptable. |
| **Reason** | The protocol is small, stable, event-driven, trivially fakeable in tests, and is the only route to the modern signals (`connection_type`, Wi-Fi 2.0 mDNS metadata) the design depends on. |
| **Risk** | Exact host-service strings and proto field numbers must be confirmed against installed platform-tools (V-1, V-9). Mitigation: capability probe at startup with text-proto cross-check tests and a documented text fallback path. |

### 2.6 Platform-tools dependency strategy

| | |
|---|---|
| **Choice** | **Depend on the user's existing platform-tools. Never bundle, never download, never auto-install, never `kill-server`.** Discovery order: `ANDROID_SDK_ROOT/platform-tools` → `ANDROID_HOME/platform-tools` → `PATH` → `%LOCALAPPDATA%\Android\Sdk\platform-tools` → `%ProgramFiles%\Android\android-sdk\platform-tools`. |
| **Alternatives** | Bundling adb.exe (licensing and version-skew hazard: two adb versions fighting over port 5037 is the single most common ADB failure mode); downloading on first run (network dependency, installer complexity, trust problem). |
| **Reason** | ADB is a machine-global singleton. A status utility must be a good citizen of whatever adb the developer already uses. If adb is absent, `DESIGN.md §18` already specifies exactly what to say. |
| **Risk** | Very old platform-tools lack proto services → capability probe degrades to `host:track-devices-l` text parsing and to one-shot mDNS listing, and logs the degradation once. |

### 2.7 Frontend stack

| | |
|---|---|
| **Choice** | React **19**, TypeScript **5.x**, Vite **8** (stable since 2026-03-12; requires Node 20.19+/22.12+), Tailwind CSS **4.3.x** with `@tailwindcss/vite` (Vite 8 support since 4.2.2). Runtime npm dependencies: `react`, `react-dom`. Nothing else. |
| **Alternatives** | **Preact/Lit/vanilla TS:** smaller, but React is what coding agents are most reliable in, and bundle size is irrelevant for local assets. **Tailwind CDN:** forbidden (`DESIGN.md §36`). **CSS-in-JS:** runtime cost, worse forced-colors story. **Framer Motion:** an entire dependency to do what four CSS duration tokens already do, and it encourages animation queues that violate "latest state wins". **lucide-react:** unnecessary — the approved icon paths are vendored as tiny SVG components. |
| **Reason** | Boring, current, offline, and the smallest surface that renders a frozen design from a single data object. |
| **Risk** | Tailwind has no LTS; pin exact versions in `package.json` + `package-lock.json` and treat upgrades as a visual-regression event. |

### 2.8 Notifications

| | |
|---|---|
| **Choice** | v1: shell balloon notifications via the app's own tray icon (`NotifyIcon.ShowBalloonTip`, i.e. `Shell_NotifyIcon` + `NIIF_*`), behind an `INotificationSink` interface. |
| **Status** | Microsoft's current recommendation for *new* notification work is Windows App SDK `AppNotificationManager` (works unpackaged, `Register()` handles AUMID/COM). `Microsoft.Toolkit.Uwp.Notifications` is archived; bare WinRT `ToastNotificationManager` is maintenance-only. |
| **Alternatives** | **Windows App SDK `AppNotificationManager`:** the "official" path, but pulls the entire Windows App SDK into a 5 MB utility, needs either a runtime bootstrapper or a self-contained payload, and buys features ADB View's design explicitly forbids (action buttons, rich interactivity). **Toolkit package:** archived — excluded on maintenance grounds alone. |
| **Reason** | `DESIGN.md §32` requires plain informational messages using flyout vocabulary, with no actions. Balloons deliver exactly that with zero dependencies, and they reuse the tray icon we already own. |
| **Risk** | Less control over presentation and grouping; suppressed by Do Not Disturb. Validation item V-5: if balloon presentation fails the quality bar on current Windows 11, the `INotificationSink` seam allows swapping in Windows App SDK notifications as an isolated, reviewable change — no other subsystem moves. |

### 2.9 Startup at login / single instance / power

| | |
|---|---|
| **Choice** | Startup: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value name `ADB View`, command `"<exe>" --startup` — written by the installer optional task **and** by the tray **Start with Windows** toggle (`StartupRegistration`). Single instance: named mutex `Local\AdbView.SingleInstance` for ownership + named pipe `AdbView.Control.<sessionId>` for "toggle the existing instance". Power/session: `SystemEvents.PowerModeChanged` / `SessionSwitch` / `DisplaySettingsChanged`. Theme: `SystemTheme` + optional `ThemePreference` (System/Light/Dark) → effective `ThemeKind` in the shell snapshot. |
| **Alternatives** | Task Scheduler startup (admin, opaque); MSIX `StartupTask` (requires packaging identity); `WM_COPYDATA` broadcast for single instance (`HWND_BROADCAST` noise, fragile); `Mutex`-only (second launch does nothing visible — bad UX). |
| **Reason** | Each is the mechanism Windows already owns, with no persisted app state and no admin rights. |
| **Risk** | Low. Pipe path is per-session so Fast User Switching cannot cross-talk. |

### 2.10 Logging

| | |
|---|---|
| **Choice** | Serilog 4.x + `Serilog.Sinks.File`, rolling daily, 4 MB per file, 7 files retained, at `%LOCALAPPDATA%\ADBView\logs\adbview-.log`. Default level Information; `--verbose` enables Debug. |
| **Alternatives** | Hand-rolled writer (loses buffered lines on crash, no rolling, reinvents rotation); `Microsoft.Extensions.Logging` + custom sink (same work, more ceremony); ETW (unreadable for the user filing a bug); Seq/OpenTelemetry (telemetry platform — forbidden). |
| **Reason** | Two small, stable packages for the one diagnostic capability this product genuinely needs: explaining an ADB reliability problem after the fact. |
| **Risk** | None material. Redaction rules are enforced by a unit test over the log-message helpers (§13). |

### 2.11 Testing

| | |
|---|---|
| **Choice** | C#: xUnit v3 **4.0.0** (2026-08-14, Microsoft Testing Platform v2), plain `Assert`, hand-written fakes, one test project. Frontend: Vitest + `@testing-library/react`. Visual: Playwright (JS) **1.6x** `toHaveScreenshot` at 100/125/150/175/200 %. Integration: same C# project, `[Trait("Category","Integration")]`, headed-session-gated. |
| **Alternatives** | NUnit/MSTest (no advantage); Moq/NSubstitute (a fake `IAdbHostConnection` is 30 lines and clearer); FluentAssertions (v8 licensing change — avoid); WinAppDriver (effectively abandoned); FlaUI (only if native UI automation becomes necessary — it does not, because the UI is web). |
| **Reason** | Deterministic, no mocking DSL for agents to misuse, and one project keeps the repo legible. |
| **Risk** | Visual baselines drift with WebView2/Chromium updates; baseline updates require an explicit, reviewed command (§16). |

### 2.12 Packaging and release

| | |
|---|---|
| **Choice** | Self-contained, `win-x64` and `win-arm64`, single-file, ReadyToRun, **no trimming** (unsupported for WinForms). Installer: Inno Setup 6, per-user install to `%LOCALAPPDATA%\Programs\ADBView` (no admin), Evergreen WebView2 runtime check, optional "Start ADB View when I sign in". **No auto-update in v1.** |
| **Alternatives** | Framework-dependent (user must install .NET 10 — unacceptable for a utility); MSIX (identity and store friction; complicates writing `Run` key and per-user placement); WiX v5/v6 (more capable, more ceremony, admin-oriented); Velopack/Squirrel (an update *system* — explicitly out of scope for v1); portable zip (secondary artifact, fine). |
| **Reason** | Double-click, no admin, no prerequisites, no update server to operate. Versioned zip is also published for portable use. |
| **Risk** | Single-file + native WebView2 loader must be validated (V-6); `IncludeNativeLibrariesForSelfExtract=true` is the fallback. Unsigned binaries trigger SmartScreen — documented in `docs/RELEASE.md`; code signing is a business decision, not an architectural one. |

---

## 3. Complete Runtime Architecture

### 3.1 Ownership boundaries

| Subsystem | Owns | Must never |
|---|---|---|
| `Program` / `AppHost` | Process lifetime, composition root, single instance, crash logging | Contain product logic |
| `AdbServerSupervisor` | Liveness of the local ADB server, `start-server` attempts, backoff | Kill the ADB server; decide UI text |
| `DeviceTracker` | One persistent `track-devices` stream, parsing, reconnection | Interpret health; touch the UI |
| `MdnsTracker` | One persistent mDNS service stream, offer/connect-service extraction | Turn its own failure into a global error |
| `PairingCoordinator` | Pairing state machine, code handling, timeouts | Claim a device is connected |
| `MetadataProbe` | Battery / OS version queries with visibility-aware policy | Run while the flyout is hidden (except the pinned low-frequency tick) |
| `StateEngine` | The only mutable `AppState`; snapshot construction; `Vocabulary` | Perform I/O, block, or call Windows APIs |
| `NotificationPolicy` | Transition detection, debounce, dedup, suppression | Read raw ADB events |
| `TrayIcon` | Icon state, tooltip, context menu, balloon delivery | Know ADB protocol details |
| `FlyoutWindow` | Window lifecycle, placement, backdrop, pin, drag, dismiss | Manipulate DOM or hold product state |
| `StatusBubbleWindow` | Bubble window lifecycle, non-activation, placement, transient expiry | Hold product state; duplicate the flyout's detail |
| `WebViewHost` | WebView2 creation, hardening, recovery, message transport | Parse ADB output; compute health |
| `Frontend` | Rendering the latest snapshot, local interaction state, motion | Execute adb, map enums to text, compute health, or recover anything |

### 3.2 Data-flow diagram

```
                       ┌──────────────────────────────── Windows ────────────────────────────────┐
                       │  Explorer/tray   DWM    Power/Session   Display/DPI   Shell balloons    │
                       └───┬──────────────┬───────────┬────────────────┬─────────────┬───────────┘
                           │              │           │                │             ▲
                           ▼              ▼           ▼                ▼             │
  ┌────────────────────────────────────────────────────────────────────────────────────────────┐
  │  HOST LAYER (UI thread, WinForms message loop)                                            │
  │  TrayIcon ◄── icon+tooltip ──┐        FlyoutWindow ── placement/backdrop/pin/drag         │
  │      │ left-click / Exit     │              │ show/hide, size                             │
  │      ▼                       │              ▼                                             │
  │  HostEvents ─────────────────┼──────► WebViewHost ──postWebMessage──► [ WebView2 ]        │
  │  (pin, theme, dpi, resume,   │              ▲                              │              │
  │   visibility, dismiss)       │              └────── web actions ───────────┘              │
  └───────────┬──────────────────┴──────────────────────────────────▲─────────────────────────┘
              │ enqueue                                             │ latest snapshot only
              ▼                                                     │ (coalesced, revision-guarded)
  ┌──────────────────────────────────────────────────────────────────┴─────────────────────────┐
  │  STATE ENGINE  (one dedicated loop; single writer; no I/O)                                 │
  │                                                                                            │
  │   Channel<AdbViewEvent> ──► Reduce(AppState, event) ──► AppState' ──► BuildSnapshot()      │
  │                                   │                                        │               │
  │                                   ├──► HealthAggregator (global vs local)   │               │
  │                                   ├──► Vocabulary (dynamic semantic strings)│               │
  │                                   ├──► TrayStatusAggregator (priority)      │               │
  │                                   └──► NotificationPolicy(prev, next) ──► NotificationSink │
  └───────────┬────────────────────────────────────────────────────────────────────────────────┘
              │ enqueue (thread-safe, from any subsystem)
  ┌───────────┴────────────────────────────────────────────────────────────────────────────────┐
  │  ADB LAYER (async, own tasks, own backoff)                                                 │
  │                                                                                            │
  │  AdbServerSupervisor ──probe host:version──┐                                               │
  │        │ start-server (adb.exe, fixed argv)│                                               │
  │        ▼                                   ▼                                               │
  │  DeviceTracker  ── host:track-devices-proto-binary ──►  stream of device lists             │
  │  MdnsTracker    ── streaming mDNS service tracking  ──►  stream of service lists            │
  │  PairingCoordinator ── host:pair:<code>:<host>:<port> (fallback: adb.exe pair, code stdin) │
  │  MetadataProbe  ── host:transport:<serial> + shell:… ──►  battery / naming / android version │
  └───────────┬────────────────────────────────────────────────────────────────────────────────┘
              │ TCP 127.0.0.1:5037 (length-prefixed, bounded reads)
              ▼
        ADB server (user's platform-tools) ──► USB / Wi-Fi transports ──► Android devices
```

### 3.3 Threading model

- **UI thread:** WinForms message loop; tray, window, WebView2, `SystemEvents`. Never blocks on ADB.
- **State thread:** one long-running task draining the event channel. All reduction is synchronous and side-effect-free apart from emitting snapshots and notification intents.
- **ADB tasks:** async socket readers and short-lived probes. They only *enqueue* events.
- **Publication:** `StateEngine` stores the snapshot in a volatile field and posts a marshalled callback to the UI thread. If a newer revision exists when the callback runs, the older post is dropped (native-side "latest wins").
- **Rule:** no `lock` anywhere in `State/`. If an agent needs a lock there, the design has been violated.

---

## 4. AppSnapshot Contract

One object describes everything the UI can show. It is built fresh per revision, is deeply immutable, and contains no ADB jargon in any string destined for display.

### 4.1 C# (System.Text.Json source-generated, camelCase)

```csharp
namespace AdbView.State;

public sealed record AppSnapshot
{
    public required int Protocol { get; init; }              // == BridgeProtocol.Version (1)
    public required long Revision { get; init; }             // monotonic, never reused
    public required DateTimeOffset GeneratedAt { get; init; }// UTC; frontend clock-skew safe
    public required HealthView Health { get; init; }
    public required ShellView Shell { get; init; }
    public required ImmutableArray<DeviceView> Devices { get; init; }      // display order = stable
    public required ImmutableArray<PairingOfferView> PairingOffers { get; init; }
    public PairingOperationView? Pairing { get; init; }                    // at most one at a time
    public required ImmutableArray<SubsystemWarningView> Warnings { get; init; }
    public required string? DeviceCountLabel { get; init; }   // "3 devices" or null (<2 devices)
}

// ---------- global health ----------
public enum GlobalHealthKind { Healthy, AdbUnavailable, AdbRestoring }

public sealed record HealthView
{
    public required GlobalHealthKind Kind { get; init; }
    public required string? Title { get; init; }       // "ADB unavailable" | "Restoring ADB…" | null
    public required string? Detail { get; init; }      // "ADB View couldn't find or start ADB."
    public required string? Hint { get; init; }        // "Install Android Platform Tools or make adb.exe available to Windows."
    public required TrayStatusKind Tray { get; init; }
    public required string TrayTooltip { get; init; }  // "Connected: Galaxy S24 (Wi-Fi)"
}

public enum TrayStatusKind { Idle, Connected, Attention, Failure, Special, PairingAvailable }

// ---------- shell / environment ----------
public enum ThemeKind { Dark, Light }

public sealed record ShellView
{
    public required bool Pinned { get; init; }
    public required ThemeKind Theme { get; init; }
    public required bool TransparencyEnabled { get; init; }  // false => opaque token set
    public required bool ForcedColors { get; init; }         // Windows contrast theme active
    public required bool ReducedMotion { get; init; }
}

`376` effective px width and the `560` effective px maximum height are frozen design/window constants, not live application state. They are owned by the frontend/native shell implementation and therefore do not travel in every `AppSnapshot`.

// ---------- devices ----------
public enum DeviceStateKind
{
    Connected, Unauthorized, Offline, Recovery, Bootloader, Sideload,
    Rescue, Authorizing, Connecting, Detached, Unknown
}

public enum SeverityKind { Positive, Attention, Failure, Informational, Neutral }
public enum TransportKind { Usb, WiFi, Unknown }

public sealed record DeviceView
{
    public required string Id { get; init; }            // stable internal id (§6.7)
    public required string DisplayName { get; init; }    // "Galaxy S24"
    public required DeviceStateKind State { get; init; } // for icon/marker selection only
    public required SeverityKind Severity { get; init; }
    public required string StateLabel { get; init; }     // "Connected" | "Authorization required"
    public required string StatusLine { get; init; }     // "Connected · Wi-Fi"
    public string? Guidance { get; init; }               // "Check the phone and allow USB debugging."
    public required TransportKind Transport { get; init; }
    public required string TransportLabel { get; init; } // "USB" | "Wi-Fi"
    public int? BatteryPercent { get; init; }            // null => omit battery entirely
    public required string MetaTemplate { get; init; }    // "Connected {age} ago" (frontend fills {age})
    public required DateTimeOffset StateSince { get; init; }
    public DateTimeOffset? ConnectedSince { get; init; }     // only when a meaningful connected instant exists
    public required DeviceDetailView Detail { get; init; } // expanded card payload
}

public sealed record DeviceDetailView
{
    /// <summary>
    /// Hardware/display serial only. Null when unknown or when the only known
    /// identifier is an ADB host:port transport selector (omit the Serial row).
    /// Does not affect DeviceId or AdbSerial transport addressing.
    /// </summary>
    public string? Serial { get; init; }
    public string? ExactModel { get; init; }             // "SM-S921B"
    public string? AndroidVersion { get; init; }         // "15"
    public string? Address { get; init; }                // "192.168.1.42:37895" (Wi-Fi only)
    public required ImmutableArray<CopyFieldView> CopyFields { get; init; } // serial, address (omit unknown)
}

public sealed record CopyFieldView
{
    public required string Key { get; init; }     // "serial" | "address"
    public required string Label { get; init; }   // "Serial"
    public required string Value { get; init; }
}

// ---------- pairing (independent of devices) ----------
public sealed record PairingOfferView
{
    public required string Id { get; init; }          // stable per mDNS instance
    public required string DisplayName { get; init; } // given_name ?? model ?? "Android device"
    public required string Heading { get; init; }     // "Wireless pairing available"
    public required string Detail { get; init; }      // "Waiting for a pairing code"
    public required string ActionLabel { get; init; } // "Pair"
}

public enum PairingPhase { AwaitingCode, Pairing, PairedWaitingForDevice, Failed }

public sealed record PairingOperationView
{
    public required string OfferId { get; init; }
    public required string DisplayName { get; init; }
    public required PairingPhase Phase { get; init; }
    public required string Title { get; init; }   // "Pairing with Galaxy S24…" | "Paired with Galaxy S24"
    public string? Detail { get; init; }          // "Enter the six-digit code shown on the phone."
                                                  // "Keep the phone nearby while pairing completes."
                                                  // "Waiting for ADB connection…" | "Check the code and try again."
    public required bool CanSubmitCode { get; init; }
    public required bool CanCancel { get; init; }
    public required bool CanRetry { get; init; }
    public required int CodeLength { get; init; } // 6
}

// ---------- local subsystem warnings (never global) ----------
public enum SubsystemKind { WirelessDiscovery }

public sealed record SubsystemWarningView
{
    public required SubsystemKind Subsystem { get; init; }
    public required SeverityKind Severity { get; init; }  // Attention or Informational
    public required string Title { get; init; }  // "Wireless discovery unavailable" | "Restoring wireless discovery…"
    public required string Detail { get; init; } // "USB ADB is still working normally."
}
```

### 4.2 TypeScript (generated, never hand-edited)

`ui/src/contract/snapshot.ts` is produced from the C# records by `scripts/gen-contract.ps1` and committed. CI fails if regeneration produces a diff — the contract cannot drift.

```ts
export const PROTOCOL_VERSION = 1;

export type GlobalHealthKind = 'healthy' | 'adbUnavailable' | 'adbRestoring';
export type TrayStatusKind = 'idle' | 'connected' | 'attention' | 'failure' | 'special' | 'pairingAvailable';
export type ThemeKind = 'dark' | 'light';
export type SeverityKind = 'positive' | 'attention' | 'failure' | 'informational' | 'neutral';
export type TransportKind = 'usb' | 'wifi' | 'unknown';
export type DeviceStateKind =
  | 'connected' | 'unauthorized' | 'offline' | 'recovery' | 'bootloader'
  | 'sideload' | 'rescue' | 'authorizing' | 'connecting' | 'detached' | 'unknown';
export type PairingPhase = 'awaitingCode' | 'pairing' | 'pairedWaitingForDevice' | 'failed';

export interface AppSnapshot {
  protocol: number;
  revision: number;
  generatedAt: string;                 // ISO-8601 UTC
  health: HealthView;
  shell: ShellView;
  devices: DeviceView[];
  pairingOffers: PairingOfferView[];
  pairing: PairingOperationView | null;
  warnings: SubsystemWarningView[];
  deviceCountLabel: string | null;
}

export interface HealthView {
  kind: GlobalHealthKind;
  title: string | null; detail: string | null; hint: string | null;
  tray: TrayStatusKind; trayTooltip: string;
}

export interface ShellView {
  pinned: boolean; theme: ThemeKind; transparencyEnabled: boolean;
  forcedColors: boolean; reducedMotion: boolean;
}

export interface DeviceView {
  id: string; displayName: string;
  state: DeviceStateKind; severity: SeverityKind;
  stateLabel: string; statusLine: string; guidance: string | null;
  transport: TransportKind; transportLabel: string;
  batteryPercent: number | null;
  metaTemplate: string;                // contains the literal "{age}"
  stateSince: string; connectedSince: string | null;
  detail: DeviceDetailView;
}

export interface DeviceDetailView {
  serial: string | null; exactModel: string | null; androidVersion: string | null;
  address: string | null; copyFields: CopyFieldView[];
}
export interface CopyFieldView { key: string; label: string; value: string }

export interface PairingOfferView {
  id: string; displayName: string; heading: string; detail: string; actionLabel: string;
}
export interface PairingOperationView {
  offerId: string; displayName: string; phase: PairingPhase;
  title: string; detail: string | null;
  canSubmitCode: boolean; canCancel: boolean; canRetry: boolean; codeLength: number;
}
export interface SubsystemWarningView {
  subsystem: 'wirelessDiscovery'; severity: SeverityKind; title: string; detail: string;
}
```

### 4.3 Coverage of every design state

| `DESIGN.md` state | Snapshot representation |
|---|---|
| Connected (USB / Wi-Fi) | `state=connected`, `severity=positive`, `transport=usb\|wifi`, `metaTemplate="Connected {age} ago"` |
| Authorization required | `state=unauthorized`, `severity=attention`, `guidance="Check the phone and allow USB debugging."` |
| Device offline | `state=offline`, `severity=failure`, `guidance="ADB can see this device, but the connection isn't responding."` |
| Recovery / Bootloader / Sideload | `state=recovery\|bootloader\|sideload`, `severity=informational` |
| Unknown state | `state=unknown`, `severity=neutral`, `stateLabel="Unknown state"` |
| Transitional ADB states (`authorizing`, `connecting`, `detached`, `rescue`) | mapped to their own kinds, all surfaced through `Vocabulary`; none leak raw enum text |
| Empty state | `devices=[]`, `health.kind=healthy`; frontend renders `EmptyState` with wireless-discovery line from `warnings` absence |
| ADB unavailable | `health.kind=adbUnavailable` + title/detail/hint; devices array empty |
| Restoring ADB | `health.kind=adbRestoring` |
| Wireless discovery unavailable / restoring | `warnings[]` entry; global health stays `healthy` |
| Pairing offer | `pairingOffers[]` |
| Pairing: awaiting code / pairing / paired-waiting / failed | `pairing.phase` |
| Multi-device | `devices[]` + `deviceCountLabel` |
| Pinned / theme / opaque / forced colors / reduced motion | `shell` |

**Invariants tested in `State` unit tests:** `pairing.phase == pairedWaitingForDevice` never implies a device entry; `health.kind != healthy` never rewrites individual device states; exactly one `pairing` at a time; `devices` order is stable across revisions (USB before Wi-Fi, then `connectedSince ?? stateSince`, then serial); all dynamic semantic/state wording originates in `Vocabulary`, while frozen static interface copy comes directly from `DESIGN.md` in the frontend.

---

## 5. Native ↔ Web Contract

Narrow by construction: **one** message type native→web, **eight** actions web→native.

### 5.1 Transport

`CoreWebView2.PostWebMessageAsJson` / `WebMessageReceived`. No host objects (`AreHostObjectsAllowed = false`), no `AddHostObjectToScript`, no `ExecuteScriptAsync` for state, no DOM manipulation from native.

### 5.2 Native → web

```json
{ "type": "snapshot", "protocol": 1, "snapshot": { ...AppSnapshot } }
```

That is the entire native→web vocabulary. Theme, pin state, transparency, forced colors, reduced motion, health, devices, pairing and warnings all travel inside the snapshot, so there is exactly one code path to test and no partial-update ordering to reason about.

Three delivery rules:
1. Sent on every revision change, coalesced to at most one post per UI-thread turn (older revisions dropped).
2. Sent immediately upon receiving `ready`, regardless of whether anything changed.
3. Never sent while the WebView is being recreated; the first thing the new instance receives is the current snapshot.

### 5.3 Web → native

```ts
type WebAction =
  | { type: 'ready';             protocol: number }
  | { type: 'pin' }
  | { type: 'unpin' }
  | { type: 'requestDismiss' }                                  // Esc while unpinned
  | { type: 'reportContentSize'; heightEffectivePx: number; phase?: 'live' }  // measured natural height; host applies an instant step whenever it differs from the applied height (decision 0012; 'structural'/'reconcile' are retired, treated as 'live')
  | { type: 'pairStart';   offerId: string }
  | { type: 'pairSubmit';  offerId: string; code: string }
  | { type: 'pairCancel';  offerId: string };
```

Not in the contract, deliberately: copy (frontend uses `navigator.clipboard`), card expansion (frontend-local presentation), exit (tray menu only, per `DESIGN.md §33`), retry/restart ADB (self-healing only, per `DESIGN.md §18`), any form of `eval`, navigation, or file access.

### 5.4 Initialization handshake

```
FlyoutWindow created (hidden)
  → WebViewHost.EnsureCoreWebView2Async(env with UserDataFolder)
  → harden settings, install navigation/resource/permission filters
  → SetVirtualHostNameToFolderMapping("app.adbview.local", wwwroot, DenyCors)
  → Navigate("https://app.adbview.local/index.html")
  → [web] window.chrome.webview.postMessage({type:'ready', protocol:1})
  → [native] validate protocol, mark web ready, post current snapshot
  → first paint contains real state; no loading spinner is part of the design
```

If `ready` does not arrive within 5 s, the host logs `bridge.ready.timeout`, reloads once, and on a second failure leaves the tray icon and tooltip as the working status channel (see §11).

### 5.5 Validation and error handling

- Deserialized with `JsonSerializerOptions { UnmappedMemberHandling = Disallow }` against source-generated contracts. Unknown `type`, unknown member, or wrong `protocol` → message dropped + `bridge.action.rejected` logged with the reason (never the payload).
- `code` must match `^[0-9]{6}$`; `offerId` must exist in the *current* snapshot; `heightEffectivePx` clamped to `[120, 560]`.
- Rate limits: ≤ 20 actions/second overall (excess dropped, logged once per second); ≤ 5 `pairSubmit` per offer per minute; `pairStart` ignored while another pairing operation is active.
- Actions are *requests*, never state. Native performs them and the effect appears only via the next snapshot. The frontend must not optimistically render pin, pairing phase, or any device change.
- Pairing codes are used and dropped: never logged, never echoed into a snapshot, never written to disk, never placed on a command line.

### 5.6 Versioning

`protocol: 1` on both sides, compared against the compiled constant. Native and web ship in the same artifact, so a mismatch means a broken build: in Debug it throws at startup; in Release it logs `bridge.protocol.mismatch`, refuses all actions, and continues pushing snapshots (read-only UI) so the user still sees truth. Adding an optional snapshot field is *not* a protocol bump; removing/renaming a field or changing action semantics is.

---

## 6. ADB Architecture

### 6.1 Why the host protocol

The ADB server exposes a trivially simple, long-stable request protocol on `127.0.0.1:5037`: send `%04x` + ASCII service name, read `OKAY`/`FAIL`, then either a length-prefixed payload (one-shot) or a sequence of length-prefixed payloads (streaming). Everything ADB View needs is a *streaming* service, which is precisely what removes polling.

```csharp
// Adb/Protocol/AdbHostConnection.cs  (shape, not final code)
interface IAdbHostConnection : IAsyncDisposable
{
    Task<AdbResponse> SendAsync(string service, CancellationToken ct);   // OKAY/FAIL + reason
    Task<ReadOnlyMemory<byte>> ReadPayloadAsync(CancellationToken ct);   // hex4-prefixed, ≤ 1 MiB
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadStreamAsync(CancellationToken ct);
}
```

Hard limits: payload length cap 1 MiB, connect timeout 2 s, one-shot request timeout 5 s, no timeout on stream idleness (silence is normal and means "nothing changed") but TCP keep-alive enabled so a dead server surfaces as a socket error.

### 6.2 Services used

| Purpose | Service | Mode | Fallback |
|---|---|---|---|
| Liveness / version | `host:version` | one-shot | — |
| Server diagnostics (adb version, mDNS enabled, mDNS backend, log path) | `host:server-status` (`AdbServerStatus` proto) | one-shot at startup and after each server restart | `adb.exe server-status` (parsed for `version` / `mdns_enabled` / `mdns_backend` only) |
| Device tracking | `host:track-devices-proto-binary` | **streaming** | `host:track-devices-l` (text) when the proto service is unavailable |
| Wireless discovery | streaming mDNS service tracking (`host:track-mdns-services`, proto) | **streaming** | child process `adb.exe mdns track-services --proto-text` with stdout streaming; last resort `host:mdns:services` refreshed on flyout-open only |
| Pairing | `host:pair:<code>:<host>:<port>` | one-shot, long | `adb.exe pair <host>:<port>` with the code written to **stdin** |
| Metadata | `host:transport:<serial>` then fixed `shell:` probes: `dumpsys battery`, `getprop ro.product.model`, `getprop ro.build.version.release`, `getprop ro.product.marketname`, `settings get global device_name`, `getprop ro.product.brand`, `getprop ro.product.manufacturer` | one-shot, on policy | field omitted; DisplayName falls back |

**Capability probe (startup and after every server restart):** attempt the proto services once; record support in `AdbCapabilities { ProtoDeviceTracking, ProtoMdnsTracking, ServerStatus, AdbVersion }`; log the result once per server generation. Degraded modes are logged at Warning, never surfaced as a product error unless they cost a design-required capability (no mDNS at all → `WirelessDiscovery` warning card).

**Never used:** `host:kill` (killing the server would break every other tool), `adb.exe kill-server`, `adb.exe connect` for unpaired devices, `adb.exe disconnect`, `adb tcpip`, `adb root`, any device-mutating command. ADB View is read-only with exactly one exception: pairing, which the user explicitly initiates.

### 6.3 Environment policy

ADB View starts `adb.exe` with the **inherited environment, unmodified**. It never sets `ADB_MDNS_OPENSCREEN`, `ADB_MDNS`, `ADB_MDNS_AUTO_CONNECT`, `ADB_LIBUSB`, `ADB_BURST_MODE`, or `ADB_TRACE`. Rationale: as of adb 37 the default and recommended mDNS backend is `libadbmdns`; the historical advice to force `ADB_MDNS_OPENSCREEN=1` is now obsolete, and forcing a backend on a server that other tools share is hostile. If `server-status` reports `mdns_enabled: false` or a non-recommended backend, ADB View *reports* reduced wireless discovery through the subsystem warning and log; it does not "fix" the user's machine.

### 6.4 Server lifecycle

```
                ┌──────────┐  probe ok        ┌────────┐
     start ───► │ Probing  │ ───────────────► │   Up   │
                └────┬─────┘                  └───┬────┘
                     │ probe fails                │ socket error / probe fails
                     ▼                            ▼
                ┌──────────┐  adb.exe found   ┌────────┐
                │ Starting │ ◄─────────────── │  Down  │
                └────┬─────┘                  └───┬────┘
                     │ start-server exit 0        │ adb.exe not found
                     ▼                            ▼
                  Probing                  ┌──────────────┐
                                           │ Unavailable  │  (AdbUnavailable + hint)
                                           └──────────────┘
```

- Probe = TCP connect + `host:version`. Cheap; no process spawn.
- `Down` → `Starting` uses `adb.exe start-server` with fixed argv, 20 s timeout, stdout/stderr captured to the log.
- Backoff on repeated failure: 1 s, 2 s, 5 s, 10 s, 30 s, 60 s cap, ±20 % jitter. Reset on success.
- `Unavailable` re-checks for `adb.exe` on the same backoff schedule and on every resume/session-unlock event, so installing platform-tools recovers without restarting ADB View.
- Distinguish `AdbRestoring` from `AdbUnavailable`: the first 10 s of any outage (or any outage while `adb.exe` is known to exist and a start attempt is in flight) is `AdbRestoring`; only a failed start or a missing executable produces `AdbUnavailable`. This is what keeps a routine `adb kill-server` by the developer from flashing a red error.

### 6.5 Device tracking

One connection, opened when the server is `Up`, holding `host:track-devices-proto-binary`. Every payload is a **complete device list** — a full-state snapshot, not a delta — which maps perfectly onto snapshot semantics and eliminates delta-reconciliation bugs.

Per device, ADB reports (proto): serial, connection state, `connection_type` (USB/SOCKET), `bus_address`, `product`, `model`, `device`, and negotiated speeds. ADB View reads only: serial, state, connection_type, model, product, device.

State mapping (raw → `DeviceStateKind`): `device`→Connected, `unauthorized`→Unauthorized, `offline`→Offline, `recovery`→Recovery, `bootloader`→Bootloader, `sideload`→Sideload, `rescue`→Rescue, `authorizing`→Authorizing, `connecting`→Connecting, `detached`→Detached, anything else→`Unknown` (logged once per distinct unrecognized literal, at Information, so a future ADB state shows up in logs rather than silently becoming "unknown" forever).

Stream failure → `DeviceTrackerDown` event → devices are **retained** for a 2 s grace period (an ADB server restart must not make cards flash out and back in), then cleared if the tracker has not recovered. Reconnect backoff: 0.5 s, 1 s, 2 s, 5 s, 10 s, 30 s cap, jittered.

### 6.6 Wireless discovery, pairing, and connection — three separate concepts

| Concept | mDNS service | Meaning | ADB View behaviour |
|---|---|---|---|
| Pairing available | `_adb-tls-pairing._tcp` | The phone's pairing screen is open right now | Becomes a `PairingOfferView`; disappears when the service withdraws |
| Connect available | `_adb-tls-connect._tcp` | The phone's TLS server is advertising (paired or not) | **Never a card.** Used only to enrich identity/address/name |
| Legacy TCP | `_adb._tcp` | `adb tcpip` style endpoint | Ignored for offers; enrichment only |

With **ADB Wi-Fi 2.0** (Android 17 + adb 37) the mDNS records carry `product_model`, `given_name`, `serial`, `ipv4`/`ipv6`, `port`, `hostname` and `mdns_service_version`, and paired devices auto-connect when they join a trusted network. Consequences designed for:

- Offers can display a real device name before any connection exists (`given_name ?? product_model ?? "Android device"`).
- On Wi-Fi 2.0 devices the connection may complete *before* our pairing call returns. `PairedWaitingForDevice` therefore resolves on either signal: our pairing success **or** the device appearing in the tracker — whichever comes first.
- On pre-Wi-Fi-2.0 devices records carry less metadata; the offer falls back to the instance-derived name and everything else still works.

### 6.7 Identity resolution (stable IDs)

```
1. Transport comes from proto connection_type: USB → Usb, SOCKET → WiFi, absent → Unknown.
2. Hardware serial:
   a. proto serial does not look like an endpoint and is not an mDNS instance → use verbatim
   b. proto serial matches ^adb-(?<serial>[^-]+)-[A-Za-z0-9]+$   → use captured group
   c. proto serial matches ^(?<host>.+):(?<port>\d+)$ → look up an mDNS connect service with the
      same address and take its `serial` field; if none, keep "host:port" as the identity
3. Id = ("usb:" | "wifi:" | "unk:") + hardwareSerial
4. Early DisplayName (before shell naming metadata) =
   usable mdns.given_name ?? proto.model ?? proto.product ?? "Android device"
   (never hardwareSerial / host:port / adb-* transport identity)
5. Refined DisplayName (after Connected metadata) =
   marketname → settings device_name → model → conservative brand+model → "Android device"
   ExactModel remains `ro.product.model` (technical). Transport identity never becomes DisplayName.
```
Rule 2b/2c are the only serial-shape heuristics in the codebase; they live in `Adb/DeviceIdentity.cs`, are covered by fixture tests, and are annotated with why they exist (ADB does not expose the hardware serial for wireless transports in older records). DisplayName refinement lives in `State/DeviceDisplayName.cs` and is applied once per device generation with model/version cache — not on the battery tick.

A device that moves USB → Wi-Fi is a *different* `Id` (different transport), which is correct: the design shows one card per transport, and connection age genuinely restarts.

### 6.8 Pairing state machine

```
        (offer present)
             │ pairStart
             ▼
      ┌──────────────┐  pairCancel   ┌───────────┐
      │ AwaitingCode │ ────────────► │  (none)   │
      └──────┬───────┘               └───────────┘
             │ pairSubmit(code)
             ▼
        ┌─────────┐   failure (bad code / refused / timeout 30 s)   ┌────────┐
        │ Pairing │ ───────────────────────────────────────────────►│ Failed │
        └────┬────┘                                                 └───┬────┘
             │ pairing success                                          │ pairStart (retry)
             ▼                                                          ▼
   ┌────────────────────────┐   device appears in tracker        AwaitingCode
   │ PairedWaitingForDevice │ ─────────────────────────────────► (cleared; device card is truth)
   └────────────┬───────────┘
                │ 60 s elapsed without a device
                ▼
          (cleared quietly; offer list continues to reflect mDNS)
```

Rules: at most one operation at a time; the code never leaves memory; the pairing socket has a 30 s timeout; `Failed` persists until retry, cancel, or offer withdrawal (`DESIGN.md §15` requires immediate retry); **success never fabricates a device** (`DESIGN.md §15`, brief §"Pairing operation"). Offer withdrawal — the active offer's binding absent from a complete mDNS list — clears the snapshot operation **and** releases the coordinator (`PairingSession.CancelIfOfferWithdrawn` on snapshot publish), so a later `pairStart` for any offer is accepted rather than rejected against a dead offer.

### 6.9 Metadata collection

| Trigger | What | Guard |
|---|---|---|
| Device enters `Connected` | battery, exact model, Android version, marketing name, device name, brand, manufacturer | once per device generation |
| Flyout opens (or pinned window becomes visible) | battery for visible devices, if older than 30 s | coalesced |
| Pinned and visible | battery every 120 s | stops immediately when hidden |
| Never | while hidden and unpinned; while device is not `Connected`; more than one in-flight request per device; polling `device_name` for renames | — |

Each full probe: `host:transport:<serial>` → fixed compile-time `shell:…` strings (see §6.2), 5 s timeout, single retry after 2 s, then give up until the next trigger. Failure ⇒ `batteryPercent = null` ⇒ the card simply omits battery (no error text, per design restraint). Naming/`getprop` values and brand/manufacturer are cached for the lifetime of the device generation; battery-only refreshes do not re-fetch them. `DisplayName` is refined when naming metadata arrives; `ExactModel` stays the technical `ro.product.model`.

### 6.10 Event reduction from ADB

All ADB subsystems emit only these events; nothing else reaches the state engine:

```csharp
record AdbServerStateChanged(AdbServerPhase Phase, AdbCapabilities? Caps, string? Reason);
record DeviceListReceived(ImmutableArray<RawDevice> Devices, DateTimeOffset At);
record DeviceTrackerFailed(string Reason);
record MdnsServicesReceived(ImmutableArray<RawMdnsService> Services, DateTimeOffset At);
record MdnsTrackerFailed(string Reason);
record MetadataReceived(string DeviceId, int? Battery, string? Model, string? AndroidVersion,
    string? MarketingName, string? DeviceName, string? Brand, string? Manufacturer);
record PairingProgressed(string OfferId, PairingPhase Phase, string? FailureReason);
```

### 6.11 Compatibility matrix

| Environment | Behaviour |
|---|---|
| platform-tools ≥ 37, Android 17 device, Wi-Fi 2.0 | Full fidelity: proto tracking, streaming mDNS with names/serials, auto-connect aware pairing |
| platform-tools ≥ 33 … 36 | Proto device tracking; mDNS tracking via CLI stream fallback; offers show instance-derived names |
| platform-tools < 33 | Text `track-devices-l`; transport inferred from serial shape; mDNS refreshed on flyout open; logged as degraded once |
| adb absent | `AdbUnavailable` with the exact `DESIGN.md §18` wording; periodic re-detection |
| mDNS blocked by network (corporate/guest Wi-Fi) | `WirelessDiscovery` warning card; USB unaffected (`DESIGN.md §17`) |
| Emulators | Appear as ordinary devices (`connection_type` SOCKET, `emulator-5554` serials); no special casing |

---

## 7. State Reduction Model

### 7.1 Single-writer pipeline

```
  any thread                      state thread (one)                       UI thread
 ─────────────                ─────────────────────────                ────────────────
 subsystem ──Post(event)──►  Channel<AdbViewEvent>  ──►  Reduce  ──►  volatile snapshot
                              (unbounded, single                         │
                               reader; no generic                        └─ BeginInvoke(publish)
                               event dropping)                                 │ revision check
                                                                              ▼
                                                                    PostWebMessageAsJson
                                                                    TrayIcon.Apply(...)
                                                                    NotificationSink.Emit(...)
```

- `Reduce` is `(AppState, AdbViewEvent) -> AppState` — pure, synchronous, allocation-light, no I/O, no `await`, no locks. This is the single most test-dense file in the repo.
- A snapshot is built only when reduction actually changed something (structural equality check on the derived view); unchanged events produce no revision, so an idle machine produces zero traffic.
- The reducer input uses `Channel.CreateUnbounded<AdbViewEvent>` with a single reader. It never applies a generic drop-oldest/drop-newest policy because the channel also carries non-replaceable events such as pairing results, server transitions, metadata results, and recovery signals. Complete-list sources such as device tracking may be explicitly coalesced at their own producer boundary **only** when the event type is documented as replaceable and tests prove that no terminal/failure/pairing event can be lost. v1 starts without such coalescing; event volume is tiny.

### 7.2 Time

One injected `IClock`. `DateTimeOffset.UtcNow` appears nowhere else. Ages are computed in the frontend from `stateSince`/`connectedSince`, so time passing does **not** generate snapshots — a hard requirement for "effectively idle when nothing changes". Age formatting rules (frozen here since `DESIGN.md` shows only the `46s` case):

| Elapsed | Rendering |
|---|---|
| < 60 s | `{n}s` |
| < 60 min | `{n}m` |
| < 24 h | `{n}h` |
| ≥ 24 h | `{n}d` |

The frontend ticks a single 1 s interval only while the flyout is visible and only if at least one visible `metaTemplate` contains `{age}`.

### 7.3 Race conditions eliminated by design

| Race | Why it cannot happen |
|---|---|
| Two subsystems updating one device concurrently | Only the reducer writes state |
| Stale device list overwriting a newer one | Lists carry `At`; the reducer ignores a list older than the one already applied |
| Pairing success arriving after the user cancelled | Reducer checks the operation is still the active one and matches `OfferId`; otherwise the result is dropped and logged |
| Metadata for a device that already disappeared | Reducer no-ops on unknown `DeviceId` |
| Older snapshot painting over a newer one | `Revision` compared at publish time (native) and at receive time (web) |
| Animation delaying truth | Frontend renders from the latest snapshot; CSS transitions retarget, they are never queued (§10.4) |
| Notification fired from a transient state | Notification policy operates on debounced, persisted transitions (§8) |

### 7.4 Health aggregation

```
global:
  AdbUnavailable  ⟸ adb.exe missing, or start-server failed and outage > 10 s
  AdbRestoring    ⟸ server down/restarting and a recovery attempt is in flight (≤ 10 s outage)
  Healthy         ⟸ otherwise (regardless of individual device states)

local (never global):
  WirelessDiscovery Unavailable ⟸ mDNS tracker failed/unsupported and retry backoff > 5 s
  WirelessDiscovery Restoring   ⟸ tracker reconnect attempt in flight
```

An `Offline` or `Unauthorized` device never changes global health (`DESIGN.md §17`). When global health is not `Healthy`, device cards are not rewritten — the device list is simply empty because ADB itself is not reporting.

### 7.5 Tray status aggregation

Priority exactly as `DESIGN.md §30`:

```
1 Failure          ⟸ health.kind == AdbUnavailable
2 Attention        ⟸ any device Unauthorized  (or Offline while no device is Connected)
3 PairingAvailable ⟸ pairingOffers not empty
4 Special          ⟸ any device Recovery | Bootloader | Sideload | Rescue
5 Connected        ⟸ any device Connected
6 Idle             ⟸ otherwise
```

Tooltip is produced by `Vocabulary.TrayTooltip(snapshot)`: single device → `Connected: Galaxy S24 (Wi-Fi)`; multiple → highest-priority condition plus count (`Authorization required: Pixel 8 · 3 devices`); none → `No ADB devices`; failure → `ADB unavailable`. Truncated to the 127-character shell limit at a word boundary.

---

## 8. Notification Policy

### 8.1 Shape

```csharp
// pure, unit-tested, no I/O
static NotificationDecision Evaluate(
    AppSnapshot? previous, AppSnapshot next, NotifierState state, DateTimeOffset now);

record NotificationIntent(NotificationKey Key, string Title, string Body, NotificationSeverity Severity);
record NotificationKey(NotificationKind Kind, string Subject);   // Subject = deviceId | offerId | "global"
```

Transitions considered (and only these):

| Kind | Trigger | Title / Body (from `Vocabulary`) |
|---|---|---|
| `DeviceConnected` | device absent/non-connected → `Connected`, held 0.5 s | `Galaxy S24 connected` / `Connected over Wi-Fi` |
| `DeviceDisconnected` | `Connected` → absent, held 0.5 s | `Galaxy S24 disconnected` |
| `AuthorizationRequired` | → `Unauthorized`, held 1.5 s | `Galaxy S24 needs authorization` / `Check the phone and allow USB debugging.` |
| `DeviceProblem` | → `Offline`, held 0.5 s | `Galaxy S24 is offline` / `ADB can see this device, but the connection isn't responding.` |
| `PairingAvailable` | new offer id, held 1.5 s | `Wireless pairing available` / `<name> is waiting for a pairing code` |
| `PairingResult` | pairing → `PairedWaitingForDevice` or `Failed` | `Paired with Galaxy S24` / `Waiting for ADB connection…` · `Pairing failed` / `Check the code and try again.` |
| `AdbUnavailable` | global health → `AdbUnavailable`, held 10 s | `ADB unavailable` / `ADB View couldn't find or start ADB.` |

Never notified: metadata refresh (battery, model, version), `AdbRestoring`, wireless-discovery degradation, recovery/bootloader/sideload (informational, visible in tray + flyout), pin/theme changes, revision churn.

> **Decision 0019:** the connection holds are **0.5 s**, not 2.5 s. Detection is a push stream and the
> flyout/tray already update instantly, so the hold is purely a flap guard on the toast; 2.5 s read as
> the application being slow. Half a second still swallows a sub-second blip. Delivered notifications
> are dropped (never deferred) while the flyout is visible.

### 8.2 Anti-spam rules

| Rule | Value | Reason |
|---|---|---|
| Startup suppression | First snapshot adopted as a silent baseline; no notifications in the first 5 s of process life | Launching must not announce five already-connected devices |
| Resume suppression | 8 s after `PowerModeChanged(Resume)` / session unlock, plus baseline re-adoption | Wake-up reconnect storms |
| Flap debounce | Transition must persist for its hold time (table above) before it notifies | USB cable jiggle produces nothing |
| Visible-flyout suppression | No toast while the flyout is on screen (it already shows the state); evaluated **before** burst/episode bookkeeping, so nothing is latched or rate-limited by a notification the user never saw | Opening the tray is not answered with a toast about what it is displaying |
| Dedup cooldown | Same `NotificationKey` suppressed for 60 s | Repeated offline/online bounce |
| Global-failure once | `AdbUnavailable` notifies once per outage; recovery is silent | Design has no "ADB restored" notification |
| Pairing-offer cooldown | Same offer id: once per 10 min | The phone's pairing screen re-advertises frequently |
| Burst cap | ≤ 3 notifications per 10 s window; excess collapses into one `N devices changed` | Multi-device dock/undock |
| User-initiated exemption | `PairingResult` bypasses debounce and burst cap (never the dedup key) | The user is staring at the UI waiting for it |

`NotifierState` (last key timestamps, baseline flag, suppression deadlines) lives inside `AppState` so the whole policy is reproducible in tests from a scripted timeline with no clock sleeping.

### 8.3 Delivery

`INotificationSink` → `BalloonNotificationSink` using the existing tray icon's `NotifyIcon.ShowBalloonTip` backend with `ToolTipIcon.Info` / `Warning` / `Error` (WinForms maps these to `NIIF_INFO` / `NIIF_WARNING` / `NIIF_ERROR`). No actions, no buttons, no deep links — clicking the balloon simply shows the flyout, which is the only place full truth lives. Delivery failures are logged and never retried.

`NIIF_RESPECT_QUIET_TIME` was researched during V-5 and intentionally not implemented: WinForms `NotifyIcon` does not expose that flag, and Microsoft's Shell "quiet time" is only the first hour after first login to a new account / after OS upgrade or clean install — not general Focus Assist or Do Not Disturb. v1 does not claim explicit Focus Assist / Do Not Disturb integration.

---

## 9. Windows Host Architecture

### 9.1 Tray icon

- WinForms `NotifyIcon` with a GUID-stable identity, a multi-resolution `.ico` per tray state (16/20/24/32/48/256 px, pre-rendered from Pass F — no runtime GDI compositing, so markers stay crisp at every DPI).
- Left click → toggle flyout. Right click → context menu with **Open ADB View**, **Start with Windows**, **Notifications**, **Status bubble**, **Theme**, and **Exit** (`DESIGN.md §33`, decision 0006, decision 0020).
- Tooltip from `health.trayTooltip`, updated with each snapshot.
- Explorer restart: `NotifyIcon` re-adds itself on the `TaskbarCreated` broadcast; an integration test kills and restarts Explorer and asserts the icon returns.
- Note: current Windows 11 no longer re-broadcasts `TaskbarCreated` on primary-DPI change, so per-DPI crispness comes from the multi-resolution icons, not from re-registration.

### 9.2 Flyout window

- Borderless `Form`: `FormBorderStyle.None`, `ShowInTaskbar = false`, `WS_EX_TOOLWINDOW` (no taskbar button, no Alt-Tab entry), `TopMost` only while pinned, never activated on hover.
- **Backdrop (v1):** `DWMWA_USE_IMMERSIVE_DARK_MODE` per theme, `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUNDSMALL` (matches the 8 px design radius). **v1 does not enable** `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW` under WebView2 — always paint the intentional opaque host surface (`DESIGN.md` §22 / decision 0004). Keep `WS_CAPTION` and collapse non-client area in `WM_NCCALCSIZE` (borderless look + DWM corners). Answer `WM_NCACTIVATE` with `TRUE` while pinned (no focus stealing). After custom-frame establishment (and after theme changes), call `SetWindowPos(..., SWP_FRAMECHANGED)` via `RecalcNonClientFrame` so the first visible frame already has a collapsed non-client area.
- **v1 opacity contract:** `shell.transparencyEnabled` is always `false` in production. WebView2 `DefaultBackgroundColor` matches the opaque host (`#202329` / `#F3F4F6`), not `Color.Transparent`. Frontend sets `data-opaque="true"` and paints solid body/root tokens. Forced-colors still maps through system roles. Live Desktop Acrylic under transparent WebView is deferred architecture work — no CSS `backdrop-filter`, screenshot blur, repaint timers, DWM jiggle, or WebView runtime pinning.
- **Sizing (decision 0013, amending 0012/0005):** width is always `376 effective px × dpiScale`. Height follows the reported content height (`clamp(natural, 120, 560)`) through **animated mode-B steps**: per settled change the WebView child is pinned to its final bounds once, the window frame animates to the target over 167 ms on the Fluent curve with the tray-adjacent edge frozen, and mid-flight changes retarget. Equal heights are suppressed; the child is never resized per frame (the measured 0003/0005 jank). The scroll container absorbs overflow past 560 epx; `scrollbar-gutter: stable both-edges` keeps measurement width constant. Presentation uses the best-known height and converges within one or two reports. Until the first valid report, the window stays hidden. Monitor/DPI/shell lifecycle events re-place and cancel any running animation.
- **Open/close reveal (decision 0013):** open slides out of the tray edge over 167 ms; every hide path (light-dismiss, Esc, tray toggle, power/session) slides into the tray edge over 83 ms, then hides. A show during a close cancels it via the `_showGeneration` guard. OS reduced-motion (`SPI_GETCLIENTAREAANIMATION`, read in `SystemTheme` and piped through `ShellAppearanceChangedEvent`) reduces all of it to 0012 instant steps.
- **First show:** Normal interactive first launch (no `--startup`, no fixture, no headless/selftest) keeps `InitialShowRequested` until WebView ready **and** the first valid `reportContentSize` **and** `Show` has yielded a visible flyout. The first measure must not depend solely on `requestAnimationFrame` (hidden WebView throttles rAF). Programmatic initial show must not be immediately light-dismissed by activation churn. `--startup` stays tray-only. Fixture/headed harness may show for visual capture. Second-instance named-pipe `toggle` is unchanged.

### 9.3 Placement

```
1. Ask the shell where our icon is: Shell_NotifyIconGetRect(NOTIFYICONIDENTIFIER{guid}).
   Success → anchor = icon rect (handles hidden/overflow flyout cases where the rect is the
   overflow button, which is still the correct visual anchor).
2. Failure → SHAppBarMessage(ABM_GETTASKBARPOS) for edge + rect; anchor = cursor position
   projected onto that edge.
3. Monitor = MonitorFromRect(anchor) → work area + per-monitor DPI (GetDpiForMonitor).
4. Place with a 12 px effective gap from the taskbar edge, aligned to the anchor, then clamp
   fully inside the work area (never offscreen, never overlapping the taskbar).
5. Taskbar edges: bottom → above-right; top → below-right; left → right of; right → left of.
6. Mixed DPI: all math in physical pixels using the *target* monitor's DPI; the window is
   created, DPI-adjusted (WM_DPICHANGED), and only then shown.
```
Re-evaluated on every show, on `DisplaySettingsChanged`, and on `WM_DPICHANGED`. Cached anchors are invalidated, never reused across shows.

### 9.4 Pin, drag, dismiss

| Behaviour | Mechanism |
|---|---|
| Pin | `pin`/`unpin` action → `SetWindowPos(HWND_TOPMOST/HWND_NOTOPMOST)`; light-dismiss disabled; state flows back through the snapshot (frontend never assumes) |
| Drag (pinned) | `CoreWebView2.Settings.IsNonClientRegionSupported = true` + `app-region: drag` on the header element, so Windows performs the move. Fallback if unsupported: header `pointerdown` → `beginDrag` … **not in the contract**; instead the host handles `WM_NCHITTEST` returning `HTCAPTION` for the top 40 px strip. Exactly one mechanism ships; the choice is settled by V-4 in Phase 1, and `AGENTS.md` forbids JS-simulated dragging either way |
| Light dismiss (unpinned) | `WM_ACTIVATEAPP`/`WM_ACTIVATE` deactivation → hide. No mouse hooks, no `SetCapture`, no polling |
| Esc | Frontend sends `requestDismiss`; host hides if unpinned |
| Focus | On show (unpinned) the window is activated so the WebView takes keyboard focus; pinned shows do not steal focus |

Pin state is **not persisted** in v1: every launch starts unpinned. Deterministic, storage-free, and reversible later without touching the contract (`shell.pinned` already exists).

### 9.5 Single instance

Named mutex `Local\AdbView.SingleInstance` decides ownership. The owner serves a named pipe `AdbView.Control.<sessionId>`; a second launch connects, sends `toggle`, and exits 0 — so double-clicking the shortcut behaves like clicking the tray icon instead of starting a competing monitor. If the pipe cannot be reached within 1 s (owner wedged), the second instance logs and exits 1 without starting a second backend.

### 9.6 Power, session, display

| Event | Response |
|---|---|
| `PowerModeChanged(Suspend)` | Suspend trackers and metadata timers; close sockets deliberately rather than leaving them half-open |
| `PowerModeChanged(Resume)` | Immediate server probe, tracker restart with zero backoff, metadata refresh for visible devices, notification baseline re-adopted, 8 s suppression |
| `SessionSwitch(SessionUnlock)` | Same as resume, minus tracker restart if already healthy |
| `SessionEnding` | Graceful shutdown: hide window, dispose sockets, flush logs |
| `DisplaySettingsChanged` / `WM_DPICHANGED` | Invalidate placement; if visible, re-place immediately |

### 9.7 WebView2 recovery

| Failure | Detection | Response |
|---|---|---|
| Render process gone / unresponsive | `CoreWebView2.ProcessFailed` (`RenderProcessExited`, `RenderProcessUnresponsive`) | `Reload()`; re-push snapshot on `ready` |
| Browser process gone | `ProcessFailed` (`BrowserProcessExited`) | Dispose and recreate the whole control, then re-navigate; backoff 1 s, 5 s, 15 s, 60 s cap |
| Creation fails (runtime missing/corrupt) | `EnsureCoreWebView2Async` throws | Log; tray icon + tooltip continue to report status (the second channel); one balloon explains that the window cannot be shown; retry on next left-click and on resume |
| `ready` never arrives | 5 s timer | Reload once, then treat as creation failure |
| Repeated failure (3 in 5 min) | Counter | Stop recreating; stay tray-only until the next explicit left-click |

The user data folder is `%LOCALAPPDATA%\ADBView\WebView2`. On two consecutive creation failures the folder is renamed aside (`WebView2.corrupt-<timestamp>`) and creation retried once — the standard fix for a poisoned profile.

---

## 10. Frontend Architecture

Zero business logic. The frontend is a pure function of `AppSnapshot` plus a small amount of local interaction state.

### 10.1 Component structure

```
ui/src/
  main.tsx                 mounts <App/>, installs the bridge listener
  bridge/
    useSnapshot.ts          subscribes to window.chrome.webview messages; revision guard
    actions.ts              the 8 typed web→native actions; nothing else may postMessage
  contract/snapshot.ts      generated; never hand-edited
  components/
    AppShell.tsx            frame, theme/opaque/forced-colors data attributes, scroll region
    Header.tsx              glyph + "ADB View" + PinButton (40×40 hit area, app-region: drag)
    DeviceList.tsx          optional "3 devices" heading; keyed list; insertion/removal motion
    DeviceCard.tsx          THE canonical B.1 card; collapsed + expanded in one component
    DeviceCardDetail.tsx    expanded fields + CopyField (inline "Copied")
    PairingSurface.tsx      offer | code entry | in-progress | success | failure, one surface
    EmptyState.tsx          "No ADB devices" …
    SubsystemWarning.tsx    local wireless-discovery warning
    GlobalState.tsx         ADB unavailable / Restoring ADB
    primitives/             StatusPill, TransportChip, BatteryReadout, Divider, Chevron, Spinner
  icons/                    vendored SVG components from the approved exports (no icon package)
  styles/
    tokens.css              all design tokens as CSS custom properties (dark/light/opaque/forced)
    app.css                 Tailwind entry (@import "tailwindcss"; @theme maps to tokens)
  lib/
    age.ts                  formatAge() + metaTemplate substitution
    fixtures/               deterministic snapshots for dev, tests, and visual regression
```

There is exactly one `DeviceCard`. No `CompactDeviceCard`, `DenseDeviceCard`, `MultiDeviceCard`, `MiniDeviceCard`, or `PinnedDeviceCard` — a lint rule (`no-restricted-syntax` on those identifiers) and a repo-scan test enforce it (`DESIGN.md §16`).

### 10.2 State rendering

```tsx
const snapshot = useSnapshot();              // latest revision only, or null before first message
const [expandedId, setExpandedId] = useState<string | null>(null);   // one at a time (§10 design)
const [copied, setCopied] = useState<{id: string; key: string} | null>(null);  // 1.2 s inline
const [codeDraft, setCodeDraft] = useState('');                       // cleared on phase change
```

Local state is limited to those three concerns. Everything else — pin, pairing phase, device data, health, theme — comes from the snapshot. `expandedId` is dropped automatically when the device disappears.

### 10.3 Design tokens

Recovered verbatim from the approved Pass B.1 export (dark = visual master) and Pass D.1 (light/opaque/forced-colors direction). These values are the implementation's source of truth; changing one is a `DESIGN.md` change, not a code change.

| Token | Dark (B.1) | Purpose |
|---|---|---|
| `--surface-flyout` | `rgba(32, 35, 41, 0.94)` over native acrylic | flyout body |
| `--surface-card` | `rgba(255,255,255,0.04)` | device / pairing card |
| `--surface-card-hover` | `rgba(255,255,255,0.065)` | hover |
| `--border-card` | `rgba(255,255,255,0.08)` | card border |
| `--border-subtle` | `rgba(255,255,255,0.05)` | in-card divider |
| `--border-window` | `rgba(255,255,255,0.09)` | 1 px window edge |
| `--accent` / `--accent-hover` | `#60a5fa` / `#3b82f6` | informational, pairing, product glyph |
| `--accent-surface` / `--accent-border` | `rgba(96,165,250,0.08)` / `rgba(96,165,250,0.28)` | pairing surface |
| `--positive` / `--positive-surface` | `#4ade80` / `rgba(74,222,128,0.09)` | connected pill |
| `--negative` / `--negative-surface` / `--negative-border` | `#f87171` / `rgba(248,113,113,0.09)` / `rgba(248,113,113,0.28)` | offline / failure |
| `--text-primary` / `--text-secondary` / `--text-tertiary` | `#ffffff` / `neutral-400` / `neutral-500` | name / metadata / affordance |
| `--radius-window` `--radius-card` `--radius-control` | `8px` `8px` `4px` | geometry |
| `--pad-card` `--pad-pairing` | `12px` `14px` | card padding |
| `--font-sans` | `"Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", system-ui` | OS-installed only |
| type scale | header `14.5px`, device name `15px`, pairing heading `13.5px`, metadata `12px` | frozen |
| `--motion-instant/feedback/state/structural` | `0ms` / `83ms` / `167ms` / `333ms` | the only four allowed |
| easing | `cubic-bezier(0.55, 0.55, 0, 1)` | Fluent Point-to-Point (DESIGN §24) |
| scrollbar | 4 px thumb `rgba(148,163,184,0.4)`, hover `rgba(100,116,139,0.6)`, transparent track | from D.1 |

Light theme applies D.1 palette to the same geometry; opaque mode replaces the translucent surfaces with solid tokens; `@media (forced-colors: active)` maps to system roles (`Canvas`, `CanvasText`, `ButtonFace`, `ButtonText`, `Highlight`, `HighlightText`, `GrayText`, `Field`, `FieldText`) and never hard-codes a contrast palette (`DESIGN.md §23`).

### 10.4 Animation architecture

- CSS transitions only, on the four duration tokens. No JS animation loop, no animation library, no `requestAnimationFrame` choreography.
- Card expansion: `grid-template-rows: 0fr → 1fr` (content-aware at every scale, no hard-coded heights, no `max-height: 999px`).
- Insertion: `@starting-style` + `transition-behavior: allow-discrete` at 167 ms.
- Removal: a `useExitTransition` hook holds a leaving element for at most 167 ms and **cancels immediately** if a newer snapshot reinstates or supersedes it. Removal never delays an incoming snapshot.
- Status changes: class/data-attribute swap; the transition retargets mid-flight, which is exactly the `Offline → Connected → Authorization required` requirement (`DESIGN.md §26`).
- Telemetry values (battery) change discretely or crossfade; no counting animation (`DESIGN.md §27`).
- Reduced motion: `@media (prefers-reduced-motion: reduce)` **and** `shell.reducedMotion` both set every duration token to `0ms`.
- Scrolling: native Chromium overflow only; no snap, no inertia, no parallax, no custom physics (`DESIGN.md §28`).

### 10.5 Accessibility

- Semantic structure: `role="list"`/`listitem` for devices; each card's identity row is a `<button aria-expanded>` controlling the detail region; the pairing code input is `<input inputmode="numeric" autocomplete="one-time-code" maxlength=6>` with an accessible label.
- Never color-only: every state renders text + glyph + color (`DESIGN.md §11`).
- Focus: visible ring on every interactive element, `Highlight` under forced colors; logical tab order header → pairing → devices; `Esc` dismisses (unpinned).
- `aria-live="polite"` on a single status region that announces *device state changes only* — not battery, not ages.
- Text scaling: layout is in `rem`/`px` with no fixed content heights; verified at 100–200 %.

### 10.6 Local asset strategy

Vite builds to `ui/dist`, copied to `src/AdbView/wwwroot` at build time and served through `SetVirtualHostNameToFolderMapping("app.adbview.local", …, DenyCors)`. Fonts come from Windows; icons are inline SVG; no network origin is reachable (CSP `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'none'; font-src local`). A build-time check fails the build if `dist/` contains any absolute `http(s)` reference (`DESIGN.md §36`).

---

## 11. Reliability Model

Failure / detection / response matrix. Every response is *owned* — no global "restart everything" timer exists anywhere in the codebase.

| # | Subsystem | Failure | Detection | Response | Backoff | User-visible |
|---|---|---|---|---|---|---|
| 1 | ADB server | Not running at startup | Probe fails | `adb start-server`, re-probe | 1/2/5/10/30/60 s ±20 % | `Restoring ADB…`, then normal |
| 2 | ADB server | `adb.exe` not installed | Discovery finds nothing | Periodic re-detect; no spawn attempts | 30 s → 5 min | `ADB unavailable` + install hint |
| 3 | ADB server | Killed while running (`adb kill-server`) | Stream socket error + probe fails | Restart server, restart trackers | as #1 | Devices held 2 s, then `Restoring ADB…` |
| 4 | ADB server | Version changed under us (another tool upgraded adb) | `server-status`/`host:version` differs | Re-run capability probe; reopen trackers | immediate | None |
| 5 | Device tracker | Stream ends / socket error | `ReadStreamAsync` completes or throws | Reopen stream; hold last list 2 s | 0.5/1/2/5/10/30 s | Nothing for blips |
| 6 | Device tracker | Proto service unsupported | `FAIL` on first request | Fall back to text tracking; log once | — | None (transport may read `Unknown`) |
| 7 | Device tracker | Malformed payload | Parser rejects frame | Drop frame, log once per minute, keep stream | — | None |
| 8 | mDNS tracker | Stream dies | Same as #5 | Reopen | 1/2/5/15/30 s | `Restoring wireless discovery…` after 5 s |
| 9 | mDNS tracker | mDNS unavailable on this network / disabled in server | `FAIL`, empty forever, or `mdns_enabled:false` | Stop retrying tightly; re-probe on resume and network change | 60 s | `Wireless discovery unavailable` + `USB ADB is still working normally.` |
| 10 | Network | Wi-Fi drops | Wireless devices vanish from tracker; mDNS silence | Nothing special — the device list is authoritative | — | Wireless cards disappear; USB untouched |
| 11 | Pairing | Wrong code / refused | `FAIL` or non-zero fallback exit | `Failed` + retry available | none (user-driven) | `Pairing failed` / `Check the code and try again.` |
| 12 | Pairing | Hangs | 30 s timeout | Abort socket, `Failed` | none | as #11 |
| 13 | Pairing | Succeeds but device never connects | 60 s timer | Clear operation quietly | — | Surface returns to offer/empty |
| 14 | Metadata | Probe timeout/failure | 5 s timeout | One retry, then omit | next trigger | Battery simply absent |
| 15 | Device state | Flapping | Notification hold timers | UI updates every time; notifications wait | — | Quiet tray, live flyout |
| 16 | WebView | Renderer crash | `ProcessFailed` | `Reload`, re-push snapshot | 1 s | Brief blank, then correct state |
| 17 | WebView | Browser process crash | `ProcessFailed` | Recreate control | 1/5/15/60 s | Window reappears with current state |
| 18 | WebView | Runtime missing/corrupt | Creation throws | Rename profile once, retry; tray keeps reporting | on demand | Tooltip + one balloon |
| 19 | Tray | Explorer restart | `TaskbarCreated` (handled by `NotifyIcon`) | Icon re-added automatically | — | Icon returns |
| 20 | Tray | Icon rect unavailable (overflow/hidden) | `Shell_NotifyIconGetRect` fails | Taskbar-edge fallback placement | — | Flyout still anchored sensibly |
| 21 | Window | Monitor removed / DPI change while visible | `DisplaySettingsChanged`, `WM_DPICHANGED` | Re-place, clamp to work area | — | Window moves, never offscreen |
| 22 | Power | Suspend/resume | `PowerModeChanged` | Suspend then fast-path recovery | zero-delay first retry | No notification storm (8 s suppression) |
| 23 | Session | Lock/unlock, RDP switch | `SessionSwitch` | Probe + re-baseline | — | None |
| 24 | Process | Second launch | Mutex held | Toggle existing flyout, exit 0 | — | Flyout appears |
| 25 | Process | Unhandled exception | `AppDomain.UnhandledException`, `Application.ThreadException` | Log with full context, attempt clean tray removal, exit non-zero | — | Icon disappears (no zombie) |
| 26 | State | Reducer backlog grows unexpectedly | Producer storm or reducer bug | Do not drop events; continue draining and emit a rate-limited backlog warning once a diagnostic pending-count threshold is exceeded | — | No loss of semantic events; investigate producer/reducer cause |
| 27 | Discovery (fallback browser, decision 0008 follow-through) | Soft-state staleness: announcements stop or instances withdraw while the server is Up | mDNS TTL expiry / TTL-0 goodbyes (no pure event mechanism covers missed packets — R-05 sanction per `research/2026-10-01-dnssd-fallback-browser.md`) | Browser loop sweeps expired entries and re-issues query bursts at ≈80 % of the smallest outstanding TTL (floor 60 s); goodbye packets evict immediately; the ADB server stream stays authoritative and the fallback fills only its empty gap | single loop slice 200 ms; requery floor 60 s | Offers survive a broken server browse; stale instances disappear |
| 28 | Pairing (QR, decision 0010) | Unscanned QR session lingers with a live single-use credential | 2-minute session window (design authority) | StateEngine timer command quietly clears the card; a new session regenerates secret + instance name | 2 min | No stale scannable credentials; quiet surface return |
| 29 | Shell (flyout sizing, decision 0012; native self-sizing decision 0021) | Content height changes while visible (device arrival, card expansion, font reflow) | ResizeObserver + snapshot commits (web) / shell LayoutUpdated (native; event-driven; no timers) | One instant step to the clamped content height when it differs from the applied height (native: ShellSizing, bottom edge frozen, [160, 560] DIP clamp); equal heights suppressed; overflow past 560 epx scrolls in the stable container | 0 ms step | Window always matches content; no animation, no oscillation |
| 30 | Shell (presentation animation, decision 0013; XAML motion decision 0021) | Same triggers as row 29, plus open/close, hover/press, card expansion, pairing phases | FlyoutRevealAnimator UI-thread tick (~16 ms; decision-sanctioned animation clock) + WPF token animations (`Motion.Feedback/State/Structural` in Themes/Controls.xaml) | Window slides 167/83 ms (unchanged); XAML motion animates opacity and RenderTransform ONLY (hover/press 83 ms; chevron + details fade 167 ms; badge/phase/bubble fades); reduced motion zeroes the tokens at window level via the ShellMotionGate (live override, no per-site branches) | 83/167 ms tokens | Motion explains change without delaying truth; layout never animates |

| 31 | Shell (status bubble, decision 0020) | A transient state (drop / offline / special mode) would linger forever as if current | The transient's own 60 s window, then the bubble hides (no periodic polling; a one-shot, generation-guarded expiry retargeted by the next state) | 60 s | Ambient truth without a stale badge; R-05 sanction per `research/2026-10-03-status-bubble-native-approaches.md` |

Chaos tests (Phase 9) automate rows 1, 3, 5, 8, 11, 16, 17, 19, 24, 26 against the fake ADB server and a real Explorer restart.

---

## 12. Security Model

### 12.1 ADB execution

- `adb.exe` is resolved to an **absolute path** from `ANDROID_SDK_ROOT` / `ANDROID_HOME` / `PATH` / known SDK locations. The current working directory is never searched, eliminating the classic `adb.exe`-in-CWD hijack.
- Every spawn uses `ProcessStartInfo` with `UseShellExecute = false`, an explicit argument list (never a concatenated command line), and no shell. Argument escaping is therefore not a code path that can be got wrong.
- The only spawn with user-influenced data is the pairing *fallback*, where the six-digit code is written to **stdin** — never argv, so it cannot appear in another user's process listing.
- Device interaction goes through the host protocol socket, not `adb.exe shell`. The only shell strings sent are seven compile-time constants (`dumpsys battery`, `getprop ro.product.model`, `getprop ro.build.version.release`, `getprop ro.product.marketname`, `settings get global device_name`, `getprop ro.product.brand`, `getprop ro.product.manufacturer`); no snapshot or user value is ever interpolated into a device command.
- The server is never killed, never reconfigured, and never has its environment overridden.
- The loopback ADB port is treated as an *untrusted* input source: length-prefixed reads are bounded (1 MiB), the protobuf reader skips unknown fields without allocation surprises, and no value from ADB is ever executed, evaluated, or used as a path.

### 12.2 WebView bridge

| Control | Setting |
|---|---|
| Host objects | `AreHostObjectsAllowed = false`; `AddHostObjectToScript` never called |
| Script execution from native | Not used for state; only permitted for the one-line theme bootstrap before first paint |
| DevTools | `AreDevToolsEnabled = false` in Release (Debug-only, behind `#if DEBUG` + `--verbose`) |
| Context menus / status bar / accelerators / swipe nav | All disabled |
| Navigation | `NavigationStarting` cancels any URI outside `https://app.adbview.local/`; `NewWindowRequested` cancelled; `WindowCloseRequested` ignored (window is hidden, not closed) |
| Resources | `AddWebResourceRequestedFilter("*", All)` → anything not on the virtual host is aborted |
| Permissions | `PermissionRequested` denies every kind by default; clipboard *write* is the only user-gesture capability the UI needs and is allowed only if the runtime surfaces it as a request (V-3 in `research/`) |
| Downloads | `DownloadStarting` cancelled |
| Content | CSP `default-src 'none'` (§10.6); assets are local files only; no remote origin resolvable |
| Inbound messages | Strict typed deserialization, unknown-member rejection, protocol check, argument validation, rate limits (§5.5) |
| Outbound messages | Snapshots only; no serials/addresses beyond what the UI displays; no file paths, no environment data, no log contents |

There is no code path by which arbitrary JavaScript can invoke native functionality: the entire native surface is eight validated message shapes with no dynamic dispatch, no reflection, and no name-based invocation.

### 12.3 Data at rest, logs, privacy

- Persisted state: **none** (no settings file, no cache beyond the WebView2 profile, no history, no pairing records — pairing trust lives in the user's adb keys, where it belongs).
- Logs are local only, never transmitted. Pairing codes are never logged in any form. Serials are truncated to their last four characters at Information level and only logged in full at Debug. Wi-Fi addresses are logged (needed to diagnose discovery) but `given_name`/`hostname` are logged at Debug only, since they can contain a person's name.
- A unit test asserts that the redaction helpers are used by scanning log-call sites for raw `Serial`/`Code` interpolation.
- No telemetry, no crash reporting, no network access of any kind at runtime. The build fails if `System.Net.Http` is referenced from the app project.

---

## 13. Logging and Diagnostics

**Library:** Serilog + file sink. **Location:** `%LOCALAPPDATA%\ADBView\logs\adbview-<date>.log`. **Rotation:** daily, 4 MB cap per file, 7 files retained (worst case ~28 MB). **Levels:** Error (broken invariant / unrecoverable), Warning (degraded capability, retry exhausted), Information (lifecycle and subsystem transitions), Debug (`--verbose`: frames, full serials, snapshot revisions).

Every line carries `{Subsystem}` and a stable event id so a user's log is greppable without reading code.

**Startup header (one block, Information):** ADB View version, build id, architecture, Windows build, WebView2 runtime version, DPI per monitor, theme/transparency/contrast state, resolved `adb.exe` path, adb version, mDNS enabled + backend, detected capabilities.

**Events worth logging (the whole list):**

| Subsystem | Information | Warning/Error |
|---|---|---|
| `app` | start, exit, single-instance toggle, resume/unlock | unhandled exception, second-instance pipe timeout |
| `adb.server` | phase changes, start-server attempt + exit code, capability probe result | start-server failed, adb not found, version changed mid-session |
| `adb.devices` | tracker opened/closed, device added/removed/state change (id + state), unrecognized raw state | reconnect exhausted, malformed frame (rate-limited) |
| `adb.mdns` | tracker opened/closed, offer appeared/withdrawn, fallback engaged | mDNS unsupported/disabled, reconnect exhausted |
| `adb.pairing` | phase transitions (never the code), duration | failure reason as reported by adb |
| `adb.metadata` | probe result summary at Debug | timeout, repeated failure |
| `state` | revision published (Debug), notification intents emitted | unexpected reducer backlog, reducer invariant violated |
| `host.tray` | icon state changes, balloon delivered | balloon failed, icon re-registration |
| `host.window` | show/hide, placement decision (monitor, anchor source, rect), pin, DPI change | backdrop unavailable, placement clamped, icon rect unavailable |
| `host.webview` | created, ready, reload, recreate | process failed (kind + reason), ready timeout, profile reset |
| `bridge` | action accepted (type only) | action rejected (reason, never payload), protocol mismatch, rate limit hit |

**Diagnostics without a dashboard:** the tray tooltip is the live status channel; the log is the post-mortem channel. `--verbose` and `--log-dir <path>` are the only diagnostic flags. There is no in-app log viewer, no diagnostics screen, and no "copy diagnostics" button (not in `DESIGN.md`).

---

## 14. Repository Structure

```text
ADBView/
├── ADBView.sln
├── global.json                      # pins .NET SDK 10.0.x (rollForward: latestFeature)
├── Directory.Build.props            # TFM, nullable, warnings-as-errors, analyzers, versioning
├── Directory.Packages.props         # central package management: every version pinned, exact
├── .editorconfig                    # formatting + analyzer severities (dotnet format gate)
├── .gitattributes / .gitignore
├── DESIGN.md                        # FROZEN product/visual authority (verbatim copy, never edited by agents)
├── ENGINEERING.md                   # this document — engineering authority
├── PHASES.md                        # implementation plan; the only place phase scope lives
├── AGENTS.md                        # enforceable rules for coding agents
├── README.md                        # what it is, how to build, how to run gates
├── CHANGELOG.md
│
├── src/AdbView/                     # THE ONLY application project
│   ├── AdbView.csproj
│   ├── Program.cs                   # entry point: single instance → AppHost.Run()
│   ├── NativeMethods.txt            # CsWin32 allow-list = audit of every native call
│   ├── app.manifest                 # PerMonitorV2 DPI awareness, no UAC elevation
│   ├── App/
│   │   ├── AppHost.cs               # composition root; wires subsystems; owns shutdown
│   │   ├── SingleInstance.cs
│   │   ├── CommandLine.cs           # --verbose, --log-dir, --startup
│   │   └── IClock.cs / SystemClock.cs
│   ├── Adb/
│   │   ├── Protocol/                # AdbHostConnection, framing, response parsing, ProtoReader
│   │   ├── AdbExecutableLocator.cs
│   │   ├── AdbServerSupervisor.cs
│   │   ├── AdbCapabilities.cs
│   │   ├── DeviceTracker.cs
│   │   ├── DeviceIdentity.cs        # the only serial-shape heuristics in the repo
│   │   ├── MdnsTracker.cs
│   │   ├── PairingCoordinator.cs
│   │   ├── MetadataProbe.cs
│   │   ├── Backoff.cs
│   │   └── Model/                   # RawDevice, RawMdnsService, AdbServerPhase, …
│   ├── State/
│   │   ├── StateEngine.cs           # the single writer
│   │   ├── AppState.cs              # internal mutable-by-reduction state
│   │   ├── Reducer.cs               # pure (state, event) -> state
│   │   ├── SnapshotBuilder.cs
│   │   ├── HealthAggregator.cs
│   │   ├── TrayStatusAggregator.cs
│   │   ├── Vocabulary.cs            # dynamic semantic/shared wording from DESIGN.md
│   │   ├── AppSnapshot.cs           # the contract (§4)
│   │   └── Events.cs
│   ├── Notifications/
│   │   ├── NotificationPolicy.cs    # pure transition evaluation
│   │   ├── NotifierState.cs
│   │   ├── INotificationSink.cs
│   │   └── BalloonNotificationSink.cs
│   ├── Host/
│   │   ├── TrayIcon.cs
│   │   ├── FlyoutWindow.cs
│   │   ├── FlyoutPlacement.cs       # tray rect, taskbar edge, work area, DPI math
│   │   ├── WindowBackdrop.cs        # DWM attributes, NCCALCSIZE/NCACTIVATE handling
│   │   ├── WebViewHost.cs           # creation, hardening, recovery, transport
│   │   ├── SystemTheme.cs           # theme, transparency, contrast, reduced motion
│   │   ├── ThemePreference.cs       # System / Light / Dark (tray)
│   │   ├── EffectiveTheme.cs        # preference + OS → ThemeKind
│   │   ├── UserPreferences.cs       # HKCU\Software\ADBView
│   │   ├── StartupRegistration.cs   # HKCU Run "ADB View" + --startup
│   │   ├── PowerAndSession.cs
│   │   └── Interop/                 # thin wrappers over CsWin32 output
│   ├── Bridge/
│   │   ├── BridgeProtocol.cs        # version constant
│   │   ├── WebActions.cs            # the 8 inbound shapes
│   │   ├── BridgeSerializer.cs      # source-generated JSON, strict options
│   │   └── ActionRateLimiter.cs
│   ├── Diagnostics/Log.cs           # Serilog setup + redaction helpers
│   ├── Resources/                   # tray/app .ico per state (multi-resolution), app icon
│   └── wwwroot/                     # build output of ui/ (gitignored; required in Release)
│
├── ui/                              # THE ONLY frontend
│   ├── package.json / package-lock.json
│   ├── vite.config.ts / tsconfig.json / eslint.config.js / .prettierrc
│   ├── index.html                   # CSP meta, no external references
│   ├── src/                         # (structure in §10.1)
│   ├── harness/                     # fixture-driven story page for visual tests + dev
│   └── tests/
│       ├── unit/                    # Vitest + Testing Library
│       └── visual/                  # Playwright specs + committed baselines/
│
├── tests/AdbView.Tests/             # THE ONLY C# test project
│   ├── AdbView.Tests.csproj
│   ├── Adb/                         # protocol, framing, proto reader, identity, backoff
│   ├── State/                       # reducer, health, tray, vocabulary, invariants
│   ├── Notifications/               # timeline-driven policy tests
│   ├── Bridge/                      # validation, rate limits, protocol mismatch
│   ├── Host/                        # placement math (pure), backdrop decisions
│   ├── Integration/                 # [Trait Category=Integration] WebView, window, tray
│   ├── Fakes/FakeAdbServer.cs       # scripted host-protocol server over loopback
│   └── Fixtures/                    # captured real-device transcripts (binary + text proto)
│
├── research/                        # dated notes; required before touching external deps
│   ├── TEMPLATE.md
│   ├── 2026-09-16-dotnet-webview2-frontend-stack.md
│   ├── 2026-09-16-adb-protocol-and-wireless.md
│   └── 2026-09-16-windows-host-integration.md
│
├── docs/
│   ├── DEVICE-TEST-MATRIX.md        # manual real-hardware checklist per release
│   ├── RELEASE.md                   # release steps, signing/SmartScreen notes
│   ├── TROUBLESHOOTING.md           # user-facing: logs, adb not found, mDNS blocked
│   └── decisions/                   # short ADRs for anything that changes this document
│
├── installer/
│   ├── AdbView.iss                  # Inno Setup script (per-user, no admin)
│   └── assets/
│
├── scripts/
│   ├── build.ps1                    # ui build → copy wwwroot → dotnet publish
│   ├── test.ps1                     # unit (C#) + unit (ui)
│   ├── verify.ps1                   # THE gate: format, analyzers, all tests, visual, boundaries
│   ├── visual.ps1                   # run/update visual baselines (update requires -Accept)
│   ├── gen-contract.ps1             # C# records → ui/src/contract/snapshot.ts
│   ├── capture-fixtures.ps1         # record real-device transcripts into tests/Fixtures
│   └── package.ps1                  # installer + portable zip
└── .github/workflows/ci.yml         # windows-latest: verify.ps1 + artifact build
```

**Why this shape:** one app project means no cross-project abstraction tax and no chance of an agent inventing a layer; folders map exactly to the ownership table in §3.1, so "where does this code go?" always has one answer. `research/`, `docs/decisions/`, and `PHASES.md` are the only places narrative may live — code comments explain *why*, never re-document architecture.

---

## 15. Dependency List

### NuGet (application) — 4 runtime + 2 build-time

| Package | Version | Class | Why |
|---|---|---|---|
| `Microsoft.Web.WebView2` | 1.0.4191.47 | **required** | The UI host |
| `QRCoder` | 1.8.0 (pinned) | **required** | QR-code pairing (decision 0010): pure C#, no transitive dependencies, MIT; payload rendering only — `research/2026-10-01-qr-pairing.md` |
| `Serilog` | 4.x (pinned) | **required** | Structured logging |
| `Serilog.Sinks.File` | 7.x (pinned) | **required** | Rolling local file sink |
| `Microsoft.Extensions.ObjectPool`-style helpers | — | **avoid** | Not needed |
| `Microsoft.Windows.CsWin32` | 0.3.333 | **required (build-time)** | Win32 bindings, `PrivateAssets=all` |
| `Microsoft.CodeAnalysis.NetAnalyzers` | SDK-provided | **required (build-time)** | Warnings-as-errors gate |

Nothing else. In particular, **avoid**: `AdvancedSharpAdbClient` (§2.5), `Google.Protobuf` (a ~40-line field reader covers the three messages we read, with cross-checked fixtures), `Microsoft.WindowsAppSDK` (§2.8), any DI container (`AppHost` is ~80 lines of explicit wiring), `MediatR`/event-bus libraries (one channel), `H.NotifyIcon`/tray wrappers (`NotifyIcon` suffices), `Newtonsoft.Json` (source-generated `System.Text.Json`), `Polly` (backoff is 30 lines and must be deterministic for tests), `System.Net.Http` (**build-breaking** if referenced), `Microsoft.Extensions.Hosting` (a generic host for one process is ceremony).

### NuGet (tests)

| Package | Class | Why |
|---|---|---|
| `xunit.v3` + `xunit.runner.visualstudio` | **required** | Test framework (v3 4.0.0, MTP v2) |
| `Microsoft.NET.Test.Sdk` | **required** | `dotnet test` |
| **avoid**: Moq/NSubstitute/FakeItEasy, FluentAssertions (v8 licence), AutoFixture, Testcontainers | — | Hand-written fakes are clearer and agent-proof |

### npm (frontend)

| Package | Class | Why |
|---|---|---|
| `react`, `react-dom` | **required (runtime)** | Rendering |
| `typescript`, `vite`, `@vitejs/plugin-react` | **required (dev)** | Build |
| `tailwindcss`, `@tailwindcss/vite` | **required (dev)** | Compiled utility CSS, no CDN |
| `vitest`, `@testing-library/react`, `@testing-library/jest-dom`, `jsdom` | **required (dev)** | Unit tests |
| `@playwright/test` | **required (dev)** | Visual regression at 5 scales |
| `eslint`, `typescript-eslint`, `eslint-plugin-react-hooks`, `prettier` | **required (dev)** | Lint/format gate |
| **avoid**: `lucide-react`/icon packs, `framer-motion`/`motion`, `zustand`/`redux`/`jotai`, `react-router`, `clsx`/`tailwind-merge` (two ternaries), `date-fns`/`dayjs` (`formatAge` is 12 lines), any component library, any font package, any polyfill | — | Each would add surface for zero behaviour the design asks for |

Every version is exact (no `^`, no floating). `Directory.Packages.props` + `package-lock.json` are the pins; `AGENTS.md` R-09 forbids adding a dependency without a `research/` note and an `ENGINEERING.md` edit in the same change.

---

## 16. Testing Architecture

### 16.1 Native unit tests (fast, no I/O, no Windows UI)

| Area | Representative cases |
|---|---|
| Protocol framing | hex4 length round-trip, `OKAY`/`FAIL` + reason, payload cap enforcement, truncated frame mid-read, multi-frame stream, cancellation |
| Proto reader | every field we read, unknown fields skipped for all wire types, nested messages, malformed varint, empty message; **cross-check**: for each captured fixture, binary parse == text-proto parse |
| Device identity | USB serial, `adb-SERIAL-xxxx`, `ip:port` with and without a matching mDNS connect service, IPv6 literal, emulator serial, duplicate serials across transports |
| State reduction | every `DeviceStateKind`, out-of-order device lists, tracker-down grace period, metadata for unknown device, pairing result after cancel, revision monotonicity, no-change events produce no revision |
| Health aggregation | offline device stays local; mDNS failure never global; `AdbRestoring` vs `AdbUnavailable` boundary at 10 s; recovery paths |
| Tray aggregation | all six priorities, ties, tooltip text and truncation |
| Vocabulary | every dynamic semantic/shared string rendered exactly; golden-string tests cite the relevant `DESIGN.md` lines. Frozen static interface copy is tested in frontend component/visual tests. |
| Notification policy | scripted timelines: startup baseline, 0.5 s flap, 60 s dedup, burst collapse, resume suppression, pairing exemption, global-failure-once, visible-flyout suppression |
| Backoff | sequence, jitter bounds, reset on success, cap |
| Bridge validation | unknown type/member, bad protocol, non-numeric code, unknown offerId, height clamping, rate limits |
| Placement math | four taskbar edges, work-area clamping, mixed DPI scale factors, 1366×768, anchor-source fallback (all pure functions over rect inputs) |

### 16.2 Fake ADB server

`FakeAdbServer` listens on an ephemeral loopback port and replays scripted conversations: device-list sequences, mid-stream disconnects, `FAIL` responses, capability-probe refusals, mDNS service appearance/withdrawal, pairing success/failure/hang, slow writes. It lets the *entire* pipeline (protocol → trackers → reducer → snapshot → notification intents) run deterministically in milliseconds, including every row of §11 that does not involve Windows itself. No mocks of our own types are needed.

### 16.3 Frontend tests (Vitest)

Every state in `DESIGN.md` is a committed fixture snapshot, rendered and asserted: connected USB, connected Wi-Fi, expanded card with copy feedback, unauthorized, offline, recovery, bootloader, sideload, unknown, empty, ADB unavailable, restoring ADB, wireless-discovery unavailable/restoring, pairing offer, code entry, pairing in progress, paired-waiting, pairing failed, 1/2/3/8 devices, pairing + devices together, pinned, light/dark/opaque/forced-colors, reduced motion, long device names, missing battery, missing model/version, age formats. Plus: only one card expanded at a time; older revision ignored; actions emitted exactly once per click; no `postMessage` outside `actions.ts` (repo-scan test).

### 16.4 Visual regression (Playwright)

The `harness/` page renders any fixture by name with the snapshot injected directly (no bridge, no ADB). For each of a curated set of ~22 fixtures × themes, screenshots are captured at `deviceScaleFactor` 1.0, 1.25, 1.5, 1.75, 2.0 with `emulateMedia` for `colorScheme`, `forcedColors`, and `prefers-reduced-motion`, and compared against committed baselines (`maxDiffPixelRatio` 0.002). The frozen Stitch exports are the human reference for the *initial* baseline review in Phase 2; thereafter baselines are truth and any diff is a failure until a human accepts it via `scripts/visual.ps1 -Accept`, which records the WebView2/Chromium version in the commit.

### 16.5 Integration tests (Windows, headed)

Real `WebView2` + real `Form`, skipped when no interactive desktop: bridge round-trip (`ready` → snapshot → action → snapshot), snapshot coalescing under 100 rapid revisions, WebView reload preserves state, forced `ProcessFailed` recovery, window show/hide/light-dismiss, pin keeps the window visible on deactivation, placement on the primary monitor within the work area, tray icon survives an Explorer restart, second-instance toggle, protocol-mismatch read-only mode.

### 16.6 Real-device validation (manual, per release)

Mocks do not prove ADB integration. `docs/DEVICE-TEST-MATRIX.md` is a signed-off checklist executed on at least one Android ≤ 16 device and one Android 17 (Wi-Fi 2.0) device:

USB connect/disconnect · first-time authorization dialog · revoke authorizations · unplug while expanded · two devices at once · wireless pairing (code) · pairing with a wrong code · pairing then waiting for auto-connect · wireless connect/disconnect · Wi-Fi off/on · switch networks · device sleep/wake · battery value matches the phone · exact model and Android version correct · recovery mode · bootloader mode · sideload mode · `adb kill-server` while running · upgrade platform-tools mid-session · another tool (Android Studio / scrcpy) using ADB simultaneously · mDNS-blocked network · laptop sleep/resume with devices attached · all of the above at 100 % and 200 % scaling on a mixed-DPI setup.

Each run records platform-tools version, Windows build, WebView2 runtime version, and device OS builds. Captured transcripts are committed as fixtures so the same behaviour becomes a permanent automated test.

### 16.7 Gates

`scripts/verify.ps1` must pass before any agent declares work complete: `dotnet format --verify-no-changes` → build with warnings-as-errors → C# unit tests → contract regeneration diff check → `npm ci` → eslint/prettier → tsc → Vitest → Playwright visual → source-boundary tests (no WinForms controls in product UI, no `postMessage` outside `actions.ts`, no forbidden component names, no `System.Net.Http`, no new dependency without a research note, no `DateTime.Now` outside `SystemClock`) → Release publish smoke run (`--exit-after-startup-selftest`).

---

## 17. Build / Packaging / Release Architecture

### 17.1 Development loop

```
ui:   npm run dev           → Vite dev server on 127.0.0.1:5173
app:  dotnet run -- --dev   → WebViewHost navigates to the dev server instead of the virtual host
                              and feeds fixture snapshots when --fixtures <name> is passed
```
`--dev` is compiled out of Release builds (`#if DEBUG`), so a shipping binary cannot load remote content. Fixture mode is how the whole UI is developed and reviewed before ADB exists (Phases 2–3).

### 17.2 Build pipeline

```
scripts/build.ps1
 ├── npm ci                        (ui/, lockfile-exact)
 ├── npm run build                 (Vite → ui/dist, fails on any absolute http(s) asset)
 ├── copy ui/dist → src/AdbView/wwwroot
 ├── dotnet publish src/AdbView -c Release -r win-x64   --self-contained
 │     PublishSingleFile=true, PublishReadyToRun=true, PublishTrimmed=false,
 │     IncludeNativeLibrariesForSelfExtract=<per V-6>, InvariantGlobalization=false
 └── repeat for win-arm64
```
A Release build **fails** if `wwwroot/index.html` is missing, so a native-only build can never ship an empty window.

### 17.3 Packaging

- **Installer:** `installer/AdbView.iss` (Inno Setup 6) — per-user install to `%LOCALAPPDATA%\Programs\ADBView`, no admin prompt, Start Menu shortcut, optional "Start ADB View when I sign in" checkbox writing `HKCU\...\Run` value `ADB View` (same registration the tray can toggle), WebView2 Evergreen runtime detection with the official bootstrapper offered if absent, clean uninstall that removes the `Run` value, the WebView2 profile, and logs.
- **Portable:** versioned zip of the publish output for users who refuse installers.
- **Versioning:** `Directory.Build.props` holds `<Version>`; SemVer; tag `vX.Y.Z` drives the release workflow. Version is visible in the installer, file properties, and every log header.

### 17.4 Release process (`docs/RELEASE.md`)

1. `scripts/verify.ps1` green on a clean clone.
2. Real-device matrix (§16.6) executed and signed off.
3. Visual baselines reviewed; no unexplained diffs.
4. Version bump + CHANGELOG.
5. `scripts/package.ps1` → installer + zip for x64 and arm64.
6. Fresh-VM install test: install → reboot → verify startup, tray, flyout, USB device, wireless pairing, uninstall.
7. Tag and publish artifacts with SHA-256 hashes.

### 17.5 Updates

**v1 intentionally ships no auto-update.** Rationale: an updater is a background network service with its own failure modes, signing requirements, and attack surface — strictly larger than the product it would update. There is no update server to operate, no silent-restart behaviour to get wrong, and no telemetry. Users update by installing the new version over the old (the installer handles it). This is a deliberate, revisitable decision recorded in `docs/decisions/0001-no-auto-update-in-v1.md`; if v2 wants updates, Velopack or MSIX + Store are the candidates to evaluate *then*.

---

## 18. AGENTS.md Rules (canonical copy lives at repo root)

The repo-root `AGENTS.md` is the enforceable text; it is reproduced here because it is part of the architecture. Rules marked **[auto]** are enforced by `scripts/verify.ps1`, not by good intentions.

1. **R-01 · DESIGN.md is law.** Never edit `DESIGN.md`. If implementation reveals a missing state, stop, write `docs/decisions/NNNN-*.md`, get the design contract updated, then implement. **[auto: DESIGN.md hash check]**
2. **R-02 · No redesign.** No new component variants, no compressed cards, no alternate layouts, no substituted visual language, no "improved" spacing, no extra chrome. Density means scrolling. **[auto: forbidden component names, token-usage scan]**
3. **R-03 · No product-scope expansion.** Nothing outside observe → explain → notify → pair. No shell, logcat, install, transfer, mirroring, reboot, forwarding, screenshots, history, charts, analytics, settings screens, or tray-menu items beyond Exit.
4. **R-04 · Architecture boundaries are fixed.** One app project, one test project, one frontend. No new projects, no DI container, no WinForms/WPF controls rendering product UI, no product state in `Host/`, no Windows API calls from `State/`, no ADB knowledge in `Bridge/` or the frontend. **[auto: source-boundary tests]**
5. **R-05 · No polling where an event or stream exists.** Device and mDNS state come from persistent streams. New timers require a justification in `ENGINEERING.md` and a `research/` note proving no event mechanism exists. The only sanctioned periodic work is the server-probe backoff, the pinned 120 s metadata tick, and the frontend's 1 s age ticker while visible. **[auto: scan for new Timer/Delay loops in Adb/ and State/]**
6. **R-06 · Latest AppSnapshot wins.** Never queue, delay, or interpolate state behind an animation; never render optimistic local state for pin, pairing, or device data; never keep a second copy of truth.
7. **R-07 · The frontend has no business logic.** No adb execution, no raw-output interpretation, no enum-to-semantic-text mapping, no health computation, no recovery policy, no retry logic. Dynamic semantic/state wording belongs in `Vocabulary`. Frozen static interface copy may live in frontend components only when it is directly specified by `DESIGN.md` and does not encode backend meaning.
8. **R-08 · Never swallow a failure.** No empty `catch`, no `catch { }` around I/O, no ignored return codes. Every failure either changes state, retries with owned backoff, or is logged with a subsystem and a reason. **[auto: analyzer + scan]**
9. **R-09 · No silent dependencies.** Adding any NuGet or npm package requires: a `research/` note, a `Directory.Packages.props`/`package.json` exact pin, and an `ENGINEERING.md §15` entry in the same change. **[auto: dependency-diff check]**
10. **R-10 · Research before touching external integrations.** Before changing anything about ADB, WebView2, DWM, tray, notifications, or the build/package chain, write or update a dated `research/` note with official sources and the version investigated. Stale model knowledge is not evidence. **[auto: phase checklist in PR template]**
11. **R-11 · Tests are part of the change.** New behaviour ships with unit tests; new UI states ship with a fixture, a Vitest test, and a visual baseline; new failure modes ship with a `FakeAdbServer` scenario. Reliability rows in §11 may not be edited without a corresponding test.
12. **R-12 · Run the gate.** `scripts/verify.ps1` must pass before declaring any task complete. Never claim success from a partial build; never disable a test, lower a threshold, or add a suppression to make the gate pass. If a baseline genuinely must change, use `scripts/visual.ps1 -Accept` and say so.
13. **R-13 · Security invariants.** No host objects in WebView2, no remote content, no DevTools in Release, no `ExecuteScriptAsync` for state, no user data on a command line, no pairing code in a log, no telemetry, no `System.Net.Http`. **[auto]**
14. **R-14 · ADB citizenship.** Never kill or reconfigure the ADB server, never set `ADB_*` environment variables, never bundle or download platform-tools, never issue device-mutating commands. Pairing is the only write operation and only on explicit user action.
15. **R-15 · Vocabulary discipline.** `Vocabulary` owns dynamic semantic/state wording shared across flyout, tooltip, and notifications. Frozen static interface copy may live in the frontend when it comes directly from `DESIGN.md`. No technical jargon (TLS, daemon, mDNS, transport id, protocol) may appear in any user-visible surface.
16. **R-16 · Small, verifiable increments.** One phase at a time, in `PHASES.md` order. Do not begin a phase whose predecessor's acceptance criteria are unmet. Do not refactor outside the phase's declared scope.

---

## 19. Implementation Phases

Twelve phases, each beginning with a short research step and ending with an automated gate. **`PHASES.md` is the authoritative, fully detailed plan** (files, tests, acceptance criteria, out-of-scope per phase); this section is the map and the rules that bind every phase.

**Rules that apply to all phases:** (a) start by writing/refreshing the phase's `research/` note; (b) no phase may begin until the previous phase's acceptance criteria are met; (c) every phase ends with `scripts/verify.ps1` green; (d) every phase declares what it is *not* doing; (e) no phase may introduce a dependency, a component variant, or a timer without the corresponding `ENGINEERING.md` edit.

| # | Phase | Proves | Key gate |
|---|---|---|---|
| 0 | Repository and gates | An empty repo that already enforces the architecture | `verify.ps1` passes on a repo with no product code |
| 1 | Windows shell | Tray + borderless acrylic flyout + placement + pin + drag + dismiss + single instance, showing a static local page | Manual DPI/taskbar matrix + integration tests; acrylic verified on a pinned, unfocused window |
| 2 | Frozen UI from fixtures | The exact B.1/C.1/C.3/D.1 design for **every** state, rendered from fixture snapshots in the browser harness | Vitest state coverage + visual baselines at 5 scales, reviewed against the Stitch exports |
| 3 | Bridge | Typed snapshot transport, 8 validated actions, `ready` handshake, coalescing, recovery re-push — still no ADB | Integration round-trip; 100-revision coalescing; reload preserves state |
| 4 | ADB protocol core | Host-protocol client, proto reader, executable discovery, server supervisor, backoff — driven entirely by `FakeAdbServer` | Unit + fake-server tests for §11 rows 1–4; capability probe logged |
| 5 | Device tracking | Real devices appear, change state, and disappear through the real pipeline | Fake-server determinism + first real-hardware run; fixtures captured |
| 6 | Wireless discovery and pairing | Offers modelled independently; full pairing state machine; paired ≠ connected | Fake-server pairing scenarios + real pairing on Android ≤16 and 17 |
| 7 | Metadata | Battery / model / Android version under the visibility-aware policy | Idle-cost test: no device traffic while hidden |
| 8 | Notifications | Transition detection with startup/flap/dedup/burst policy, delivered via the tray | Timeline unit tests; no notification storm on launch, resume, or flapping |
| 9 | Reliability hardening | Every §11 row exercised deliberately, including Explorer restart and WebView kill | Chaos suite green; no path restarts everything on a timer |
| 10 | Packaging | Self-contained x64/arm64 packages, per-user installer, portable zip, host package-lifecycle acceptance | Package + install/upgrade/uninstall gates green; pristine clean-VM / ARM64-runtime / reboot / WebView2-missing / SmartScreen deferred to Phase 11 |
| 11 | Release hardening | Real-device matrix, docs, log review, performance idle check; Phase 10 deferred release validation | Signed-off matrix; deferred packaging rows closed or public support narrowed; idle CPU ≈ 0 %, working set within budget; v1.0.0 tagged |

**Sequencing rationale:** Phases 1–3 prove the Windows shell, the frozen UI, and the contract *before* ADB exists, using deterministic fixtures — exactly as the brief requires. ADB then arrives behind a fake server (4) before real hardware (5), then the genuinely tricky wireless/pairing domain (6), then cheap metadata (7), then the derived notification layer (8). Reliability (9) is a dedicated phase because it is the product's actual value proposition, not a cleanup pass. Packaging (10) and release (11) are last because they are cheapest to redo.

**Performance budget, enforced from Phase 7 onward:** idle CPU indistinguishable from 0 % over 10 minutes with one connected device and the flyout hidden; zero device traffic while hidden and unpinned; flyout visible within 120 ms of the tray click (window already created, WebView warm); state change visible within 250 ms of the ADB event; working set < 120 MB with the WebView alive.

---

## 20. Major Risks

| Risk | Why it is dangerous | Containment |
|---|---|---|
| **Acrylic on a borderless flyout** | DWM ignores `DWMWA_SYSTEMBACKDROP_TYPE` on frameless `WS_POPUP` windows and flattens material on inactive windows — a pinned flyout is inactive most of the time. Failure looks like "the design is wrong", not "the API is subtle". | Documented technique (`WS_CAPTION` + `WM_NCCALCSIZE` collapse + `WM_NCACTIVATE(TRUE)`) proven in Phase 1 before any UI work; opaque token fallback is a first-class, already-designed state, so worst case is a finished-looking opaque window (`DESIGN.md §22`) |
| **Tray anchoring across taskbar positions, overflow, and mixed DPI** | The most visible possible bug (window offscreen or over the taskbar) and hard to unit-test | Placement is a pure function over rects → exhaustively unit-tested; three-tier anchor resolution (icon rect → taskbar edge → cursor); mandatory manual matrix in Phases 1 and 11 |
| **ADB host-service and proto details** | Exact service strings/field numbers are AOSP implementation detail; a wrong guess means no devices at all | Capability probe at startup, text-proto cross-check tests, documented text fallbacks for every proto service, transcripts captured from real hardware as permanent fixtures (V-1, V-9) |
| **Wi-Fi 2.0 changing pairing timing** | On Android 17 the device may auto-connect *before* our pairing call returns, so a naive machine would either hang in "pairing" or claim success wrongly | `PairedWaitingForDevice` resolves on either signal, whichever arrives first; device tracker is always authoritative; 60 s quiet timeout |
| **mDNS environments** | Corporate/guest/VM networks silently block multicast; users blame the app | Wireless discovery is a *local* subsystem warning with explicit "USB ADB is still working normally", never a global failure; documented in `docs/TROUBLESHOOTING.md` |
| **ADB version skew / shared server** | Another tool upgrading platform-tools restarts the server under us; two adb versions fight for port 5037 | Never bundle adb, never kill the server, detect version change and re-probe capabilities; hold devices 2 s across restarts so the UI does not flicker |
| **WebView2 evergreen drift** | A Chromium update can change rendering or break a CSS feature we rely on (`0fr→1fr`, `@starting-style`) | Visual baselines at 5 scales with recorded runtime version; graceful degradation (expansion without animation still renders correctly); V-8 |
| **Notification spam** | The fastest way to make a background utility hated | Notifications are derived from debounced transitions by one pure function with startup/resume suppression, dedup, and a burst cap — all timeline-tested; nothing else in the codebase may raise a notification |
| **Agent drift** | Agents "improve" the design, add a compact card, or add a dependency | `AGENTS.md` R-01…R-16 with automated enforcement in `verify.ps1`: DESIGN hash, forbidden names, source boundaries, dependency diff, contract regeneration, visual baselines |
| **Single-file self-contained + WebView2 loader** | Ships fine, fails on a clean machine | V-6 host package lifecycle + self-contained publish proven in Phase 10; pristine clean-VM proof deferred to Phase 11; `IncludeNativeLibrariesForSelfExtract` fallback; portable zip as second artifact |
| **Hand-rolled protobuf reader** | A subtle parser bug would be silent and total | Reads only known field numbers, skips unknown by wire type, capped lengths, fuzz-ish malformed-input tests, and a binary-vs-text cross-check on every captured fixture |

---

## 21. Open Validation Items

Only items that genuinely require implementation or device testing. Each must be closed by a `research/` note or a test before the phase that depends on it completes.

| ID | Item | Phase | Fallback if it fails |
|---|---|---|---|
| **V-1** | Exact host-service strings and payload shapes on installed platform-tools 37.x: `host:track-devices-proto-binary`, streaming mDNS tracking (`host:track-mdns-services` vs a suffixed variant), `host:server-status`, `host:pair:<code>:<host>:<port>` | 4–6 | Text variants (`track-devices-l`, `adb mdns track-services --proto-text` child stream, `adb server-status`, `adb pair` with stdin code) |
| **V-2** | Desktop Acrylic on the borderless flyout, including a pinned/unfocused window keeping live material, with a transparent WebView2 on top | 1 | Opaque token set (already designed) |
| **V-3** | `Shell_NotifyIconGetRect` behaviour when the icon is in the overflow flyout or hidden, and on secondary monitors with different DPI; WebView2 clipboard-write permission behaviour | 1, 2 | Taskbar-edge placement fallback; `document.execCommand('copy')` shim if clipboard API is gated |
| **V-4** | `IsNonClientRegionSupported` + `app-region: drag` in a frameless host: drag, double-click, and no interference with card clicks | 1 | Native `WM_NCHITTEST`→`HTCAPTION` over the header strip |
| **V-5** | Tray balloon presentation quality on current Windows 11 (banner appearance, app naming, Action Center behaviour, Do-Not-Disturb) | 8 | Swap `INotificationSink` for Windows App SDK `AppNotificationManager` (isolated change; adds one dependency and a §15 entry) |
| **V-6** | Single-file self-contained publish + WebView2 loader + Segoe UI Variable presence on clean Windows 11 SKUs (x64 and arm64) | 10 (host package lifecycle closed); pristine clean-SKU / ARM64-runtime rows → 11 | `IncludeNativeLibrariesForSelfExtract=true`; non-single-file publish; Segoe UI fallback chain already in the token set |
| **V-7** | Battery via `dumpsys battery` across OEMs (Samsung/Xiaomi/Pixel) and in Doze; exact-model/Android-version property availability | 5, 7 | Omit battery (design already supports absence); `cmd battery get level` as an alternative probe |
| **V-8** | Instant disclosure layout + frozen visible HWND (decision 0005) at 125–200 % scaling without blank frames or outer-window churn | 2 / pre-10 | Content-driven height on show only; inner scroll for overflow; no FLIP/native structural animator |
| **V-9** | Minimal protobuf reader vs `--proto-text` output for the same real-device state, including multi-device and IPv6 records | 4–6 | Use text variants as the primary parse path |
| **V-10** | Wi-Fi 2.0 auto-connect interaction: whether a paired device connects before `host:pair` returns, and whether offers withdraw promptly | 6 | Dual-resolution rule already designed; tune the 60 s quiet timeout |

---

## 22. Final Architecture Verdict

**This architecture is appropriately simple for the product.**

What ships: one process, one application project, one test project, one frontend, four runtime dependencies (WebView2, two Serilog packages, plus React/React-DOM on the web side), two build-time dependencies, three long-lived sockets, one state loop, one snapshot type, one message type native→web, eight actions web→native, and one component per design element.

What the reliability requirements genuinely force, and therefore what stays: supervised streams with owned backoff (the app must survive an ADB server restart), a single-writer state engine (concurrent device/mDNS/metadata events otherwise race), a pure notification policy with debounce (otherwise the product is hated within a day), a fake ADB server (otherwise nothing about recovery is testable), a hand-written protocol client (otherwise the modern event-driven signals are out of reach and the critical subsystem rests on an unmaintained dependency), and the DWM/WebView2 host details that make a frameless acrylic flyout behave like a Windows flyout instead of an app window.

Removed before presenting this architecture, each because reliability did not depend on it: a second process and any IPC service; a DI container; `Microsoft.Extensions.Hosting`; a message-bus library; Windows App SDK (pulled in only by a notification API richer than the design allows); `Google.Protobuf` (three messages, a few fields); AdvancedSharpAdbClient; a tray-icon library; an animation library; an icon package; a state-management library; a router; a settings store and settings screen; persisted pin state; an auto-updater and its signing/hosting apparatus; telemetry and crash reporting; an in-app log viewer or diagnostics screen; `Retry ADB` / `Restart ADB` controls (self-healing makes them dishonest); a history/analytics store; separate Domain/Application/Infrastructure projects; mocking and assertion libraries; a per-second snapshot tick (ages are formatted client-side from timestamps, which is why the app is genuinely idle).

The remaining complexity is concentrated exactly where real Windows and real ADB are hostile — tray anchoring, DWM material, WebView lifetime, ADB server lifetime, wireless pairing semantics — and it is contained behind small, individually testable owners. Everything else is a straight line from an ADB event to one immutable snapshot to a frozen design.

Sophistication here is meant to be invisible: the tray icon is right, the flyout says the true thing, nothing shouts, and it is still running correctly a month later.
