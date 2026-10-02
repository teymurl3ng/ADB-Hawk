"""Stitch screen generation driver (Phase D1).

Generates the remaining native-UI screens via the Stitch MCP endpoint,
saves PNG + HTML exports into docs/design-native/exports/, and appends
a per-screen result to .stitch-genlog.json.

Usage: python scripts/stitch-generate.py <screenKey>
Screen keys: s2 s3 s4 s5 s6 s7 s8 s9 s10 s11
"""
import json
import sys
import base64
import urllib.request

CONFIG = r"C:\Users\Teymur\.zcode\cli\config.json"
PROJECT_ID = "3831602511481100098"
DESIGN_SYSTEM = "assets/0919f15088234eb797968405c10bfdcd"  # "Fluent Mica ADB Flyout"
ENDPOINT = "https://stitch.googleapis.com/mcp"
EXPORTS = "docs/design-native/exports"

FOUNDATION = """Design a high-fidelity dark-themed UI for "ADB View" - a compact Windows 11 system-tray flyout that shows the live status of Android devices connected to the PC (USB and Wi-Fi) and handles wireless pairing. It is the same kind of surface as the Windows 11 Wi-Fi quick-settings flyout: a narrow system panel, compact and precise, NOT a web dashboard.

Canvas: a single narrow column, 376 px wide, full height. Windows 11 Fluent design language: Mica-like very dark backdrop (#202329, ~94% opacity), Segoe UI Variable font, 8 px rounded corners, hairline borders at low opacity (rgba(255,255,255,0.08)), cards as slightly lighter surfaces (rgba(255,255,255,0.04)), compact 14 px card padding, 12 px vertical gaps between cards.

Typography: card titles 13.5 px semibold; device names 15 px semibold; supporting text 12 px regular in rgba(255,255,255,0.64); values 12 px medium in rgba(255,255,255,0.83). Sentence case everywhere - no uppercase labels.

Semantic colors (as small pills with a 6 px status dot + text): connected = green #4ade80, attention/unauthorized = amber #fbbf24, failure = red #f87171, special modes (recovery/bootloader) = sky #38bdf8. Accent for interactive elements and pairing: #60a5fa.

Icons: Segoe Fluent Icons-style 16 px line icons, 2 px stroke (smartphone, wifi, usb, battery, copy, checkmark, pin, chevron-down, alert). No emoji, no filled duotone icons, no illustrations.

The design should feel like a refined Windows 11 system component: quiet, precise, premium. No gradients, no decorative elements.

Use the established design system for this project (same palette, fonts, spacing, and card anatomy as the previously generated "ADB View - No Devices Connected" screen).

SCREEN: """

SCREENS = {
    "s2": """One device card, connected over USB: "Galaxy S24" (15 px semibold) with age line "Connected just now" (12 px secondary) beside a 32 px rounded-square icon tile containing a 16 px smartphone line icon.

Footer row of the card, separated by a hairline top border: a green status pill ("Connected", 6 px green dot + text, pill background rgba(74,222,128,0.09)) + a small transport chip: 13 px USB line icon + text "USB" (12 px secondary) + on the right: "82%" (12 px medium) + a 15 px battery line icon with three fill bars.

Show BOTH states side by side if possible: the collapsed card above, and the same card expanded below. The expanded card: below the footer, a detail section with label/value rows (12 px; labels muted left-aligned - "Model", "Android", "Serial"; values right-aligned, 11.5 px monospace - "SM-S921W", "16", "R5CW-REDACTED") and a "Serial" row with a small 20 px copy icon button at the far right. A chevron-down at the right edge of the header row.""",
    "s3": """One device card, connected over Wi-Fi: "Galaxy S24" with age line "Connected 46s ago", transport chip showing a 13 px Wi-Fi line icon + "Wi-Fi", battery "29%", and the expanded details showing an "Address" row with the value 192.168.1.144:39421 (11.5 px monospace) + a 20 px copy icon button. Include the chevron-down. Show the collapsed card.""",
    "s4": """A screen with several device cards showing the state system (cards stacked in the scroll column):
- "Galaxy S24" - amber pill "Unauthorized" (amber surface rgba(251,191,36,0.09)) + guidance line under the header: "Check the phone and allow USB debugging." (12 px secondary). No battery.
- "Galaxy S24" - neutral pill "Offline" (plain card colors, muted dot), age "4m ago".
- "Pixel 8" - sky-blue pill "Recovery" (sky surface rgba(56,189,248,0.09)).
- "Pixel 8" - sky-blue pill "Bootloader".
Same card anatomy for every card: 32 px icon tile, name 15 px, chevron, hairline footer.""",
    "s5": """The shell with SIX device cards stacked in a scrollable column (scrollbar subtly visible): Galaxy S24 Wi-Fi 82%, Pixel 8 USB 67%, Pixel 5 unauthorized USB, Galaxy Tab S10 recovery USB, OnePlus 13 offline Wi-Fi with address, Nothing Phone USB 74%. The scroll column scrolls under the fixed header; the window never grows past its max height. Individual cards stay full-size - no compact variants.""",
    "s6": """A single accent-tinted pairing card (accent surface rgba(96,165,250,0.08), accent border, 8 px radius) with a 32 px rounded accent icon tile containing a 16 px wireless-waves pairing icon (concentric arcs), and:
- Title "Wireless pairing available" (13.5 px semibold) with a 6 px accent dot
- Device name "Galaxy S24" (14 px medium)
- Line "Waiting for a pairing code" (12 px secondary)
- Right side: an accent-filled "Pair" button (accent background, white text, 12 px medium, 28 px tall, 8 px radius) with a 12 px arrow-right icon.
Cool informational treatment - an opportunity, not an error.""",
    "s7": """The pairing card in code-entry state:
- Header row: "Galaxy S24" (14.5 px semibold) with a right-aligned small label "Wi-Fi Pairing" (11 px accent)
- Instruction: "Enter the six-digit code shown on the phone." (12 px)
- SIX digit slots in a single row (equal widths, 8 px gaps): each 36 px tall, rounded, slot background rgba(0,0,0,0.4), hairline border; filled digits 15 px semibold white (show 3 filled: 2 4 8); the 4th slot shows a blinking 1.5 px accent caret with a subtle focus ring; empty slots show an underscore.
- Actions row, right-aligned, hairline top border above: a ghost "Cancel" button (12 px, muted) and an accent "Pair" submit button.""",
    "s8": """THREE states of the pairing card, stacked with small gaps, to show the lifecycle:
- Progress: centered layout - a 32 px ring spinner (accent, 2 px track at 20% opacity, accent arc), title "Pairing with Galaxy S24…", line "Keep the phone nearby while pairing completes." Below, a quiet full-width "Cancel pairing" text button above a hairline border.
- Success: green-tinted card surface - 32 px green circle with a white checkmark, title "Paired with Galaxy S24", line "Waiting for ADB connection…". No buttons.
- Failure: red-tinted card surface - 28 px red circle with an alert-circle line icon, title "Pairing failed", body line "Check the code and try again." Actions: ghost "Cancel" + a red "Retry" button (red background, white text).""",
    "s9": """The pairing card in QR mode: same accent header (wireless-waves icon + "Pair a device over Wi-Fi"), below it a centered 220 x 220 px white rounded panel containing a DENSE REAL QR CODE (many small modules - this encodes a real credential), then two instruction lines: "On the phone, open Developer options → Wireless debugging." and "Tap Pair device with QR code and point the camera at this code." (12 px secondary), and a right-aligned ghost "Cancel" button.""",
    "s10": """A warning-idiom card (Fluent InfoBar personality - amber, structured): 32 px amber circular icon tile (a "decaying signal" line icon - a large arc breaking into a diagonal line), title "Wireless discovery isn't working" (13.5 px semibold), body: "Galaxy S24 is connected over Wi-Fi, but ADB isn't receiving device announcements on this network." (12 px).

Below the body, an evidence list (12 px secondary, "•" bullets):
- "3 device announcements are visible on this network."
- "Microsoft Edge is using the discovery port."
- "Virtual network adapters are active (Tailscale)."

Then a button row (wrapping, small): "Restart ADB" and "Stop Tailscale service" (12 px medium, card buttons with hairline borders; the latter with a small admin/shield glyph). Footer right-aligned: "Check again" (12 px).""",
    "s11": """The full shell in its most loaded state, top to bottom: header (ADB View + pin), the "Pair a device over Wi-Fi" quiet entry row, one connected device card over Wi-Fi (Galaxy S24, 29%, address details expanded), and the amber discovery-health warning card below. Everything in one scrollable 376 px column with 12 px gaps. This is the composition shot proving ordering: entry actions, devices, diagnostics.""",
}


def rpc(payload):
    key = json.load(open(CONFIG))["mcp"]["servers"]["stitch"]["headers"]["X-Goog-Api-Key"]
    req = urllib.request.Request(
        ENDPOINT,
        data=json.dumps(payload).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "X-Goog-Api-Key": key,
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=540) as resp:
        body = resp.read().decode("utf-8")
    return json.loads(body)


def main(screen_key: str) -> int:
    if screen_key not in SCREENS:
        print(f"unknown screen key: {screen_key}")
        return 2
    prompt = FOUNDATION + "\nSCREEN: " + SCREENS[screen_key]
    print(f"[{screen_key}] generating...", flush=True)
    result = rpc({
        "jsonrpc": "2.0",
        "id": 10,
        "method": "tools/call",
        "params": {
            "name": "generate_screen_from_text",
            "arguments": {
                "projectId": PROJECT_ID,
                "prompt": prompt,
                "deviceType": "MOBILE",
                "designSystem": DESIGN_SYSTEM,
            },
        },
    })
    if "error" in result:
        print(f"[{screen_key}] RPC error:", json.dumps(result["error"])[:300], flush=True)
        return 1

    inner = None
    for c in result.get("result", {}).get("content", []):
        if c.get("type") == "text":
            inner = json.loads(c["text"])
            break
    screen = None
    for comp in inner.get("outputComponents", []):
        if "design" in comp:
            screen = comp["design"]["screens"][0]
            break
    if screen is None:
        print(f"[{screen_key}] no screen in response", flush=True)
        return 1

    sid = screen.get("id")
    shot_url = screen.get("screenshot", {}).get("downloadUrl")
    html_url = screen.get("htmlCode", {}).get("downloadUrl")
    png_path = f"{EXPORTS}/{screen_key}-{sid[:8]}.png"
    html_path = f"{EXPORTS}/{screen_key}-{sid[:8]}.html"
    urllib.request.urlretrieve(shot_url, png_path)
    urllib.request.urlretrieve(html_url, html_path)
    print(f"[{screen_key}] OK id={sid} title={screen.get('title')!r} -> {png_path}", flush=True)

    log = json.load(open(f"{EXPORTS}/.stitch-genlog.json")) if __import__("os").path.exists(f"{EXPORTS}/.stitch-genlog.json") else {}
    log[screen_key] = {"id": sid, "png": png_path, "html": html_path, "title": screen.get("title")}
    json.dump(log, open(f"{EXPORTS}/.stitch-genlog.json", "w"), indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1].lower() if len(sys.argv) > 1 else ""))
