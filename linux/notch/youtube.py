"""Find songs on YouTube (no API key) and play them: in mpv when it's installed (with yt-dlp), else in the browser."""
import json
import os
import shutil
import socket
import subprocess
import tempfile
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
        self.sock = os.path.join(tempfile.gettempdir(), f"notch-mpv-{os.getuid()}.sock")

    @staticmethod
    def available() -> bool:
        return bool(shutil.which("mpv") and (shutil.which("yt-dlp") or shutil.which("youtube-dl")))

    def play(self, results, index=0) -> bool:
        """Plays results[index:] as a playlist. Without mpv the first one opens in the browser."""
        self.stop()
        urls = [f"https://www.youtube.com/watch?v={r['id']}" for r in results[index:index + 15]]
        if not urls:
            return False
        if not self.available():
            from .system import open_uri
            open_uri(urls[0])
            return True
        self.proc = subprocess.Popen(["mpv", "--no-video", "--really-quiet", "--force-media-title=",
                                      f"--input-ipc-server={self.sock}", "--title=Notch"] + urls,
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
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
