# 0020 — Connection-status bubble (second surface + tray menu item)

- **Date:** 2026-10-03
- **Status:** ACCEPTED — the design authority approved the wording on 2026-10-04 and the
  amendment was applied in the same change (DESIGN.md §4/§30/§33, AGENTS.md R-03/R-04,
  ENGINEERING.md, regenerated `docs/DESIGN.sha256`).
- **Approved in principle:** 2026-10-03 (owner chose "amend via decision note"; one aggregate
  bubble; transients disappear at the end of their minute).
- **Companion research:** `research/2026-10-03-status-bubble-native-approaches.md`
  (non-activating windows, DWM rounding, taskbar/work-area placement, fullscreen suppression).

## Problem

The product can only be read by opening the flyout or hovering the tray icon. The owner wants
an ambient, always-visible cue for the current connection state — a very small bubble in the
bottom-right with the same colours the tray and flyout already use, toggled from the tray menu,
never taking focus, showing detail on hover and opening the flyout on click.

Two frozen contracts stand in the way, and they are the reasons this note exists:

1. **`AGENTS.md` R-03 / `DESIGN.md` §33** enumerate the tray menu exhaustively ("…and **Exit**
   — nothing else"). A toggle item contradicts them.
2. **`DESIGN.md` §4** says the product has *"one primary application surface: a compact flyout
   anchored to the Windows notification area"*, and §19 defines "always on top" only as a
   property of the *pinned flyout*. A second persistent window is a new surface that the design
   contract does not contemplate.

`DESIGN.md` §38/§39 reinforce the freeze ("adding anything else would begin to create clutter";
"Do not silently redesign in code"), so this is a design-authority matter, not an
implementation freedom.

## Scope of the change (what the bubble is, and is not)

- **Is:** one optional, very small, always-on-top, **non-activating** capsule in the bottom-right
  of the working area, above the taskbar; an ambient echo of the tray icon using the **same
  aggregate state and the same colours**; hover shows the exact status text; click opens the
  flyout; hidden whenever there is nothing to report.
- **Is not:** a second device list, a settings surface, a notification channel, an appbar, a
  draggable/positionable widget, or a per-device stack. It adds no controls and no copy of its
  own beyond the existing status text.

## Proposed contract text (for approval)

### A. `DESIGN.md` §4 "Primary Surface" — after line 169, insert:

```
It also has exactly one optional secondary surface:

**a very small always-on-top status bubble** (off by default; toggled from the tray
menu). It is an ambient echo of the tray icon — the same aggregate state and the same
marker colours, no controls of its own. It is not a second device list, and it never
duplicates the flyout's detail.
```

The existing "There is no:" list (lines 171-178) is **unchanged** — none of those surfaces is
being added.

### B. `DESIGN.md` §30 "Tray States" — append as a new subsection after "### Tray Aggregate
Priority" (i.e. after line 1150):

```
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
```

### C. `DESIGN.md` §33 "Right-Click Tray Menu" — after line 1209 (`**Notifications**`), insert:

```
**Status bubble** (check — shows/hides the always-on-top status bubble; default off)
```

Everything else in §33 (including "These options live only in the native tray menu. There is no
settings screen, no preferences flyout, and no WebView settings page.") is **unchanged**.

### D. `AGENTS.md` R-03 — the menu sentence becomes:

> The tray context menu is **Open ADB View**, **Start with Windows**, **Notifications**,
> **Status bubble**, **Theme** (System / Light / Dark), and **Exit** — nothing else (see
> `DESIGN.md` §33 / decisions 0006, 0020).

### E. `AGENTS.md` R-04 — replace the now-inaccurate sentence

Current: *"No WinForms/WPF control renders product UI — WinForms exists only as the message loop,
the tray icon, and the window that hosts WebView2."*

Proposed:

> WinForms exists only as the message loop and the tray icon. The product's two windows — the
> flyout and the status bubble — are WPF and live in `src/AdbView/NativeUi/`. No other control
> renders product UI, and no third surface may be added without a `docs/decisions/` note
> (`DESIGN.md` §4).

(The old sentence is already contradicted by decision 0015's native WPF shell; this makes the
rule match reality.)

### F. `ENGINEERING.md`

- §3.1 ownership table: add a row for `NativeUi/StatusBubbleWindow` — "Bubble window lifecycle,
  placement, non-activation, transient expiry | Hold product state".
- Line ~850 (stale since decision 0006): the tray-menu sentence currently says "Right click →
  context menu with **Exit** only" — correct it to the full current list.
- Line ~40: the preference store "holds NotificationsEnabled and Theme only" — add the new
  `StatusBubbleEnabled`.
- §11 reliability matrix: new row for the bubble's 60 s transient expiry (one-shot,
  generation-guarded; cancelled or retargeted by a newer state; never periodic polling).

### G. `docs/DESIGN.sha256`

Regenerated in the same change (hand-computed SHA-256 of `DESIGN.md`, LF-normalised — the
algorithm the boundary test pins). The test's own failure message calls this "an explicit
design-authority action, never a build fix"; this note is that action.

### H. Tests that pin the menu (three copies of one array)

- `tests/AdbView.Tests/Host/TrayContextMenuTests.cs` — `ExpectedTopLevel` gains `"Status bubble"`
  after `"Notifications"`; `FakePrefs` gains the new preference member.
- `tests/AdbView.Tests/Host/BalloonNotificationSinkTests.cs` — both copies of the expected array
  (two classes) gain the same entry.
- `tests/AdbView.Tests/Host/StartupAndPreferencesTests.cs` — `FakeStore` gains the new value
  name if the store contract changes; a round-trip test for the new preference is added.

## Behaviour fixed by this note

- **Aggregate, not per-device.** One bubble, mirroring `TrayStatusAggregator`'s output and its
  documented priority order, so the bubble and the tray icon can never disagree.
- **Colours are the existing ones.** No new palette: green Connected, amber Attention,
  blue/cyan PairingAvailable, blue Special, red Failure. The pre-existing blue overload (special
  modes vs ready-to-pair) is resolved by the hover text, exactly as the tray does today
  (§30: "Exact meaning is supplied by tooltip / notification / flyout").
- **"Just disconnected" is new state.** Today a disconnect collapses the aggregate straight to
  Idle, so an explicit drop episode is required for the red to exist at all. It is a
  presentation-layer episode (see below), not a snapshot field.
- **Transience lives in the presentation layer.** The 60 s expiry is a one-shot,
  generation-guarded timer in the bubble's own view model — the same shape the engine already
  uses for tracker-failure grace and the 60 s pairing wait — so `AppSnapshot` and
  `BridgeProtocol` are untouched and no new periodic work is introduced (R-05).
- **Never disruptive.** `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, no activation on
  show or click, hidden while a full-screen application, presentation or the lock screen is
  active. It is a second surface, so this is a design-authority change — which is why §4 must
  actually say so (item A).

## Consequences

- `DESIGN.md` changes for the first time since the freeze; `docs/DESIGN.sha256` changes with it.
- The tray menu grows from five items to six.
- A new preference (`StatusBubbleEnabled`, default **off**) joins the registry store.
- R-02's "no new component variants" is respected: the bubble is a new *surface* with its own
  small view, not a variant of any existing card, and its name avoids the forbidden identifiers
  (`MiniDeviceCard` etc.).
