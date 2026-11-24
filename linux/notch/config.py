"""Paths, settings and the log. Everything personal stays in ~/.config/notch and ~/.local/share/notch."""
import json
import os
import pathlib
import threading
import time

APP_DIR = pathlib.Path(__file__).resolve().parent.parent
try:
    VERSION = (APP_DIR / "VERSION").read_text().strip()
except OSError:
    VERSION = "0.0.0"

CONFIG_DIR = pathlib.Path(os.environ.get("XDG_CONFIG_HOME") or pathlib.Path.home() / ".config") / "notch"
DATA_DIR = pathlib.Path(os.environ.get("XDG_DATA_HOME") or pathlib.Path.home() / ".local" / "share") / "notch"
for d in (CONFIG_DIR, DATA_DIR):
    d.mkdir(parents=True, exist_ok=True)

_log_lock = threading.Lock()


def log(msg: str) -> None:
    line = time.strftime("[%Y-%m-%d %H:%M:%S] ") + str(msg)
    print(line, flush=True)
    with _log_lock:
        try:
            p = DATA_DIR / "notch.log"
            if p.exists() and p.stat().st_size > 2_000_000:
                p.replace(DATA_DIR / "notch.old.log")
            with p.open("a", encoding="utf-8") as f:
                f.write(line + "\n")
        except OSError:
            pass


DEFAULTS = {
    "gemini_key": "",              # kept in settings.json, readable only by you (chmod 600)
    "gemini_voice": "Kore",
    "eyes_talk": True,
    "claps": False,                # 👏👏 lock · 👏👏👏👏 talk
    "lyrics": True,
    "talk_on_reminder": True,
    "proactive_chats": True,
    "breaks": False,
    "break_every": 120,
    "break_minutes": 5,
    "scheduled_break_at": 0.0,     # unix time of a break you planned (can't be cancelled)
    "scheduled_break_minutes": 5,
    "auto_update": True,
    "collapse_delay": 3.0,
}


class Settings(dict):
    path = CONFIG_DIR / "settings.json"

    def __init__(self):
        super().__init__(DEFAULTS)
        try:
            self.update(json.loads(self.path.read_text()))
        except (OSError, ValueError):
            pass

    def save(self) -> None:
        try:
            tmp = self.path.with_suffix(".tmp")
            tmp.write_text(json.dumps(self, indent=2))
            os.chmod(tmp, 0o600)
            tmp.replace(self.path)
        except OSError as e:
            log(f"settings: {e}")


def load_json(name: str, default):
    try:
        return json.loads((DATA_DIR / name).read_text())
    except (OSError, ValueError):
        return default


def save_json(name: str, value) -> None:
    try:
        p = DATA_DIR / name
        tmp = p.with_suffix(".tmp")
        tmp.write_text(json.dumps(value, indent=1, ensure_ascii=False))
        os.chmod(tmp, 0o600)
        tmp.replace(p)
    except OSError as e:
        log(f"save {name}: {e}")
