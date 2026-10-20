# adb-hawk-linux — Design (delta)

The product contract is **identical** to Windows ADB-Hawk (`../ADB-Hawk/DESIGN.md`): *what is ADB doing right now?* — **observe → explain → notify → pair**, nothing else. The adopted fixed-panel shell (`DESIGN.md` §41: 376×480 panel, header with Back/Pin, scrollable body, device details page, pairing in the same region, 32×32 stationary status bubble, motion recipes 0/83/167 ms) is the layout authority. Semantic states, wording, tray aggregation priority, notification meanings and the forbidden-feature list are unchanged.

This file lists only what differs on Linux. Anything not listed here is as in the Windows document.

## Wording
- "Install Android Platform Tools or make adb.exe available to Windows." → **"Install Android Platform Tools or make adb available on your PATH."**
- "Start with Windows" → **"Start at login"**.
- **New state — USB access not permitted** (adb reports `no permissions`): sentence-case, jargon-free, e.g. *"This computer isn't allowing access to the phone's USB connection."* No "udev", no group names, no rule paths in any user-visible surface; technical detail goes to the log only. It is **not** `Authorization required` (that one is fixed on the phone; this one is fixed on the computer).

## Tray and menu
- Same six states and the same marker colours. Menu items: **Open ADB Hawk**, **Start at login**, **Notifications**, **Status bubble**, **Theme ▸ System / Light / Dark**, **Exit**.
- The status dot is rendered by the app as a pixmap; the neutral glyph carries an outline that reads on both dark and light panels.
- If no tray host exists, the app shows the flyout at start and posts one notification; launching the binary again always toggles the flyout.

## Placement
- Click coordinates from the tray are a hint, never trusted blindly; fall back to the pointer position and clamp to monitor geometry.
- Tier chosen by capability, not desktop name: X11 → anchored near the tray; Wayland with layer-shell → anchored surface; Wayland without it → ordinary fixed-size window with immediate show/hide and internal navigation motion only (as `DESIGN.md` §41.4's last fallback). On that last tier the status bubble is disabled rather than mis-placed.
- On tiling window managers the flyout is a fixed-size utility window (min size = max size); floating rules are documented, not forced.

## Theme, contrast, reduced motion
- Theme/contrast: XDG Settings portal → GSettings → GTK theme-name heuristic. Reduced motion: GTK animations setting / desktop `enable-animations`. No universal Linux equivalent of "reduced motion" exists; the chain above is best-effort and is stated as such.
