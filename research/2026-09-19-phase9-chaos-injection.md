# Phase 9 chaos injection and recovery definitions

- **Date:** 2026-09-19
- **Author:** agent (Phase 9A)
- **Phase:** 9
- **Status:** current

## Problem

PHASES.md Phase 9 requires a dated note recording how each ENGINEERING.md §11
failure is injected and what “recovered” means, before claiming chaos coverage.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| ENGINEERING.md §11 Reliability Model | repo | Phase 8-complete HEAD |
| ENGINEERING.md §7.1 / §9.6–9.7 | repo | same |
| PHASES.md Phase 9 | repo | same |
| FakeAdbServer / tracker / supervisor tests | `tests/AdbView.Tests` | observed |
| WebView2 ProcessFailed | Microsoft Learn CoreWebView2.ProcessFailed | current |
| WinForms NotifyIcon TaskbarCreated | .NET WinForms NotifyIcon | .NET 10 |

## Version investigated

- OS: Windows 11 10.0.26100
- .NET SDK: 10.0.401
- WebView2 Evergreen (host runtime)
- platform-tools: 37.0.1 (live matrix only)

## Findings

### Injection seams (hermetic)

| §11 row | Injection | Recovered means |
|---|---|---|
| 1, 3 | Fake process runner / FakeAdbServer refuse probe then accept; composition posts supervisor transitions | Phase Up + trackers running for new/same generation; devices return via list |
| 5 | Fake track-devices EOF / socket close mid-stream | New stream session; grace retains list ≤2 s then clears only if unrecovered |
| 8 | Fake mDNS stream EOF / CLI hang-then-cancel | Tracker reconnect; wireless warning timers cancel on success |
| 11 | Fake host:pair FAIL / wrong-code script | PairingPhase Failed; retry available; no code in logs |
| 26 | Flood `StateEngine.Post` faster than drain | Events retained; rate-limited backlog Warning once pending ≥ 256 |

### Injection seams (Windows / headed)

| §11 row | Injection | Recovered means |
|---|---|---|
| 16 | Test hook / ProcessFailed RenderProcessExited | Reload; bridge ready; latest snapshot |
| 17 | ProcessFailed BrowserProcessExited | Control recreate with backoff 1/5/15/60 s; ≤3 attempts per 5 min |
| 19 | Real Explorer restart or `HandleTaskbarCreated` | Single tray icon Visible again; tooltip/status reapplied |
| 24 | Second process launch | Mutex owner receives toggle; second exits 0 |

### Soak / resource budgets (proposed)

PHASES requires a “fixed budget” without numbers. Proposed acceptance for Phase 9C
(measured on a warm Release run vs cold baseline on the same machine):

| Metric | Acceptance |
|---|---|
| Working set | &lt; 120 MB (ENGINEERING §19, hard) |
| Private bytes / handles / threads | no unbounded monotonic growth across soak samples; sample Δ ≤ 25% vs median of first 3 samples after warmup |
| Unhandled exceptions in log | zero |
| Hermetic churn soak | ≥ 2000 FakeAdbServer connect/disconnect/fail cycles; ≤1 DeviceTracker and ≤1 MdnsTracker per active generation |

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| New chaos framework NuGet | Rich DSL | Forbidden dependency surface |
| Only live destructive chaos | Realism | Non-deterministic; slow |
| Extend FakeAdbServer + virtual clocks | Matches §16.2; deterministic | Must still do headed Explorer/WebView |

## Chosen approach

Extend existing fakes and add `tests/AdbView.Tests/Integration/Chaos/*` with
virtual clocks/delays. Headed rows use thin WebViewHost test hooks and a
gated Explorer restart test. Implement missing row-26 backlog warning and
align WebView recreate backoff to §9.7.

## Why

Matches frozen architecture: owned recovery, no blanket restart timer, no new
subsystems.

## Rejected approaches

- kill-server as a product API
- Dropping channel events under load
- WinAppSDK notifications for reliability

## Compatibility concerns

Explorer restart is destructive to the desktop session; gate behind interactive
desktop / explicit env. WebView ProcessFailed cannot be forced without hooks on
CI agents without WebView2.

## Testing implications

- Unit: backlog threshold, WebView backoff sequence, lifecycle log wiring
- Chaos*: §11 rows 1,3,5,8,11,26 hermetic; 16,17,19,24 headed or recorded
- Soak: hermetic churn in CI + `scripts/soak-phase9.ps1` for 12 h evidence
- Live A06: disconnect/connect only; honesty tags in DEVICE-TEST-MATRIX

## Open items

- Confirm soak Δ% budget after first overnight run; adjust only with research refresh.
- Left-click WebView retry after tray-only stop (ENGINEERING §9.7) wired in AppHost ShowFlyout.
