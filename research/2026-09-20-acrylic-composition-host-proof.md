# Pre-Phase-10 Acrylic composition-hosting proof (bounded)

- **Date:** 2026-09-20
- **Status:** proof **not viable** within frozen dependency/stack

## Question

Can ADB View migrate from WinForms `WebView2` child-HWND hosting to
`CoreWebView2CompositionController` / visual hosting to keep Transient Acrylic
live under transparent web content — **without** WinAppSDK and **without** a new
package beyond `Microsoft.Web.WebView2` 1.0.4191.47?

## Sources

- Microsoft: `ICoreWebView2CompositionController` (Win32) — visual hosting exists in native C++.
- MicrosoftEdge/WebView2Feedback #4630 (2024): **.NET does not implement
  `CoreWebView2CompositionController`**; no plans stated for WinForms.
- WPF `WebView2CompositionControl` exists only as **prerelease** and is WPF-scoped —
  not usable by this WinForms host without a framework change forbidden by ENGINEERING.md.
- Diagnosis pixel sampler (`artifacts/diagnosis/acrylic-pixel-*.json`):
  - native Acrylic only: median **37 ms**, 0/12 multi-second stale
  - transparent WebView2 child: **12/12** no-detect ≥3.5 s

## Rejected alternatives (forbidden by correction brief)

RedrawWindow/DwmFlush timers, 1px jiggle, CSS backdrop-filter, GPU disable,
pinned runtime, private internals.

## Verdict

**Composition-hosted proof cannot be built** in the current product stack.

## Recommendation

**OPAQUE V1 FALLBACK DECISION REQUIRED** (human approval before DESIGN.md change).

Do not auto-apply opaque fallback in this correction wave.
