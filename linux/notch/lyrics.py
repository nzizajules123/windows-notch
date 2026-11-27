"""Synced lyrics from LRCLIB (free, no account): '[mm:ss.xx] line'."""
import json
import re
import urllib.parse
import urllib.request

_cache = {}


def fetch(title: str, artist: str, duration: float = 0):
    """[(seconds, line)] or [] — cached per track."""
    key = (title.lower(), artist.lower())
    if key in _cache:
        return _cache[key]
    q = {"track_name": title, "artist_name": artist}
    if duration > 0:
        q["duration"] = str(int(round(duration)))
    lines = []
    try:
        req = urllib.request.Request("https://lrclib.net/api/get?" + urllib.parse.urlencode(q),
                                     headers={"User-Agent": "NotchLinux"})
        with urllib.request.urlopen(req, timeout=8) as r:
            synced = json.loads(r.read().decode()).get("syncedLyrics") or ""
        for raw in synced.splitlines():
            m = re.match(r"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$", raw)
            if m:
                text = m.group(3).strip()
                lines.append((int(m.group(1)) * 60 + float(m.group(2)), text or "♪"))
    except Exception:
        pass
    _cache[key] = lines
    return lines


def stage(lines, pos: float):
    """(previous, current, next) lines around pos, for the three-line lyrics stage."""
    i = -1
    for k, (t, _) in enumerate(lines):
        if t <= pos + 0.25:
            i = k
        else:
            break
    get = lambda k: lines[k][1] if 0 <= k < len(lines) else ""  # noqa: E731
    return get(i - 1), get(i) if i >= 0 else "♪", get(i + 1)


def line_at(lines, pos: float):
    cur = None
    for t, text in lines:
        if t <= pos + 0.25:
            cur = text
        else:
            break
    return None if cur in (None, "♪") else cur
