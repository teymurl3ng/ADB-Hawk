# 0018 — Native shell metrics: header inset, pin hit target, hover chrome

- **Date:** 2026-10-03
- **Relationship to DESIGN.md:** DESIGN.md is untouched. It fixes the header at 40 px
  (§5) and mandates a 4 px grid (§6) with a ~40×40 pin interaction target (§5), but
  **specifies no outer inset** for the native shell — so this note fills a gap rather
  than changing the frozen contract. R-02 forbids unrequested "tidied" spacing; the
  owner reported this as a usability problem, which is why it is recorded here.

## Problem

Owner report: "ADB View, the logo and the pin button are so close to the edge of the app
— it doesn't feel right", plus "a thin line appears when I hover over the Pair wirelessly
button".

Measured before the change:

- Header `Margin="12,0,8,8"` while the content cards used 12 → the pin's right inset was
  **8** (4 tighter than the cards below it).
- The 8 px bottom margin left a 32 px box inside the 40 px header for a 28 px button, so
  the pin sat **2 px** from the window's top edge and the row's contents rode 4 px above
  the row centre.
- The pin's hit area was 28×28, below DESIGN.md §5's ~40×40 target.
- The QR row was a card `Border` (radius 8, `Padding="10,10"`) wrapping an inner Button,
  so the button's hover fill (radius 4) was inset 10 px inside the card. That boundary
  read as a stray hairline — worst in light theme (5 % black on opaque white). The web
  reference and the Stitch brief never filled this row on hover at all; only the text
  brightened.

## Decision

1. **Uniform 16 px inset** for the header and the content column (header
   `Margin="16,0,16,0"`, content `Margin="16,4,16,0"`), matching the reference geometry
   recorded in `research/2026-09-20-header-drag-native-probe.md` (`p-4` = 16 px). The pin
   is centred in the full 40 px header (6 px above/below) and the header/content gap stays
   ≈10 px via the content's top margin.
2. **Pin hit target 36×36** — the largest that keeps breathing room inside a 40 px header
   (DESIGN.md's ~40×40 approximated within the header's own height); the glyph is
   unchanged at 14 px, so the visual size does not change.
3. **Hover chrome correction.** The QR row's card chrome now belongs to the button itself
   (`RowCardButton`: radius 8, the card background preserved under a hover overlay), so
   hovering fills the whole row at the card's own radius with no interior edge. The dead
   `BorderBrush` hover setter in the shared button template is deleted (every button has
   `BorderThickness` 0, so it could never render — it only invited stray lines).
4. **Chip buttons inherit the theme template.** `ChipButton` in the discovery-health card
   gained `BasedOn="{StaticResource {x:Type Button}}"`; without it an explicit `Style`
   replaces the implicit one and those buttons fell back to raw Aero2 chrome — exactly what
   `Themes/Controls.xaml` exists to prevent. The unused `QuietButton` style is deleted.

## Acceptance

- Header/pin inset and vertical centring as measured above; hover on the QR row fills the
  card with no interior hairline; the three repair chips use the themed template.
- `scripts/verify.ps1` green; screenshots captured in both themes.
