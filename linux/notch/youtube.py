"""Find songs on YouTube (no API key) and play them: in mpv when it's installed (with yt-dlp), else in the browser."""
import json
import os
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import urllib.parse
import urllib.request

from .config import log

UA = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"


def _get(url, data=None, headers=None):
    h = {"User-Agent": UA, "Accept-Language": "en-US,en;q=0.8"}
    h.update(headers or {})
    req = urllib.request.Request(url, data=data, headers=h)
    with urllib.request.urlopen(req, timeout=10) as r:
        return r.read().decode("utf-8", "replace")


def search(query: str, limit: int = 12):
    """[{id, title, channel, length}] — results page first (old and new layout), then YouTube's own search service."""
    q = urllib.parse.quote(query.strip())
    if not q:
        return []
    for url in (f"https://www.youtube.com/results?search_query={q}&sp=EgIQAQ%253D%253D",
                f"https://www.youtube.com/results?search_query={q}"):
        try:
            html = _get(url)
            i = html.find("var ytInitialData = ")
            if i < 0:
                continue
            i = html.find("{", i)
            j = html.find(";</script>", i)
            out = []
            _walk(json.loads(html[i:j]), out, limit)
            if out:
                return out
        except Exception as e:
            log(f"youtube page: {e}")
    try:
        body = json.dumps({"context": {"client": {"clientName": "WEB", "clientVersion": "2.20241120.01.00", "hl": "en", "gl": "US"}},
                           "query": query, "params": "EgIQAQ%3D%3D"}).encode()
        data = json.loads(_get("https://www.youtube.com/youtubei/v1/search?prettyPrint=false", body,
                               {"Content-Type": "application/json", "Origin": "https://www.youtube.com"}))
        out = []
        _walk(data, out, limit)
        return out
    except Exception as e:
        log(f"youtube service: {e}")
        return []


def _runs(v, prop):
    t = v.get(prop) or {}
    if t.get("runs"):
        return t["runs"][0].get("text", "")
    return t.get("simpleText", "")


def _badge(e):
    if isinstance(e, dict):
        b = e.get("thumbnailBadgeViewModel")
        if isinstance(b, dict) and str(b.get("text", ""))[:1].isdigit() and ":" in b.get("text", ""):
            return b["text"]
        for v in e.values():
            r = _badge(v)
            if r:
                return r
    elif isinstance(e, list):
        for v in e:
            r = _badge(v)
            if r:
                return r
    return ""


def _walk(e, out, limit):
    if len(out) >= limit:
        return
    if isinstance(e, dict):
        v = e.get("videoRenderer")
        if isinstance(v, dict) and v.get("videoId"):
            length = _runs(v, "lengthText")
            if length and all(x["id"] != v["videoId"] for x in out):      # skip live streams
                out.append({"id": v["videoId"], "title": _runs(v, "title"),
                            "channel": _runs(v, "ownerText") or _runs(v, "longBylineText"), "length": length})
            return
        lv = e.get("lockupViewModel")
        if isinstance(lv, dict) and lv.get("contentId"):
            meta = (lv.get("metadata") or {}).get("lockupMetadataViewModel") or {}
            title = (meta.get("title") or {}).get("content", "")
            length = _badge(lv)
            if lv.get("contentType", "LOCKUP_CONTENT_TYPE_VIDEO") == "LOCKUP_CONTENT_TYPE_VIDEO" and title and length:
                out.append({"id": lv["contentId"], "title": title, "channel": "", "length": length})
            return
        for v in e.values():
            _walk(v, out, limit)
    elif isinstance(e, list):
        for v in e:
            _walk(v, out, limit)


class Player:
    """mpv in the background (audio only), controlled over its IPC socket. It shows up in Media like any player."""

    def __init__(self):
        self.proc = None
        self.items = []
        self._last = None
        self.sock = os.path.join(tempfile.gettempdir(), f"notch-mpv-{os.getuid()}.sock")

    @property
    def running(self) -> bool:
        return bool(self.proc and self.proc.poll() is None)

    def ask(self, *props):
        """Reads mpv properties in one go: {name: value} (missing ones are left out)."""
        if not self.running:
            return {}
        out = {}
        try:
            with socket.socket(socket.AF_UNIX) as s:
                s.settimeout(0.6)
                s.connect(self.sock)
                for i, p in enumerate(props):
                    s.sendall((json.dumps({"command": ["get_property", p], "request_id": i + 1}) + "\n").encode())
                buf = b""
                while len(out) < len(props):
                    chunk = s.recv(65536)
                    if not chunk:
                        break
                    buf += chunk
                    *lines, buf = buf.split(b"\n")
                    for line in lines:
                        try:
                            m = json.loads(line)
                        except ValueError:
                            continue
                        rid = m.get("request_id")
                        if rid and 0 < rid <= len(props):
                            out[props[rid - 1]] = m.get("data") if m.get("error") == "success" else None
        except OSError:
            pass
        return {k: v for k, v in out.items() if v is not None}

    def status(self):
        """What the notch's own player is playing: {id, title, channel, position, length, playing}, or None.
        Read from a cache a background thread keeps fresh, so the UI never waits on mpv (it can be busy loading)."""
        if not self.running or not self.items:
            return None
        st = self._last
        if st is None:
            return None
        return {**st, "position": st["position"] + (time.monotonic() - st["at"] if st["playing"] else 0)}

    def _poll(self, proc):
        while self.proc is proc and proc.poll() is None:
            st = self._read_status()
            self._last = st
            time.sleep(1)
        self._last = None

    def _read_status(self):
        st = self.ask("playlist-pos", "time-pos", "duration", "pause")
        i = st.get("playlist-pos", 0)
        if not isinstance(i, int) or not 0 <= i < len(self.items):
            return None
        it = self.items[i]
        return {"id": it["id"], "title": it["title"], "channel": it.get("channel", ""), "at": time.monotonic(),
                "position": float(st.get("time-pos") or 0), "length": float(st.get("duration") or 0), "playing": not st.get("pause", False)}

    def set_volume(self, percent):
        self.command("set_property", "volume", int(percent))

    def seek(self, seconds):
        self.command("seek", float(seconds), "absolute")

    @staticmethod
    def available() -> bool:
        return bool(shutil.which("mpv") and (shutil.which("yt-dlp") or shutil.which("youtube-dl")))

    def play(self, results, index=0) -> bool:
        """Plays results[index:] as a playlist. Without mpv the first one opens in the browser."""
        self.stop()
        self.items = list(results[index:index + 15])
        urls = [f"https://www.youtube.com/watch?v={r['id']}" for r in self.items]
        if not urls:
            return False
        if not self.available():
            from .system import open_uri
            open_uri(urls[0])
            return True
        self.proc = subprocess.Popen(["mpv", "--no-video", "--really-quiet", "--force-media-title=",
                                      f"--input-ipc-server={self.sock}", "--title=Notch"] + urls,
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        # until mpv answers, show the first song as loading
        first = self.items[0]
        self._last = {"id": first["id"], "title": first["title"], "channel": first.get("channel", ""), "at": time.monotonic(),
                      "position": 0.0, "length": 0.0, "playing": True}
        threading.Thread(target=self._poll, args=(self.proc,), daemon=True, name="mpv-status").start()
        return True

    def command(self, *args):
        if not self.proc or self.proc.poll() is not None:
            return
        try:
            with socket.socket(socket.AF_UNIX) as s:
                s.settimeout(1)
                s.connect(self.sock)
                s.sendall((json.dumps({"command": list(args)}) + "\n").encode())
        except OSError:
            pass

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
        self.proc = None
        self.items = []
