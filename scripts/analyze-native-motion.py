"""M07-only pixel validation on the existing M02 desktop capture, including retarget controls."""
import csv
import gzip
import json
import runpy
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

prefix = Path(sys.argv[1])
runpy.run_path(str(Path(__file__).resolve().parents[1] / "prototypes/WinUiShellProbe/diagnostics/analyze-desktop.py"), run_name="__main__")
metadata = json.loads(Path(str(prefix) + ".ready.json").read_text(encoding="utf-8-sig"))
interruptions = json.loads(Path(str(prefix) + ".interruptions.json").read_text(encoding="utf-8-sig"))
rows = list(csv.DictReader(Path(str(prefix) + ".frames.csv").open(encoding="utf-8-sig", newline="")))
width, height, frequency = metadata["Width"], metadata["Height"], metadata["Frequency"]
if (width, height) != (476, 606):
    raise RuntimeError("This validated content band requires the recorded 476x606 extent.")
samples = [[] for _ in interruptions]
frame_bytes = width * height * 4
with gzip.open(str(prefix) + ".bgra.gz", "rb") as stream:
    for row in rows:
        raw = stream.read(frame_bytes)
        if len(raw) != frame_bytes:
            raise RuntimeError("Truncated frame data")
        qpc = int(row["PresentQpc"])
        for index, event in enumerate(interruptions):
            if event["InputQpc"] <= qpc <= event["ReplacedQpc"] + frequency * .29:
                pixels = np.frombuffer(raw, dtype=np.uint8).reshape(height, width, 4)
                samples[index].append((qpc, pixels[100:145, 12:width-12, :3].copy()))
    if stream.read(1):
        raise RuntimeError("Unindexed frame data")

results = []
tiles = []
for index, event in enumerate(interruptions):
    frames = samples[index]
    settled = [frame for frame in frames if frame[0] >= event["ReplacedQpc"] + frequency * .23]
    if not settled:
        raise RuntimeError("Missing settled reference for retarget")
    reference = settled[-1][1].astype(np.float64)
    background = np.median(reference.reshape(-1, 3), axis=0)
    reference -= background
    if np.count_nonzero(np.max(reference, axis=2) > 80) < 50:
        raise RuntimeError("Retarget reference has insufficient text")
    fits = []
    for qpc, pixels in frames:
        candidate = pixels.astype(np.float64) - background
        best = None
        for shift in range(-2, 18):
            shifted = np.roll(reference, shift, axis=1)[:, 20:-20, :]
            observed = candidate[:, 20:-20, :]
            alpha = float(np.sum(observed * shifted) / max(1, np.sum(shifted * shifted)))
            residual = float(np.mean((observed - alpha * shifted) ** 2))
            if best is None or residual < best[0]:
                best = (residual, shift, alpha)
        fits.append({"MillisecondsFromReplace": (qpc-event["ReplacedQpc"])/frequency*1000,
                     "ShiftPhysicalPixels": best[1], "OpacityProxy": best[2], "Residual": best[0]})
    before = [fit for fit in fits if fit["MillisecondsFromReplace"] < 0]
    after = [fit for fit in fits if 0 <= fit["MillisecondsFromReplace"] <= 70]
    if not before or len(after) < 3:
        raise RuntimeError("Insufficient samples around retarget")
    prior = before[-1]
    monotonic_opacity = min(fit["OpacityProxy"] for fit in after) >= prior["OpacityProxy"] - .08
    no_endpoint_restart = max(fit["ShiftPhysicalPixels"] for fit in after) <= prior["ShiftPhysicalPixels"] + 2
    results.append({"InterruptionMs": event["Milliseconds"], "Before": prior, "After": after,
                    "NoOpacityRestart": monotonic_opacity, "NoTranslationEndpointRestart": no_endpoint_restart,
                    "Passed": monotonic_opacity and no_endpoint_restart})
    for qpc, pixels in frames:
        ms = (qpc-event["ReplacedQpc"])/frequency*1000
        if -20 <= ms <= 70:
            tiles.append((f"{event['Milliseconds']} ms retarget; frame {ms:+.1f} ms", pixels))

strip = Image.new("RGB", (width-24, len(tiles)*65), "#202329")
draw = ImageDraw.Draw(strip)
for index, (label, pixels) in enumerate(tiles):
    draw.text((0,index*65), label, fill="white")
    strip.paste(Image.fromarray(pixels[:,:,::-1]), (0,index*65+20))
strip.save(str(prefix) + ".retarget-evidence.png")
analysis = {"Passed": all(result["Passed"] for result in results), "Results": results,
            "Method": "Fit the same static destination text against its settled reference: integer physical-pixel horizontal shift and a least-squares opacity proxy. Inspect recorded frames from -20 to +70 ms around replacement.",
            "Limits": "Pixel proxy, not compositor property reads or velocity continuity. At late interruption, near-zero remaining travel limits discrimination. Physical scanout/hardware qualification remains M09."}
Path(str(prefix) + ".retarget-analysis.json").write_text(json.dumps(analysis, indent=2), encoding="utf-8")
print(json.dumps(analysis, indent=2))
if not analysis["Passed"]:
    raise RuntimeError("Retargeted pixels restart/jump; M07 temporal gate failed.")
