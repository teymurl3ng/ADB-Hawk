# ADB View — Native Fluent redesign: Stitch design briefs

- **Date:** 2026-10-02
- **Phase:** D1 of the native-UI migration (decision 0015 roadmap)
- **Purpose:** paste-ready prompts for [Stitch](https://stitch.withgoogle.com/) to design the native Fluent UI. Stitch output is a **visual reference only** — implementation is hand-built WPF + WPF-UI. The approved exports become the parity baseline (Phase D2).

## How to use this document

1. Work in Stitch in **web design mode** (it outputs web-style screens; that's fine — we're using it to explore the *look*, not to ship code). Set each generation to a **narrow mobile-ish canvas** — the flyout is a 376 px-wide column; mobile form factor approximates it best.
2. Start every NEW Stitch thread with the **Foundation prompt** below so the style is consistent across screens, then follow with the per-screen brief.
3. Iterate conversationally ("make the cards darker", "use 8px rounded corners everywhere") rather than regenerating from scratch — consistency across screens matters more than any single perfect screen.
4. When a screen is final, export it (Figma or HTML) into `docs/design-native/exports/<screen>.<ext>` and note the Stitch thread link next to the file.
5. The product copy (every string in quotes) is frozen — do not let iteration paraphrase it. If Stitch rewrites a string, correct it before exporting.

## Design language guardrails (apply to every brief)

- Windows 11 **Fluent** look: the app is the same genre as the Windows 11 Wi-Fi / quick-settings flyouts — a system flyout, not a web dashboard.
- **Mica-like dark backdrop** (Windows 11 flyout material: very dark blue-grey with a faint tint of the desktop behind it — think `#202329` at ~94% opacity; suggest the material, Stitch can't do real translucency).
- **Segoe UI Variable** typography; weights: 600 for card titles, 500 for values/buttons, 400 for supporting text.
- **8 px rounded corners** on cards and the flyout itself; 4 px on small controls; fully rounded pills.
- Compact density: the whole screen is 376 px wide; card padding 14 px; nothing oversized, no hero imagery.
- **Segoe Fluent Icons** language for icons (line icons, 16 px): phone, wifi, usb, battery, copy, checkmark, pin, chevron, alert. If Stitch draws decorative icons, ask for simple 2px-stroke line icons instead.
- Accent color: **Windows blue `#60a5fa`** (dark) — used for pairing, links, focus. Semantic: green `#4ade80` = connected/success, amber `#fbbf24` = attention, red `#f87171` = failure, sky `#38bdf8` = special states (Recovery/Bootloader/Sideload).
- Motion is Fluent-standard (subtle fades/slides, ~150 ms) — note any motion intent as a text annotation on the export; Stitch won't animate.

---

## Foundation prompt (start every new Stitch thread with this)

> Design a high-fidelity dark-themed UI for **"ADB View"** — a compact Windows 11 system-tray flyout that shows the live status of Android devices connected to the PC (USB and Wi-Fi) and handles wireless pairing. It is the same kind of surface as the Windows 11 Wi-Fi quick-settings flyout: a narrow system panel, compact and precise, **not** a web dashboard.
>
> Canvas: a single narrow column, **376 px wide**, full height. Windows 11 Fluent design language: **Mica-like very dark backdrop (#202329, ~94% opacity)**, **Segoe UI Variable** font, **8 px rounded corners**, hairline borders at low opacity (rgba(255,255,255,0.08)), cards as slightly lighter surfaces (rgba(255,255,255,0.04)), compact 14 px card padding, 12 px vertical gaps between cards.
>
> Typography: card titles 13.5 px semibold; device names 15 px semibold; supporting text 12 px regular in rgba(255,255,255,0.64); values 12 px medium in rgba(255,255,255,0.83). Sentence case everywhere — no uppercase labels.
>
> Semantic colors (as small pills with a 6 px status dot + text): connected = green #4ade80, attention/unauthorized = amber #fbbf24, failure = red #f87171, special modes (recovery/bootloader) = sky #38bdf8. Accent for interactive elements and pairing: #60a5fa.
>
> Icons: **Segoe Fluent Icons**-style 16 px line icons, 2 px stroke (smartphone, wifi, usb, battery, copy, checkmark, pin, chevron-down, alert). No emoji, no filled duotone icons, no illustrations.
>
> The design should feel like a refined Windows 11 system component: quiet, precise, premium. No gradients, no shadows heavier than the host window's own, no decorative elements.

---

## Screen briefs

### S1 — Flyout shell + empty state (fixture: `empty`)

> The flyout is open with **no devices connected**.
>
> Top: a 40 px header row — left: an 18 px smartphone line icon in accent blue #60a5fa + title "ADB View" (13.5 px semibold); right: a subtle 28 px **pin button** (line pin icon; when unpinned it's just a muted glyph, no box).
>
> Below: a single centered card (8 px radius, card surface, hairline border, 24 px padding) containing: a 40 px circular icon tile (subtle border, muted smartphone icon), the title **"No devices connected"** (14 px semibold), and the line **"Attach a device via USB or connect over Wi-Fi to observe ADB state."** (12 px, secondary color, centered, max-width 220 px).
>
> Below the card: a slim full-width action row — small 13 px wifi line icon + text **"Pair a device over Wi-Fi"** (secondary color, hover brightens to full white) styled as a quiet card button (card surface, hairline border, 10 px padding, 8 px radius).
>
> No settings gear, no refresh button, no footer. The pin is the only header control.

### S2 — Device card, connected over USB (fixture: `connected-usb`)

> One device card on the shell: **"Galaxy S24"** (15 px semibold) with the age line **"Connected just now"** (12 px secondary) beside a 32 px rounded-square icon tile containing a 16 px smartphone line icon.
>
> Footer row of the card, separated by a hairline top border: a green status pill (**"Connected"**, 6 px green dot + text, pill background rgba(74,222,128,0.09)) + a small transport chip: 13 px USB line icon + text **"USB"** (12 px secondary) + on the right: **"100%"** (12 px medium) + a 15 px battery line icon with three fill bars.
>
> The card header row is clickable (chevron-down at the right edge, 14 px, muted) to expand details. Show both states side by side if possible: collapsed above, expanded below.
>
> Expanded card (same card, grown): below the footer, a second hairline-bordered detail section with three label/value rows (12 px; labels muted left-aligned — **"Model"**, **"Android"**, **"Serial"**; values right-aligned, 11.5 px monospace — "SM-S921W", "16", "R5CW••••••••") and an **"Address"** row for Wi-Fi devices whose value is an IP:port in monospace with a small 20 px copy icon button at the far right. On copy, the icon is replaced by a small green **"Copied"** badge (10 px checkmark + word) for a moment.

### S3 — Device card, connected over Wi-Fi (fixture: `connected-wifi`)

> Same device card as S2 but the transport chip shows a **13 px Wi-Fi line icon + "Wi-Fi"**, the battery reads **"29%"**, and the expanded details include the **"Address"** row with the value `192.168.1.144:39421` (monospace) + copy button. Show the collapsed card with a Wi-Fi chip.

### S4 — Device states collage (fixtures: `unauthorized`, `offline`, `recovery`, `bootloader`, `sideload`, `unknown`)

> A screen with several device cards to show the state system:
> - **"Galaxy S24"** — amber pill **"Unauthorized"** + guidance line under the header: "Check the phone and allow USB debugging." (12 px secondary). No battery.
> - **"Galaxy S24"** — neutral pill **"Offline"** (plain card colors, muted dot), age "4m ago".
> - **"Pixel 8"** — sky-blue pill **"Recovery"** (sky surface rgba(56,189,248,0.09)).
> - **"Pixel 8"** — sky-blue pill **"Bootloader"**.
> Same card anatomy as S2 for every card: 32 px icon tile, name 15 px, chevron, hairline footer.

### S5 — Multi-device composition (fixture: `multi-device`)

> The shell with **six device cards** in a scrollable column (Galaxy S24 Wi-Fi 82%, Pixel 8 USB 67%, Pixel 5 unauthorized, Galaxy Tab S10 recovery, OnePlus 13 offline Wi-Fi, Nothing Phone USB 74%). This proves the scroll: the window never grows past its max height; content scrolls under a fixed header. Include the quiet heading treatment if density demands it — but individual cards must stay full-size (no compact variants).

### S6 — Pairing card: offer (fixture: `pairing-offer`)

> A single accent-tinted card (accent surface rgba(96,165,250,0.08), accent border, 8 px radius) with a 32 px rounded accent icon tile containing the 16 px **wireless-waves pairing icon** (concentric arcs), and:
> - Title **"Wireless pairing available"** (13.5 px semibold) with a 6 px accent dot
> - Device name **"Galaxy S24"** (14 px medium)
> - Line **"Waiting for a pairing code"** (12 px secondary)
> - Right side: an accent-filled **"Pair"** button (accent background, white text, 12 px medium, 28 px tall, 8 px radius) with a 12 px arrow-right icon.
>
> Cool informational treatment — this is an opportunity, not an error.

### S7 — Pairing card: code entry (fixture: `pairing-code`)

> The same card, now in code-entry state:
> - Header row: **"Galaxy S24"** (14.5 px semibold) with a right-aligned small label **"Wi-Fi Pairing"** (11 px accent)
> - Instruction: **"Enter the six-digit code shown on the phone."** (12 px)
> - **Six digit slots** in a single row (equal widths, 8 px gaps): each 36 px tall, rounded, slot background rgba(0,0,0,0.4), hairline border; filled digits 15 px semibold white; the current slot shows a blinking 1.5 px accent caret and a subtle focus ring; empty slots show an underscore.
> - Actions row, right-aligned, hairline top border above: a ghost **"Cancel"** button (12 px, muted, hover brightens) and an accent **"Pair"** submit button (disabled state: muted grey background).

### S8 — Pairing card: progress + success + failure (fixtures: `pairing-progress`, `pairing-success`, `pairing-failure`)

> Three states of the same card:
> - **Progress**: centered layout — a 32 px ring spinner (accent, 2 px track at 20% opacity, accent arc), title **"Pairing with Galaxy S24…"**, line **"Keep the phone nearby while pairing completes."** Below, a quiet full-width **"Cancel pairing"** text button above a hairline border.
> - **Success**: green-tinted card surface — 32 px green circle with a white checkmark, title **"Paired with Galaxy S24"**, line **"Waiting for ADB connection…"**. No buttons.
> - **Failure**: red-tinted card surface — 28 px red circle with an alert-circle icon, title **"Pairing failed"**, body line **"Check the code and try again."** Actions: ghost **"Cancel"** + a red **"Retry"** button (red background, white text).

### S9 — Pairing card: QR session (fixture: `pairing-qr`)

> The card in QR mode: same accent header (wifi icon + **"Pair a device over Wi-Fi"**), below it a centered **220 × 220 px white rounded panel containing a QR code** (generate a real dense QR-like pattern — this is a real credential), then two instruction lines: **"On the phone, open Developer options → Wireless debugging."** and **"Tap Pair device with QR code and point the camera at this code."** (12 px secondary), and a right-aligned ghost **"Cancel"** button.

### S10 — Discovery health card (fixture: `discovery-health`)

> A warning-idiom card (Fluent InfoBar personality — amber, but structured): 32 px **amber** circular icon tile (a "decaying signal" line icon — a large arc breaking into a diagonal line), title **"Wireless discovery isn't working"** (13.5 px semibold), body: **"Galaxy S24 is connected over Wi-Fi, but ADB isn't receiving device announcements on this network."** (12 px).
>
> Below the body, an evidence list (12 px secondary, "•" bullets):
> - "3 device announcements are visible on this network."
> - "Microsoft Edge is using the discovery port."
> - "Virtual network adapters are active (Tailscale)."
>
> Then a button row (wrapping, small): **"Restart ADB"** and **"Stop Tailscale service"** (12 px medium, card buttons with hairline borders; the latter marked with a small admin/shield glyph). Footer right-aligned: **"Check again"** (12 px, disabled styling while a measurement runs).

### S11 — Composition: shell with QR entry + devices + health card

> The full shell in its most loaded state, top to bottom: header, the "Pair a device over Wi-Fi" quiet entry row, one connected device card (Wi-Fi), and the discovery-health warning card. This proves the ordering: entry actions at top, devices, diagnostics last — everything in one scrollable 376 px column.

---

## Export checklist (per screen)

- [ ] Copy strings verbatim (no paraphrase)
- [ ] 376 px column, 8 px radii, hairline borders
- [ ] Semantic colors correct (green/amber/red/sky, accent blue)
- [ ] Icons are Fluent line-style
- [ ] Dark theme only (light theme is a token swap, designed later from the same structure)
- [ ] Exported to `docs/design-native/exports/` + thread link noted below

## Thread log

| Screen | Stitch thread | Export file | Status |
|---|---|---|---|
| S1 shell+empty | | | pending |
| S2 device usb | | | pending |
| S3 device wifi | | | pending |
| S4 states collage | | | pending |
| S5 multi-device | | | pending |
| S6 pairing offer | | | pending |
| S7 code entry | | | pending |
| S8 progress/success/failure | | | pending |
| S9 QR | | | pending |
| S10 discovery health | | | pending |
| S11 full composition | | | pending |
