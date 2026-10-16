# Shared WinUI motion and presentation inactivity

- **Date:** 2026-10-16
- **Author:** Codex
- **Phase:** authorized M07
- **Status:** current

## Problem

Carry the proven M02 animation mechanism into the candidate without delaying snapshots/input or leaving presentation clocks/subscriptions active while hidden.

## Sources consulted

| Source | URL | Version / last updated |
| --- | --- | --- |
| Microsoft XAML / Composition interop | https://learn.microsoft.com/en-us/windows/apps/develop/composition/xaml-comp-interop | 2026-07-07; consulted 2026-10-16 |
| Microsoft ExpressionAnimation | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.expressionanimation?view=windows-app-sdk-2.0 | App SDK API; consulted 2026-10-16 |
| Microsoft AnimationsEnabledChanged | https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.animationsenabledchanged?view=winrt-26100 | SDK 26100; consulted 2026-10-16 |
| Microsoft DispatcherQueueTimer | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherqueuetimer?view=windows-app-sdk-2.0 | App SDK API; consulted 2026-10-16 |
| Microsoft scoped-batch lifecycle | https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositionscopedbatch?view=winrt-26100 | Composition lifecycle contract; consulted 2026-10-16; pinned Microsoft.UI behavior also tested |

## Version investigated

.NET SDK 10.0.401; Windows App SDK 2.5.1 (resolved WinUI component 2.3.9); net10.0-windows10.0.26100.0; Windows build 26100. Existing M02 SurfaceMotionProbe / ProbeContentController and retained temporal results are the shipped-binary evidence. No new dependency.

## Findings

The documented UIElement.StartAnimation interop targets opacity/translation directly. M02 already exercised keyframes starting from `this.StartingValue`, scoped completion batches, and explicit settling. Do not read a XAML base property as presented-value evidence. UISettings exposes the preference and change event; marshal events to the dispatcher and remove the subscription on disposal. The sanctioned one-second age ticker may use DispatcherQueueTimer but must stop whenever no age text is visible. This is text formatting, never state polling.

## Alternatives considered

| Option | Pros | Cons |
| --- | --- | --- |
| Existing M02 Composition mechanism | Proven pinned API; independent animation | Requires explicit generation/subscription/handle cleanup |
| Per-frame application callback or timer estimator | Simple diagnostics | Prohibited production mechanism; duplicates compositor |
| Surface animation in opaque host | Visible movement | Violates chosen fallback B |

## Chosen approach / why

Named MotionPolicy recipes and separate SurfaceTransitionController/ContentTransitionController. Fallback B means immediate outer show/hide and no SurfaceEnter/Exit clocks. Animate only the current incoming page container; detach old content immediately. Replacement while active uses the M02 StartingValue mechanism; no stopping/snapping first. Each completion owns a generation and releases its resources. Same-target navigation is a no-op. Reduced motion and lifecycle stops invalidate pending callbacks, settle current content, stop native busy indicators and cancel copy feedback. Re-enable only future motion. One-second age updates touch only age text, gated by window and route visibility. Expose the existing ShellAppearanceChangedEvent destination; fixture composition records it without live backend startup. M08 attaches power/session/native lifetime sources.

## Rejected approaches

No transparent envelope reopening, new renderer, dependency, WPF motion rewrite, new periodic work, application rendering callback or synthetic estimate of presented position. Feedback animation is optional; the existing acknowledgement remains immediate with a cancellable hold.

## Compatibility concerns

Read system preference before first show and combine with snapshot reduced motion; either disabled input wins. Native controls retain their own platform behavior. Physical system-toggle, mixed-DPI and hardware cadence qualification remain M09; injected source tests do not claim those results.

## Testing implications

Real WinUI interruption at start/middle/end, replacement input, same target, snapshot churn, busy/reduced motion, suspend/hide/reopen/dispose and 500 cycles with bounded handles/clocks. Reuse the existing desktop pixel recorder for temporal proof; static screenshots and completion counters alone cannot prove it. All existing fixtures/tests/map assertions remain.

## Open items

Current-machine temporal evidence required for M07. Integrated hardware/accessibility/performance and actual power/session subscription qualification stay in their assigned later gates.

## M07 result

The final full gate passes 1,066 cases and 28 real WinUI fixtures / 661 assertions. Existing M02 StartAnimation/StartingValue/scoped-batch mechanism works in the candidate; no alternative renderer or third animation experiment. The final-binary cropped DXGI recording has 589 frames, 12 ordinary navigations and same-pixel replacements at 42/83/150 ms. Integer physical-pixel shift plus fitted opacity against each settled same-text control detects no endpoint restart; late near-zero travel limits precision. Inspected raw-phase and content/retarget strips accompany JSON. Navigation changing-frame p95/p99/max 7.65/14.22/14.41 ms; first-content proxy p95 147.22 ms. This is not physical scanout, a warm-open result or 60 Hz certification.

Native Click subscription retention on discarded detail controls was removed at the page-disposal boundary; no cache/redesign. Initial unequal first-use resource measurements failed and are preserved. Equal 500-cycle native-control warmup followed by 500 animated cycles uses no forced collection and passes ≤10 handles / ≤10 MiB growth: +6 handles, +3,346,432 private bytes. All owned animation handles/subscriptions and hidden age/progress clocks stop. A 10-second scheduled 1,000-snapshot burst reaches latest truth without dropping commands. Static reduced-busy and four native captures pass on build 26100. Existing backend/WPF/prototype sources remain unchanged.

Temporal tooling reuses the M02 observer source verbatim except class/strict process-name adaptation in a generated nonshipping artifact. Production has no frame callbacks or recording path outside the explicit fixture verification mode. No Windows preference or physical suspend cycle was changed. UISettings subscription is real; injection proves event dispatch/disposal and the existing appearance-event shape, while M08 composes that destination into the live backend and attaches actual power/session sources. M09/M12 retain physical and integrated qualification.
