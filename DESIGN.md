# ADB View — Design Authority

**Status:** Adopted shell amendment 2026-10-15; earlier shell mechanics superseded as listed in §41
**Purpose:** Governing visual, interaction, and product-design specification for ADB View  
**Target:** Windows 11 system-tray ADB status utility  
**Primary UI:** native WPF shell (Fluent 2) hosted by the native C# Windows shell (decision 0022: the v1 WebView2 frontend was removed after the native shell reached full parity)

**Current implementation versus target:** the existing WPF shell remains operational during migration. The owner-authorized WinUI replacement is governed by §41, which overrides conflicting shell layout, motion, ownership and deployment assumptions below. Semantic states, vocabulary, product boundary and existing actions remain authoritative. A prototype is required before production migration; adoption is not a claim that WinUI is proven.

---

## 1. Product Identity

ADB View is a tiny Windows utility whose job is to answer, at a glance:

> **What is happening with ADB right now?**

The product character is:

**Precise · Quiet · Technical · Premium · Glanceable**

ADB View is not an Android management suite or an ADB toolbox.

The product boundary is:

> **Observe → explain → notify → pair**

The design must not expand into:

- terminal / shell
- logcat
- APK installation
- file transfer
- screen mirroring
- process/app management
- reboot controls
- port forwarding
- screenshots
- connection history
- analytics
- dashboards
- settings-heavy navigation

The product should feel like a refined Windows system instrument rather than a developer dashboard.

---

## 2. Design Authority Hierarchy

The Stitch exports are references, but they are **not all equally authoritative**.

When two exports disagree, use this hierarchy:

### Pass B.1 — Visual / Component Master

Canonical source for:

- flyout shell
- header
- typography
- spacing
- device card
- expanded device card
- pairing offer
- pairing-code surface
- buttons
- battery treatment
- transport treatment
- copy interaction
- dark-theme geometry
- component density
- component proportions

**Pass B.1 is the visual master.**

### Pass C.1 — Semantic-State Master

Canonical source for:

- Authorization required
- Device offline
- Recovery
- Bootloader
- Sideload
- Unknown state
- ADB unavailable
- ADB recovering
- wireless-discovery failure/recovery
- separation of global health from individual device health

### Pass C.3 — Composition / Density / Scrolling Reference

Canonical source for:

- multiple-device composition
- scrollable maximum-height behavior
- pairing + devices coexistence
- pinned multi-device composition
- repeated-component architecture

**Important:** C.3 is not the visual component master. Where its card styling differs from B.1, B.1 wins.

### Pass D.1 — Theme Direction Master

Canonical source for:

- light-theme palette
- light Acrylic-style direction
- opaque dark fallback
- opaque light fallback
- Windows forced-colors / contrast-theme strategy

**Important:** D.1 theme tokens apply to B.1 geometry. D.1 compact card variants are not authoritative.

### Pass E.1 — Motion / Interaction Reference

Canonical source for:

- flyout opening/closing
- card expansion/collapse
- device insertion/removal
- pairing transformations
- status updates
- pin/unpin
- reduced motion
- latest-state-wins behavior
- native-vs-web responsibility split

Where the E.1 interactive demo contradicts the corrected rules in this document, this document wins.

### Pass F — App / Tray Icon Master

Canonical source for:

- Aperture Link product identity
- application icon
- tray icon
- tray status marker system
- light/dark taskbar behavior
- semantic tray-state mapping

---

## 3. Governing Rule for Implementation

No implementation agent may:

- redesign approved components
- reinterpret component geometry
- compress components because multiple devices exist
- invent alternate compact cards
- substitute a different visual language
- change information hierarchy for implementation convenience

If implementation reveals a genuinely missing state, the design contract must be updated first.

The correct rule is:

> **When density increases, scroll — do not redesign.**

And:

> **When state changes, change semantic content — do not change component architecture.**

---

## 4. Primary Surface

ADB View has one primary application surface:

**a compact flyout anchored to the Windows notification area.**

It also has exactly one optional secondary surface:

**a very small always-on-top status bubble** (off by default; toggled from the tray
menu). It is an ambient echo of the tray icon — the same aggregate state and the same
marker colours, no controls of its own. It is not a second device list, and it never
duplicates the flyout's detail.

There is no:

- sidebar
- tab bar
- navigation stack
- dashboard
- settings screen
- separate device-details page

### Width

**376 effective pixels**

### Height

Content-driven.

The surface should become shorter when little information is present and grow only as required — immediately, while visible (decision 0012).

Recommended maximum is approximately **560 effective pixels**, after which the relevant content region scrolls.

### Scrolling

When content exceeds maximum height:

- header remains fixed
- device/pairing content scrolls
- no horizontal scroll
- no component shrinking
- no alternate compact component variants
- scrollbar remains subtle and Windows-appropriate

---

## 5. Header

Canonical source: **Pass B.1**

Approximate height:

**40 px**

Contains only:

- ADB View product glyph
- `ADB View`
- Pin control

Do not add:

- settings
- refresh
- reconnect
- close button
- help
- hamburger menu
- search
- overflow menu unless explicitly approved later

The visible pin remains compact but should have an approximately **40×40 px interaction target**.

---

## 6. Geometry and Spacing

Use a disciplined **4 px base grid**.

Preferred spacing vocabulary:

- 4 px — micro gap
- 8 px — related controls
- 12 px — grouped content
- 16 px — normal section/card spacing
- 20 px — larger separation
- 24 px — major separation

Canonical geometry:

- Flyout radius: **8 px**
- Device card radius: **8 px**
- Pairing card radius: **8 px**
- Small controls: approximately **4 px**
- Pills: fully rounded where appropriate

Do not introduce arbitrary spacing simply to make one state fit.

---

## 7. Typography

Primary family:

**Segoe UI Variable**

Canonical approximate sizes from B.1:

- Header title: ~14.5 px
- Device name: **15 px**
- State-age metadata: **12 px**
- Pairing heading: **13.5 px**
- Supporting text: ~12 px

Use sentence case.

Do not introduce:

- uppercase technical section labels
- oversized headings
- alternate typefaces
- SaaS-style display typography

---

## 8. Material

### v1 production surface (authoritative)

v1 ships the **intentional opaque** dark/light Windows flyout surface.

Live Desktop Acrylic behind transparent WebView2 is **not** used in v1:
measured diagnosis showed multi-second stale presentation, and composition
hosting is unavailable on the frozen WinForms/WebView2 stack. Acrylic is
deferred architecture work — not a v1 defect.

### Unpinned / Pinned

Same opaque application surface for both. Pinned mode adds:

- active/filled pin
- optional subtle elevation difference

Pinned mode must **not** become a different widget design.

### Transparency / Acrylic (deferred)

When a future hosting path can keep live material under Web content without
stale capture:

Native Windows host may provide Desktop Acrylic / appropriate Windows backdrop
with transparent WebView content over the native material.

Until then, production must use intentional opaque theme tokens.

### Important

Do not implement production Acrylic via CSS `backdrop-filter`.
Do not use screenshot blur, repaint timers, or DWM jiggle hacks.

---

## 9. Device Card — Canonical Structure

Canonical source: **Pass B.1**

Hard geometry:

- device glyph: **32×32 px**
- device name: **15 px**
- state-age metadata: **12 px**
- card padding: **12 px**
- card radius: **8 px**

Structure:

```text
DeviceCard
│
├── Identity row
│   ├── 32×32 device glyph
│   ├── Device name
│   ├── State-age metadata
│   └── Disclosure chevron
│
├── Divider
│
└── Status row
    ├── Semantic status treatment
    ├── Transport icon + label
    └── Battery percentage + battery icon
```

Do not replace with a compressed row.

### Collapsed Information

The collapsed card may show:

- device identity
- semantic ADB state
- USB / Wi-Fi
- battery
- state age
- short actionable guidance when required

Do not show by default:

- IP address
- port
- serial
- Android version
- transport IDs
- protocol internals

---

## 10. Expanded Device Card

Clicking a device expands the same card **in place**.

No modal.  
No navigation.  
No separate details screen.

Allowed expanded fields:

- friendly name
- exact model
- Android version
- ADB state
- transport
- serial
- IP:port for Wi-Fi
- battery
- connection/state-change age

Copy controls may appear for technical fields such as:

- serial
- address

Copy feedback is small and inline:

`Copied`

No toast is required.

Only one device should be expanded at a time.

---

## 11. Semantic State System

### Green — Connected / Healthy

Examples:

- Connected USB
- Connected Wi-Fi

Use the approved B.1 connected status treatment.

Do not make the whole card green.

### Amber — Attention Required

Example:

- Authorization required

Supporting guidance:

`Check the phone and allow USB debugging.`

### Red — Failure / Unusable Transport

Examples:

- Device offline
- ADB unavailable

Red should remain restrained.

### Blue — Valid Special / Informational State

Examples:

- Recovery
- Bootloader
- Sideload
- pairing

These are not errors.

### Neutral

Examples:

- no devices
- unknown state
- inactive

Color must never be the only state indicator.

Use text + icon/symbol + color.

---

## 12. Canonical Device States

### Connected

Example:

`Galaxy S24`

`Connected · Wi-Fi`

`82%`

`Connected 46s ago`

### Authorization Required

`Galaxy S24`

`Authorization required · USB`

`Check the phone and allow USB debugging.`

### Device Offline

`Galaxy S24`

`Device offline · Wi-Fi`

`ADB can see this device, but the connection isn’t responding.`

### Recovery

`Pixel 8`

`Recovery · USB`

### Bootloader

`Pixel 8`

`Bootloader · USB`

### Sideload

`Pixel 8`

`Sideload · USB`

### Unknown

`Galaxy S24`

`Unknown state · USB`

Do not expose raw enum/internal names in the collapsed UI.

---

## 13. Empty State

Canonical healthy empty state:

`No ADB devices`

`Connect a device using USB or wireless debugging.`

`Wireless discovery active`

This is not an error state.

Do not use:

- giant illustrations
- sad faces
- oversized empty cards
- excessive blank space

---

## 14. Pairing Offer

Canonical source: **Pass B.1**

Hard geometry:

- pairing icon: **32×32 px**
- heading: **13.5 px**
- padding: **14 px**
- radius: **8 px**

Content hierarchy:

- pairing icon
- `Wireless pairing available`
- device name
- `Waiting for a pairing code`
- `Pair`

Use cool informational/accent treatment.

Do not treat pairing as an error.

---

## 15. Pairing Flow

Pairing remains inside the same pairing surface.

### Offer

`Wireless pairing available`

`Galaxy S24`

`Waiting for a pairing code`

`Pair`

### Code Entry

`Galaxy S24`

`Enter the six-digit code shown on the phone.`

Six-digit input.

Actions:

- Cancel
- Pair

### In Progress

`Pairing with Galaxy S24…`

Optional supporting text:

`Keep the phone nearby while pairing completes.`

Do not expose:

- TLS
- security tokens
- ADB daemon
- protocol internals

### Success

`Paired with Galaxy S24`

`Waiting for ADB connection…`

Important:

Successful pairing does **not** mean the device is already connected.

This state remains visible until the authoritative backend state replaces it.

### Failure

`Pairing failed`

The supporting line is selected from this fixed set by the failure cause
(decision 0007); wording never includes technical detail:

- Code wrong or refused: `Check the code and try again.`
- Phone did not respond in time: `The phone did not respond in time. Try again.`
- Too many attempts: `Too many attempts. Wait a moment and try again.`
- Offer changed or withdrawn: `The pairing offer changed. Tap Pair and enter the new code.`

Any other failure uses the wrong-code line. The same line is the notification body.

Retry is available immediately.

Error styling remains restrained.

### QR Pairing (decision 0010)

An alternative pairing entry that removes code transcription entirely. The desktop displays a
QR code; the phone's camera starts the pairing (Android 11+ Wireless debugging → Pair device
with QR code).

- Entry: a quiet `Pair a device over Wi-Fi` action, visible whenever no pairing operation is
  active — in the empty state and below the device list otherwise. Same cool informational
  treatment as the pairing offer.
- Active session (`PairingQrCard`): the QR code (backend-rendered image), the instruction lines
  naming the exact phone screen, and `Cancel`. Placement follows the pairing surface: after
  pairing offers, before device cards.
- Once the phone scans, the standard pairing cards take over (progress → success → device
  arrival). An unscanned session quietly clears after 2 minutes; Cancel clears quietly; no
  failure card is produced by the QR layer itself.
- The rendered code contains a single-use credential: never logged, never exposed beyond the
  displayed image.

---

## 16. Multi-Device Rules

Individual devices remain individually visible.

Do not replace them with only:

`3 devices connected`

A quiet count heading such as:

`3 devices`

is acceptable.

Every device uses the same canonical B.1 DeviceCard.

There is no:

- CompactDeviceCard
- DenseDeviceCard
- MultiDeviceCard
- MiniDeviceCard
- PinnedDeviceCard

If more devices exist than fit:

**scroll.**

---

## 17. Global vs Local Health

Global application health must not overwrite truthful device states.

Examples:

If USB ADB works but wireless discovery fails:

- USB device remains Connected
- show a local warning:
  - `Wireless discovery unavailable`
  - `USB ADB is still working normally.`

If one device is Offline and another is Connected:

- one card is Offline
- one card remains Connected
- do not turn the entire flyout into a global failure state

Only true global ADB failure gets the global error surface.

---

## 18. Global States

### ADB Unavailable

`ADB unavailable`

`ADB View couldn’t find or start ADB.`

`Install Android Platform Tools or make adb.exe available to Windows.`

No Retry / Restart ADB button.

ADB View self-recovers when possible.

### Restoring ADB

`Restoring ADB…`

Optional:

`ADB View is reconnecting to the local ADB service.`

Temporary progress treatment only.

### Wireless Discovery Unavailable

Local subsystem surface only.

### Restoring Wireless Discovery

Temporary local informational surface.

### Discovery Health (decision 0008)

Local informational surface for a measured discovery failure, rendered as the
`DiscoveryHealthCard`, placed after pairing cards and before subsystem warnings.

Appears only when all of the following hold (event-driven, no timers):

- at least one connected device reports the Wi-Fi transport,
- wireless discovery currently reports zero services,
- the ADB server is healthy and wireless discovery is enabled.

A healthy but quiet network shows nothing. The card clears as soon as any
service returns or the Wi-Fi device disappears.

Content:

- Title: `Wireless discovery isn't working`
- Body: `{device} is connected over Wi-Fi, but ADB isn't receiving device
  announcements on this network.`
- Evidence lines generated from structured diagnosis, only when a finding
  exists; wording is human, never technical (no protocols, ports, or driver
  names).
- `Check again` re-runs the measurement; disabled while it runs.

The visual treatment reuses the local-warning card language. Notifications are
out of scope for this surface until a later decision.

---

## 19. Pinning

### Unpinned

- tray anchored
- light-dismiss
- no taskbar button

### Pinned

- remains visible
- always on top
- active/filled pin
- header acts as draggable region
- no light-dismiss

Do not add:

- `Pinned` badge
- special title bar
- widget frame
- extra chrome

Pinned dragging is owned by the **native Windows host**, not simulated in JavaScript.

---

## 20. Dark Theme

Canonical visual source:

**Pass B.1**

Dark theme remains the primary visual master.

The production implementation should recreate B.1 exactly, not one of the later compact Stitch board variants.

---

## 21. Light Theme

Canonical palette/direction source:

**Pass D.1**

Apply D.1 light tokens to B.1 geometry.

Light theme should feel:

- soft
- layered
- precise
- premium
- system-like

Avoid:

- pure white everywhere
- heavy black borders
- sterile SaaS appearance
- oversaturated semantic colors

---

## 22. Opaque Surface (v1)

**v1 production always uses intentional opaque dark/light tokens.**

Same components.  
Same geometry.  
Same hierarchy.

Opaque mode is not a separate design — it is the v1 material.

Dark: intentional dark Windows flyout surface (`#202329`).  
Light: intentional light Windows flyout surface (`#F3F4F6`).

Forced-colors behavior is unchanged (§23).

Live Acrylic remains deferred; see `docs/decisions/0004-v1-opaque-surface-acrylic-deferred.md`.

---

## 23. Forced Colors / Windows Contrast Themes

Do not use the cyber/terminal-style High Contrast mockups from Stitch.

Production behavior should use real system-aware forced-colors behavior.

Conceptual CSS:

```css
@media (forced-colors: active) {
    /* use system color roles */
}
```

Prefer system color values such as:

- Canvas
- CanvasText
- ButtonFace
- ButtonText
- Highlight
- HighlightText
- GrayText
- Field
- FieldText

Do not globally disable forced colors.

Do not hard-code black/yellow/cyan contrast themes.

The same components remain structurally recognizable.

---

## 24. Motion Vocabulary

Visible motion may use:

- **0 ms** — immediate truth/state update
- **83 ms** — immediate feedback
- **167 ms** — standard state / opacity / chevron transition
- **333 ms** — reserved token only (not used for interactive native resize in v1)

Shared production easing (Fluent Existing Elements / Point to Point):

- **`cubic-bezier(0.55, 0.55, 0, 1)`**

Every production animation uses this curve — the native window slide and the
XAML state/opacity/chevron transitions alike (decision 0023).

Do not introduce arbitrary timing values for production behavior.

### v1 interactive structural policy (decision 0005, amended by 0012/0013/0021/0023)

While the flyout is **visible**, the native HWND height follows the reported
content height through **instant steps** (decisions 0012/0021): one clamped
window-size change per settled content change, the tray-adjacent edge frozen,
the step deduplicated against the window's actual height. The height is never
interpolated — a 0 ms step is the truth, and no animation may sit between a
content change and the geometry that reports it. Per-frame HWND resize,
per-frame child resize, FLIP coordination, and cross-layer height
interpolation remain banned.

The window presents at its best-known height and converges to content truth
within one or two reports. Overflow beyond the 560 epx ceiling scrolls inside a
stable scroll container. Motion while open is limited to 83 ms feedback and
167 ms state/opacity/chevron.

Opening and closing animate along the tray axis (decision 0013): open slides
out of the tray edge over 167 ms; close (light-dismiss, Esc, toggle) slides
into the tray edge over 83 ms, then hides. The slide is a **position-only**
translation — the window's size is fixed for its duration, so the reveal never
resizes the window per frame. OS reduced-motion (client-area animation
preference) reduces every one of these to the instant behavior.

### Examples

- hover: 83 ms
- press: 83 ms
- flyout close: 83 ms (native slide into tray — decision 0013)
- flyout open: 167 ms (native slide out of tray — decision 0013)
- status change: 167 ms
- device insertion/removal: instant (native height step — decisions 0012/0021)
- pairing state change: 167 ms (web-only)
- card expansion feedback: 167 ms chevron / detail opacity (layout immediate)

No:

- spring
- bounce
- overshoot
- idle pulsing
- glowing loops
- FLIP coordinating native window-size changes
- per-frame HWND resize, including animating the window's height while it is
  visible (decisions 0012/0021: the height is an instant step)
- interpolating the flyout window's size during the open/close slide — the
  slide is a position-only translation (decision 0023)
- the retired Pass B.1 structural curve `cubic-bezier(0.1, 0.9, 0.2, 1)`
- structural duration 250 ms

---

## 25. Structural Size Mechanics (v1)

Do not rely on:

- hard-coded expanded heights
- `height: 0 → auto` assumptions
- `max-height: 999px`
- fixed screenshot-derived height targets
- FLIP / measured-height animation driving HWND while visible

Use:

- measure natural content height continuously (events and ResizeObserver)
- `clamp(naturalHeight, minimumHeight, 560)` for the presented height
- apply the clamped height as an instant step whenever it differs — visible or hidden (decision 0012); never animate
- instant web layout for disclosure; scroll when content exceeds the 560 epx ceiling
- grid `0fr` / `1fr` for collapsed/expanded detail **without** animating rows

Content-aware at:

- 100%
- 125%
- 150%
- 175%
- 200% scaling

---

## 26. Latest State Wins

ADB View is a live monitor.

Presentation must never queue stale backend states.

If backend state changes during an animation:

- cancel/retarget current animation
- converge on latest AppSnapshot

Example:

`Offline → Connected → Authorization required`

If `Connected` becomes stale before animation completes, the UI must end at:

`Authorization required`

No animation may delay current truth.

---

## 27. Telemetry Updates

Do not fabricate intermediate telemetry values.

Example:

Battery:

`82% → 79%`

may update immediately or crossfade briefly.

Do not animate fake:

`82 → 81 → 80 → 79`

unless those values were actually measured.

---

## 28. Scrolling

Use standard WebView2/Chromium scrolling.

No custom JS scroll physics.

No:

- snap
- parallax
- custom inertia
- card snapping

Scrollbar:

- vertical only
- subtle
- clearer on interaction
- no horizontal scroll
- no layout shift from transient structural animation (none exists; height changes are instant steps, decision 0012)
- the native HWND follows content height in instant steps (decision 0012); the scrollbar
  gutter stays width-stable so scrollbar state never drives a resize
- `scrollbar-gutter: stable` is required for that stability

When content fits the visible flyout: no scrollbar.
When it exceeds the 560 epx ceiling: internal content scrolls.

---

## 29. Native vs Web Responsibilities

### Web Frontend Owns

- device-card rendering
- pairing surfaces
- hover/press/focus
- card expansion
- pairing transformations
- status presentation
- insertion/removal presentation
- theme token application
- scrollbar styling
- copy feedback

### Native C# / Windows Host Owns

- tray icon
- tray anchoring
- flyout window placement
- actual window lifecycle
- pinned native window movement
- DWM/Acrylic/backdrop
- monitor-aware positioning
- Windows DPI/window bounds
- startup behavior
- notifications

### Coordinated

- pin state
- appearance/theme
- transparency on/off
- reduced-motion preference
- WebView recovery

---

## 30. App / Tray Icon Identity

Canonical source:

**Pass F**

Chosen identity:

**Aperture Link**

Application icon and tray icon share the same visual family.

### Tray Base

Neutral ADB View glyph.

The base glyph represents the application only.

Semantic state is added via a small marker.

### Tray States

- Idle: neutral base glyph, no status dot
- Connected: green marker
- Attention: amber marker
- Global ADB failure: red marker
- Special state: blue marker
- Pairing available: blue/cyan informational marker

Do not recolor the entire icon.

### Tray Aggregate Priority

Recommended priority:

1. Global ADB unavailable / global failure
2. Attention required
3. Pairing available
4. Special ADB mode
5. Healthy connected
6. Idle

An individual Offline device does not automatically force global red if other devices remain healthy.

### Important

Special and Pairing may look similar at 16 px.

Exact meaning is supplied by:

- tooltip
- notification
- flyout

Do not depend on a microscopic icon distinction.

### Status Bubble

An optional, very small always-on-top bubble sits in the bottom-right of the working
area, above the taskbar. It mirrors the tray's aggregate state and reuses the marker
colours above:

- Connected: green, while connected
- Attention required: amber, while attention is required
- Pairing available: blue/cyan, while a phone is discoverable and ready to pair
- Global ADB failure: red, for 60 seconds
- Special ADB mode: blue, for 60 seconds
- Just disconnected: red, for 60 seconds
- Idle / nothing to report: hidden

The first three are permanent: they persist while their condition holds. The transient
states appear for 60 seconds and then the bubble hides itself. Hovering reveals the
exact status text; clicking opens the flyout. The bubble never takes focus and never
appears over a full-screen application.

---

## 31. Tray Tooltip

Tooltip is exact, concise explanation.

Examples:

- `Connected: Galaxy S24 (Wi-Fi)`
- `Authorization required: Pixel 8`
- `ADB unavailable`
- `No ADB devices`

Tray icon:

quick semantic signal.

Tooltip:

exact status.

Flyout:

full truth.

---

## 32. Notifications

Notifications should occur only for meaningful transitions:

- connected
- disconnected
- authorization required
- offline/problem
- pairing offer
- pairing success/failure
- global ADB failure

Do not notify for ordinary metadata refresh.

Repeated rapid flapping should be debounced.

Notifications use the same terminology as the flyout.

---

## 33. Right-Click Tray Menu

For v1 the tray context menu is:

**Open ADB View** (primary; shows/activates the flyout; does not toggle closed)

---

**Start with Windows** (check — registers/unregisters the product `HKCU` Run entry)

**Notifications** (check — enables/disables ADB View balloons; default on)

**Status bubble** (check — shows/hides the always-on-top status bubble; default off)

**Theme** ▸ **System** / **Light** / **Dark** (exactly one checked; default System)

---

**Exit**

These options live only in the native tray menu. There is no settings screen,
no preferences flyout, and no WebView settings page.

Checks refresh from authoritative state each time the menu opens. Theme and
notification changes apply immediately. Start with Windows uses the same
per-user Run registration as the installer (`--startup`). Windows Startup Apps
may independently suppress execution; ADB View only owns its Run registration.

Primary interaction remains left-click → flyout toggle.

---

## 34. Accessibility

The product must:

- never rely on color alone
- remain keyboard operable
- show visible focus
- support Windows Contrast Themes
- work without transparency
- remain legible under DPI/text scaling
- preserve hierarchy across themes

High Contrast is a system presentation mode, not a separate ADB View design.

---

## 35. DPI / Display Validation

Required validation:

- 100%
- 125%
- 150%
- 175%
- 200%

Also test:

- multiple monitors
- monitors with different DPI
- taskbar edges
- taskbar auto-hide
- tray overflow
- 1366×768
- 4K displays

No:

- clipping
- overlap
- blurry scaling
- horizontal overflow
- broken tray anchoring

---

## 36. Production Web Requirements

The final WebView frontend must be fully local/offline.

Do not depend on runtime:

- Tailwind CDN
- Google Fonts
- external JS
- external CSS
- remote icon assets

Use:

- locally compiled frontend bundle
- locally bundled assets
- Windows-installed Segoe UI Variable
- local icon package/assets

The Stitch HTML files are design references, not production frontend code.

---

## 37. Explicitly Non-Authoritative Stitch Artifacts

Do not treat these as production requirements:

- compact multi-device cards from later Stitch passes
- 360 px flyout references
- WinUI 3 implementation notes
- C++ implementation notes
- cyber/terminal High Contrast designs
- Retry ADB Service buttons
- shell/logcat transfer references
- hard-coded animation heights
- arbitrary 42/120/200 ms timings
- CSS backdrop blur as production Acrylic
- Tailwind CDN usage
- technical pairing text about TLS/security tokens/ADB daemon
- unverified accessibility/compliance badges

Where these appear in Stitch boards, they are overridden by this document.

---

## 38. Design Acceptance Criteria

The design is considered correct when:

- ADB state is understandable at a glance
- every visible device is individually identifiable
- USB vs Wi-Fi is obvious
- pairing availability is obvious without being alarming
- errors explain what matters without technical jargon
- multiple devices remain readable without becoming a dashboard
- nothing important requires navigation to another screen
- pinned mode is attractive enough to remain visible all day
- light and dark both look intentionally designed
- opaque fallback still feels finished
- Windows Contrast Themes preserve usability
- no component shrinks merely because density increases
- motion explains change without delaying truth
- tray states remain recognizable at 16 px

The design objective is:

> **Removing anything else would remove useful information; adding anything else would begin to create clutter.**

---

## 39. Freeze Rule

This design is frozen for v1.

Implementation may not alter presentation simply because another approach is easier to code.

If implementation exposes a missing state or genuine usability problem:

1. document the issue,
2. update the design contract,
3. then implement the approved change.

Do not silently redesign in code.

---

## 40. Implementation Handoff

The implementation should reconstruct the approved UI from the design authorities above, rather than taking any one Stitch export wholesale.

Recommended interpretation:

```text
Pass B.1
    ↓
canonical components

C.1
    ↓
state semantics

C.3
    ↓
composition / scrolling

D.1
    ↓
theme tokens

E.1 + this document
    ↓
motion / responsibilities

Pass F
    ↓
icon system
```

The final production frontend should have one reusable implementation of each core component:

```text
Header
DeviceCard
ExpandedDeviceCard
PairingOffer
PairingCode
PairingQrCard (decision 0010)
EmptyState
DiscoveryHealthCard (decision 0008)
SubsystemWarning
```

State changes should be driven by data, not by alternate markup families.

---

**ADB View v1 semantic/product contract retained; replacement shell governed below.**

## 41. Adopted stable WinUI shell — decision 0029

**Adopted 2026-10-15 on the owner's instruction to begin the researched rebuild.** This section overrides conflicting shell mechanics in §§2–10, 15–16, 19, 22–30, 34–40. The earlier WPF layout and implementation text describes the supported baseline until M10; it is not authority for the replacement. Semantic states, guidance, health scope, icon identity, vocabulary, notification policy, existing actions, product scope and privacy remain unchanged.

### 41.1 Stable panel and navigation

- New UI is WinUI 3, with the existing two product surfaces: flyout and optional bubble. The host also owns native tray/menu plumbing. No browser surface or additional product window.
- Flyout is initially **376 × 480 DIP**, each dimension clamped to work-area size minus **16 DIP**, with an **8 DIP** margin where possible. Include the actual shadow/envelope extent in placement; use target-monitor DPI and round native pixel bounds once.
- Outer size is fixed for the open session. Content, device count, metadata and pairing phases never resize it. DPI/work-area/monitor changes may settle motion and make one immediate recomputation. Pinned native drag changes position only.
- One stable header and scrollable body. Native focusable device rows open a details page in the same content region; Back restores keyed scroll/focus. This replaces collapsed/expanded device cards. Pairing uses the same region and stable action placement across code, QR, working, waiting and error phases.
- Header contains app identity, pin and contextual Back. No new settings/reconnect/refresh/help chrome. Initial padding **16 DIP**, section gap **12 DIP**, header minimum **48 DIP**, controls minimum **32 DIP**. Text scaling may enlarge content and scroll; never clip required actions to preserve these minimums.
- Preserve visual identity, typography, semantic colors and icon assets; native theme resources own control states. Reserve stable scrollbar space so overflow cannot change wrapping width. Long device data stays readable/copyable without widening the panel.
- All existing diagnosis, repair/undo, pairing and copy actions remain available. Local wireless failure stays local. A removed device or invalid QR is removed from visual, hit-test and accessibility trees immediately; no stale outgoing live page.

### 41.2 Input and lifetime

Opening from user action activates the flyout; snapshots do not. Unpinned genuine outside activation dismisses it; owned popups/menus do not. Pinned mode survives deactivation and enables native header drag. Esc closes an owned popup first, then returns from a subpage, then hides the flyout even when pinned. Tray toggle toggles; Open always shows/activates. Hide does not silently unpin; pin still resets on process restart.

Back/light dismiss are presentation actions, not automatic pairing submission, retry or cancellation. Active pairing remains reachable from the list; explicit Cancel retains its existing meaning. Unsubmitted codes are cleared on leaving/hiding, never logged or persisted. Pairing success never implies connected-device truth.

Bubble is **32 × 32 DIP**, stationary with a normal tooltip, and never expands on hover. Preserve aggregate-state meanings and permanent/60-second transient lifetimes, idle hiding and full-screen suppression. Hide it while the flyout is visible. Show/click/tooltip never activates the bubble; its click explicitly opens the flyout.

### 41.3 App-owned motion

| Recipe | Behavior | Duration / cubic curve |
| --- | --- | --- |
| SurfaceEnter | Whole visible surface opacity 0→1 and 12→0 DIP travel from tray direction | 167 ms, (0,0,0,1) |
| SurfaceExit | Current surface opacity→0 and current position→12 DIP toward tray | 167 ms, (1,0,1,1) |
| ContentNavigate | Incoming content only, opacity 0→1 and 12→0 DIP horizontal travel, reversed for Back | 167 ms, (0,0,0,1) |
| Feedback | Nonsemantic app-owned highlight/opacity | 83 ms, linear |
| SemanticUpdate / EnvironmentChange | Latest semantic content, validity, command state, DPI, contrast and reduced motion | 0 ms |

These are shared platform-animation recipes. Native control focus/press/hover/scroll behavior keeps platform defaults; custom effects never compete for the same properties. No text/QR/window scaling, Width/Height/Margin animation, bounce, overshoot, stagger, idle decorative loop or production per-frame application callback. The old WPF 333 ms token and point-to-point curve are historical baseline mechanics, not additional replacement recipes.

Same-target requests are no-ops. Interrupted surface effects retarget from the presented value, with position continuity and maximum 167 ms for a replacement segment. Generation-checked completions cannot hide reopened/disposed windows. Close disables content interaction immediately; native hide waits only for the current exit completion. Incoming user navigation replaces the previous destination without queuing. Semantic updates never wait for animation.

Read reduced motion before first show and react to its event immediately. Disable/settle all current effects, including busy rotation; use static progress wording instead. Re-enable only future effects. Age and progress work runs only while its content is visible. Suspend/shutdown cancels presentation lifetimes without delaying backend cleanup.

### 41.4 Rendering and platform gate

Preferred: a supported transparent bounded WinUI envelope containing `SystemBackdropElement` Desktop Acrylic, with outline/shadow moving as one visible panel. If only material fails, use the same envelope with opaque visual material. If transparent-envelope correctness fails, use an ordinary opaque native surface with immediate outer show/hide and internal navigation motion only. This last fallback explicitly disables SurfaceEnter/Exit. Never animate children inside an unanimated visible host rectangle and call it whole-surface motion.

Prove stable-package APIs, actual presentation, current-value interruption, cross-process margin hit testing, focus, DPI and self-contained startup in M02 before production migration. No preview dependency, per-frame HWND motion/resize or new renderer to bypass a failure. A failed gate retains the current app and records the evidence.

### 41.5 Acceptance authority

`docs/WINUI-REBUILD-RUNBOOK.md` §§3–9 supplies the detailed interaction matrix, M00–M12 sequence, measurement methods and numerical budgets. `docs/WINUI-TEST-MIGRATION.csv` preserves every baseline assertion. Pass requires static captures plus temporal evidence, keyboard/UIA/contrast testing, latest-snapshot correctness, package validation and honest real-hardware/soak status. A successful compile, callback counter or screenshot alone does not prove motion or release readiness.

**Owner-approved resource amendment, 2026-10-15:** the complete WinUI application, including the ADB/backend, may use up to **200 MiB working set** (209,715,200 bytes). This replaces the historical 120 MB working-set target for the replacement only. Record private bytes and retain every other latency, idle, traffic and lifetime threshold. A prototype within this ceiling does not prove the integrated application fits.
