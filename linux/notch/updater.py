"""Keeps the Linux notch up to date from the project's GitHub releases (tags "linux-v…"): downloads the new tarball,
checks the SHA-256 GitHub published for it, replaces the app's files and restarts. Your settings and data are untouched."""
import hashlib
import io
import json
import os
import shutil
import sys
import tarfile
import tempfile
import urllib.request

from .config import APP_DIR, VERSION, log

REPO = "nzizajules123/windows-notch"


def _ver(s):
    return tuple(int("".join(ch for ch in p if ch.isdigit()) or 0) for p in s.split("."))


def check():
    """(tag, url, digest) of a newer Linux release, or None."""
    req = urllib.request.Request(f"https://api.github.com/repos/{REPO}/releases?per_page=30",
                                 headers={"User-Agent": "NotchLinux-Updater", "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(req, timeout=10) as r:
        releases = json.loads(r.read())
    best = None
    for rel in releases:
        tag = rel.get("tag_name", "")
        if rel.get("draft") or rel.get("prerelease") or not tag.startswith("linux-v"):
            continue
        if best is None or _ver(tag[7:]) > _ver(best["tag_name"][7:]):
            best = rel
    if not best or _ver(best["tag_name"][7:]) <= _ver(VERSION):
        return None
    for a in best.get("assets", []):
        url = a.get("browser_download_url", "")
        if a.get("name", "").endswith(".tar.gz") and url.startswith(f"https://github.com/{REPO}/releases/download/"):
            return best["tag_name"], url, a.get("digest") or ""
    return None


def can_update() -> bool:
    return os.access(APP_DIR, os.W_OK) and not (APP_DIR / ".git").exists() and not (APP_DIR.parent / ".git").exists()


def install(url, digest) -> bool:
    """Downloads, verifies and unpacks over the app folder. Returns True when it's ready to restart."""
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "NotchLinux-Updater"}), timeout=120) as r:
        data = r.read()
    if digest.startswith("sha256:") and hashlib.sha256(data).hexdigest() != digest[7:].lower():
        log("update: checksum mismatch")
        return False
    with tempfile.TemporaryDirectory() as tmp:
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as t:
            try:
                t.extractall(tmp, filter="data")
            except TypeError:                     # Python without the extraction filter
                t.extractall(tmp)
        src = os.path.join(tmp, "notch-linux")
        if not os.path.isfile(os.path.join(src, "notch", "__main__.py")):
            log("update: package looks wrong")
            return False
        for name in os.listdir(src):
            s, d = os.path.join(src, name), APP_DIR / name
            if os.path.isdir(s):
                shutil.rmtree(d, ignore_errors=True)
                shutil.copytree(s, d)
            else:
                shutil.copy2(s, d)
    return True


def restart(tag):
    os.chdir(APP_DIR)
    os.execv(sys.executable, [sys.executable, "-m", "notch", "--updated", tag])
