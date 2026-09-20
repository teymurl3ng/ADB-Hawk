# ADB View — Design Authority

**Status:** Frozen for v1 implementation  
**Purpose:** Governing visual, interaction, and product-design specification for ADB View  
**Target:** Windows 11 system-tray ADB status utility  
**Primary UI:** WebView2-rendered frontend hosted by a native C# Windows shell

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

The surface should become shorter when little information is present and grow only as required.

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

`Check the code and try again.`

Retry is available immediately.

Error styling remains restrained.

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

Do not introduce arbitrary timing values for production behavior.

### v1 interactive structural policy (decision 0005)

While the flyout is **visible**, the native HWND height is **frozen**.
DeviceCard expand/collapse and PairingCard phase changes do **not** animate
or resize native flyout bounds. Reliability over animated reflow.

Outer content-driven sizing is evaluated only when **presenting** the flyout
(hide → show). Motion while open is limited to 83 ms feedback and 167 ms
state/opacity/chevron. Layout height may change immediately in the web
layer; overflow scrolls inside a stable scroll container.

### Examples

- hover: 83 ms
- press: 83 ms
- flyout close: 83 ms
- flyout open: 167 ms
- status change: 167 ms
- device insertion/removal: 167 ms
- pairing state change: 167 ms (web-only; no native height animation)
- card expansion feedback: 167 ms chevron / detail opacity (layout immediate)

No:

- spring
- bounce
- overshoot
- idle pulsing
- glowing loops
- FLIP coordinating native window-size changes
- native structural height interpolation while visible
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

- measure natural content height on show
- `clamp(naturalHeight, minimumHeight, 560)` for presentation height
- freeze that native height for the open session
- instant web layout for disclosure; scroll when content exceeds viewport
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
- no layout shift from transient structural animation (v1 has none while open)
- do not resize the native HWND merely to remove a scrollbar
- `scrollbar-gutter: stable` is allowed when it improves horizontal stability
  without changing the frozen design

When expanded content fits the frozen visible flyout: no scrollbar.
When it exceeds the content viewport: internal content scrolls.

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

For v1:

**Exit**

No duplicated application controls.

Primary interaction is left-click → flyout.

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
EmptyState
SubsystemWarning
```

State changes should be driven by data, not by alternate markup families.

---

**ADB View v1 design status: FROZEN**
