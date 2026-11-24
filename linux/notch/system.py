"""Talking to the Linux desktop: battery, idle time, locking, power, apps, links, windows."""
import ctypes
import ctypes.util
import glob
import os
import shutil
import subprocess

from gi.repository import Gio, GLib

from .config import log

# ------------------------------------------------------------------ battery


def battery():
    """(percent, charging, has_battery)"""
    for bat in sorted(glob.glob("/sys/class/power_supply/BAT*")):
        try:
            pct = int(open(f"{bat}/capacity").read().strip())
            status = open(f"{bat}/status").read().strip()
            return pct, status in ("Charging", "Full"), True
        except (OSError, ValueError):
            continue
    return 100, False, False


# ------------------------------------------------------------------ idle time (for breaks / proactive chats)
_xss = None


def _xss_idle():
    global _xss
    if _xss is False:
        return None
    try:
        if _xss is None:
            x11 = ctypes.cdll.LoadLibrary(ctypes.util.find_library("X11") or "libX11.so.6")
            xss = ctypes.cdll.LoadLibrary(ctypes.util.find_library("Xss") or "libXss.so.1")

            class Info(ctypes.Structure):
                _fields_ = [("window", ctypes.c_ulong), ("state", ctypes.c_int), ("kind", ctypes.c_int),
                            ("til_or_since", ctypes.c_ulong), ("idle", ctypes.c_ulong), ("eventMask", ctypes.c_ulong)]

            x11.XOpenDisplay.restype = ctypes.c_void_p
            x11.XDefaultRootWindow.restype = ctypes.c_ulong
            x11.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
            xss.XScreenSaverAllocInfo.restype = ctypes.POINTER(Info)
            xss.XScreenSaverQueryInfo.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(Info)]
            dpy = x11.XOpenDisplay(None)
            if not dpy:
                raise OSError("no display")
            _xss = (x11, xss, dpy, xss.XScreenSaverAllocInfo())
        x11, xss, dpy, info = _xss
        xss.XScreenSaverQueryInfo(dpy, x11.XDefaultRootWindow(dpy), info)
        return info.contents.idle / 1000.0
    except Exception:
        _xss = False
        return None


def idle_seconds() -> float:
    """Seconds since the last key press / mouse move. GNOME's idle monitor first (works on Wayland), then X11."""
    try:
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        r = bus.call_sync("org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core",
                          "org.gnome.Mutter.IdleMonitor", "GetIdletime", None, GLib.VariantType("(t)"),
                          Gio.DBusCallFlags.NONE, 300, None)
        return r.unpack()[0] / 1000.0
    except GLib.Error:
        pass
    v = _xss_idle()
    return v if v is not None else 0.0


def locked() -> bool:
    """The session is locked (logind's LockedHint)."""
    sid = os.environ.get("XDG_SESSION_ID")
    if not sid or not shutil.which("loginctl"):
        return False
    try:
        out = subprocess.run(["loginctl", "show-session", sid, "-p", "LockedHint", "--value"],
                             capture_output=True, text=True, timeout=2).stdout.strip()
        return out == "yes"
    except (OSError, subprocess.SubprocessError):
        return False


# ------------------------------------------------------------------ power
def _first_ok(*cmds) -> bool:
    for c in cmds:
        if shutil.which(c[0]):
            try:
                if subprocess.run(c, timeout=5).returncode == 0:
                    return True
            except (OSError, subprocess.SubprocessError):
                continue
    return False


def lock_screen() -> bool:
    return _first_ok(["loginctl", "lock-session"], ["xdg-screensaver", "lock"], ["gnome-screensaver-command", "-l"],
                     ["dm-tool", "lock"], ["xflock4"])


def suspend():
    _first_ok(["systemctl", "suspend"])


def reboot():
    _first_ok(["systemctl", "reboot"])


def poweroff():
    _first_ok(["systemctl", "poweroff"])


# ------------------------------------------------------------------ links, files, apps
def open_uri(uri: str) -> None:
    try:
        Gio.AppInfo.launch_default_for_uri(uri, None)
    except GLib.Error:
        subprocess.Popen(["xdg-open", uri], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def _norm(s: str) -> str:
    import re
    s = re.sub(r"[^\w ]", " ", s.lower())
    s = re.sub(r"\b(the|app)\b", " ", s)
    return re.sub(r"\s+", " ", s).strip()


ALIASES = {"vs code": "visual studio code", "vscode": "visual studio code", "code": "visual studio code",
           "browser": "firefox", "files": "files", "my files": "files", "file manager": "files",
           "terminal": "terminal", "calculator": "calculator", "settings": "settings"}


def open_app(spoken: str):
    """Launches the best-matching installed app; returns its name or None."""
    want = _norm(spoken)
    want = ALIASES.get(want, want)
    best, score = None, 0
    for a in Gio.AppInfo.get_all():
        if not a.should_show():
            continue
        name = _norm(a.get_display_name() or a.get_name() or "")
        s = 100 if name == want else 85 if name.startswith(want + " ") or name.endswith(" " + want) else \
            80 if name.startswith(want) else 70 if f" {want} " in f" {name} " else 60 if len(want) >= 4 and want in name else 0
        if s > score or (s == score and best and len(name) < len(_norm(best.get_display_name()))):
            best, score = a, s
    if best and score >= 60:
        try:
            best.launch([], None)
            log(f"voice: opened app {best.get_display_name()!r}")
            return best.get_display_name()
        except GLib.Error as e:
            log(f"open app: {e}")
    return None


def windows():
    """[(id, title)] of open windows (needs wmctrl)."""
    if not shutil.which("wmctrl"):
        return None
    try:
        out = subprocess.run(["wmctrl", "-l"], capture_output=True, text=True, timeout=3).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    res = []
    for line in out.splitlines():
        parts = line.split(None, 3)
        if len(parts) == 4 and parts[1] != "-1":       # -1 = sticky desktop / panels
            res.append((parts[0], parts[3]))
    return res


def close_windows(spoken: str):
    """Closes windows whose title matches (politely, like their X button). Returns their titles, or None without wmctrl."""
    ws = windows()
    if ws is None:
        return None
    want = _norm(spoken)
    hits = [w for w in ws if want and want in _norm(w[1]) and "notch" not in w[1].lower()]
    for wid, _ in hits:
        subprocess.run(["wmctrl", "-ic", wid], timeout=3)
    return [t for _, t in hits]


def focus_window(spoken: str):
    ws = windows() or []
    want = _norm(spoken)
    for wid, title in ws:
        if want and want in _norm(title):
            subprocess.run(["wmctrl", "-ia", wid], timeout=3)
            return title
    return None


def xdg_dir(name: str) -> str:
    """Desktop / Documents / Download in the user's language (xdg-user-dir), with sensible fallbacks."""
    key = {"desktop": "DESKTOP", "documents": "DOCUMENTS", "downloads": "DOWNLOAD", "pictures": "PICTURES",
           "music": "MUSIC", "videos": "VIDEOS"}.get(name)
    if key and shutil.which("xdg-user-dir"):
        try:
            p = subprocess.run(["xdg-user-dir", key], capture_output=True, text=True, timeout=2).stdout.strip()
            if p:
                return p
        except (OSError, subprocess.SubprocessError):
            pass
    home = os.path.expanduser("~")
    return os.path.join(home, {"desktop": "Desktop", "documents": "Documents", "downloads": "Downloads",
                               "pictures": "Pictures", "music": "Music", "videos": "Videos"}.get(name, ""))
