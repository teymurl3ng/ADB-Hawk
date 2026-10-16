# WinUI pairing presentation and existing action port

- **Date:** 2026-10-16
- **Author:** Codex
- **Phase:** authorized shell migration M06
- **Status:** current

## Problem

Port the existing pairing/diagnosis actions without moving backend policy into WinUI, recreating valid text input, or retaining an expired QR after asynchronous decoding.

## Sources consulted

| Source | URL | Version / last updated |
| --- | --- | --- |
| Microsoft Text box | https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/text-box | 2026-02-24; consulted 2026-10-16 |
| Microsoft SvgImageSource | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.imaging.svgimagesource?view=windows-app-sdk-2.0 | Windows App SDK API; consulted 2026-10-16 |
| Microsoft SetSourceAsync | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.imaging.svgimagesource.setsourceasync?view=windows-app-sdk-2.0 | Windows App SDK API; consulted 2026-10-16 |
| Microsoft DtdProcessing | https://learn.microsoft.com/en-us/dotnet/api/system.xml.xmlreadersettings.dtdprocessing?view=net-10.0 | .NET 10; consulted 2026-10-16 |
| Microsoft Segoe MDL2 icon table | https://learn.microsoft.com/en-us/windows/uwp/style/segoe-ui-symbol-font | Windows in-box font; consulted 2026-10-16; EA18 is Shield |

## Version investigated

.NET SDK 10.0.401, net10.0-windows10.0.26100.0, Windows App SDK 2.5.1, QRCoder 1.8.0, Windows 10.0.26100. No package or protocol changes. Existing QrPairingRequest.RenderSvg and the pinned QRCoder binary remain the payload authority.

## Findings

Microsoft documents local-stream SVG decoding and explicit logical decode dimensions. SetSourceAsync reports a status and may finish asynchronously; that API alone does not establish current operation identity. TextBox input scope is a keyboard hint, not validation. Explicit ASCII digit sanitization remains necessary. DTD processing can be prohibited. Actual shipped decoding and control identity require headed tests, not inference from documentation.

Observed pinned QRCoder 1.8.0 output: `svg` with width/height/viewBox/shape-rendering, one white `rect` and one black `path`. The first headed QR rejection identified the missing `shape-rendering` attribute in the restricted allowlist; permitting that existing attribute made the real decoder pass. No alternative renderer experiment was needed. The nonsecret fixture is 37 modules at four logical pixels each (148 DIP); keeping that natural extent avoids an arbitrary QR scale. Admin repair hints use the documented EA18 shield in the in-box Segoe MDL2 Assets font.

## Alternatives considered

| Option | Pros | Cons |
| --- | --- | --- |
| Native SvgImageSource from a validated local stream | Existing platform decoder; no package; preserves QRCoder output | Requires generation checks and immediate source removal |
| Handwritten SVG geometry converter | Synchronous | Adds a parser for an already supported image format |
| New SVG package or browser | General SVG support | Unnecessary dependency; browser prohibited |

## Chosen approach

Use a local SvgImageSource, fixed QR extent and no app-owned scale. Validate the backend's restricted SVG vocabulary before handing a stream to the decoder; prohibit DTDs and all external references. A generation check prevents a completed obsolete decode attaching after invalidation/navigation. Log only fixed subsystem/reason messages, never payloads or decode exception text. Preserve the same textbox/action row while a page remains valid; clear the draft on identity/phase change and leave/hide. Dispatch through an explicit backend action interface; fixture implementations record counts and opaque identities only. Backend sessions continue to validate bindings and own cancellation/retry.

## Why / rejected approaches

The platform already renders SVG. Avoid a new rasterizer, general XAML parsing, network URI loading, backend recovery changes and temporary real credentials. Do not infer device connection from pairing success.

## Compatibility concerns

Minimum supported Windows 11 exceeds the API's documented minimum. Documentation is not proof of this pinned binary's behavior; headed fixtures verify it. Mixed DPI, contrast and hardware scanning retain M09/M12 acceptance. M08 owns native notification subscriptions and live candidate lifetime.

## Testing implications

Exercise QRCoder with a fixed noncredential payload, stable decode/control identity, replacement/invalidation during decode, metadata updates, all operation phases, stale commands, hide/reopen, cancellation after Back and repair/undo links. Preserve all old assertions and run the full gate. Capture only synthetic inputs.

## Open items

Real-device wireless pairing/QR scan and physical environment qualification remain the existing M09/M12 matrix items; no backend acceptance is claimed from fixtures.

## M06 result

Full gate passed: 1,062 xUnit cases, 26 headed Release fixtures and 561 XAML assertions, including 150 in the pairing driver. Captured all seven changed surfaces on Windows build 26100. Current-snapshot and active-session guards reject stale action identities. A held decoder completion cannot reattach after invalidation. The real reducer can publish an operation before QR clearing; the page prioritizes the operation during that overlap. Native sizes remain unchanged. Back restores the existing keyed bookmark. No new dependency/timer, live pairing credential, machine repair or backend/WPF policy edit. Actual native subscription/composition remains M08; real-device proof remains M12.
