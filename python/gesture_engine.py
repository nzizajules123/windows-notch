"""Windows Notch - Python hand-gesture engine.

Reads the camera with OpenCV (picking the cleanest backend/format), cleans the picture
(stripe removal + exposure fix), runs MediaPipe's gesture recognizer and reports to the
notch over stdout as JSON lines. Optionally shows a small always-on-top preview with
the hand skeleton drawn on top.

stdin commands:  start | stop | preview on | preview off | minhand 0.3 | quit
stdout messages: {"type": "ready"|"status"|"frame"|"diag"|"error"|"preview_closed", ...}
"""
import json
import os
import sys
import threading
import time
import urllib.request

import cv2
import numpy as np

MODEL_URL = ("https://storage.googleapis.com/mediapipe-models/gesture_recognizer/"
             "gesture_recognizer/float16/1/gesture_recognizer.task")
WIN = "Gesture camera"
PREVIEW_W, PREVIEW_H = 300, 225

BONES = [(0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8), (5, 9), (9, 10), (10, 11),
         (11, 12), (9, 13), (13, 14), (14, 15), (15, 16), (13, 17), (17, 18), (18, 19), (19, 20), (0, 17)]

# BGR colours
GREEN, PINK, BLUE = (88, 209, 48), (95, 55, 255), (255, 132, 10)
YELLOW, WHITE, DARK = (10, 214, 255), (255, 255, 255), (20, 20, 22)


def send(**msg):
    sys.stdout.write(json.dumps(msg) + "\n")
    sys.stdout.flush()


def log(text):
    send(type="diag", text=text)


# --------------------------------------------------------------------------- camera
def stripe_score(frame):
    g = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY).astype(np.float32)
    col = np.abs(np.diff(g, axis=1)).mean()
    row = np.abs(np.diff(g, axis=0)).mean()
    return col / max(row, 1e-3)


def open_camera(index, wanted=None):
    """Try backend/format combinations and keep the one with the cleanest picture."""
    combos = [(cv2.CAP_DSHOW, "MJPG"), (cv2.CAP_MSMF, None), (cv2.CAP_DSHOW, "YUY2"),
              (cv2.CAP_DSHOW, None), (cv2.CAP_MSMF, "MJPG")]
    if wanted:
        combos = [c for c in combos if f"{c[0]}:{c[1]}" == wanted] or combos
    best = None
    for api, fourcc in combos:
        cap = cv2.VideoCapture(index, api)
        if not cap.isOpened():
            continue
        if fourcc:
            cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*fourcc))
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
        frames, t0 = [], time.time()
        while time.time() - t0 < 4 and len(frames) < 10:   # slow webcams can run at ~1-5 fps
            ok, f = cap.read()
            if ok and f is not None and f.size:
                frames.append(f)
        if len(frames) < 2:
            cap.release()
            continue
        fps = len(frames) / max(time.time() - t0, 0.1)
        bright = float(frames[-1].mean())
        score = stripe_score(frames[-1]) + (5 if bright < 8 else 0)   # all-black picture = useless
        name = f"{'DSHOW' if api == cv2.CAP_DSHOW else 'MSMF'}/{fourcc or 'default'}"
        log(f"camera {index} {name}: {frames[-1].shape[1]}x{frames[-1].shape[0]} fps~{fps:.1f} "
            f"brightness={bright:.0f} stripes={score:.2f}")
        if bright < 8:
            log("camera is sending black frames - is it covered, or does it need re-plugging?")
        if best is None or score < best[0]:
            if best:
                best[1].release()
            best = (score, cap, name)
        else:
            cap.release()
        if score < 1.25:   # clean enough, stop searching
            break
    if best is None:
        raise RuntimeError("no camera could be opened")
    log(f"using camera {index} via {best[2]} (stripes={best[0]:.2f})")
    return best[1]


def clean(frame):
    """Make hands easier to find: remove vertical stripe noise and fix over-exposure."""
    f = frame.astype(np.float32)
    # 1) fixed-pattern vertical stripes: each column's offset from its smoothed neighbourhood
    colmean = f.mean(axis=0, keepdims=True)                       # 1 x W x 3
    smooth = cv2.blur(colmean, (31, 1))
    f -= (colmean - smooth)
    f = np.clip(f, 0, 255).astype(np.uint8)
    # 2) speckle noise
    f = cv2.medianBlur(f, 3)
    # 3) exposure / contrast on luminance only (keeps skin colour natural)
    lab = cv2.cvtColor(f, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    mean = l.mean()
    if mean > 150:   # washed out → pull down highlights
        l = cv2.convertScaleAbs(l, alpha=0.8, beta=-(mean - 140) * 0.8)
    l = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8)).apply(l)
    return cv2.cvtColor(cv2.merge((l, a, b)), cv2.COLOR_LAB2BGR)


# --------------------------------------------------------------------------- model
def model_path():
    d = os.path.join(os.environ.get("LOCALAPPDATA", "."), "WindowsNotch")
    os.makedirs(d, exist_ok=True)
    p = os.path.join(d, "gesture_recognizer.task")
    if not os.path.exists(p) or os.path.getsize(p) < 1_000_000:
        log("downloading hand model…")
        urllib.request.urlretrieve(MODEL_URL, p + ".part")
        os.replace(p + ".part", p)
    return p


def make_recognizer():
    from mediapipe.tasks import python as mp_tasks
    from mediapipe.tasks.python import vision
    opts = vision.GestureRecognizerOptions(
        base_options=mp_tasks.BaseOptions(model_asset_path=model_path()),
        running_mode=vision.RunningMode.VIDEO,
        num_hands=1,
        min_hand_detection_confidence=0.45,
        min_hand_presence_confidence=0.4,
        min_tracking_confidence=0.4,
    )
    return vision.GestureRecognizer.create_from_options(opts)


# --------------------------------------------------------------------------- preview
def draw_preview(img, landmarks, gesture, score, size, min_hand):
    h, w = img.shape[:2]
    view = cv2.flip(img, 1)  # selfie view
    if landmarks:
        col = PINK if gesture == "Closed_Fist" else GREEN if gesture == "Open_Palm" else BLUE
        pts = [(int((1 - p.x) * w), int(p.y * h)) for p in landmarks]
        glow = view.copy()
        for a, b in BONES:
            cv2.line(glow, pts[a], pts[b], col, 9, cv2.LINE_AA)
        view = cv2.addWeighted(glow, 0.35, view, 0.65, 0)          # soft glow
        for a, b in BONES:
            cv2.line(view, pts[a], pts[b], col, 3, cv2.LINE_AA)
        for i, p in enumerate(pts):
            cv2.circle(view, p, 5 if i in (4, 8, 12, 16, 20) else 3, WHITE, -1, cv2.LINE_AA)
        xs, ys = [p[0] for p in pts], [p[1] for p in pts]
        cv2.rectangle(view, (min(xs) - 12, min(ys) - 12), (max(xs) + 12, max(ys) + 12), col, 1, cv2.LINE_AA)

    view = cv2.resize(view, (PREVIEW_W, PREVIEW_H), interpolation=cv2.INTER_AREA)
    # bottom bar
    bar = view.copy()
    cv2.rectangle(bar, (0, PREVIEW_H - 34), (PREVIEW_W, PREVIEW_H), DARK, -1)
    view = cv2.addWeighted(bar, 0.75, view, 0.25, 0)
    near = size >= min_hand
    if not landmarks:
        label, lcol = "No hand", (200, 200, 200)
    elif gesture == "Closed_Fist":
        label, lcol = f"FIST - lock  {score * 100:.0f}%", PINK
    elif gesture == "Open_Palm":
        label, lcol = f"OPEN PALM - restart  {score * 100:.0f}%", GREEN
    else:
        label, lcol = f"Hand ({gesture.replace('_', ' ').lower()})", BLUE
    cv2.putText(view, label, (10, PREVIEW_H - 13), cv2.FONT_HERSHEY_SIMPLEX, 0.45, lcol, 1, cv2.LINE_AA)
    # closeness meter
    mx, my, mw = PREVIEW_W - 82, PREVIEW_H - 21, 70
    full = max(min_hand * 1.6, 0.5)
    cv2.rectangle(view, (mx, my), (mx + mw, my + 6), (70, 70, 70), -1)
    cv2.rectangle(view, (mx, my), (mx + int(min(1, size / full) * mw), my + 6), GREEN if near else YELLOW, -1)
    tick = mx + int(min_hand / full * mw)
    cv2.line(view, (tick, my - 3), (tick, my + 9), WHITE, 1)
    if landmarks:
        cv2.putText(view, "close enough" if near else "move closer", (mx, my - 6),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.33, WHITE, 1, cv2.LINE_AA)
    # live dot
    if int(time.time() * 2) % 2 == 0:
        cv2.circle(view, (12, 12), 4, (58, 69, 255), -1, cv2.LINE_AA)
    cv2.putText(view, "LIVE", (21, 16), cv2.FONT_HERSHEY_SIMPLEX, 0.35, WHITE, 1, cv2.LINE_AA)
    return view


# --------------------------------------------------------------------------- main loop
class Engine:
    def __init__(self, camera):
        self.camera = camera
        self.running = False
        self.preview = "--preview" in sys.argv
        self.min_hand = 0.30
        self.quit = False
        self.window_open = False

    def commands(self):
        for line in sys.stdin:
            cmd = line.strip()
            if cmd == "start":
                self.running = True
            elif cmd == "stop":
                self.running = False
            elif cmd == "preview on":
                self.preview = True
            elif cmd == "preview off":
                self.preview = False
            elif cmd.startswith("minhand "):
                try:
                    self.min_hand = float(cmd.split()[1])
                except ValueError:
                    pass
            elif cmd == "quit":
                break
        self.quit = True

    def close_window(self):
        if self.window_open:
            cv2.destroyWindow(WIN)
            cv2.waitKey(1)
            self.window_open = False

    def show(self, view):
        if not self.window_open:
            cv2.namedWindow(WIN, cv2.WINDOW_AUTOSIZE)
            cv2.setWindowProperty(WIN, cv2.WND_PROP_TOPMOST, 1)
            sw, sh = screen_size()
            cv2.moveWindow(WIN, sw - PREVIEW_W - 24, sh - PREVIEW_H - 90)
            self.window_open = True
        cv2.imshow(WIN, view)
        cv2.waitKey(1)
        if cv2.getWindowProperty(WIN, cv2.WND_PROP_VISIBLE) < 1:   # user clicked the window's X
            self.window_open = False
            self.preview = False
            send(type="preview_closed")

    def run(self, run_for=0.0):
        if run_for:   # self-test: start at once, stop after run_for seconds
            self.running = True
            threading.Timer(run_for, lambda: setattr(self, "quit", True)).start()
        else:
            threading.Thread(target=self.commands, daemon=True).start()
        try:
            recognizer = make_recognizer()
        except Exception as e:  # noqa: BLE001
            send(type="error", message=f"Model: {e}")
            return
        send(type="ready")

        cap, last_sent, t0 = None, "None", time.time()
        stats = {"frames": 0, "hands": 0}
        while not self.quit:
            if not self.running:
                if cap is not None:
                    cap.release()
                    cap = None
                    self.close_window()
                    send(type="status", state="off")
                time.sleep(0.1)
                continue
            if cap is None:
                try:
                    cap = open_camera(self.camera)
                except Exception as e:  # noqa: BLE001
                    send(type="error", message=f"Camera: {e}")
                    self.running = False
                    continue
                send(type="status", state="watching")

            ok, frame = cap.read()
            if not ok or frame is None:
                time.sleep(0.05)
                continue
            img = clean(frame)
            import mediapipe as mp
            rgb = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
            result = recognizer.recognize_for_video(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb),
                                                    int(time.time() * 1000))
            stats["frames"] += 1

            landmarks, gesture, score, size = None, "None", 0.0, 0.0
            if result.hand_landmarks:
                stats["hands"] += 1
                landmarks = result.hand_landmarks[0]
                if result.gestures and result.gestures[0]:
                    gesture = result.gestures[0][0].category_name or "None"
                    score = float(result.gestures[0][0].score)
                xs = [p.x for p in landmarks]
                ys = [p.y for p in landmarks]
                size = max(max(xs) - min(xs), max(ys) - min(ys))
                send(type="frame", gesture=gesture, score=score, size=size)
                last_sent = gesture
            elif last_sent != "None":
                send(type="frame", gesture="None", score=0, size=0)
                last_sent = "None"

            if self.preview:
                self.show(draw_preview(img, landmarks, gesture, score, size, self.min_hand))
            else:
                self.close_window()

            if time.time() - t0 >= 5:
                log(f"python frames={stats['frames']} hands={stats['hands']} preview={self.preview}")
                stats = {"frames": 0, "hands": 0}
                t0 = time.time()

        if cap is not None:
            cap.release()
        self.close_window()


def screen_size():
    try:
        import ctypes
        u = ctypes.windll.user32
        return u.GetSystemMetrics(0), u.GetSystemMetrics(1)
    except Exception:  # noqa: BLE001
        return 1920, 1080


if __name__ == "__main__":
    cam, run_for = 0, 0.0
    for i, a in enumerate(sys.argv):
        if a == "--camera" and i + 1 < len(sys.argv):
            cam = int(sys.argv[i + 1])
        if a == "--run-for" and i + 1 < len(sys.argv):
            run_for = float(sys.argv[i + 1])
    try:
        import ctypes
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:  # noqa: BLE001
        pass
    Engine(cam).run(run_for)
