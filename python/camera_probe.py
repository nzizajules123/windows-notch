"""Scores every capture backend/format of each camera for stripe noise and exposure.

Prints one line per combination; lower "stripes" is cleaner. No images are saved.
Usage:  python camera_probe.py [max_cameras]
"""
import sys
import time

import cv2
import numpy as np

BACKENDS = {"DSHOW": cv2.CAP_DSHOW, "MSMF": cv2.CAP_MSMF}
FORMATS = {"MJPG": "MJPG", "YUY2": "YUY2", "default": None}


def stripe_score(frame):
    """Vertical-stripe noise: how much more neighbouring columns differ than neighbouring rows."""
    g = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY).astype(np.float32)
    col = np.abs(np.diff(g, axis=1)).mean()
    row = np.abs(np.diff(g, axis=0)).mean()
    return col / max(row, 1e-3), col


def probe(index, backend_name, fmt_name):
    cap = cv2.VideoCapture(index, BACKENDS[backend_name])
    if not cap.isOpened():
        return None
    fourcc = FORMATS[fmt_name]
    if fourcc:
        cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*fourcc))
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    frames = []
    t0 = time.time()
    while time.time() - t0 < 3 and len(frames) < 25:
        ok, f = cap.read()
        if ok and f is not None and f.size:
            frames.append(f)
    actual = int(cap.get(cv2.CAP_PROP_FOURCC))
    cap.release()
    if len(frames) < 5:
        return f"{backend_name:5} {fmt_name:7} -> no frames"
    f = frames[-1]
    ratio, col = stripe_score(f)
    bright = f.mean()
    clipped = (f.max(axis=2) >= 250).mean() * 100
    got = "".join(chr((actual >> 8 * i) & 0xFF) for i in range(4)) if actual else "?"
    fps = len(frames) / (time.time() - t0)
    return (f"{backend_name:5} {fmt_name:7} got={got:4} {f.shape[1]}x{f.shape[0]} fps~{fps:4.1f} "
            f"stripes={ratio:5.2f} colnoise={col:5.1f} brightness={bright:5.1f} overexposed={clipped:4.1f}%")


if __name__ == "__main__":
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 2
    for idx in range(n):
        for b in BACKENDS:
            for fmt in FORMATS:
                r = probe(idx, b, fmt)
                if r:
                    print(f"cam{idx} {r}", flush=True)
