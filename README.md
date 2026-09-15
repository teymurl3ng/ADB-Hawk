# ADB View

ADB View is a small Windows 11 system-tray utility for observing, explaining, notifying, and pairing ADB connections.

## Repository status

This package is the **v1 design + engineering architecture freeze**.

No implementation code is included yet.

## Governing documents

Read these before changing the project:

1. `DESIGN.md` — frozen product and visual authority
2. `ENGINEERING.md` — frozen greenfield engineering architecture
3. `PHASES.md` — ordered implementation plan
4. `AGENTS.md` — mandatory rules for coding agents

Supporting current research lives in `research/`.

The complete Stitch design source/reference archive lives in `design/reference/stitch_adb_view_frozen_design.zip`.

## Product boundary

**Observe → explain → notify → pair**

ADB View is not an ADB toolbox. Do not add shell, logcat, APK installation, file transfer, screen mirroring, reboot controls, process management, dashboards, analytics, or history unless the governing specifications are explicitly revised first.

## Next step

Begin **Phase 0** from `PHASES.md`.

Phase 0 establishes the greenfield repository skeleton, pinned toolchains, build/test verification, CI, and architecture guardrails. It must not add application features.

## Freeze checkpoint

After extracting this package into the repository root, make a dedicated freeze commit before Phase 0 implementation.

Suggested commands:

```bash
git add .
git commit -m "Freeze ADB View v1 design and engineering architecture"
git tag architecture-v1-freeze
git push
git push origin architecture-v1-freeze
```
