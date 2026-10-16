"""Analyze actual cropped desktop pixels and OS QPC presentation timestamps."""
import csv
import gzip
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

prefix = Path(sys.argv[1])
metadata = json.loads(Path(str(prefix) + ".ready.json").read_text(encoding="utf-8-sig"))
events = json.loads(Path(str(prefix) + ".events.json").read_text(encoding="utf-8-sig"))
with open(str(prefix) + ".frames.csv", newline="", encoding="utf-8-sig") as stream:
    rows = list(csv.DictReader(stream))
width, height = metadata["Width"], metadata["Height"]
frequency = metadata["Frequency"]
frame_bytes = width * height * 4
changed = []
accumulation = []
previous = None
saved = set()
content_samples = {event["Index"]: [] for event in events}
content_before = {}
last_content = None
warm_reference = None
with gzip.open(str(prefix) + ".bgra.gz", "rb") as stream:
    for index, row in enumerate(rows):
        raw = stream.read(frame_bytes)
        if len(raw) != frame_bytes:
            raise RuntimeError("Frame data is truncated; evidence cannot pass")
        pixels = np.frombuffer(raw, dtype=np.uint8).reshape(height, width, 4)
        if index == 0 and metadata.get("WarmOpen") and width == 476 and height == 606:
            warm_reference = pixels[100:145, 12:width-12, :3].copy()
        # Exclude default native border, header/pin, and any cursor (not recorded).
        body = pixels[100:height-8, 12:width-12, :3]
        qpc = int(row["PresentQpc"])
        accumulation.append(int(row["AccumulatedFrames"]))
        # Static destination heading/first row only, above the pairing spinner.
        # This diagnostic band is verified for this recorded 476x606 fixture.
        if width == 476 and height == 606:
            owner = next((event for event in reversed(events) if event["InputQpc"] <= qpc), None)
            if owner is not None and qpc < owner["InputQpc"] + frequency * .30:
                if not content_samples[owner["Index"]] and last_content is not None:
                    content_before[owner["Index"]] = last_content
                content_samples[owner["Index"]].append((qpc, pixels[100:145, 12:width-12, :3].copy()))
            last_content = pixels[100:145, 12:width-12, :3].copy()
        if previous is not None and np.any(body != previous):
            changed.append(qpc)
        previous = body.copy()
        relevant = [e for e in events if e["InputQpc"] <= qpc <= e["InputQpc"] + frequency * .16]
        if relevant and relevant[0]["Index"] < 3:
            phase = int((qpc-relevant[0]["InputQpc"]) / frequency * 1000) // 30
            key = (relevant[0]["Index"], phase)
            if key not in saved:
                saved.add(key)
                Image.fromarray(pixels[:, :, [2, 1, 0, 3]], "RGBA").save(str(prefix)+f".event{key[0]}-phase{key[1]}.png")
    if stream.read(1):
        raise RuntimeError("Frame stream contains unindexed data")

transitions = []
all_intervals = []
for event in events:
    start = event["InputQpc"]
    # Exclude time between transitions. Keep leading delay as its own metric.
    active = [qpc for qpc in changed if start <= qpc < start + .19*frequency]
    intervals = np.diff(active) / frequency * 1000
    all_intervals.extend(intervals.tolist())
    transitions.append({"Index": event["Index"], "Command": event["Command"], "ChangingFrames": len(active),
                        "FirstChangeMs": ((active[0]-start)/frequency*1000) if active else None,
                        "LastChangeMs": ((active[-1]-start)/frequency*1000) if active else None,
                        "MaximumIntervalMs": float(max(intervals)) if len(intervals) else None})

first = [e["FirstChangeMs"] for e in transitions if e["FirstChangeMs"] is not None]
content_results = []
content_tiles = []
for event in events:
    samples = content_samples[event["Index"]]
    settled = [sample for sample in samples if sample[0] >= event["InputQpc"] + frequency * .23]
    if metadata.get("WarmOpen"):
        # A static desktop need not present a new image in an arbitrary late
        # time band. The indexed, fully warmed baseline predates all opens.
        settled = [(metadata["BaselinePresentQpc"], warm_reference)] if warm_reference is not None else []
    ready = None
    wrong_route_rejected = None
    hidden_baseline_rejected = None
    if settled:
        reference = settled[-1][1].astype(np.int16)
        ink = np.max(reference, axis=2) >= 100
        if np.count_nonzero(ink) < 50:
            raise RuntimeError("Destination content band has too little text for readiness evidence")
        def matches(candidate):
            error = np.max(np.abs(candidate.astype(np.int16) - reference), axis=2)[ink]
            return np.mean(error <= 16) >= .95 and np.mean(error) <= 8
        ready = next((sample for sample in samples if matches(sample[1])), None)
        other = [item for item in events if item["Command"] != event["Command"] and content_samples[item["Index"]]]
        wrong_route_rejected = bool(other) and not matches(content_samples[other[0]["Index"]][-1][1])
        if metadata.get("WarmOpen") and event["Index"] in content_before:
            baseline = content_before[event["Index"]]
            magenta = np.mean(np.all(baseline == [255,0,255], axis=2)) >= .95
            hidden_baseline_rejected = bool(magenta and not matches(baseline))
        if ready is not None:
            content_tiles.append((event["Index"], (ready[0]-event["InputQpc"])/frequency*1000, ready[1]))
    content_results.append({"Index": event["Index"], "Command": event["Command"],
                            "UsefulContentProxyMs": (ready[0]-event["InputQpc"])/frequency*1000 if ready else None,
                            "WrongRouteRejected": wrong_route_rejected,
                            "HiddenBaselineRejected": hidden_baseline_rejected})
if content_tiles:
    from PIL import ImageDraw
    strip = Image.new("RGB", (width-24, len(content_tiles)*65), "#202329")
    draw = ImageDraw.Draw(strip)
    for row_index, (event_index, milliseconds, pixels) in enumerate(content_tiles):
        draw.text((0,row_index*65), f"Event {event_index}: {milliseconds:.2f} ms", fill="white")
        strip.paste(Image.fromarray(pixels[:, :, ::-1], "RGB"), (0,row_index*65+20))
    strip.save(str(prefix)+".content-evidence.png")
content_times = [item["UsefulContentProxyMs"] for item in content_results if item["UsefulContentProxyMs"] is not None]
result = {"Instrument": "DXGI Desktop Duplication; OS LastPresentTime plus cropped probe pixels",
          "Frames": len(rows), "ChangingCropFrames": len(changed), "Transitions": transitions,
          "ObservedAccumulationMax": max(accumulation), "ObserverFramesWithAccumulation": sum(a > 1 for a in accumulation),
          "FirstChangeP95Ms": float(np.percentile(first, 95)) if first else None,
          "IntervalP95Ms": float(np.percentile(all_intervals,95)) if all_intervals else None,
          "IntervalP99Ms": float(np.percentile(all_intervals,99)) if all_intervals else None,
          "IntervalMaximumMs": float(max(all_intervals)) if all_intervals else None,
          "ContentEvidence": content_results,
          "ContentProxyCount": len(content_times),
          "ContentProxyP95Ms": float(np.percentile(content_times,95)) if content_times else None,
          "ContentProxyMaximumMs": max(content_times) if content_times else None,
          "ContentProxyDefinition": "Recorded 476x606 fixture only: static first-content band excluding progress, >=95% of settled bright destination text pixels within 16/255 and mean channel error <=8/255. Navigation reference >=230ms; warm-open reference is the indexed fully warmed baseline before all input. Wrong-route/hidden magenta control retained. Pixel readiness proxy, not physical scanout or complete-content comprehension.",
          "Limits": "Observed desktop image presentation, not physical scanout. UI-command→first pixel change, not necessarily useful-content readiness. Busy indicator contributes changes on pairing. Accumulated desktop frames must be considered before interpreting cadence."}
Path(str(prefix)+".analysis.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps({key:value for key,value in result.items() if key not in ("Transitions", "ContentEvidence")}, indent=2))
