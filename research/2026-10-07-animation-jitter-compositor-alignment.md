# Animation jitter: top-level window bounds animation, compositor alignment, and why window alpha is not the fix

- **Date:** 2026-10-07
- **Author:** agent (ZCode)
- **Phase:** post-Phase-12 hardening (native shell; the I1–I5 phases exist only in decision 0015)
- **Status:** current

## Problem

The owner reports that the native shell's animations are "quite jittery": the flyout sliding
out of and into the tray, and device details opening and closing. This note establishes, from
official sources, what causes that class of jitter, what the sanctioned alternatives are, and
whether the owner's two candidate remedies — *slow the animations down* and *add an alpha to the
window so a resize does not expose background* — are sound. It is required by R-10 before
touching presentation behaviour.

The implementation under investigation:

- `Host/FlyoutRevealAnimator.cs` — a 16 ms tick that interpolates a `Rectangle` and applies it
  with `SetWindowPos`; 167 ms open, 83 ms close, 167 ms growth; the frozen curve
  `cubic-bezier(0.55, 0.55, 0, 1)` solved by 24-iteration bisection.
- `NativeUi/DispatcherTimerTickSource.cs` — `new DispatcherTimer(DispatcherPriority.Send) { Interval = 16 ms }`.
- `NativeUi/NativeFlyoutWindow.xaml.cs` — `ApplySelfSizedHeight()` attached to
  `shell.ShellRoot.LayoutUpdated`, applying an instant clamped height step with `SetWindowPos`
  *and then* assigning the WPF `Height`/`Top` properties; window keeps `WS_CAPTION` for DWM
  framing plus `DWMWCP_ROUNDSMALL`; ctor seeds `Background = Brushes.Black`.
- `NativeUi/Themes/Controls.xaml` — `Motion.Feedback` 83 ms, `Motion.State` 167 ms,
  `Motion.Structural` 333 ms, and every trigger animation is a plain linear
  `DoubleAnimation` with no easing function.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| `SetWindowPos` function | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos | ms.date 2018-12-05, updated 2025-07-01 |
| `AnimateWindow` function | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-animatewindow | ms.date 2018-12-05 |
| Window Features (Layered Windows) | https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features | Win32 |
| Desktop Window Manager overview | https://learn.microsoft.com/en-us/windows/win32/dwm/dwm-overview | ms.date 2018-05-31, updated 2025-03-11 |
| The Desktop Window Manager (LearnWin32) | https://learn.microsoft.com/en-us/windows/win32/learnwin32/the-desktop-window-manager | Win32 |
| DWM Performance Considerations and Best Practices | https://learn.microsoft.com/en-us/windows/win32/dwm/bestpractices-ovw | `ms.topic: best-practice` |
| DXGI flip model | https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model | Windows 8 / DXGI 1.2 |
| Extended Window Styles (`WS_EX_COMPOSITED`) | https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles | Win32 |
| Resized Windows (GDI) | https://learn.microsoft.com/en-us/windows/win32/gdi/resized-windows | GDI |
| `WM_ERASEBKGND` message | https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-erasebkgnd | Win32 |
| `RedrawWindow` function | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-redrawwindow | Win32 |
| `UpdateLayeredWindow` function | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow | Win32 |
| `DWMWINDOWATTRIBUTE` (`DWMWA_WINDOW_CORNER_PREFERENCE`) | https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute | Win11 Build 22000+ |
| `DWM_WINDOW_CORNER_PREFERENCE` | https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_window_corner_preference | Win11 Build 22000+ |
| Apply rounded corners in desktop apps | https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners | Windows 11 |
| How to: Render on a Per Frame Interval Using CompositionTarget | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-render-on-a-per-frame-interval-using-compositiontarget | ms.date 2017-03-30 |
| `CompositionTarget.Rendering` event | https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.compositiontarget.rendering | netframework-3.0 … windowsdesktop-11.0 |
| `DispatcherTimer` class | https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatchertimer | netframework-3.0 … windowsdesktop-11.0 |
| `DispatcherPriority` enum | https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatcherpriority | netframework-3.0 … windowsdesktop-11.0 |
| Optimizing Performance: Other Recommendations | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-other-recommendations | WPF |
| Optimizing Performance: Layout and Design | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-layout-and-design | WPF |
| Graphics Rendering Tiers | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/graphics-rendering-tiers | WPF |
| `Timeline.DesiredFrameRate` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.timeline.desiredframerate | netframework-3.0 … windowsdesktop-11.0 |
| Technology Regions Overview (WPF interop / airspace) | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/technology-regions-overview | WPF |
| `Window.AllowsTransparency` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency | netframework-3.0 … windowsdesktop-10.0 |
| `UIElement.Opacity` | https://learn.microsoft.com/en-us/dotnet/api/system.uielement.opacity | netframework-3.0 … windowsdesktop-10.0 |
| `HwndSource.UsesPerPixelOpacity` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.hwndsource.usesperpixelopacity | WPF |
| `WM_DPICHANGED` message | https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged | Win32 |
| High DPI Desktop Application Development on Windows | https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows | Win32 |
| Extended Window Styles → `WindowChrome` class | https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome | WPF |

## Version investigated

- .NET SDK 10.0.401, `net10.0-windows` (the pinned toolchain).
- WPF from the Windows Desktop runtime shipped with that SDK (`windowsdesktop-10.0` docs moniker).
- Windows 11, OS build 10.0.26100 (the machine under test).
- Target app: `AdbView` 0.1.0, native WPF shell only (decision 0022).

## Findings

Each finding is traceable to a row above. Where the documentation is silent, that is stated
explicitly, and implementation findings observed in this repository are labelled as such.

**F1 · Microsoft explicitly warns against animating a window that has a drop shadow.**
`AnimateWindow` Remarks: *"Avoid animating a window that has a drop shadow because it produces
visually distracting, jerky animations."* The flyout keeps `WS_CAPTION` specifically so DWM
draws its frame and shadow (`NativeFlyoutWindow.xaml.cs`, `WindowStyle.SingleBorderWindow` with
a `WM_NCCALCSIZE` collapse), and sets `DWMWCP_ROUNDSMALL`. The open/close slide therefore
animates a shadowed window by construction — the exact case the documentation names.

**F2 · DWM redirects window drawing to off-screen surfaces and composites them.** `dwm-overview`:
*"individual windows no longer draw directly to the screen or primary display device as they did
in previous versions of Windows. Instead, their drawing is redirected to off-screen surfaces in
video memory, which are then rendered into a desktop image and presented on the display."* The
compositor is the thing that presents frames, so an application-driven window move is only
visible once DWM recomposes — the application does not control that moment.

**F3 · There is no documented per-frame performance guidance for `SetWindowPos`.** The
`SetWindowPos` page is silent on repeated calls, cost, and flicker. Its flags do document the
default repaint behaviour: `SWP_NOCOPYBITS` — *"Discards the entire contents of the client area.
If this flag is not specified, the valid contents of the client area are saved and copied back
into the client area after the window is sized or repositioned."* So the default is to preserve
valid bits and repaint only what changed. There is **no** documented `SetWindowPos` API misuse
here; the cost is inherent to per-frame window geometry changes, not a wrong flag.

**F4 · A resized window exposes an unpainted strip until the next paint.** `resized-windows`:
*"When a window changes size, the system assumes that the contents of the previously exposed
portion of the window are not affected and need not be redrawn. The system invalidates only the
newly exposed portion of the window, which saves time when the eventual WM_PAINT message is
processed by the application."* This is the mechanism behind the owner's "resizing exposes
background" observation: the newly revealed strip is invalidated and painted later, so there is
a window of time in which it shows whatever the surface last held.

**F5 · `WM_ERASEBKGND` is the erase step, and its suppression is a community technique, not a
documented flicker fix.** `wm-erasebkgnd`: *"Sent when the window background must be erased (for
example, when a window is resized)."* and *"An application should return nonzero if it erases the
background; otherwise, it should return zero."* `RedrawWindow` documents `RDW_NOERASE` —
*"Suppresses any pending WM_ERASEBKGND messages."* No Microsoft page states that suppressing the
erase removes resize flicker; that is a widely used community technique and is recorded here as
such, not as official guidance.

**F6 · The documented no-flicker guarantees belong to layered/composited window styles.**
`window-features` (Layered Windows): *"Using a layered window can significantly improve
performance and visual effects for a window that has a complex shape, animates its shape, or
wishes to use alpha blending effects. The system automatically composes and repaints layered
windows and the windows of underlying applications. As a result, layered windows are rendered
smoothly, without the flickering typical of complex window regions."* `WS_EX_COMPOSITED`:
*"Double-buffering allows the window and its descendents to be painted without flicker."* So the
no-flicker language is attached to layered windows and `WS_EX_COMPOSITED`, not to plain DWM
composition of an ordinary window.

**F7 · DWM composition is not documented as preventing client-area resize flicker.** No fetched
Microsoft page claims that plain DWM composition makes a client-area resize flicker-free. On the
DWM best practices page the only move/resize cost statement concerns blur-behind: *"window
operations on the window (move/resize/transitions) will incur more costs."* A Microsoft
maintainer comment on the WinUI tracker (`microsoft/microsoft-ui-xaml` issue #5148) states the
mechanism plainly: *"Windows has code to attempt to synchronize window frame redraw with when the
app content has finished drawing, but that synchronization code requires Windows to know when
the app is finished drawing. WinUI3 draws all app content into a swapchain, which Windows
intentionally never synchronizes to favor performance of the swapchain. Window resize is a case
where synchronization might be desirable."* That is a maintainer statement, not documentation,
and is recorded as such.

**F8 · Layered windows and rounded corners are mutually exclusive on Windows 11.**
`apply-rounded-corners`: *"On Windows 11, the system automatically rounds top-level window
corners for all inbox apps"* and *"All standard WinForms and WPF apps are rounded automatically,
but if you customize your window frame or use a third party framework, you might need to opt-in
to rounded corners."* Critically, the apps that *"cannot ever be rounded, even if they call the
opt-in API"* include *"Per-pixel alpha layering"* and *"Window regions"*. Turning on window
transparency would therefore forfeit the corner rounding the shell currently opts into via
`DWMWCP_ROUNDSMALL`. `DWMWA_WINDOW_CORNER_PREFERENCE` and `DWM_WINDOW_CORNER_PREFERENCE` are
supported from Windows 11 Build 22000.

**F9 · WPF's managed constant-alpha path is not a documented lever.** `Window.Opacity` does not
exist on Learn (the URL 404s; `Opacity` is declared on `UIElement`, and `UIElement.Opacity`
Remarks never mention `Window`, `AllowsTransparency`, or layered windows). `technology-regions-overview`
states WPF's position: *"WPF supports non-rectangular windows by using Win32 APIs—HRGNs for
non-rectangular windows, and layered windows for a per-pixel alpha."* and *"Constant alpha and
color keys are not supported. Win32 layered window capabilities vary by platform."* In the
`dotnet/wpf` source the layered path is wired to `AllowsTransparency`
(`UsesPerPixelOpacity = AllowsTransparency`) while the constant-alpha branch that would have
consumed `_opacity` is commented out. So a whole-window `Opacity < 1` is not a documented,
reliable mechanism, and the documented path (`AllowsTransparency`) is per-pixel alpha.

**F10 · `AllowsTransparency` is a contract-level change here, and it costs hit-testing semantics.**
`Window.AllowsTransparency` throws for any `WindowStyle` other than `None`, and exists *"to
facilitate the creation of non-rectangular windows"*. `window-features`: *"Hit testing of a
layered window is based on the shape and transparency of the window. This means that the areas of
the window that are color-keyed or whose alpha value is zero will let the mouse messages
through."* `HwndSource.UsesPerPixelOpacity` Remarks: *"When you enable per-pixel opacity, the
system no longer draws the non-client area."* That is incompatible with this shell's `WS_CAPTION`
+ `WM_NCCALCSIZE` framing, its light-dismiss hit-testing, and its opaque-surface contract
(decision 0004, `DESIGN.md` §8/§22).

**F11 · `CompositionTarget.Rendering` is the official per-frame hook, and it fires once per
frame.** *"The Rendering event is raised each time the application's scene is drawn."* /
*"This event handler method gets called once per frame."* / *"The Rendering event is routed to
the specified event handler after animation and layout have been applied to the composition
tree."* Documented caveats: *"You may discover that your custom drawing runs at different speeds
on different computers. This is because your custom drawing is not frame-rate independent"* and
*"rendering events are not guaranteed to be called in any particular order."* The performance
page adds the operational rule: *"The CompositionTarget.Rendering event causes WPF to
continuously animate. If you use this event, detach it at every opportunity."*

**F12 · `DispatcherTimer` firing is explicitly not time-accurate.** *"Timers are not guaranteed
to execute exactly when the time interval occurs, but they are guaranteed to not execute before
the time interval occurs. This is because DispatcherTimer operations are placed on the Dispatcher
queue like other operations. When the DispatcherTimer operation executes is dependent on the
other jobs in the queue and their priorities."* And `DispatcherPriority`: `Send` = 10,
`Normal` = 9 (*"This is the typical application priority."*), `Render` = 7 (*"Operations processed
at the same priority as rendering."*). A 16 ms timer at `Send` therefore runs *above* both normal
work and the render priority, and is free to fire late but never early.

**F13 · A keyframe `KeyTime` cannot consume a `Duration` resource, but percent key times can.**
Relevant because the reduced-motion mechanism here is a whole-dictionary swap whose animated
sibling uses `Duration="{StaticResource Motion.*}"`: a `SplineDoubleKeyFrame` with an absolute
`KeyTime` literal would decouple the animation from the token, while `KeyTime="100%"` scales with
the timeline's own `Duration`. This is a WPF type-system fact (there is no converter from
`Duration` to `KeyTime`), recorded because it constrains how the curve may be applied.

**F14 (implementation, observed here, not documented) · Two writers own the flyout's window rect
with no mutual exclusion.** `FlyoutRevealAnimator` writes X/Y/W/H every 16 ms;
`ApplySelfSizedHeight()` is attached to `shell.ShellRoot.LayoutUpdated`, which fires on every
layout pass — *including the passes the animator's own `SetWindowPos` provokes*. When the
self-sizer decides a step is warranted (its threshold is 0.5 DIP against `ActualHeight`, which
the animator is changing), it issues its own `SetWindowPos` **and** assigns the WPF `Height`/`Top`
properties, which is a second application of the same window rect. Two independent writers
stepping/interpolating one height is oscillation by construction.

**F15 (implementation, observed here) · Self-sizing does heavy work on every layout pass.**
`ApplySelfSizedHeight()` performs a full `shell.ShellRoot.Measure(new Size(width,
PositiveInfinity))` of the entire visual tree plus a Win32 taskbar/AppBar query
(`TrayIcon.GetTaskbarScreen()`) on every `LayoutUpdated`. The repository already documents one
stall caused by UI-thread starvation of the tick (`AppHost.cs`, "the open-slide ticks starve and
the 167 ms reveal takes seconds (observed live: a 3 s slide on first show)").

**F16 (implementation, observed here) · No XAML animation uses the frozen curve.**
`DESIGN.md` §24 declares the shared production easing as `cubic-bezier(0.55, 0.55, 0, 1)`, but
there is no `EasingFunction`, `KeySpline`, or `CubicEase` anywhere in `src/`. Every storyboard
animation relies on WPF's default linear interpolation. `timing-behaviors-overview` documents
`Timeline.DesiredFrameRate` as *"a guideline only, and is not a guaranteed value"*, and no
Microsoft page makes a claim about what "cheap vs expensive" properties are — so the curve
finding is a contract-fidelity issue, not a performance one.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Keep the 16 ms `DispatcherTimer`, fix writers only | Smallest change; no doc edits | Leaves the tick unaligned to the compositor (`F11`, `F12`) and above `Render` priority |
| Drive the bounds animation from `CompositionTarget.Rendering` | Official per-frame hook; aligns with the compositor; `Start`/`Stop` already maps to attach/detach | Needs an `ENGINEERING.md` §10.4 amendment, which currently bans it; can fire more than once per frame |
| Lengthen 83/167 ms | Slightly easier to follow | Not a cause (`F1`, `F12`, `F14`); frozen tokens (`DESIGN.md` §24); longer makes jitter *more* visible; R-01/R-02 |
| `AllowsTransparency` / layered window / whole-window alpha | Would let the HWND stay one size while content changes | Forfeits Win11 corner rounding (`F8`); wrong rendering path (`F9`); breaks `WS_CAPTION` framing and the opaque-surface contract (`F10`); previously measured unreliable here |
| `WS_EX_COMPOSITED` | Documented double-buffering without flicker (`F6`) | A GDI mechanism; WPF renders through DirectX; WPF's `HwndTarget` rewrites ex-styles itself; no official WPF guidance; unverified |
| Suppress `WM_ERASEBKGND` | Cheap; targets the erase step (`F5`) | Not a documented flicker fix; alone does not address the invalidated-strip window (`F4`) |
| Reimplement the animated height step from `DESIGN.md` §24 | Matches the literal text | With no WebView child to pin, degrades to a per-frame resize of a shadowed HWND — the `F1` pattern; decision 0012 already recorded animated visible resize as failing human acceptance twice |
| Custom `EasingFunctionBase` for the frozen curve | Applies the exact curve; keeps `Duration` tokens and To-only retargeting; one shared implementation with `Easing.FluentPointToPoint` | One new ~35-line UI class; must be constructible from XAML |
| `SplineDoubleKeyFrame` with percent `KeyTime` | No new class | An explicit `0%` keyframe breaks To-only mid-flight retargeting, which `DESIGN.md` §26 requires |

## Chosen approach

1. **One writer per window rect.** Expose `FlyoutRevealAnimator.IsAnimating` as a gate and have
   `ApplySelfSizedHeight()` return early while a bounds animation is running; stop assigning the
   WPF `Height`/`Top` properties after the `SetWindowPos` that already moved the window (or do it
   under a re-entrancy guard). (`F14`)
2. **Make the open/close slide provably position-only**, by deriving its endpoints from computed
   rects of identical size rather than from the live window rect, and by asserting/logging if a
   slide's width or height ever changes; skip frames identical to the last applied one. (`F1`, `F3`)
3. **Take the redundant work out of the layout-pass path** — drop the root
   `Measure(PositiveInfinity)` only if a fixture test proves the `ScrollViewer` already measures
   unconstrained, and hoist the taskbar work-area query out of per-pass evaluation, invalidated on
   DPI change, display change, and taskbar change. (`F15`, and the DPI requirements in
   `wm-dpichanged` / `high-dpi-desktop-application-development-on-windows`)
4. **Drive the bounds animation from `CompositionTarget.Rendering`**, attaching on `Start` and
   detaching on `Stop` exactly as the performance guidance requires, keeping the `DispatcherTimer`
   source as a `DispatcherPriority.Render` fallback. Progress stays wall-clock based, so an extra
   callback is idempotent; a last-applied dedup removes the duplicate `SetWindowPos`. (`F11`, `F12`)
5. **Apply the frozen curve to every storyboard animation** via one custom `EasingFunctionBase`
   whose math is `Host.Easing.FluentPointToPoint` — one implementation shared by the XAML and the
   code animator, so they cannot disagree. The curve is a single asymmetric curve, so the class
   fixes its `EasingMode` in the constructor rather than exposing the `EaseIn`/`EaseOut`/`EaseInOut`
   composition. (`F16`, `F13`)
6. **Seed the window background from the themed opaque brush** instead of `Brushes.Black`, so a
   frame composited before the first themed paint cannot show black. (`F4`, `F5`)
7. **Reconcile the height-step contract** in `DESIGN.md` §24 with the shipped instant steps
   (decision 0012/0021), via a decision note and a re-hash of `docs/DESIGN.sha256`. The literal
   §24 text still mandates an animated step written for the WebView era; with the WebView gone it
   would mean implementing the `F1` pattern deliberately. (`F1`, and decision 0012's record that
   animated visible resize failed human acceptance twice)

## Why

The measured cause is not the easing and not the duration — it is that a shadowed window's
geometry is being changed from two writers, on a clock that is not aligned to the compositor and
that outranks rendering. Every item above removes a cause rather than masking one: the writer
conflict is eliminated, the slide becomes the least-expensive form of window animation (pure
translation of a fixed-size window), the per-frame callbacks become frame-aligned and
deduplicated, and the remaining animation is brought onto the curve the design already froze. No
new dependency, no new surface, no transparency, and no new timer is introduced — the sanctioned
animation clock already exists and only changes its source.

## Rejected approaches

- **Slowing the animations.** Not a cause (F1, F12, F14); the tokens are frozen (R-01/R-02) and
  the values already sit at the fast end of the Fluent scale; a longer duration makes jitter more
  visible, not less.
- **Window alpha / `AllowsTransparency` / layered windows.** Forfeits Windows 11 corner rounding
  (F8), puts the shell on a rendering path WPF documents as per-pixel-alpha-only with no reliable
  constant alpha (F9), and conflicts with the `WS_CAPTION` framing, hit testing, and the frozen
  opaque-surface contract (F10). The owner's instinct targets the right symptom (F4) with the
  wrong mechanism: the fix for a resize exposing background is to stop resizing the window per
  frame, not to make the exposed region transparent.
- **`WS_EX_COMPOSITED`.** A GDI double-buffering style, not a WPF mechanism; WPF manages its own
  extended styles; no official guidance for WPF.
- **Animated height steps as `DESIGN.md` §24 currently reads.** Would deliberately implement the
  pattern F1 warns about, and contradicts decision 0012's twice-failed human acceptance.
- **`SplineDoubleKeyFrame` with absolute `KeyTime`.** Decouples the animations from the swappable
  `Duration` tokens (F13) and breaks To-only retargeting for the chevron (`DESIGN.md` §26).

## Compatibility concerns

- **Reduced motion.** Unchanged in mechanism: `AppHost` swaps `Controls.xaml` for
  `ControlsReduced.xaml`. The new easing class is referenced only from the animated dictionary, so
  the reduced twin stays animation-free; `ReducedMotionDictionaryTests` pins key parity and the
  no-animation invariant.
- **`CompositionTarget.Rendering` availability.** Present on every WPF version this product
  targets (netframework-3.0 through windowsdesktop-11.0). The `DispatcherTimer` source is kept as
  a fallback so the animator is never without a clock.
- **DPI.** The hoisted work-area lookup must be invalidated on `WM_DPICHANGED`, display change,
  and taskbar change, or placement can go stale on a mixed-DPI setup. The existing per-monitor-v2
  handling (`WM_DPICHANGED` adopting the OS-suggested rect) is unchanged.
- **Priority.** Moving the timer to `Render` (fallback path) or to the frame callback removes the
  `Send`-priority preemption of normal UI work, which is a behaviour change for anything that
  previously ran below the tick. No code depends on the tick preempting input.

## Testing implications

- Unit-testable without a window: the animator's position-only invariant, the last-applied dedup,
  the writer gate (`IsAnimating` suppresses self-sizing), and the easing curve.
- The easing class must be exercised through a derived probe so the `EasingMode` composition is
  covered, not just `EaseInCore`.
- `ReducedMotionDictionaryTests` must stay green (key parity, no `BeginAnimation`/`DoubleAnimation`
  in the reduced file).
- Removing the root `Measure` must be justified by a fixture-rendered height test; if the
  `ScrollViewer` does not already measure unconstrained, the `Measure` stays.
- Hardware-verified per `ENGINEERING.md` §16.5 / §17.2 and recorded in
  `docs/DEVICE-TEST-MATRIX.md`: the open/close slide and the expand/collapse at 100 % and 200 %.
- A release launch from a path that does not contain "smoke" (R-12), since the gate's publish
  smoke run exits before the shell is created.

## Open items

- **V-8** (`ENGINEERING.md` §21): instant self-sizing + frozen tray-adjacent edge at 125–200 %
  scaling without blank frames or outer-window churn. The exposed-strip window described by F4 is
  the thing to watch; its fallback there is "content-driven height on show only; inner scroll for
  overflow".
- If the aligned, single-writer slide still reads as jerky to the owner's eye, the honest next
  step is **no slide** (instant open/close), which decision 0013 already named as its rollback —
  not a longer slide. That is an owner-facing contract decision, not an implementation tweak.
- The pairing spinner (`PairingCardView.xaml`, a hardcoded 850 ms `RepeatBehavior="Forever"`
  linear loop) is **not** gated by reduced motion and is not covered by any `Motion.*` token.
  Recorded as a known defect; fixing it needs either a fifth token or a view-model flag, i.e. a
  separate `DESIGN.md` §24 decision. Out of scope for the jitter work.
- F7 and F5 rest partly on a maintainer comment and on community technique respectively; neither
  is official documentation, and both are flagged as such above.
