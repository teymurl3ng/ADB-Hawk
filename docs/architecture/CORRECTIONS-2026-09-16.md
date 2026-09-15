# Architecture corrections applied — 2026-09-16

This package preserves the Opus architecture and applies the reviewed corrections before freeze.

1. Platform-tools baseline updated to 37.0.1; Openscreen removal and the now-ineffective `ADB_MDNS_OPENSCREEN` flag are recorded.
2. .NET 10 current patch updated to 10.0.12 (2026-09-08), support through 2028-11-14.
3. AdvancedSharpAdbClient 3.6.16 is correctly described as a current, .NET 10-compatible library; the decision not to use it is based on architecture fit, not abandonment.
4. The reducer event channel is unbounded/single-reader with no generic event dropping. Replaceable full-state events may only be coalesced explicitly at their producer boundary if later proven necessary.
5. Wording ownership is clarified: native `Vocabulary` owns dynamic semantic/shared wording; the frontend may own frozen static interface copy directly specified by `DESIGN.md`.
6. `ConnectedSince` is nullable; `StateSince` remains universal.
7. Frozen design/window constants (376 px width, 560 px max height) were removed from `AppSnapshot`; they remain implementation constants in the native shell/frontend.
