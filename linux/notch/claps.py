"""Hand claps from the microphone — the same detector as Windows and Android: a sudden, loud, bright burst that fades
within ~0.1 s; claps in a steady rhythm form a sequence. Sound is analysed live and never recorded."""
import array
import math

RATE = 16000
FRAME = RATE // 100           # 10 ms
MIN_GAP, MAX_GAP, END_GAP = 170, 750, 800


class ClapAnalyzer:
    def __init__(self, on_clap, on_sequence):
        self.on_clap, self.on_sequence = on_clap, on_sequence
        self.buf = array.array("h")
        self.frame_no = 0
        self.floor = 0.003
        self.prev = 0.0
        self.burst_start = -1
        self.burst_peak = self.burst_zcr = self.burst_base = 0.0
        self.decayed = False
        self.claps = []

    def feed(self, pcm: bytes):
        self.buf.frombytes(pcm[: len(pcm) - len(pcm) % 2])
        while len(self.buf) >= FRAME:
            self._frame(self.buf[:FRAME])
            del self.buf[:FRAME]

    def _frame(self, f):
        s = 0.0
        peak = 0.0
        cross = 0
        last = f[0]
        for x in f:
            v = x / 32768.0
            s += v * v
            a = abs(v)
            if a > peak:
                peak = a
            if (x >= 0) != (last >= 0):
                cross += 1
            last = x
        rms = math.sqrt(s / FRAME)
        zcr = cross / FRAME
        now = self.frame_no
        self.frame_no += 1
        if self.burst_start >= 0:
            age = now - self.burst_start
            if age <= 2:
                self.burst_peak = max(self.burst_peak, rms)
                self.burst_zcr = max(self.burst_zcr, zcr)
            elif rms < max(self.burst_peak * 0.22, self.burst_base * 1.5):
                self.decayed = True
            if self.decayed or age >= 14:
                if self.decayed and age <= 13 and self.burst_zcr >= 0.08:
                    self._clap(self.burst_start)
                self.burst_start = -1
        else:
            need = max(min(self.floor, 0.04) * 6, 0.025)
            if rms > need and peak > 0.12 and rms > self.prev * 2.5:
                self.burst_start, self.burst_peak, self.burst_zcr, self.burst_base, self.decayed = now, rms, zcr, self.prev, False
            else:
                k = 0.08 if rms < self.floor else 0.02
                self.floor = min(max(self.floor + (rms - self.floor) * k, 0.0005), 0.2)
        self.prev = rms
        if self.claps and (now - self.claps[-1]) * 10 >= END_GAP:
            n, span = len(self.claps), (self.claps[-1] - self.claps[0]) * 10
            self.claps = []
            self.on_sequence(n, span)

    def _clap(self, at):
        if self.claps:
            gap = (at - self.claps[-1]) * 10
            if gap < MIN_GAP:
                return
            if gap > MAX_GAP:
                self.claps = []
            elif len(self.claps) >= 2:
                prev = (self.claps[-1] - self.claps[-2]) * 10
                if gap > prev * 2.2 or gap * 2.2 < prev:
                    self.claps = []
        self.claps.append(at)
        self.on_clap(len(self.claps))
