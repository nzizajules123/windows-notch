"""What the notch knows about you and your chats: your name, its name, your hobbies, recent chats and long-term notes.
All kept in ~/.local/share/notch, readable only by you."""
import datetime as dt
import re

from .config import load_json, save_json


class Profile:
    def __init__(self):
        p = load_json("profile.json", {})
        self.user_name = p.get("user_name", "")
        self.assistant_name = p.get("assistant_name", "")
        self.interests = p.get("interests", [])
        self.last_proactive = p.get("last_proactive", 0.0)
        self.proactive_day = p.get("proactive_day", "")
        self.proactive_today = p.get("proactive_today", 0)

    def save(self):
        save_json("profile.json", self.__dict__)

    def remember(self, user=None, assistant=None, add=(), remove=()):
        saved = []
        if user:
            self.user_name = user.strip(); saved.append(f"user's name: {self.user_name}")
        if assistant:
            self.assistant_name = assistant.strip(); saved.append(f"your name: {self.assistant_name}")
        for i in add:
            if i.strip() and i.lower() not in (x.lower() for x in self.interests):
                self.interests.append(i.strip()); saved.append(f"interest: {i.strip()}")
        for r in remove:
            before = len(self.interests)
            self.interests = [x for x in self.interests if x.lower() != r.lower()]
            if len(self.interests) < before:
                saved.append(f"forgot interest: {r}")
        self.save()
        return "Saved — " + "; ".join(saved) + "." if saved else "Nothing new to save."

    def for_prompt(self) -> str:
        parts = []
        if self.assistant_name:
            parts.append(f"Your name is {self.assistant_name} — the user chose it for you, so answer to it happily.")
        if self.user_name:
            parts.append(f"The user's name is {self.user_name}. Call them by name now and then (naturally, not in every sentence).")
        if self.interests:
            parts.append("They like: " + ", ".join(self.interests) + ". Bring these up when it fits.")
        if not (self.user_name and self.assistant_name and self.interests):
            parts.append("You don't know everything about them yet. Early in this chat, warmly "
                         + ("ask their name, " if not self.user_name else "")
                         + ("ask them to give you a name (you'd love one), " if not self.assistant_name else "")
                         + ("and ask what they like doing — their hobbies, " if not self.interests else "")
                         + "one question at a time, and save each answer with remember_profile straight away.")
        parts.append("Whenever they mention a new hobby or interest, or want to change a name, save it with remember_profile.")
        return " ".join(parts)


class Memory:
    KEEP = 300

    def __init__(self):
        self.items = load_json("talk-memory.json", [])     # [{t, you, notch}]
        self.notes = load_json("talk-notes.json", [])      # [{t, text}]

    def add(self, you, notch):
        self.items.append({"t": dt.datetime.now().isoformat(timespec="seconds"), "you": you[:400], "notch": notch[:600]})
        self.items = self.items[-self.KEEP:]
        save_json("talk-memory.json", self.items)

    def clear(self):
        self.items, self.notes = [], []
        save_json("talk-memory.json", self.items)
        save_json("talk-notes.json", self.notes)

    def add_note(self, text):
        text = text.strip()[:300]
        if not text:
            return "Nothing to note."
        if any(n["text"].lower() == text.lower() for n in self.notes):
            return "Already noted."
        self.notes.append({"t": dt.datetime.now().isoformat(timespec="seconds"), "text": text})
        self.notes = self.notes[-80:]
        save_json("talk-notes.json", self.notes)
        return "Noted."

    def remove_notes(self, match):
        before = len(self.notes)
        if match.strip().lower() == "all":
            self.notes = []
        else:
            self.notes = [n for n in self.notes if match.strip().lower() not in n["text"].lower()]
        save_json("talk-notes.json", self.notes)
        return before - len(self.notes)

    def notes_text(self):
        return "\n".join(f"- ({n['t'][:10]}) {n['text']}" for n in self.notes)

    def recent(self, max_chars=6000):
        out, total = [], 0
        for e in reversed(self.items):
            line = f"[{e['t'][:16].replace('T', ' ')}] User: {e['you']}\n  Notch: {e['notch']}"
            if total + len(line) > max_chars:
                break
            out.append(line); total += len(line)
        return "\n".join(reversed(out))

    def search(self, query, limit=8):
        words = [w for w in re.split(r"[ ,.?]+", query.lower()) if len(w) > 2]
        if not words:
            return ""
        scored = [(sum(w in (e["you"] + " " + e["notch"]).lower() for w in words), e) for e in self.items]
        hits = sorted([x for x in scored if x[0] > 0], key=lambda x: (-x[0], x[1]["t"]))[:limit]
        return "\n".join(f"[{e['t'][:16].replace('T', ' ')}] User: {e['you']}\n  Notch: {e['notch']}"
                         for _, e in sorted(hits, key=lambda x: x[1]["t"]))
