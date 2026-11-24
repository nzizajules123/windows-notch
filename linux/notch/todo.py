from __future__ import annotations

"""To-dos with reminders, written in plain words: "call mom at 5pm", "stretch in 20 min", "pay rent tomorrow"."""
import datetime as dt
import re
import uuid

from .config import load_json, save_json


class Todos:
    def __init__(self):
        self.items = load_json("todo.json", [])

    def save(self):
        save_json("todo.json", self.items)

    def add(self, text: str, due: dt.datetime | None):
        item = {"id": uuid.uuid4().hex, "text": text, "done": False, "due": due.isoformat() if due else None, "notified": False}
        self.items.insert(0, item)
        self.save()
        return item

    def toggle(self, item):
        item["done"] = not item["done"]
        self.save()

    def remove(self, item):
        self.items = [i for i in self.items if i["id"] != item["id"]]
        self.save()

    def due_now(self):
        now = dt.datetime.now()
        out = []
        for i in self.items:
            if i["done"] or i["notified"] or not i["due"]:
                continue
            if dt.datetime.fromisoformat(i["due"]) <= now:
                i["notified"] = True
                out.append(i)
        if out:
            self.save()
        return out


def parse(text: str):
    """(clean text, due datetime or None)."""
    s = text.strip()
    now = dt.datetime.now()
    due = None
    m = re.search(r"\bin (\d+)\s*(min|minute|minutes|mins|m|hour|hours|hr|hrs|h)\b", s, re.I)
    if m:
        n = int(m.group(1))
        due = now + (dt.timedelta(hours=n) if m.group(2).lower().startswith("h") else dt.timedelta(minutes=n))
        s = s[:m.start()] + s[m.end():]
    day = now.date()
    m = re.search(r"\b(tomorrow|tonight|today)\b", s, re.I)
    word = m.group(1).lower() if m else ""
    if m:
        s = s[:m.start()] + s[m.end():]
        if word == "tomorrow":
            day = day + dt.timedelta(days=1)
    m = re.search(r"\bat (\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b", s, re.I)
    if m:
        h, mi = int(m.group(1)), int(m.group(2) or 0)
        ap = (m.group(3) or "").lower()
        if ap == "pm" and h < 12:
            h += 12
        if ap == "am" and h == 12:
            h = 0
        if not ap and h < 7 and word != "tomorrow":
            h += 12                                  # "at 5" means 5 pm
        due = dt.datetime.combine(day, dt.time(h % 24, mi))
        if due <= now and not word:
            due += dt.timedelta(days=1)
        s = s[:m.start()] + s[m.end():]
    elif word and not due:
        due = dt.datetime.combine(day, dt.time(20 if word == "tonight" else 9, 0))
    s = re.sub(r"\s+", " ", s).strip(" ,.-")
    return s, due
