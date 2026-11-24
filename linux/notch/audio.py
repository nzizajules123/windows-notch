"""Microphone in / speaker out through the desktop's sound server (PipeWire or PulseAudio), as raw 16-bit mono PCM.
Uses the tools every desktop ships (parec / pacat, or pw-record / pw-play), so no extra Python packages are needed."""
import shutil
import subprocess
import threading
import time


def _rec_cmd(rate):
    if shutil.which("parec"):
        return ["parec", "--raw", "--format=s16le", f"--rate={rate}", "--channels=1", "--latency-msec=50"]
    if shutil.which("pw-record"):
        return ["pw-record", "--rate", str(rate), "--channels", "1", "--format", "s16", "-"]
    return None


def _play_cmd(rate):
    if shutil.which("pacat"):
        return ["pacat", "--raw", "--format=s16le", f"--rate={rate}", "--channels=1", "--latency-msec=80"]
    if shutil.which("pw-play"):
        return ["pw-play", "--rate", str(rate), "--channels", "1", "--format", "s16", "-"]
    return None


def available() -> bool:
    return _rec_cmd(16000) is not None and _play_cmd(24000) is not None


class Mic:
    """Calls on_block(bytes) with each ~100 ms of audio, on a background thread."""

    def __init__(self, rate, on_block, block_ms=100):
        self.rate, self.on_block, self.block = rate, on_block, rate * 2 * block_ms // 1000
        self.proc = None

    def start(self) -> bool:
        cmd = _rec_cmd(self.rate)
        if not cmd:
            return False
        self.proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        threading.Thread(target=self._run, daemon=True, name="mic").start()
        return True

    def _run(self):
        p = self.proc
        while p and p.poll() is None:
            data = p.stdout.read(self.block)
            if not data:
                break
            try:
                self.on_block(data)
            except Exception as e:      # never let a bad block stop the microphone
                print("mic:", e)

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
        self.proc = None


class Speaker:
    """Streams PCM to the speakers; knows roughly when it's still talking (for half-duplex)."""

    def __init__(self, rate):
        self.rate = rate
        self.proc = None
        self.until = 0.0
        self._lock = threading.Lock()

    def _ensure(self):
        if self.proc is None or self.proc.poll() is not None:
            cmd = _play_cmd(self.rate)
            if not cmd:
                return False
            self.proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return True

    def write(self, pcm: bytes):
        with self._lock:
            if not self._ensure():
                return
            try:
                self.proc.stdin.write(pcm)
                self.proc.stdin.flush()
            except (BrokenPipeError, OSError):
                self.proc = None
                return
            secs = len(pcm) / 2 / self.rate
            self.until = max(self.until, time.monotonic()) + secs

    @property
    def playing(self) -> bool:
        return time.monotonic() < self.until

    def clear(self):
        """Stop what's playing now (you interrupted)."""
        with self._lock:
            if self.proc and self.proc.poll() is None:
                self.proc.kill()
            self.proc = None
            self.until = 0.0

    def stop(self):
        self.clear()
