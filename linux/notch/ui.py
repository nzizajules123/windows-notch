from __future__ import annotations

"""The notch on Linux: a little island at the top of the screen. Collapsed: album art · time · living eyes ·
equalizer · battery (and the lyric line or live captions underneath). Hover to open: Media (cover, progress, lyrics
stage, YouTube search), To-do, Apps, Talk (live chat with Gemini and what it remembers), Control. Plus the break /
privacy screens, claps, reminders that talk, and automatic updates."""
import datetime as dt
import math
import os
import random
import sys
import threading
import time
import urllib.parse
import urllib.request

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("PangoCairo", "1.0")
from gi.repository import Gdk, GdkPixbuf, GLib, Gtk, Pango, PangoCairo  # noqa: E402

from . import files, lyrics, system, todo as todomod, updater, youtube  # noqa: E402
from .audio import Mic, available as audio_available  # noqa: E402
from .claps import ClapAnalyzer  # noqa: E402
from .config import DATA_DIR, VERSION, Settings, log  # noqa: E402
from .media import Media, OwnTrack  # noqa: E402
from .memory import Memory, Profile  # noqa: E402
from .talk import Talk  # noqa: E402

PINK = (1.0, 0.37, 0.6)

CSS = b"""
.panel { padding: 12px 16px 14px 16px; }
.panel label, .curtain label { color: #f5f5f7; }
.dim { color: rgba(245,245,247,0.62); }
.faint { color: rgba(245,245,247,0.42); }
.title { font-weight: 700; font-size: 15px; }
.big-title { font-weight: 700; font-size: 16px; }
.section { font-size: 10.5px; font-weight: 700; color: rgba(245,245,247,0.5); letter-spacing: 1px; }
.clock { font-weight: 700; font-size: 13px; }
.date { color: rgba(245,245,247,0.5); font-size: 11.5px; }
.batt { font-size: 11.5px; font-weight: 600; }
.time { font-size: 10.5px; color: rgba(245,245,247,0.55); }
.appchip { background-color: rgba(255,255,255,0.10); border-radius: 10px; padding: 2px 9px; font-size: 11px; color: rgba(245,245,247,0.78); }
button.tab, button.chip, button.tile, button.icon, button.flat, button.togglechip {
  background-image: none; box-shadow: none; text-shadow: none; color: #f5f5f7; -gtk-icon-shadow: none;
  border: 1px solid rgba(255,255,255,0.08); background-color: rgba(255,255,255,0.07); border-radius: 12px; padding: 4px 10px; }
button.tab { background-color: transparent; border-color: transparent; color: rgba(245,245,247,0.6); padding: 5px 9px; }
button.tab:checked { background-color: rgba(255,255,255,0.13); border-color: rgba(255,255,255,0.12); color: #ffffff; }
button.tab:checked label { font-weight: 700; }
button.flat { background-color: transparent; border-color: transparent; padding: 6px; border-radius: 10px; }
button.chip:hover, button.tile:hover, button.icon:hover, button.tab:hover, button.flat:hover, button.togglechip:hover { background-color: rgba(255,255,255,0.17); }
button.tile { border-radius: 16px; padding: 10px 4px; font-weight: 600; }
button.danger { background-image: none; background-color: rgba(255,69,58,0.88); border: none; color: #fff; border-radius: 12px; font-weight: 700; padding: 4px 14px; }
button.primary { background-image: none; box-shadow: none; text-shadow: none; border: none; border-radius: 12px;
  background-color: #ffffff; color: #111111; font-weight: 700; font-size: 15px; padding: 4px 14px; }
button.primary:hover { background-color: #e6e6ea; }
button.primary label, button.primary image { color: #111111; }
button.play { background-image: none; background-color: #ffffff; border: none; border-radius: 24px; min-width: 46px; min-height: 46px;
  padding: 0; box-shadow: 0 2px 10px rgba(0,0,0,0.5); }
button.play:hover { background-color: #ececf0; }
button.play image { color: #111111; }
switch { background-image: none; background-color: rgba(255,255,255,0.14); border: 1px solid rgba(255,255,255,0.1); }
switch:checked { background-color: #30d158; border-color: #30d158; }
switch slider { background-image: none; background-color: #ffffff; border: none; box-shadow: 0 1px 2px rgba(0,0,0,0.4); }
entry { background-image: none; background-color: rgba(255,255,255,0.08); color: #ffffff; border-radius: 12px; border: 1px solid rgba(255,255,255,0.1);
  caret-color: #ffffff; padding: 6px 10px; box-shadow: none; }
entry:focus { border-color: rgba(255,255,255,0.28); }
list, row { background-color: transparent; }
row { border-radius: 12px; padding: 2px 0; }
row:hover { background-color: transparent; }
.card { background-color: rgba(255,255,255,0.05); border: 1px solid rgba(255,255,255,0.07); border-radius: 14px; padding: 6px 8px 6px 12px; }
.card:hover { background-color: rgba(255,255,255,0.08); }
.stage { background-color: rgba(255,255,255,0.05); border: 1px solid rgba(255,255,255,0.08); border-radius: 16px; padding: 8px 14px 10px 14px; }
.lyric-cur { font-size: 16px; font-weight: 700; }
.lyric-side { font-size: 12px; color: rgba(245,245,247,0.42); }
.due { color: #ffb040; font-size: 11.5px; }
.due-late { color: #ff6b6b; font-size: 11.5px; font-weight: 700; }
.status { font-weight: 700; font-size: 18px; }
.quote { font-size: 12.5px; }
scale.seek trough { background-image: none; min-height: 4px; border-radius: 2px; background-color: rgba(255,255,255,0.14); border: none; }
scale.seek highlight { background-image: none; min-height: 4px; border-radius: 2px; border: none; }
scale.seek slider { background-image: none; min-width: 10px; min-height: 10px; margin: -4px 0; background-color: #ffffff; border: none;
  border-radius: 6px; box-shadow: none; }
check { background-image: none; border-radius: 9px; border: 1.5px solid rgba(255,255,255,0.45); background-color: transparent; color: #111; }
check:checked { background-color: #30d158; border-color: #30d158; }
.curtain { background-color: #0b0a12; }
.big-time { font-size: 112px; font-weight: 800; }
.big-date { font-size: 22px; font-weight: 600; }
.break-title { font-size: 24px; font-weight: 700; }
.countdown { font-size: 64px; font-weight: 800; color: #7fd8ff; }
.tip { font-size: 19px; }
.warn { font-size: 16px; font-weight: 700; color: #ffb040; }
.hint { font-size: 12px; color: rgba(245,245,247,0.5); }
"""

# colours that follow the album art (seek bar, toggles, the lyrics badge)
TINT_CSS = """
scale.seek highlight {{ background-color: {c}; }}
button.togglechip:checked {{ background-color: {c}; border-color: {c}; color: #111111; }}
button.togglechip:checked label, button.togglechip:checked image {{ color: #111111; }}
.badge {{ background-color: {soft}; color: {c}; border-radius: 8px; padding: 1px 7px; font-size: 9.5px; font-weight: 800; }}
.lyric-cur {{ color: #ffffff; }}
"""

TIPS = ["Stand up and stretch your arms and back 🧘", "Look at something far away for 20 seconds 👀",
        "Drink a glass of water 💧", "Roll your shoulders and gently turn your neck", "Take five slow, deep breaths 😌",
        "Walk around the room for a minute 🚶", "Open a window and get some fresh air 🌿"]
WARNINGS = ["Not yet — the break isn't over 🙂", "Nice try! Your eyes and back need this 💙",
            "Still on break. Stretch while you wait 🧘", "Hands off the keyboard — you've earned these minutes ☕"]

# how a chat can start: the user's own ways, and the notch's (which wait a while after a chat ended)
USER_SOURCES = ("button", "talk tab", "4 claps", "--talk")


def on_main(fn, *args):
    """Runs fn on the GTK thread and waits for its result (for Gemini's actions, which arrive on the talk thread)."""
    if threading.current_thread() is threading.main_thread():
        return fn(*args)
    box, done = {}, threading.Event()

    def run():
        try:
            box["v"] = fn(*args)
        except Exception as e:      # report the problem back to Gemini instead of crashing
            box["v"] = (f"That didn't work: {e}", False)
        done.set()
        return False
    GLib.idle_add(run)
    done.wait(30)
    return box.get("v", ("That took too long.", False))


def rounded(cr, x, y, w, h, r):
    r = min(r, h / 2, w / 2)
    cr.new_sub_path()
    cr.arc(x + w - r, y + r, r, -math.pi / 2, 0)
    cr.arc(x + w - r, y + h - r, r, 0, math.pi / 2)
    cr.arc(x + r, y + h - r, r, math.pi / 2, math.pi)
    cr.arc(x + r, y + r, r, math.pi, 3 * math.pi / 2)
    cr.close_path()


def text(cr, s, x, y, font="Sans Bold 10", rgba=(1, 1, 1, 1), align="left", max_w=None):
    layout = PangoCairo.create_layout(cr)
    layout.set_font_description(Pango.FontDescription(font))
    layout.set_text(s, -1)
    if max_w:
        layout.set_width(int(max_w * Pango.SCALE))
        layout.set_ellipsize(Pango.EllipsizeMode.END)
        if align == "center":
            layout.set_alignment(Pango.Alignment.CENTER)
    w, h = layout.get_pixel_size()
    if align == "center" and max_w:
        x -= max_w / 2
    elif align == "center":
        x -= w / 2
    elif align == "right":
        x -= w
    cr.set_source_rgba(*rgba)
    cr.move_to(x, y - h / 2)
    PangoCairo.show_layout(cr, layout)
    return w


def draw_battery(cr, x, y, pct, charging):
    """A little battery icon, its right edge at x, centred on y."""
    w, h = 21, 10.5
    x0 = x - w - 2
    col = (0.19, 0.82, 0.35) if charging else (1, 0.27, 0.23) if pct <= 20 else (1, 1, 1)
    cr.set_line_width(1.1)
    cr.set_source_rgba(1, 1, 1, 0.55)
    rounded(cr, x0 + 0.5, y - h / 2, w, h, 3)
    cr.stroke()
    cr.rectangle(x0 + w + 1, y - 2, 1.6, 4)
    cr.fill()
    cr.set_source_rgb(*col)
    rounded(cr, x0 + 2, y - h / 2 + 1.5, max(2.0, (w - 3) * pct / 100), h - 3, 1.6)
    cr.fill()
    if charging:
        cr.set_source_rgb(0.06, 0.06, 0.08)
        cx = x0 + w / 2 + 0.5
        cr.move_to(cx + 1.2, y - 4); cr.line_to(cx - 2.2, y + 0.6); cr.line_to(cx - 0.2, y + 0.6)
        cr.line_to(cx - 1.2, y + 4); cr.line_to(cx + 2.2, y - 0.6); cr.line_to(cx + 0.2, y - 0.6)
        cr.close_path(); cr.fill()
    return x0


def draw_eq(cr, x, y_bottom, rgb, playing, bars=4, gap=5, width=3.2, height=12):
    now = time.monotonic()
    for i in range(bars):
        bh = (3 + (height - 3) * abs(math.sin(now * (3.6 + i * 0.9) + i * 1.7))) if playing else 3
        cr.set_source_rgb(*rgb)
        rounded(cr, x + i * gap, y_bottom - bh, width, bh, 1.4)
        cr.fill()


def icon(names, fallback, size=16):
    """A symbolic icon from the theme (first one that exists), or a text glyph if the theme has none of them."""
    theme = Gtk.IconTheme.get_default()
    for n in ([names] if isinstance(names, str) else names):
        if theme.has_icon(n):
            img = Gtk.Image.new_from_icon_name(n, Gtk.IconSize.BUTTON)
            img.set_pixel_size(size)
            return img
    return Gtk.Label(label=fallback)


def fit_labels(w):
    """Ellipsized labels otherwise ask for their whole text's width, which would stretch the (non-resizable) window."""
    if isinstance(w, Gtk.Label):
        if w.get_ellipsize() != Pango.EllipsizeMode.NONE and w.get_max_width_chars() < 1:
            w.set_max_width_chars(1)
            w.set_hexpand(True)
    elif isinstance(w, Gtk.Container):
        for c in w.get_children():
            fit_labels(c)


def tint_of(pb):
    """The album art's main colour, made vivid enough to tint the notch."""
    try:
        small = pb.scale_simple(6, 6, GdkPixbuf.InterpType.BILINEAR)
        data, n, stride = small.get_pixels(), small.get_n_channels(), small.get_rowstride()
        best, best_s = None, -1
        for yy in range(6):
            for xx in range(6):
                o = yy * stride + xx * n
                r, g, b = data[o] / 255, data[o + 1] / 255, data[o + 2] / 255
                mx, mn = max(r, g, b), min(r, g, b)
                s = (mx - mn) * (0.4 + mx)
                if s > best_s:
                    best, best_s = (r, g, b), s
        r, g, b = best
        mx = max(r, g, b, 0.01)
        r, g, b = r / mx, g / mx, b / mx                      # full brightness
        avg = (r + g + b) / 3
        r, g, b = (avg + (c - avg) * 1.25 for c in (r, g, b))  # a bit more saturated
        if best_s < 0.08:                                       # grey cover: keep the notch's pink
            return PINK
        return tuple(max(0.25, min(1.0, c)) for c in (r, g, b))
    except Exception:
        return PINK


class Eyes:
    """Two living eyes: they blink, glance around and pull faces (happy, love, sleepy, surprised)."""

    def __init__(self):
        self.mood, self.mood_until = "neutral", 0
        self.blink_until, self.next_blink = 0, time.monotonic() + 3
        self.look, self.next_look = (0.0, 0.0), time.monotonic() + 2

    def express(self, mood, secs=1.5):
        self.mood, self.mood_until = mood, time.monotonic() + secs

    def tick(self):
        now = time.monotonic()
        if now > self.mood_until:
            self.mood = "neutral"
        if now > self.next_blink:
            self.blink_until, self.next_blink = now + 0.13, now + random.uniform(2.5, 6)
        if now > self.next_look:
            self.look, self.next_look = (random.uniform(-0.25, 0.25), random.uniform(-0.15, 0.15)), now + random.uniform(1.5, 4)

    def draw(self, cr, cx, cy, size):
        now = time.monotonic()
        for dx in (-size * 0.85, size * 0.85):
            x = cx + dx
            cr.new_path()          # don't join on to whatever was drawn before (the clock's text)
            if self.mood == "love":
                cr.set_source_rgb(1, 0.33, 0.55)
                s = size * 0.55
                cr.move_to(x, cy + s * 0.9)
                cr.curve_to(x - s * 1.6, cy - s * 0.2, x - s * 0.6, cy - s * 1.3, x, cy - s * 0.35)
                cr.curve_to(x + s * 0.6, cy - s * 1.3, x + s * 1.6, cy - s * 0.2, x, cy + s * 0.9)
                cr.fill()
                continue
            if self.mood == "happy":
                cr.set_source_rgb(1, 1, 1)
                cr.set_line_width(size * 0.22)
                cr.arc(x, cy + size * 0.25, size * 0.45, math.pi * 1.1, math.pi * 1.9)
                cr.stroke()
                continue
            h = size * (0.16 if now < self.blink_until or self.mood == "sleepy" else 1.35 if self.mood == "surprised" else 1.15)
            cr.save()
            cr.translate(x, cy)
            cr.scale(size / 2, h / 2)
            cr.arc(0, 0, 1, 0, 2 * math.pi)
            cr.restore()
            cr.set_source_rgb(1, 1, 1)
            cr.fill_preserve()
            if h > size * 0.4:
                cr.save()
                cr.clip()
                lx, ly = self.look
                cr.set_source_rgb(0.08, 0.08, 0.1)
                cr.arc(x + lx * size, cy + ly * size, size * 0.27, 0, 2 * math.pi)
                cr.fill()
                cr.set_source_rgb(1, 1, 1)
                cr.arc(x + lx * size + size * 0.1, cy + ly * size - size * 0.1, size * 0.08, 0, 2 * math.pi)
                cr.fill()
                cr.restore()
            cr.new_path()


class Curtain(Gtk.Window):
    """Full screen: the privacy screen (any key / click closes it) or a break (can't be closed until it's over;
    emergency only: hold Ctrl+Shift+E for 5 seconds)."""

    def __init__(self, app, break_until=None):
        super().__init__(title="Notch")
        self.app, self.break_until = app, break_until
        self.shown_at = time.monotonic()
        self.last_warn = 0
        self.escapes = 0
        self.allow_close = break_until is None
        self.emergency_since = None
        self.set_decorated(False)
        self.set_keep_above(True)
        self.set_skip_taskbar_hint(True)
        self.get_style_context().add_class("curtain")
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, valign=Gtk.Align.CENTER, halign=Gtk.Align.CENTER)
        self.date = Gtk.Label(); self.date.get_style_context().add_class("big-date")
        self.clock = Gtk.Label(); self.clock.get_style_context().add_class("big-time")
        box.pack_start(self.date, False, False, 0)
        box.pack_start(self.clock, False, False, 0)
        self.left = Gtk.Label(); self.tip = Gtk.Label(); self.warn = Gtk.Label()
        if break_until:
            t = Gtk.Label(label="☕  Break time"); t.get_style_context().add_class("break-title"); t.set_margin_top(40)
            self.left.get_style_context().add_class("countdown")
            self.tip.get_style_context().add_class("tip")
            self.warn.get_style_context().add_class("warn")
            for w in (t, self.left, self.tip, self.warn):
                box.pack_start(w, False, False, 0)
        hint = Gtk.Label(label="The break ends by itself  ·  emergency only: hold Ctrl+Shift+E for 5 seconds" if break_until
                         else "Click or press any key to return")
        hint.get_style_context().add_class("hint")
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        outer.pack_start(box, True, True, 0)
        outer.pack_end(hint, False, False, 30)
        self.add(outer)
        self.add_events(Gdk.EventMask.KEY_PRESS_MASK | Gdk.EventMask.KEY_RELEASE_MASK | Gdk.EventMask.BUTTON_PRESS_MASK)
        self.connect("key-press-event", self._key)
        self.connect("key-release-event", lambda w, e: setattr(self, "emergency_since", None) if e.keyval in (Gdk.KEY_e, Gdk.KEY_E) else None)
        self.connect("button-press-event", lambda *a: self._tried() if self.break_until else self.dismiss())
        self.connect("delete-event", lambda *a: not self.allow_close)
        self._tick()
        GLib.timeout_add(1000, self._tick)

    def show_it(self):
        # cover the monitor ourselves too: fullscreen() alone needs a cooperating window manager
        display = Gdk.Display.get_default()
        geo = (display.get_primary_monitor() or display.get_monitor(0)).get_geometry()
        self.set_default_size(geo.width, geo.height)
        self.move(geo.x, geo.y)
        self.fullscreen()
        self.show_all()
        self.present()
        if self.break_until:
            # keep the keyboard and mouse here for the break
            GLib.timeout_add(300, self._grab)

    def _grab(self):
        try:
            seat = Gdk.Display.get_default().get_default_seat()
            seat.grab(self.get_window(), Gdk.SeatCapabilities.ALL, True, None, None, None)
        except Exception:
            pass
        return False

    def _key(self, w, e):
        if not self.break_until:
            self.dismiss()
            return True
        ctrl = e.state & Gdk.ModifierType.CONTROL_MASK
        shift = e.state & Gdk.ModifierType.SHIFT_MASK
        if e.keyval in (Gdk.KEY_e, Gdk.KEY_E) and ctrl and shift:
            self.emergency_since = self.emergency_since or time.monotonic()
            held = time.monotonic() - self.emergency_since
            if held >= 5:
                self.allow_close = True
                self.app.on_break_emergency()
                self.dismiss()
            else:
                self._warn(f"Emergency exit… keep holding ({math.ceil(5 - held)} s)")
            return True
        if e.keyval not in (Gdk.KEY_Control_L, Gdk.KEY_Control_R, Gdk.KEY_Shift_L, Gdk.KEY_Shift_R, Gdk.KEY_Alt_L, Gdk.KEY_Super_L):
            self._tried()
        return True

    def _tried(self):
        left = max(0, self.break_until - time.time())
        self.escapes += 1
        self._warn(WARNINGS[(self.escapes - 1) % len(WARNINGS)] + f"  ·  {int(left // 60)}:{int(left % 60):02d} left")
        if time.monotonic() - self.last_warn > 8:
            self.last_warn = time.monotonic()
            self.app.on_break_escape(left, self.escapes)

    def _warn(self, s):
        self.warn.set_text(s)
        GLib.timeout_add(3500, lambda: (self.warn.get_text() == s and self.warn.set_text(""), False)[1])

    def _tick(self):
        now = dt.datetime.now()
        self.clock.set_text(now.strftime("%H:%M"))
        self.date.set_text(now.strftime("%A %-d %B"))
        if self.break_until:
            left = self.break_until - time.time()
            if left <= 0:
                self.allow_close = True
                self.app.on_break_over()
                self.dismiss()
                return False
            self.left.set_text(f"{int(left // 60)}:{int(left % 60):02d}")
            self.tip.set_text(TIPS[int((time.monotonic() - self.shown_at) // 20) % len(TIPS)])
        return True

    def dismiss(self):
        if not self.allow_close:
            return
        try:
            Gdk.Display.get_default().get_default_seat().ungrab()
        except Exception:
            pass
        self.app.curtain = None
        self.destroy()


class Island(Gtk.Window):
    PILL = (250, 38)
    PILL_PLAYING = 300
    PANEL = (640, 364)

    def __init__(self):
        super().__init__(title="Notch")
        self.s = Settings()
        self.media = Media()
        self.todos = todomod.Todos()
        self.profile = Profile()
        self.memory = Memory()
        self.player = youtube.Player()
        self.talk = Talk(lambda: self.s["gemini_key"], lambda: self.s["gemini_voice"] or "Kore")
        self.eyes = Eyes()
        self.expanded = False
        self.collapse_at = 0
        self.ticker = None          # (text, until) speech bubble / caption
        self.caption = None
        self.chat = []              # this chat's exchanges [(you, notch)], for the Talk tab
        self.lyrics, self.lyrics_key = [], None
        self.art_key, self.art_pb = None, None
        self.tint = PINK
        self.curtain: Curtain | None = None
        self.break_until = None
        self.work_seconds, self.break_warned = 0.0, False
        self.countdown = None       # (verb, seconds_left, action)
        self.started = time.monotonic()
        self.talk_ended_at = -1e9   # when the last chat ended: nothing automatic may switch it straight back on
        self.ducked = None          # (bus_name, volume before) while the music is turned down for a chat
        self.claps = None
        self.clap_cooldown = 0
        self.seeking = False
        self.demo_own = None
        self._apps_at = 0

        self._setup_window()
        self._build_pill()
        self._build_panel()
        self._wire_talk()
        self._set_tint(PINK)
        self.add(self.stack)
        self.show_all()
        self._after_show()
        self._place()
        GLib.timeout_add(60, self._frame)
        GLib.timeout_add(1000, self._second)
        GLib.timeout_add_seconds(15, self._quarter_minute)
        GLib.timeout_add_seconds(90, self._check_update)
        if self.s["claps"]:
            self._start_claps()
        if "--demo" in sys.argv:
            # QA: open a view by itself (nobody can hover a virtual screen):
            # --demo pill|playing|media|todo|apps|talk|control|break|privacy|caption
            i = sys.argv.index("--demo") + 1
            what = sys.argv[i] if i < len(sys.argv) else "media"
            GLib.timeout_add_seconds(2, lambda: (self._demo(what), False)[1])
        if "--ask" in sys.argv:
            # QA: typed messages instead of the microphone: --ask "play some jazz" --ask "what's playing?"
            asks = [sys.argv[i + 1] for i, a in enumerate(sys.argv) if a == "--ask" and i + 1 < len(sys.argv)]
            GLib.timeout_add_seconds(3, lambda: (self._qa_ask(asks), False)[1])
        elif "--updated" in sys.argv:
            GLib.timeout_add_seconds(5, lambda: (self.say(f"Updated to v{VERSION} ✨", 5), False)[1])
        else:
            GLib.timeout_add_seconds(3, lambda: (self.say(self._greeting()), False)[1])

    # ------------------------------------------------------------------ window
    def _setup_window(self):
        self.set_decorated(False)
        self.set_keep_above(True)
        self.set_skip_taskbar_hint(True)
        self.set_skip_pager_hint(True)
        self.set_type_hint(Gdk.WindowTypeHint.UTILITY)
        self.stick()
        self.set_resizable(False)
        self.set_app_paintable(True)
        screen = self.get_screen()
        visual = screen.get_rgba_visual()
        if visual and screen.is_composited():
            self.set_visual(visual)
        provider = Gtk.CssProvider()
        provider.load_from_data(CSS)
        Gtk.StyleContext.add_provider_for_screen(screen, provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        self.tint_css = Gtk.CssProvider()
        Gtk.StyleContext.add_provider_for_screen(screen, self.tint_css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION + 1)
        self.connect("draw", self._clear)
        self.add_events(Gdk.EventMask.ENTER_NOTIFY_MASK | Gdk.EventMask.LEAVE_NOTIFY_MASK)
        self.connect("enter-notify-event", self._enter)
        self.connect("leave-notify-event", self._leave)
        self.connect("destroy", lambda *a: self.quit())

    @staticmethod
    def _clear(w, cr):
        import cairo
        cr.set_operator(cairo.OPERATOR_SOURCE)
        cr.set_source_rgba(0, 0, 0, 0)
        cr.paint()
        cr.set_operator(cairo.OPERATOR_OVER)
        return False

    def _pill_size(self):
        t = self.media.current
        w = self.PILL_PLAYING if t and t.playing else self.PILL[0]
        return w, self.PILL[1] + (24 if self._ticker_text() else 0)

    def _place(self):
        w, h = self.PANEL if self.expanded else self._pill_size()
        display = Gdk.Display.get_default()
        mon = display.get_primary_monitor() or display.get_monitor(0)
        geo = mon.get_geometry()
        if not self.expanded:
            self.pill.set_size_request(w, h)   # a DrawingArea asks for nothing by itself
        else:
            self.panel.set_size_request(w, h)  # the window isn't resizable: it takes the size its content asks for
        self.resize(w, h)
        self.move(geo.x + (geo.width - w) // 2, geo.y + 6)

    def _enter(self, w, e):
        self.collapse_at = 0
        if not self.expanded:
            GLib.timeout_add(140, self._maybe_expand)

    def _maybe_expand(self):
        x, y = self.get_pointer()
        a = self.get_allocation()
        if 0 <= x <= a.width and 0 <= y <= a.height and not self.expanded:
            self.expand()
        return False

    def _leave(self, w, e):
        if e.detail != Gdk.NotifyType.INFERIOR:
            self.collapse_at = time.monotonic() + self.s["collapse_delay"]

    def expand(self):
        self.expanded = True
        self.stack.set_visible_child_name("panel")
        self._refresh_panel()
        self._place()
        self.present()

    def collapse(self):
        if self.countdown:
            return
        self.expanded = False
        self.stack.set_visible_child_name("pill")
        self._place()

    def _set_tint(self, rgb):
        self.tint = rgb
        c = "#%02x%02x%02x" % tuple(int(v * 255) for v in rgb)
        soft = "rgba(%d,%d,%d,0.2)" % tuple(int(v * 255) for v in rgb)
        self.tint_css.load_from_data(TINT_CSS.format(c=c, soft=soft).encode())

    # ------------------------------------------------------------------ the pill
    def _build_pill(self):
        self.stack = Gtk.Stack()
        self.stack.set_transition_type(Gtk.StackTransitionType.CROSSFADE)
        self.stack.set_transition_duration(180)
        # size to the page that's showing, or the window can never shrink back to the pill
        self.stack.set_hhomogeneous(False)
        self.stack.set_vhomogeneous(False)
        self.pill = Gtk.DrawingArea()
        self.pill.connect("draw", self._draw_pill)
        self.pill.add_events(Gdk.EventMask.BUTTON_PRESS_MASK)
        self.pill.connect("button-press-event", lambda *a: self.expand())
        self.stack.add_named(self.pill, "pill")

    def _ticker_text(self):
        if self.talk.active:
            return self.caption or ("🎙  Connecting…" if self.talk.state == "connecting" else "🎙  Listening — just talk")
        if self.ticker and time.monotonic() < self.ticker[1]:
            return self.ticker[0]
        t = self.media.current
        if self.s["lyrics"] and t and t.playing and self.lyrics:
            line = lyrics.line_at(self.lyrics, t.position)
            return f"♪  {line}" if line else None
        return None

    def _background(self, cr, W, H, radius, strength):
        """The island's body: near-black glass, washed with the album art's colour from the left."""
        import cairo
        rounded(cr, 0.5, 0.5, W - 1, H - 1, radius)
        cr.set_source_rgba(0.065, 0.063, 0.085, 0.97)
        cr.fill_preserve()
        r, g, b = self.tint
        lg = cairo.LinearGradient(0, 0, W, H * 0.6)
        lg.add_color_stop_rgba(0, r, g, b, 0.30 * strength)
        lg.add_color_stop_rgba(0.55, r, g, b, 0.06 * strength)
        lg.add_color_stop_rgba(1, r, g, b, 0.10 * strength)
        cr.set_source(lg)
        cr.fill_preserve()
        cr.set_source_rgba(1, 1, 1, 0.10)
        cr.set_line_width(1)
        cr.stroke()

    def _draw_art(self, cr, x, y, size, radius):
        rounded(cr, x, y, size, size, radius)
        if self.art_pb:
            cr.save()
            cr.clip()
            sc = size / self.art_pb.get_width()
            cr.translate(x, y)
            cr.scale(sc, sc)
            Gdk.cairo_set_source_pixbuf(cr, self.art_pb, 0, 0)
            cr.paint()
            cr.restore()
        else:
            r, g, b = self.tint
            cr.set_source_rgba(r, g, b, 0.25)
            cr.fill()
            text(cr, "♫", x + size / 2, y + size / 2, f"Sans {max(8, size * 0.36):.0f}", (1, 1, 1, 0.8), "center")

    def _draw_pill(self, w, cr):
        a = w.get_allocation()
        W, H = a.width, a.height
        t = self.media.current
        playing = bool(t and t.playing)
        self._background(cr, W, H, 19, 1.0 if playing else 0.0)
        top = self.PILL[1] / 2
        x = 14
        if playing:
            self._draw_art(cr, 9, top - 12, 24, 7)
            x = 40
        text(cr, dt.datetime.now().strftime("%H:%M"), x, top, "Sans Bold 10.5")
        cx = W / 2 + (8 if playing else 0)
        if self.talk.active:
            # listening: a soft red pulse; speaking: happy eyes
            if self.talk.state == "speaking":
                self.eyes.mood, self.eyes.mood_until = "happy", time.monotonic() + 0.3
                self.eyes.draw(cr, cx, top, 9)
            else:
                pulse = 0.55 + 0.45 * math.sin(time.monotonic() * 5)
                cr.set_source_rgba(1, 0.27, 0.23, 0.25 * pulse)
                cr.arc(cx, top, 10, 0, 2 * math.pi); cr.fill()
                cr.set_source_rgba(1, 0.27, 0.23, 0.6 + 0.4 * pulse)
                cr.arc(cx, top, 5, 0, 2 * math.pi); cr.fill()
        else:
            self.eyes.draw(cr, cx, top, 9)
        right = W - 14
        pct, charging, has = system.battery()
        if has:
            x0 = draw_battery(cr, right, top, pct, charging)
            col = (0.19, 0.82, 0.35, 1) if charging else (1, 0.27, 0.23, 1) if pct <= 20 else (1, 1, 1, 0.85)
            right = x0 - 5 - text(cr, f"{pct}%", x0 - 5, top, "Sans Bold 9", col, "right") - 10
        if playing:
            draw_eq(cr, right - 18, top + 6, self.tint, True)
        tk = self._ticker_text()
        if tk and H > self.PILL[1] + 4:
            col = (*self.tint, 1) if tk.startswith("♪") else (1, 1, 1, 0.9)
            text(cr, tk, W / 2, top + 24, "Sans 9.5", col if tk.startswith("♪") else (1, 1, 1, 0.9), "center", W - 30)
        return True

    def _frame(self):
        self.eyes.tick()
        if self.stack.get_visible_child_name() == "pill":
            self.pill.queue_draw()
        elif self.expanded:
            for w in (self.art_area, self.mini_eq, self.orb, self.batt_icon):
                w.queue_draw()
        return True

    # ------------------------------------------------------------------ the open panel
    def _build_panel(self):
        self.panel = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        self.panel.get_style_context().add_class("panel")
        self.panel.set_app_paintable(True)
        self.panel.connect("draw", lambda w, cr: (self._background(cr, w.get_allocated_width(), w.get_allocated_height(), 26,
                                                                   1.0 if self.media.current else 0.35), False)[1])
        head = Gtk.Box(spacing=2)
        self.tabs, self.tab_labels = {}, {}
        for key, label, names, glyph in (
                ("media", "Media", ["audio-x-generic-symbolic", "folder-music-symbolic"], "♫"),
                ("todo", "To-do", ["checkbox-checked-symbolic", "object-select-symbolic"], "✓"),
                ("apps", "Apps", ["view-app-grid-symbolic", "view-grid-symbolic", "applications-system-symbolic"], "▦"),
                ("talk", "Talk", ["audio-input-microphone-symbolic"], "🎙"),
                ("control", "Control", ["system-shutdown-symbolic", "emblem-system-symbolic"], "⚙")):
            b = Gtk.ToggleButton()
            inner = Gtk.Box(spacing=6)
            inner.pack_start(icon(names, glyph), False, False, 0)
            lbl = Gtk.Label(label=label)
            inner.pack_start(lbl, False, False, 0)
            b.add(inner)
            b.set_tooltip_text(label)
            b.get_style_context().add_class("tab")
            b.connect("toggled", self._tab, key)
            head.pack_start(b, False, False, 0)
            self.tabs[key], self.tab_labels[key] = b, lbl
        self.batt_label = Gtk.Label(); self.batt_label.get_style_context().add_class("batt")
        self.batt_icon = Gtk.DrawingArea(); self.batt_icon.set_size_request(26, 14)
        self.batt_icon.connect("draw", self._draw_batt_icon)
        self.clock_label = Gtk.Label(); self.clock_label.get_style_context().add_class("clock")
        self.date_label = Gtk.Label(); self.date_label.get_style_context().add_class("date")
        head.pack_end(self.batt_icon, False, False, 0)
        head.pack_end(self.batt_label, False, False, 4)
        head.pack_end(self.clock_label, False, False, 8)
        head.pack_end(self.date_label, False, False, 0)
        self.panel.pack_start(head, False, False, 0)
        self.pages = Gtk.Stack()
        self.pages.set_transition_type(Gtk.StackTransitionType.CROSSFADE)
        self.pages.set_transition_duration(140)
        self.pages.add_named(self._media_page(), "media")
        self.pages.add_named(self._todo_page(), "todo")
        self.pages.add_named(self._apps_page(), "apps")
        self.pages.add_named(self._talk_page(), "talk")
        self.pages.add_named(self._control_page(), "control")
        self.pages.add_named(self._countdown_page(), "countdown")
        self.panel.pack_start(self.pages, True, True, 0)
        self.stack.add_named(self.panel, "panel")

    def _after_show(self):
        self.tabs["media"].set_active(True)
        self._show_tab_labels("media")
        self._update_below()

    def _show_tab_labels(self, active):
        # like on Windows: icons only, the open tab shows its name
        for k, lbl in self.tab_labels.items():
            lbl.set_visible(k == active)

    def _draw_batt_icon(self, w, cr):
        pct, charging, has = system.battery()
        if has:
            draw_battery(cr, w.get_allocated_width() - 1, w.get_allocated_height() / 2, pct, charging)
        return False

    def _chip(self, label, cb, tip=None, cls="chip"):
        b = Gtk.Button(label=label)
        b.get_style_context().add_class(cls)
        b.connect("clicked", lambda *a: cb())
        if tip:
            b.set_tooltip_text(tip)
        return b

    def _icon_button(self, names, glyph, cb, tip=None, cls="flat", size=18):
        b = Gtk.Button()
        b.add(icon(names, glyph, size))
        b.get_style_context().add_class(cls)
        b.connect("clicked", lambda *a: cb())
        if tip:
            b.set_tooltip_text(tip)
        return b

    def _tab(self, btn, key):
        if not btn.get_active():
            if not any(b.get_active() for b in self.tabs.values()):
                btn.set_active(True)
            return
        for k, b in self.tabs.items():
            if k != key and b.get_active():
                b.set_active(False)
        self._show_tab_labels(key)
        self.pages.set_visible_child_name(key)
        if key == "apps":
            self._refresh_apps()
        if key == "todo":
            self._refresh_todos()
        if key == "talk":
            self._refresh_notes()
        self._refresh_panel()

    # ---- Media
    def _media_page(self):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        top = Gtk.Box(spacing=16)
        self.art_area = Gtk.DrawingArea()
        self.art_area.set_size_request(112, 112)
        self.art_area.connect("draw", self._draw_big_art)
        top.pack_start(self.art_area, False, False, 0)
        info = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=3, valign=Gtk.Align.CENTER)
        row1 = Gtk.Box(spacing=8)
        self.m_title = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END); self.m_title.get_style_context().add_class("big-title")
        self.m_app = Gtk.Label(); self.m_app.get_style_context().add_class("appchip")
        row1.pack_start(self.m_title, True, True, 0)
        row1.pack_end(self.m_app, False, False, 0)
        row2 = Gtk.Box(spacing=6)
        self.mini_eq = Gtk.DrawingArea(); self.mini_eq.set_size_request(16, 12)
        self.mini_eq.connect("draw", lambda w, cr: (draw_eq(cr, 0, 12, self.tint, bool(self.media.current and self.media.current.playing),
                                                            bars=4, gap=4, width=2.4, height=11), False)[1])
        self.m_artist = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END); self.m_artist.get_style_context().add_class("dim")
        row2.pack_start(self.mini_eq, False, False, 0)
        row2.pack_start(self.m_artist, True, True, 0)
        prog = Gtk.Box(spacing=8)
        self.m_pos = Gtk.Label(label="0:00"); self.m_pos.get_style_context().add_class("time")
        self.m_len = Gtk.Label(label="0:00"); self.m_len.get_style_context().add_class("time")
        self.seek = Gtk.Scale(orientation=Gtk.Orientation.HORIZONTAL, adjustment=Gtk.Adjustment(value=0, lower=0, upper=1, step_increment=1, page_increment=10))
        self.seek.set_draw_value(False)
        self.seek.get_style_context().add_class("seek")
        self.seek.connect("button-press-event", lambda *a: setattr(self, "seeking", True) or False)
        self.seek.connect("button-release-event", self._seek_done)
        prog.pack_start(self.m_pos, False, False, 0)
        prog.pack_start(self.seek, True, True, 0)
        prog.pack_start(self.m_len, False, False, 0)
        ctl = Gtk.Box(spacing=10)
        ctl.pack_start(self._icon_button(["media-skip-backward-symbolic"], "⏮", lambda: self._media("previous"), "Previous"), False, False, 0)
        self.play_btn = Gtk.Button()
        self.play_icons = {True: icon(["media-playback-pause-symbolic"], "⏸", 20), False: icon(["media-playback-start-symbolic"], "▶", 20)}
        self.play_btn.add(self.play_icons[False])
        self.play_btn.get_style_context().add_class("play")
        self.play_btn.connect("clicked", lambda *a: self._media("play_pause"))
        self.play_btn.set_valign(Gtk.Align.CENTER)
        ctl.pack_start(self.play_btn, False, False, 0)
        ctl.pack_start(self._icon_button(["media-skip-forward-symbolic"], "⏭", lambda: self._media("next"), "Next"), False, False, 0)
        self.search_toggle = Gtk.ToggleButton()
        sbox = Gtk.Box(spacing=5); sbox.pack_start(icon(["system-search-symbolic", "edit-find-symbolic"], "⌕", 14), False, False, 0)
        sbox.pack_start(Gtk.Label(label="Search"), False, False, 0)
        self.search_toggle.add(sbox)
        self.search_toggle.get_style_context().add_class("togglechip")
        self.search_toggle.connect("toggled", lambda b: (self._update_below(), b.get_active() and GLib.idle_add(self.search_entry.grab_focus)))
        self.search_toggle.set_valign(Gtk.Align.CENTER)
        self.lyrics_toggle = Gtk.ToggleButton(label="Lyrics")
        self.lyrics_toggle.get_style_context().add_class("togglechip")
        self.lyrics_toggle.set_active(self.s["lyrics"])
        self.lyrics_toggle.set_valign(Gtk.Align.CENTER)
        self.lyrics_toggle.connect("toggled", lambda b: (self.s.__setitem__("lyrics", b.get_active()), self.s.save(), self._update_below()))
        ctl.pack_end(self.search_toggle, False, False, 0)
        ctl.pack_end(self.lyrics_toggle, False, False, 0)
        for wdg in (row1, row2, prog, ctl):
            info.pack_start(wdg, False, False, 0)
        self.prog_row = prog
        top.pack_start(info, True, True, 0)
        box.pack_start(top, False, False, 0)

        self.below = Gtk.Stack()
        self.below.set_transition_type(Gtk.StackTransitionType.CROSSFADE)
        self.below.set_vhomogeneous(False)
        # the lyrics stage
        stage = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
        stage.get_style_context().add_class("stage")
        badge_row = Gtk.Box()
        badge = Gtk.Label(label="●  LYRICS"); badge.get_style_context().add_class("badge")
        badge_row.pack_start(badge, False, False, 0)
        self.ly_prev = Gtk.Label(ellipsize=Pango.EllipsizeMode.END); self.ly_prev.get_style_context().add_class("lyric-side")
        self.ly_cur = Gtk.Label(ellipsize=Pango.EllipsizeMode.END); self.ly_cur.get_style_context().add_class("lyric-cur")
        self.ly_next = Gtk.Label(ellipsize=Pango.EllipsizeMode.END); self.ly_next.get_style_context().add_class("lyric-side")
        lines = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=3, valign=Gtk.Align.CENTER)
        for wdg in (self.ly_prev, self.ly_cur, self.ly_next):
            lines.pack_start(wdg, False, False, 0)
        stage.pack_start(badge_row, False, False, 0)
        stage.pack_start(lines, True, True, 0)
        self.below.add_named(stage, "lyrics")
        # YouTube search
        sbox = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        srow = Gtk.Box(spacing=6)
        self.search_entry = Gtk.Entry(placeholder_text="Search a song on YouTube…  (Enter)")
        self.search_entry.connect("activate", lambda e: self._search(e.get_text()))
        srow.pack_start(self.search_entry, True, True, 0)
        srow.pack_start(self._icon_button(["system-search-symbolic", "edit-find-symbolic"], "Search",
                                          lambda: self._search(self.search_entry.get_text()), cls="primary", size=16), False, False, 0)
        sbox.pack_start(srow, False, False, 0)
        sc = Gtk.ScrolledWindow(); sc.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        sc.set_min_content_height(96)
        self.results = Gtk.ListBox()
        self.results.set_selection_mode(Gtk.SelectionMode.NONE)
        self.results.connect("row-activated", lambda lb, row: self._play_results(row.get_index()))
        sc.add(self.results)
        sbox.pack_start(sc, True, True, 0)
        self.below.add_named(sbox, "search")
        # a quiet hint
        self.hint_label = Gtk.Label(wrap=True, justify=Gtk.Justification.CENTER)
        self.hint_label.get_style_context().add_class("faint")
        hint_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL); hint_box.get_style_context().add_class("stage")
        hint_box.pack_start(self.hint_label, True, True, 6)
        self.below.add_named(hint_box, "hint")
        box.pack_start(self.below, True, True, 0)
        self.found = []
        return box

    def _draw_big_art(self, w, cr):
        size = min(w.get_allocated_width(), w.get_allocated_height())
        # a soft glow in the cover's colour behind it
        r, g, b = self.tint
        playing = bool(self.media.current and self.media.current.playing)
        for i in range(5, 0, -1):
            cr.set_source_rgba(r, g, b, (0.05 if playing else 0.02))
            rounded(cr, 4 - i, 6 - i, size - 8 + 2 * i, size - 8 + 2 * i, 18 + i)
            cr.fill()
        self._draw_art(cr, 4, 4, size - 8, 18)
        return False

    def _update_below(self):
        t = self.media.current
        if self.search_toggle.get_active() or not t:
            self.below.set_visible_child_name("search")
            return
        if self.s["lyrics"] and self.lyrics:
            self.below.set_visible_child_name("lyrics")
            return
        self.hint_label.set_text("Looking for synced lyrics…" if self.s["lyrics"] and self.lyrics_key == t.key and self.lyrics is None
                                 else "No synced lyrics for this one 🎶" if self.s["lyrics"] else "Lyrics are off")
        self.below.set_visible_child_name("hint")

    def _media(self, what):
        t = self.media.current
        own = t and t.bus_name == OwnTrack.OWN
        if what == "play_pause":
            self.player.command("cycle", "pause") if own else self.media.play_pause()
        elif what == "play":
            self.player.command("set_property", "pause", False) if own else self.media.play()
        elif what == "pause":
            self.player.command("set_property", "pause", True) if own else self.media.pause()
        elif what == "next":
            self.player.command("playlist-next") if own else self.media.next()
        elif what == "previous":
            self.player.command("playlist-prev") if own else self.media.previous()
        GLib.timeout_add(250, lambda: (self._refresh_media(), False)[1])

    def _seek_done(self, *a):
        self.seeking = False
        t = self.media.current
        if t:
            v = self.seek.get_value()
            if t.bus_name == OwnTrack.OWN:
                self.player.seek(v)
            else:
                self.media.seek(v)
        return False

    def _search(self, q):
        q = q.strip()
        if not q:
            return
        for c in self.results.get_children():
            self.results.remove(c)
        self.results.add(Gtk.Label(label="Searching…", xalign=0))
        self.results.show_all()

        def work():
            res = youtube.search(q)
            GLib.idle_add(self._show_results, res, q)
        threading.Thread(target=work, daemon=True).start()

    def _show_results(self, res, q):
        self.found = res
        for c in self.results.get_children():
            self.results.remove(c)
        if not res:
            self.results.add(Gtk.Label(label=f"No songs found for “{q}”", xalign=0))
        for r in res:
            row = Gtk.Box(spacing=10)
            row.get_style_context().add_class("card")
            row.pack_start(icon(["media-playback-start-symbolic"], "▶", 12), False, False, 0)
            row.pack_start(Gtk.Label(label=r["title"], xalign=0, ellipsize=Pango.EllipsizeMode.END), True, True, 0)
            d = Gtk.Label(label="  ·  ".join(x for x in (r["channel"], r["length"]) if x)); d.get_style_context().add_class("faint")
            row.pack_end(d, False, False, 0)
            self.results.add(row)
        self.results.show_all()
        return False

    def _play_results(self, index):
        if 0 <= index < len(self.found):
            self.player.play(self.found, index)
            self.eyes.express("happy", 1.2)
            self.search_toggle.set_active(False)
            if not youtube.Player.available():
                self.say("Opened in your browser (install mpv + yt-dlp to play here)", 4)

    def _refresh_media(self):
        t = self.media.current
        want = self.play_icons[bool(t and t.playing)]
        if self.play_btn.get_child() is not want:
            self.play_btn.remove(self.play_btn.get_child())
            self.play_btn.add(want)
            want.show()
        self.prog_row.set_opacity(1 if t else 0)
        self.mini_eq.set_visible(bool(t))
        if t:
            self.m_title.set_text(t.title)
            self.m_artist.set_text(t.artist or "—")
            self.m_app.set_text(t.app)
            self.m_app.show()
            if t.length > 0:
                self.seek.set_range(0, t.length)
                if not self.seeking:
                    self.seek.set_value(t.position)
                self.m_pos.set_text(_mmss(t.position))
                self.m_len.set_text(_mmss(t.length))
            else:
                self.seek.set_range(0, 1); self.seek.set_value(0)
                self.m_pos.set_text(_mmss(t.position)); self.m_len.set_text("live" if t.playing else "")
            if self.art_key != t.art_url:
                self.art_key = t.art_url
                self._load_art(t.art_url)
            if self.lyrics:
                p, c, n = lyrics.stage(self.lyrics, t.position)
                self.ly_prev.set_text(p); self.ly_cur.set_text(c); self.ly_next.set_text(n)
        else:
            self.m_title.set_text("Nothing playing")
            self.m_artist.set_text("Play something in Spotify, Firefox, VLC… or search a song")
            self.m_app.hide()
            self.seek.set_range(0, 1); self.seek.set_value(0)
            self.m_pos.set_text(""); self.m_len.set_text("")
            if self.art_key is not None:
                self.art_key, self.art_pb = None, None
                self._set_tint(PINK)
        self._update_below()

    def _load_art(self, url):
        def work():
            pb = None
            try:
                if url.startswith("file://"):
                    pb = GdkPixbuf.Pixbuf.new_from_file(urllib.request.url2pathname(url[7:]))
                elif url.startswith("http"):
                    data = urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "NotchLinux"}), timeout=8).read()
                    loader = GdkPixbuf.PixbufLoader()
                    loader.write(data); loader.close()
                    pb = loader.get_pixbuf()
                if pb:
                    # square, centred (YouTube thumbnails are 4:3 with black bars)
                    w, h = pb.get_width(), pb.get_height()
                    side = min(w, h) if abs(w - h) < 8 else int(min(w, h) * (0.75 if "ytimg" in url else 1))
                    pb = pb.new_subpixbuf((w - side) // 2, (h - side) // 2, side, side).scale_simple(224, 224, GdkPixbuf.InterpType.BILINEAR)
            except Exception:
                pb = None
            GLib.idle_add(self._art_loaded, url, pb)
        threading.Thread(target=work, daemon=True).start()

    def _art_loaded(self, url, pb):
        if url != self.art_key:
            return False
        self.art_pb = pb
        self._set_tint(tint_of(pb) if pb else PINK)
        self.panel.queue_draw()
        return False

    # ---- To-do
    def _todo_page(self):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        row = Gtk.Box(spacing=6)
        self.todo_entry = Gtk.Entry(placeholder_text="Add a to-do… (\"call mom at 5pm\", \"stretch in 20 min\")")
        self.todo_entry.connect("activate", lambda e: self._add_todo())
        row.pack_start(self.todo_entry, True, True, 0)
        row.pack_start(self._chip("+", self._add_todo, cls="primary"), False, False, 0)
        box.pack_start(row, False, False, 0)
        self.todo_head = Gtk.Label(xalign=0); self.todo_head.get_style_context().add_class("section")
        box.pack_start(self.todo_head, False, False, 0)
        sc = Gtk.ScrolledWindow(); sc.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.todo_list = Gtk.ListBox()
        self.todo_list.set_selection_mode(Gtk.SelectionMode.NONE)
        sc.add(self.todo_list)
        box.pack_start(sc, True, True, 0)
        return box

    def _add_todo(self):
        t, due = todomod.parse(self.todo_entry.get_text())
        if t:
            self.todos.add(t, due)
            self.todo_entry.set_text("")
            self.eyes.express("happy", 1)
            self._refresh_todos()

    def _refresh_todos(self):
        for c in self.todo_list.get_children():
            self.todo_list.remove(c)
        items = self.todos.items
        open_n = sum(1 for i in items if not i["done"])
        self.todo_head.set_text(f"{open_n} TO DO  ·  {len(items) - open_n} DONE" if items else "")
        if not items:
            empty = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
            e1 = Gtk.Label(label="Nothing to do 🎉"); e1.get_style_context().add_class("title")
            e2 = Gtk.Label(label="Add one above, or just tell me: “remind me to stretch in 20 minutes”")
            e2.get_style_context().add_class("faint")
            empty.pack_start(e1, False, False, 18); empty.pack_start(e2, False, False, 0)
            self.todo_list.add(empty)
        now = dt.datetime.now()
        for it in sorted(items, key=lambda i: (i["done"], i["due"] or "9999")):
            row = Gtk.Box(spacing=10)
            row.get_style_context().add_class("card")
            cb = Gtk.CheckButton()
            cb.set_active(it["done"])
            cb.connect("toggled", lambda b, i=it: (self.todos.toggle(i), self._refresh_todos()))
            row.pack_start(cb, False, False, 0)
            lbl = Gtk.Label(label=it["text"], xalign=0, ellipsize=Pango.EllipsizeMode.END)
            if it["done"]:
                lbl.get_style_context().add_class("faint")
            row.pack_start(lbl, True, True, 0)
            if it["due"] and not it["done"]:
                due = dt.datetime.fromisoformat(it["due"])
                when = due.strftime("%H:%M") if due.date() == now.date() else due.strftime("%a %H:%M")
                d = Gtk.Label(label=("⏰ late · " if due < now else "⏰ ") + when)
                d.get_style_context().add_class("due-late" if due < now else "due")
                row.pack_start(d, False, False, 0)
            row.pack_end(self._icon_button(["window-close-symbolic"], "✕", lambda i=it: (self.todos.remove(i), self._refresh_todos()), "Delete", size=14),
                         False, False, 0)
            self.todo_list.add(row)
        self.todo_list.show_all()

    # ---- Apps (open windows)
    def _apps_page(self):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        row = Gtk.Box(spacing=6)
        self.app_entry = Gtk.Entry(placeholder_text="Open an app…  (Firefox, Files, Terminal, VS Code…)")
        self.app_entry.connect("activate", lambda e: self._open_typed_app())
        row.pack_start(self.app_entry, True, True, 0)
        row.pack_start(self._chip("Open", self._open_typed_app, cls="primary"), False, False, 0)
        box.pack_start(row, False, False, 0)
        self.apps_head = Gtk.Label(xalign=0); self.apps_head.get_style_context().add_class("section")
        box.pack_start(self.apps_head, False, False, 0)
        sc = Gtk.ScrolledWindow(); sc.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.apps_list = Gtk.ListBox()
        self.apps_list.set_selection_mode(Gtk.SelectionMode.NONE)
        sc.add(self.apps_list)
        box.pack_start(sc, True, True, 0)
        self._apps_sig = None
        return box

    def _open_typed_app(self):
        name = self.app_entry.get_text().strip()
        if not name:
            return
        got = system.focus_window(name) or system.open_app(name)
        self.say(f"Opened {got}" if got else f"No app called “{name}”", 3)
        if got:
            self.app_entry.set_text("")
            GLib.timeout_add(1500, lambda: (self._refresh_apps(), False)[1])

    def _refresh_apps(self):
        # wmctrl and the icon lookup can be slow: never on the UI thread
        if getattr(self, "_apps_busy", False):
            return
        self._apps_busy, self._apps_at = True, time.monotonic()

        def work():
            ws = system.windows()
            icons = [system.app_icon(c) for _, _, c in ws] if ws else []
            GLib.idle_add(self._show_apps, ws, icons)
        threading.Thread(target=work, daemon=True).start()

    def _show_apps(self, ws, icons):
        self._apps_busy, self._apps_at = False, time.monotonic()
        sig = repr(ws)
        if sig == self._apps_sig:
            return False
        self._apps_sig = sig
        for c in self.apps_list.get_children():
            self.apps_list.remove(c)
        if ws is None:
            self.apps_head.set_text("OPEN WINDOWS")
            self.apps_list.add(Gtk.Label(label="Install wmctrl to see and close open windows (sudo apt install wmctrl)", wrap=True))
        else:
            self.apps_head.set_text(f"{len(ws)} OPEN WINDOW{'S' if len(ws) != 1 else ''}")
            if not ws:
                e = Gtk.Label(label="No windows open"); e.get_style_context().add_class("faint")
                self.apps_list.add(e)
            for (wid, title, cls), icon_name in zip(ws, icons):
                row = Gtk.Box(spacing=10)
                row.get_style_context().add_class("card")
                img = Gtk.Image.new_from_icon_name(icon_name, Gtk.IconSize.DND)
                img.set_pixel_size(22)
                row.pack_start(img, False, False, 0)
                col = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
                t = Gtk.Label(label=title, xalign=0, ellipsize=Pango.EllipsizeMode.END)
                c = Gtk.Label(label=cls, xalign=0); c.get_style_context().add_class("faint")
                col.pack_start(t, False, False, 0); col.pack_start(c, False, False, 0)
                row.pack_start(col, True, True, 0)
                row.pack_end(self._icon_button(["window-close-symbolic"], "✕", lambda i=wid: (system.close_id(i), GLib.timeout_add(600, lambda: (self._refresh_apps(), False)[1])),
                                               "Close", size=14), False, False, 0)
                row.pack_end(self._chip("Switch", lambda i=wid: (system.focus_id(i), self.collapse())), False, False, 0)
                self.apps_list.add(row)
        self.apps_list.show_all()
        return False

    # ---- Talk (live chat with Gemini)
    def _talk_page(self):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        top = Gtk.Box(spacing=18)
        self.orb = Gtk.DrawingArea(); self.orb.set_size_request(104, 104)
        self.orb.connect("draw", self._draw_orb)
        self.orb.add_events(Gdk.EventMask.BUTTON_PRESS_MASK)
        self.orb.connect("button-press-event", lambda *a: self.toggle_talk(source="talk tab"))
        top.pack_start(self.orb, False, False, 0)
        col = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4, valign=Gtk.Align.CENTER)
        self.talk_status = Gtk.Label(xalign=0); self.talk_status.get_style_context().add_class("status")
        self.talk_sub = Gtk.Label(xalign=0, wrap=True); self.talk_sub.get_style_context().add_class("dim")
        btns = Gtk.Box(spacing=8)
        self.talk_go = Gtk.Button(label="Start talking")
        self.talk_go.get_style_context().add_class("primary")
        self.talk_go.connect("clicked", lambda *a: self.toggle_talk(source="talk tab"))
        btns.pack_start(self.talk_go, False, False, 0)
        btns.pack_start(self._chip("Talk setup", self.setup_dialog), False, False, 0)
        col.pack_start(self.talk_status, False, False, 0)
        col.pack_start(self.talk_sub, False, False, 0)
        col.pack_start(btns, False, False, 6)
        top.pack_start(col, True, True, 0)
        box.pack_start(top, False, False, 0)
        self.talk_quote = Gtk.Label(xalign=0, wrap=True, lines=2, ellipsize=Pango.EllipsizeMode.END)
        self.talk_quote.get_style_context().add_class("quote")
        box.pack_start(self.talk_quote, False, False, 0)
        mem_head = Gtk.Box()
        self.mem_title = Gtk.Label(xalign=0); self.mem_title.get_style_context().add_class("section")
        mem_head.pack_start(self.mem_title, True, True, 0)
        box.pack_start(mem_head, False, False, 0)
        sc = Gtk.ScrolledWindow(); sc.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.notes_list = Gtk.ListBox(); self.notes_list.set_selection_mode(Gtk.SelectionMode.NONE)
        sc.add(self.notes_list)
        box.pack_start(sc, True, True, 0)
        return box

    def _draw_orb(self, w, cr):
        W, H = w.get_allocated_width(), w.get_allocated_height()
        cx, cy, R = W / 2, H / 2, min(W, H) / 2 - 6
        now = time.monotonic()
        st = self.talk.state
        col = {"off": (0.55, 0.55, 0.65), "connecting": (1, 0.7, 0.25), "listening": (1, 0.27, 0.35), "speaking": self.tint}[st]
        if st != "off":
            for i in range(3):
                ph = (now * 0.8 + i / 3) % 1
                cr.set_source_rgba(*col, 0.35 * (1 - ph))
                cr.set_line_width(2)
                cr.arc(cx, cy, R * (0.72 + 0.28 * ph), 0, 2 * math.pi)
                cr.stroke()
        import cairo
        g = cairo.RadialGradient(cx - R * 0.3, cy - R * 0.35, R * 0.1, cx, cy, R * 0.72)
        g.add_color_stop_rgba(0, min(1, col[0] + 0.3), min(1, col[1] + 0.3), min(1, col[2] + 0.3), 1)
        g.add_color_stop_rgba(1, col[0] * 0.55, col[1] * 0.55, col[2] * 0.55, 1)
        bump = 1 + (0.05 * math.sin(now * 9) if st == "speaking" else 0.02 * math.sin(now * 3) if st == "listening" else 0)
        cr.arc(cx, cy, R * 0.7 * bump, 0, 2 * math.pi)
        cr.set_source(g)
        cr.fill()
        self.eyes.draw(cr, cx, cy, 11)
        return False

    def _refresh_talk(self):
        st = self.talk.state
        who = self.profile.assistant_name or "Notch"
        if not self.s["gemini_key"]:
            self.talk_status.set_text("Talk with me")
            self.talk_sub.set_text("Add a free Gemini API key in Talk setup to start.")
        else:
            self.talk_status.set_text({"off": f"Hi, I'm {who}", "connecting": "Connecting…", "listening": "Listening — just talk",
                                       "speaking": "Speaking…"}[st])
            self.talk_sub.set_text("Click me, press Start, or clap 4 times. I can open apps, play music, write files, set reminders…"
                                   if st == "off" else "Say “stop” or press Stop to end. I keep listening while music plays.")
        self.talk_go.set_label("Stop" if self.talk.active else "Start talking")
        ctx = self.talk_go.get_style_context()
        ctx.remove_class("primary" if self.talk.active else "danger")
        ctx.add_class("danger" if self.talk.active else "primary")
        if self.talk.active and self.caption:
            self.talk_quote.set_text(self.caption)
        elif self.chat:
            you, notch = self.chat[-1]
            self.talk_quote.set_text(f"🗣  {you}\n✨  {notch}")
        else:
            self.talk_quote.set_text("")

    def _refresh_notes(self):
        for c in self.notes_list.get_children():
            self.notes_list.remove(c)
        p = self.profile
        facts = []
        if p.user_name:
            facts.append(f"you're {p.user_name}")
        if p.assistant_name:
            facts.append(f"I'm {p.assistant_name}")
        if p.interests:
            facts.append("you like " + ", ".join(p.interests[:4]))
        self.mem_title.set_text("WHAT I REMEMBER" + (f"  ·  {len(self.memory.notes)} NOTES" if self.memory.notes else ""))
        if facts:
            f = Gtk.Label(label="  ·  ".join(facts), xalign=0, ellipsize=Pango.EllipsizeMode.END)
            f.get_style_context().add_class("dim")
            self.notes_list.add(f)
        if not self.memory.notes and not facts:
            e = Gtk.Label(label="Nothing yet — tell me about your goals and plans and I'll keep track.", xalign=0)
            e.get_style_context().add_class("faint")
            self.notes_list.add(e)
        for n in reversed(self.memory.notes[-12:]):
            row = Gtk.Box(spacing=8)
            row.get_style_context().add_class("card")
            row.pack_start(Gtk.Label(label=n["text"], xalign=0, ellipsize=Pango.EllipsizeMode.END), True, True, 0)
            d = Gtk.Label(label=n["t"][5:10]); d.get_style_context().add_class("faint")
            row.pack_start(d, False, False, 0)
            row.pack_end(self._icon_button(["window-close-symbolic"], "✕", lambda t=n["text"]: (self.memory.remove_notes(t), self._refresh_notes()),
                                           "Forget this", size=14), False, False, 0)
            self.notes_list.add(row)
        self.notes_list.show_all()

    # ---- Control
    def _control_page(self):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        tiles = Gtk.Box(spacing=8, homogeneous=True)
        for label, names, glyph, fn in (
                ("Lock", ["system-lock-screen-symbolic", "changes-prevent-symbolic"], "🔒", system.lock_screen),
                ("Sleep", ["weather-clear-night-symbolic", "media-playback-pause-symbolic"], "🌙", system.suspend),
                ("Restart", ["system-reboot-symbolic", "view-refresh-symbolic"], "↻", lambda: self.start_countdown("Restarting", system.reboot)),
                ("Shut down", ["system-shutdown-symbolic"], "⏻", lambda: self.start_countdown("Shutting down", system.poweroff))):
            b = Gtk.Button()
            inner = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
            inner.pack_start(icon(names, glyph, 20), False, False, 0)
            inner.pack_start(Gtk.Label(label=label), False, False, 0)
            b.add(inner)
            b.get_style_context().add_class("tile")
            b.connect("clicked", lambda *a, f=fn: f())
            tiles.pack_start(b, True, True, 0)
        box.pack_start(tiles, False, False, 0)
        grid = Gtk.Grid(column_spacing=14, row_spacing=6)
        self.switches = {}
        rows = [("claps", "Claps 👏  (×2 lock · ×4 talk)"), ("eyes_talk", "Eyes talk"), ("breaks", "Breaks (5 min every 2 h)"),
                ("auto_update", "Auto-update"), ("autostart", "Start at login"), ("proactive_chats", "Chats on its own")]
        for i, (key, label) in enumerate(rows):
            sw = Gtk.Switch(valign=Gtk.Align.CENTER)
            sw.set_active(self._autostart_on() if key == "autostart" else bool(self.s.get(key)))
            sw.connect("notify::active", self._switch, key)
            lbl = Gtk.Label(label=label, xalign=0, hexpand=True)
            grid.attach(lbl, (i % 2) * 2, i // 2, 1, 1)
            grid.attach(sw, (i % 2) * 2 + 1, i // 2, 1, 1)
            self.switches[key] = sw
        box.pack_start(grid, False, False, 0)
        self.break_label = Gtk.Label(xalign=0); self.break_label.get_style_context().add_class("dim")
        box.pack_start(self.break_label, False, False, 0)
        bottom = Gtk.Box(spacing=6)
        bottom.pack_start(self._chip("🔒 Privacy screen", lambda: self.privacy(True)), False, False, 0)
        self.update_btn = self._chip(f"v{VERSION} · check updates", lambda: self._check_update(force=True))
        bottom.pack_start(self.update_btn, False, False, 0)
        bottom.pack_end(self._chip("✕ Quit", self.quit), False, False, 0)
        box.pack_end(bottom, False, False, 0)
        return box

    def _switch(self, sw, _p, key):
        on = sw.get_active()
        if key == "autostart":
            self._set_autostart(on)
            return
        self.s[key] = on
        self.s.save()
        if key == "claps":
            self._start_claps() if on else self._stop_claps()
        if key == "breaks":
            self.work_seconds, self.break_warned = 0, False

    AUTOSTART = os.path.expanduser("~/.config/autostart/notch.desktop")

    def _autostart_on(self):
        return os.path.exists(self.AUTOSTART)

    def _set_autostart(self, on):
        if on:
            os.makedirs(os.path.dirname(self.AUTOSTART), exist_ok=True)
            launcher = os.path.expanduser("~/.local/bin/notch")
            with open(self.AUTOSTART, "w") as f:
                f.write(f"[Desktop Entry]\nType=Application\nName=Notch\nExec={launcher if os.path.exists(launcher) else sys.executable + ' -m notch'}\n"
                        "X-GNOME-Autostart-enabled=true\nNoDisplay=true\n")
        elif os.path.exists(self.AUTOSTART):
            os.remove(self.AUTOSTART)

    # ---- Restart / shut down countdown
    def _countdown_page(self):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12, valign=Gtk.Align.CENTER)
        self.cd_label = Gtk.Label(); self.cd_label.get_style_context().add_class("countdown")
        self.cd_title = Gtk.Label(); self.cd_title.get_style_context().add_class("title")
        btns = Gtk.Box(spacing=10, halign=Gtk.Align.CENTER)
        btns.pack_start(self._chip("Cancel", self.cancel_countdown), False, False, 0)
        btns.pack_start(self._chip("Now", lambda: self._fire_countdown(), cls="danger"), False, False, 0)
        for w in (self.cd_label, self.cd_title, btns):
            box.pack_start(w, False, False, 0)
        return box

    def start_countdown(self, verb, action, secs=5):
        if self.countdown:
            return
        self.countdown = [verb, secs, action]
        self.expand()
        self.pages.set_visible_child_name("countdown")
        self._countdown_tick()
        GLib.timeout_add(1000, self._countdown_tick)

    def _countdown_tick(self):
        if not self.countdown:
            return False
        verb, secs, _ = self.countdown
        if secs <= 0:
            self._fire_countdown()
            return False
        self.cd_label.set_text(str(secs))
        self.cd_title.set_text(f"{verb} in {secs} s")
        self.countdown[1] -= 1
        return True

    def _fire_countdown(self):
        if self.countdown:
            action = self.countdown[2]
            self.countdown = None
            action()

    def cancel_countdown(self):
        self.countdown = None
        self.pages.set_visible_child_name("control")

    def _refresh_panel(self):
        fit_labels(self.panel)
        now = dt.datetime.now()
        self.date_label.set_text(now.strftime("%a %-d %b"))
        self.clock_label.set_text(now.strftime("%H:%M"))
        pct, charging, has = system.battery()
        self.batt_label.set_text(f"{pct}%" if has else "")
        self.batt_label.set_markup(f"<span foreground='{'#30d158' if charging else '#ff453a' if pct <= 20 else '#f5f5f7'}'>{pct}%</span>" if has else "")
        self.batt_icon.set_visible(has)
        page = self.pages.get_visible_child_name()
        if page == "media":
            self._refresh_media()
        elif page == "talk":
            self._refresh_talk()
        elif page == "apps" and time.monotonic() - self._apps_at > 2:
            self._refresh_apps()
        self.break_label.set_text(self._break_text())

    # ------------------------------------------------------------------ timers
    def _own_status(self):
        if self.demo_own:
            d = self.demo_own
            return {**d, "position": (time.monotonic() - d["t0"]) % d["length"]}
        return self.player.status()

    def _second(self):
        prev = self.media.current.key if self.media.current else None
        was_playing = bool(self.media.current and self.media.current.playing)
        t = self.media.refresh(self._own_status())
        if t and t.key != prev:
            self._fetch_lyrics(t)
        if self.expanded:
            self._refresh_panel()
            if self.collapse_at and time.monotonic() > self.collapse_at and not any(
                    e.has_focus() for e in (self.search_entry, self.todo_entry, self.app_entry)):
                self.collapse_at = 0
                self.collapse()
        else:
            if t and self.art_key != t.art_url:
                self.art_key = t.art_url
                self._load_art(t.art_url)
            if self.get_size() != self._pill_size() or was_playing != bool(t and t.playing):
                self._place()
        self._break_tick()
        return True

    def _fetch_lyrics(self, t):
        key = t.key
        self.lyrics, self.lyrics_key = None, key

        def work():
            res = lyrics.fetch(t.title, t.artist, t.length) if t.artist else []
            if not res and " - " in t.title:          # YouTube titles: "Artist - Song (Official Video)"
                artist, song = t.title.split(" - ", 1)
                import re
                song = re.sub(r"\s*[\(\[][^)\]]*[\)\]]", "", song).strip()
                res = lyrics.fetch(song, artist.strip(), t.length)
            GLib.idle_add(lambda: (self.lyrics_key == key and setattr(self, "lyrics", res), False)[1])
        threading.Thread(target=work, daemon=True).start()

    def _quarter_minute(self):
        for it in self.todos.due_now():
            log("reminder: " + it["text"])
            self.eyes.express("surprised", 1)
            self.say(f"⏰  {it['text']}", 8)
            self._talk_about_reminder(it)
            self._refresh_todos()
        self._n = getattr(self, "_n", 0) + 1
        if self._n % 4 == 0:
            self._maybe_proactive()
        return True

    def say(self, s, secs=3):
        if not self.s["eyes_talk"] and not s.startswith(("⏰", "👏", "⚠", "Update")):
            return
        self.ticker = (s, time.monotonic() + secs)
        self._place()

    def _greeting(self):
        h = dt.datetime.now().hour
        name = f", {self.profile.user_name}" if self.profile.user_name else ""
        return ("Up late? 🌙" if h < 5 else f"Good morning{name} ☀️" if h < 12 else f"Good afternoon{name} 🌤" if h < 17
                else f"Good evening{name} 🌆" if h < 22 else "Good night soon? 🌙")

    # ------------------------------------------------------------------ live talk
    def _wire_talk(self):
        self.talk.tools = TOOLS
        self.talk.handler = lambda name, args: on_main(self.act, name, args)
        self.talk.music_playing = lambda: bool(self.media.current and self.media.current.playing)
        self.talk.on_state = lambda s, msg=None: GLib.idle_add(self._talk_state, s, msg)
        self.talk.on_caption = lambda c: setattr(self, "caption", c)
        self.talk.on_exchange = lambda you, notch: GLib.idle_add(self._exchange, you, notch)

    def _exchange(self, you, notch):
        self.memory.add(you, notch)
        self.chat = (self.chat + [(you, notch)])[-6:]
        return False

    def _talk_state(self, s, msg):
        log(f"live talk: {s}" + (f" — {msg}" if msg else ""))
        if s == "off":
            self.talk_ended_at = time.monotonic()
            self.caption = None
            self._duck(False)
            if msg:
                self.say("⚠ " + msg, 5)
            if self.talk.text_only:                      # QA run finished
                GLib.timeout_add_seconds(2, lambda: (self.quit(), False)[1])
        else:
            self._duck(True)
            if s == "speaking":
                self.eyes.express("happy", 1)
        self._place()
        if self.expanded and self.pages.get_visible_child_name() == "talk":
            self._refresh_talk()
            if s == "off":
                self._refresh_notes()
        return False

    def _duck(self, on):
        """While you talk to the notch, the music plays quieter, so you and Gemini can hear each other."""
        if on and not self.ducked:
            t = self.media.current
            if self.player.running:
                self.player.set_volume(35)
            vol = None
            if t and t.bus_name != OwnTrack.OWN and t.playing:
                vol = self.media.get_volume(t.bus_name)
                if vol:
                    self.media.set_volume(t.bus_name, vol * 0.35)
            self.ducked = (t.bus_name if t else None, vol)
        elif not on and self.ducked:
            bus, vol = self.ducked
            self.ducked = None
            if self.player.running:
                self.player.set_volume(100)
            if bus and vol:
                self.media.set_volume(bus, vol)

    def toggle_talk(self, opening=None, source="button"):
        """Start or stop a chat. source: who asked — the user (button, claps…) or the notch itself (a reminder, a break…)."""
        if self.talk.active:
            if opening:
                self.talk.tell(opening)
            else:
                self.talk_ended_at = time.monotonic()
                self.talk.stop(f"you ended it · {source}")
            return
        since = time.monotonic() - self.talk_ended_at
        if since < 2:
            # a double click (or a late echo) right after stopping must not switch it straight back on
            log(f"live talk: start ignored, it just ended ({source})")
            return
        if not self.s["gemini_key"]:
            if source not in USER_SOURCES:
                return
            self.setup_dialog()
            if not self.s["gemini_key"]:
                return
        if not audio_available() and not self.talk.text_only:
            self.say("⚠ Install pulseaudio-utils or pipewire tools for the microphone", 5)
            return
        log(f"live talk: start ({source})")
        self.chat = []
        if source in USER_SOURCES:
            self.collapse()
        self.talk.start(self.prompt(), opening)

    def _qa_ask(self, asks):
        if not asks:
            return
        log(f"QA: {len(asks)} typed message(s)")
        self.talk.text_only = True
        self.talk.script = list(asks[1:])
        self.toggle_talk(asks[0], source="--talk")

    def prompt(self):
        now = dt.datetime.now()
        t = self.media.current
        media = f" Right now \"{t.title}\" by {t.artist or t.app} is {'playing' if t.playing else 'paused'}." if t else ""
        pct, charging, has = system.battery()
        who = self.profile.assistant_name or "Notch"
        return (f"You are {who}, a cute little island with living eyes that sits at the top of the user's Linux desktop. "
                + self.profile.for_prompt() + " "
                "You can search Google (if available) for fresh news about the user's interests, and you love sharing a quick bit of history now and then. "
                "You are their friendly companion: warm, playful, encouraging and a little funny. "
                "Speak naturally like a friend in a voice chat, and keep answers short (one to three sentences) unless asked for more. "
                "Reply in the language the user speaks. "
                "You can act on the PC with your tools: open and switch to apps, list and close open apps, search and play music (it plays quietly while you talk), "
                "pause / resume / skip it, search the web, open websites, add to-dos with reminders, lock the PC, and turn the privacy screen on or off "
                "(that only hides the screen; lock_pc really locks the PC). "
                "When the user asks for one of these, call the tool straight away (don't ask for confirmation), then say in a few words what you did. "
                "You can write anything the user asks (letters, emails, essays, notes, lists, code) and save it as a file in the folder they choose with write_file; "
                "write the complete text yourself and tell them where it was saved. "
                "When a to-do comes due, tell the user about it warmly and offer two or three practical ideas or first steps, then ask if they want help "
                "(for example writing something for it). "
                "You look after the user's health with computer breaks: you can tell them when the next break is, start one, or change the schedule. "
                "During a break you keep them company; you can never end a break early, so if they ask, kindly say no and encourage them. "
                "If a tool says something wasn't found, tell the user kindly and suggest what to try. You can't restart or shut down the PC. "
                "When the user says goodbye or asks you to stop listening, say a short goodbye. "
                "Music may be playing quietly in the background while you talk: ignore song lyrics and only answer when the user speaks to you. "
                f"It is {now:%A %-d %B %Y, %H:%M}.{media}" + (f" The battery is at {pct}%{' and charging' if charging else ''}." if has else "")
                + "\n\nYou remember things long-term to help the user stay on track: when they mention a goal, plan, project, deadline, decision or something "
                "you agreed to do, save it with remember_note; remove notes that are done with forget_note. When it fits, follow up on open notes "
                "(\"how did the exam go?\"). For older conversations you don't see below, use search_memory. "
                "The user can ask for a break of any length now (take_break_now) or plan one for later (schedule_break); breaks can't be cancelled once started or planned."
                + (f"\n\nYour notes about the user (keep these in mind):\n{self.memory.notes_text()}" if self.memory.notes else "")
                + (f"\n\nYour recent conversations with the user (oldest first). Use them to remember what you talked about; don't list them unless asked:\n"
                   f"{self.memory.recent()}" if self.memory.items else ""))

    def _talk_about_reminder(self, it):
        if not (self.s["talk_on_reminder"] and self.s["gemini_key"]) or system.locked():
            return
        if not self.talk.active and time.monotonic() - self.talk_ended_at < 120:
            return      # you just switched it off: the reminder card is enough
        note = (f"(Reminder from the user's to-do list: \"{it['text']}\" is due now. Tell them about it in a friendly way and give two or three "
                "short, practical ideas or first steps for doing it. Then ask if they'd like help with it.)")
        GLib.timeout_add(2500, lambda: (self.toggle_talk(note, source="reminder"), False)[1])

    def _maybe_proactive(self):
        if not (self.s["proactive_chats"] and self.s["gemini_key"]) or self.talk.active or self.curtain:
            return
        if time.monotonic() - self.talk_ended_at < 45 * 60:
            return      # you just finished talking: don't start another chat on its own
        now = dt.datetime.now()
        if now.hour < 9 or now.hour >= 21 or time.monotonic() - self.started < 600:
            return
        p = self.profile
        if p.proactive_day != now.date().isoformat():
            p.proactive_day, p.proactive_today = now.date().isoformat(), 0
        if p.proactive_today >= 3 or time.time() - p.last_proactive < 3 * 3600:
            return
        idle = system.idle_seconds()
        if idle < 20 or idle > 90 or system.locked() or (self.media.current and self.media.current.playing) or self.expanded:
            return
        p.last_proactive, p.proactive_today = time.time(), p.proactive_today + 1
        p.save()
        name = p.user_name or "the user"
        topic = (f"something new and interesting about one of their interests ({', '.join(p.interests)}) — if you can search Google, find a real update from the last few days; otherwise share a fun fact"
                 if p.interests and random.random() < 0.66 else f"a short, fascinating 'on this day in history' fact for {now:%-d %B}")
        log("proactive chat: starting")
        self.toggle_talk(f"(You are starting this chat yourself — {name} didn't ask. Greet them by name in a few words, then share {topic}. "
                         "Two or three sentences, then ask a light question to keep the conversation going. If they say they're busy, wish them well and keep it short.)",
                         source="proactive")

    # ------------------------------------------------------------------ what Gemini can do
    def act(self, name, a):
        g = lambda k: str(a.get(k, "")).strip()  # noqa: E731
        if name == "open_app":
            if focused := system.focus_window(g("name")):
                return f"Switched to {focused} (it was already open).", False
            opened = system.open_app(g("name"))
            return (f"Opened {opened}." if opened else f"No app called \"{g('name')}\" is installed on this PC."), False
        if name == "close_app":
            closed = system.close_windows(g("name"))
            if closed is None:
                return "Closing apps needs the wmctrl tool (sudo apt install wmctrl).", False
            return ("Closed " + ", ".join(closed) + "." if closed else f"No open window called \"{g('name')}\"."), False
        if name == "list_open_apps":
            ws = system.windows()
            if ws is None:
                return "I can't see the open windows (needs wmctrl).", False
            return ("Open: " + "; ".join(f"{t} ({c})" for _, t, c in ws) + "." if ws else "Nothing is open."), False
        if name == "play_music":
            res = youtube.search(g("query"))
            if not res:
                return f"No songs found for \"{g('query')}\".", False
            self.found = res
            self.player.play(res, 0)
            if self.talk.active and self.player.running:
                GLib.timeout_add(1500, lambda: (self.player.set_volume(35), False)[1])   # quiet while you talk
            return (f"Now playing \"{res[0]['title']}\"" + (f" ({res[0]['channel']})" if res[0]["channel"] else "")
                    + (". The music is playing quietly while you talk." if youtube.Player.available() else " in the browser.")), False
        if name == "media_control":
            act = g("action")
            if act in ("pause", "stop"):
                self._media("pause")
                return "Paused.", False
            if act == "next":
                self._media("next")
                return "Skipped to the next track.", False
            if act == "previous":
                self._media("previous")
                return "Went back.", False
            self._media("play")
            return "Playing.", False
        if name == "now_playing":
            t = self.media.refresh(self._own_status())
            if not t:
                return "Nothing is playing.", False
            when = f" ({_mmss(t.position)} of {_mmss(t.length)})" if t.length > 0 else ""
            return f"\"{t.title}\" by {t.artist or t.app} is {'playing' if t.playing else 'paused'}{when}.", False
        if name == "web_search":
            system.open_uri("https://www.google.com/search?q=" + urllib.parse.quote(g("query")))
            return f"Searched the web for \"{g('query')}\" in the browser.", False
        if name == "open_website":
            u = g("url").replace(" ", "")
            u = u if u.startswith(("http://", "https://")) else "https://" + u
            system.open_uri(u)
            return f"Opened {u}.", False
        if name == "add_todo":
            t, due = todomod.parse(g("text"))
            if not t:
                return "The task was empty.", False
            self.todos.add(t, due)
            self._refresh_todos()
            return (f"Added \"{t}\" with a reminder at {due:%A %H:%M}." if due else f"Added \"{t}\"."), False
        if name == "lock_pc":
            GLib.timeout_add(1200, lambda: (system.lock_screen(), False)[1])
            return "Locking the PC now.", True
        if name == "privacy_screen":
            if a.get("on"):
                self.privacy(True)
                return "The privacy screen is on.", False
            if self.break_until:
                return "It's break time: the screen stays until the break is over.", False
            self.privacy(False)
            return "The privacy screen is off.", False
        if name == "write_file":
            try:
                path = files.save(g("content") or str(a.get("content", "")), g("file_name"), g("folder"), g("format"), bool(a.get("overwrite")))
                if a.get("open_after", True):
                    files.open_path(path)
                self.say("📄 Saved " + os.path.basename(path), 4)
                return f"Saved to {path}.", False
            except ValueError as e:
                return f"Couldn't save it: {e}", False
        if name == "open_path":
            try:
                return f"Opened {files.open_path(g('path'))}.", False
            except ValueError as e:
                return str(e), False
        if name == "remember_profile":
            r = self.profile.remember(g("user_name"), g("assistant_name"), a.get("add_interests") or [], a.get("remove_interests") or [])
            if not r.startswith("Nothing"):
                self.eyes.express("love", 1.4)
            return r, False
        if name == "remember_note":
            return self.memory.add_note(g("note")), False
        if name == "forget_note":
            n = self.memory.remove_notes(g("match"))
            return (f"Removed {n} note(s)." if n else "No matching note."), False
        if name == "search_memory":
            found = self.memory.search(g("query"))
            return ("From earlier conversations:\n" + found if found else "Nothing about that in earlier conversations."), False
        if name == "forget_memory":
            self.memory.clear()
            return "Forgot all earlier conversations and notes.", False
        if name == "break_status":
            return self._break_text() or "Breaks are off.", False
        if name == "take_break_now":
            if self.break_until:
                return "Already on a break.", False
            mins = int(a.get("minutes") or self.s["break_minutes"])
            self.start_break("asked", mins)
            return f"Started a {mins}-minute break. It can't be cancelled.", False
        if name == "schedule_break":
            mins = max(1, min(120, int(a.get("minutes") or self.s["break_minutes"])))
            if a.get("in_minutes"):
                when = time.time() + int(a["in_minutes"]) * 60
            else:
                try:
                    hh, mm = map(int, g("at_time").split(":"))
                    w = dt.datetime.combine(dt.date.today(), dt.time(hh, mm))
                    if w <= dt.datetime.now():
                        w += dt.timedelta(days=1)
                    when = w.timestamp()
                except ValueError:
                    return "I need a time (like 15:00) or a number of minutes from now.", False
            if self.s["scheduled_break_at"] > time.time():
                return "There's already a break planned; planned breaks can't be changed or cancelled.", False
            self.s["scheduled_break_at"], self.s["scheduled_break_minutes"] = when, mins
            self.s.save()
            return f"Planned: a {mins}-minute break at {dt.datetime.fromtimestamp(when):%H:%M}. It can't be cancelled.", False
        if name == "set_breaks":
            self.s["breaks"] = bool(a.get("enabled", True))
            if a.get("every_minutes"):
                self.s["break_every"] = max(30, min(240, int(a["every_minutes"])))
            if a.get("break_minutes"):
                self.s["break_minutes"] = max(1, min(30, int(a["break_minutes"])))
            self.s.save()
            self.switches["breaks"].set_active(self.s["breaks"])
            return (f"Breaks are on: {self.s['break_minutes']} minutes after every {self.s['break_every']} minutes of use."
                    if self.s["breaks"] else "Breaks are off."), False
        return "Unknown action.", False

    # ------------------------------------------------------------------ privacy screen & breaks
    def privacy(self, on):
        if on and not self.curtain:
            self.collapse()
            self.curtain = Curtain(self)
            self.curtain.show_it()
        elif not on and self.curtain and not self.curtain.break_until:
            self.curtain.dismiss()

    def _break_text(self):
        planned = ""
        if self.s["scheduled_break_at"] > time.time():
            planned = f"  A {self.s['scheduled_break_minutes']}-minute break is planned at {dt.datetime.fromtimestamp(self.s['scheduled_break_at']):%H:%M}."
        if self.break_until:
            left = self.break_until - time.time()
            return f"On a break now: {int(left // 60)} min {int(left % 60)} s left."
        if not self.s["breaks"]:
            return ("Automatic breaks are off." + planned).strip()
        left = max(0, self.s["break_every"] * 60 - self.work_seconds)
        return f"Next {self.s['break_minutes']}-minute break in {int(left // 3600)} h {int(left % 3600 // 60)} min." + planned

    def _break_tick(self):
        if self.break_until:
            return
        if self.s["scheduled_break_at"] and time.time() >= self.s["scheduled_break_at"] and not system.locked():
            mins = self.s["scheduled_break_minutes"]
            self.s["scheduled_break_at"] = 0.0
            self.s.save()
            self.start_break("scheduled", mins)
            return
        if not self.s["breaks"]:
            return
        idle = system.idle_seconds()
        if system.locked() or idle >= self.s["break_minutes"] * 60:
            self.work_seconds, self.break_warned = 0, False
            return
        if idle < 120:
            self.work_seconds += 1
        total = self.s["break_every"] * 60
        if not self.break_warned and self.work_seconds >= total - 60:
            self.break_warned = True
            self.say("☕ Break in 1 minute — save your work", 8)
        if self.work_seconds >= total:
            self.start_break("time")

    def start_break(self, why, minutes=None):
        if self.break_until:
            return
        mins = max(1, min(120, int(minutes or self.s["break_minutes"])))
        self.break_until = time.time() + mins * 60
        log(f"breaks: starting a {mins}-min break ({why})")
        if self.curtain:
            self.curtain.allow_close = True
            self.curtain.dismiss()
        self.collapse()
        self.curtain = Curtain(self, self.break_until)
        self.curtain.show_it()
        reason = {"time": f"the user has been using the computer for about {self.s['break_every'] / 60:.1f} hours, so a {mins}-minute break has just started",
                  "scheduled": f"the {mins}-minute break the user planned earlier has just started"}.get(why, f"the user asked for a {mins}-minute break, and it has just started")
        note = (f"(Break time: {reason}. Their screen is covered until it's over and they can't end it early. Tell them warmly, suggest a couple of things "
                "to do (stretch, water, rest the eyes, a short walk), and keep them company. If they ask to end the break early, kindly say no — you have no way to end it.)")
        if self.s["gemini_key"] and audio_available():
            self.talk.keep_alive = True
            if self.talk.active:
                self.talk.tell(note)
            else:
                self.talk_ended_at = -1e9          # the break chat is meant to start, even right after another chat
                self.toggle_talk(note, source="break")

    def on_break_escape(self, left, n):
        log(f"breaks: escape attempt #{n}")
        if self.talk.active:
            self.talk.tell(f"(The user just tried to leave the break screen early (attempt {n}); {int(left // 60)} min {int(left % 60)} s are left. "
                           + ("Kindly tell them it isn't over yet and why the break matters." if n == 1 else "Be a little firmer but still friendly; suggest something to do instead.") + ")")

    def on_break_over(self):
        self.break_until = None
        self.talk.keep_alive = False
        self.work_seconds, self.break_warned = 0, False
        self.eyes.express("happy", 1.6)
        if self.talk.active:
            self.talk.tell("(The break is over. Welcome them back in one cheerful sentence.)")
        else:
            self.say("Break's over — welcome back! 💪", 4)

    def on_break_emergency(self):
        log("breaks: ended early with the emergency keys")
        self.break_until = None
        self.talk.keep_alive = False
        self.work_seconds = max(0, (self.s["break_every"] - 30) * 60)
        if self.talk.active:
            self.talk.tell("(The user ended the break early with the emergency keys. Say you hope everything is okay, in one sentence.)")

    # ------------------------------------------------------------------ claps
    def _start_claps(self):
        if self.claps or not audio_available():
            return

        def on_clap(n):
            GLib.idle_add(lambda: (self._clap_heard(n), False)[1])

        def on_seq(n, span):
            GLib.idle_add(lambda: (self._clap_sequence(n, span), False)[1])
        analyzer = ClapAnalyzer(on_clap, on_seq)
        self.claps = Mic(16000, analyzer.feed)
        if self.claps.start():
            log("claps: listening")
        else:
            self.claps = None

    def _stop_claps(self):
        if self.claps:
            self.claps.stop()
        self.claps = None

    def _clap_blocked(self, span_ms=0):
        if time.monotonic() < self.clap_cooldown or self.talk.active or system.locked():
            return True
        if time.monotonic() - self.talk_ended_at < 8:      # the end of a chat can sound like claps
            return True
        if self.media.current and self.media.current.playing:
            return True
        return system.idle_seconds() * 1000 < span_ms + 500        # hands on the keyboard / mouse

    def _clap_heard(self, n):
        if not self._clap_blocked():
            self.ticker = (["", "👏  Clap again to lock", "👏👏  Locking…", "👏👏👏", "👏👏👏👏  Let's talk"][min(n, 4)], time.monotonic() + 1.2)

    def _clap_sequence(self, n, span):
        if n not in (2, 4) or self._clap_blocked(span):
            return
        self.clap_cooldown = time.monotonic() + 4
        log(f"claps fired: {n}")
        if n == 2:
            system.lock_screen()
        else:
            self.toggle_talk(source="4 claps")

    def _demo(self, what):
        log(f"demo: {what}")
        if what in ("playing", "pill-playing", "caption"):
            self.demo_own = {"id": "kPa7bsKwL-c", "title": "Lady Gaga, Bruno Mars - Die With A Smile (Official Music Video)", "channel": "LadyGagaVEVO",
                             "length": 252.0, "playing": True, "t0": time.monotonic() - 68}
            self.lyrics_key = None
            self.media.refresh(self._own_status())
            self.lyrics, self.lyrics_key = [(60.0, "I, I just woke up from a dream"), (66.0, "Where you and I had to say goodbye"),
                                            (71.0, "And I don't know what it all means"), (77.0, "But since I survived, I realized")], \
                self.media.current.key
            if what == "caption":
                self.caption = "✨  Now playing Die With A Smile — enjoy! 🎶"
                self.talk.state = "speaking"
            elif what == "playing":
                GLib.timeout_add(2500, lambda: (self.expand(), self.tabs["media"].set_active(True), setattr(self, "collapse_at", time.monotonic() + 3600), False)[3])
            return
        if what in ("media", "todo", "apps", "talk", "control"):
            if what == "todo" and not self.todos.items:
                self.todos.add("Call mom", todomod.parse("call mom at 5pm")[1])
                self.todos.add("Push the release", None)
                self.todos.add("Stretch", todomod.parse("stretch in 20 min")[1])
            self.expand()
            self.tabs[what].set_active(True)
            self.collapse_at = time.monotonic() + 3600
        elif what == "break":
            self.start_break("asked", 5)
        elif what == "privacy":
            self.privacy(True)

    # ------------------------------------------------------------------ setup & updates
    def setup_dialog(self):
        d = Gtk.Dialog(title="Talk with the notch", transient_for=self, modal=True)
        d.add_buttons("Cancel", Gtk.ResponseType.CANCEL, "Save", Gtk.ResponseType.OK)
        box = d.get_content_area()
        box.set_spacing(8)
        box.set_margin_start(16); box.set_margin_end(16); box.set_margin_top(12); box.set_margin_bottom(12)
        box.pack_start(Gtk.Label(label="A Gemini API key (aistudio.google.com → Get API key) lets you talk with the notch.\n"
                                       "It's saved in ~/.config/notch/settings.json, readable only by you.", xalign=0), False, False, 0)
        key = Gtk.Entry(text=self.s["gemini_key"], visibility=False, placeholder_text="Gemini API key")
        voice = Gtk.ComboBoxText.new_with_entry()
        for v in ("Kore", "Puck", "Aoede", "Charon", "Fenrir", "Leda", "Orus", "Zephyr"):
            voice.append_text(v)
        voice.get_child().set_text(self.s["gemini_voice"] or "Kore")
        remind = Gtk.CheckButton(label="Talk to me when a to-do is due (tells me the task and gives ideas)")
        remind.set_active(self.s["talk_on_reminder"])
        pro = Gtk.CheckButton(label="Let the notch start a chat now and then (hobby news, a bit of history; max 3 a day)")
        pro.set_active(self.s["proactive_chats"])
        for w in (key, Gtk.Label(label="Voice", xalign=0), voice, remind, pro):
            box.pack_start(w, False, False, 0)
        d.show_all()
        if d.run() == Gtk.ResponseType.OK:
            self.s.update(gemini_key=key.get_text().strip(), gemini_voice=voice.get_child().get_text().strip() or "Kore",
                          talk_on_reminder=remind.get_active(), proactive_chats=pro.get_active())
            self.s.save()
            self.switches["proactive_chats"].set_active(self.s["proactive_chats"])
        d.destroy()

    def _check_update(self, force=False):
        def work():
            try:
                found = updater.check()
            except Exception as e:
                log(f"update check: {e}")
                found = None
            GLib.idle_add(self._update_found, found, force)
        threading.Thread(target=work, daemon=True).start()
        if not force:
            GLib.timeout_add_seconds(6 * 3600, lambda: (self._check_update(), False)[1])
        return False

    def _update_found(self, found, force):
        if not found:
            self.update_btn.set_label(f"v{VERSION} · up to date")
            return False
        tag, url, digest = found
        self.update_btn.set_label(f"Update to {tag[6:]}")
        if not updater.can_update():
            self.say(f"Notch {tag[6:]} is out — run install.sh again to update", 6)
            return False
        if force or (self.s["auto_update"] and system.idle_seconds() > 180 and not (self.media.current and self.media.current.playing)):
            self.say(f"Updating to {tag[6:]}…", 4)

            def work():
                if updater.install(url, digest):
                    GLib.idle_add(lambda: updater.restart(tag))
            threading.Thread(target=work, daemon=True).start()
        else:
            self.say(f"Update {tag[6:]} is ready — I'll install it when you're away", 5)
            GLib.timeout_add_seconds(600, lambda: (self._update_found(found, False), False)[1])
        return False

    def quit(self, *a):
        self.talk.stop("quit")
        self._stop_claps()
        self.player.stop()
        Gtk.main_quit()


def _mmss(s):
    s = int(max(0, s or 0))
    return f"{s // 3600}:{s % 3600 // 60:02d}:{s % 60:02d}" if s >= 3600 else f"{s // 60}:{s % 60:02d}"


def _s(d):
    return {"type": "STRING", "description": d}


def _fn(fn_name, desc, required=(), /, **params):
    # positional-only, so a parameter called "name" (open_app's) can't collide with these
    f = {"name": fn_name, "description": desc}
    if params:
        f["parameters"] = {"type": "OBJECT", "properties": params}
        if required:
            f["parameters"]["required"] = list(required)
    return f


TOOLS = [
    _fn("open_app", "Open or switch to an app on the user's Linux PC by its name (e.g. Firefox, VS Code, Spotify, Files, Terminal, Settings). If it's already open it comes to the front.",
        ["name"], name=_s("The app's name as the user said it")),
    _fn("close_app", "Close an open app or window by name, politely (it may ask to save).", ["name"], name=_s("The app or window name")),
    _fn("list_open_apps", "List the windows that are open on the PC right now."),
    _fn("play_music", "Search YouTube for a song, artist or playlist and play the top result in the background.", ["query"], query=_s("What to play, e.g. 'Die With A Smile Bruno Mars'")),
    _fn("media_control", "Control the music or video playing on the PC.", ["action"],
        action={"type": "STRING", "enum": ["play", "pause", "stop", "next", "previous"]}),
    _fn("now_playing", "Find out what song is playing right now."),
    _fn("web_search", "Search the web in the user's browser.", ["query"], query=_s("The search")),
    _fn("open_website", "Open a website in the browser.", ["url"], url=_s("The address, e.g. youtube.com")),
    _fn("add_todo", "Add a task to the user's to-do list. Include any time in the text so a reminder is set (e.g. 'call mom at 5pm', 'stretch in 20 minutes').", ["text"], text=_s("The task with its time, if any")),
    _fn("lock_pc", "Lock the PC. Only when the user clearly asks to lock it. The chat ends after your short reply."),
    _fn("privacy_screen", "Show or hide the privacy screen (a big clock that hides the screen; it doesn't lock it).", ["on"], on={"type": "BOOLEAN"}),
    _fn("write_file", "Write something the user asked for (letter, email, essay, notes, a list, code…) and save it as a file where they want. Compose the complete text yourself (# headings, - bullets, **bold**). Default folder: Documents/Notch.",
        ["content", "file_name"], content=_s("The full text"), file_name=_s("A short file name"),
        folder=_s("Desktop, Documents, Downloads, a sub-folder like 'Documents/Letters', or a full path"),
        format={"type": "STRING", "enum": ["docx", "txt", "md", "html", "csv", "json", "py"]},
        overwrite={"type": "BOOLEAN", "description": "Replace an existing file. Only if the user clearly asked."},
        open_after={"type": "BOOLEAN", "description": "Open it when saved (default true)"}),
    _fn("open_path", "Open a file or folder (e.g. something you saved).", ["path"], path=_s("Full path, or a folder like Documents")),
    _fn("remember_profile", "Save what you learn about the user: their name, the name they give you, their hobbies (or remove ones they no longer have).",
        user_name=_s("The user's name"), assistant_name=_s("The name the user gives you"),
        add_interests={"type": "ARRAY", "items": {"type": "STRING"}}, remove_interests={"type": "ARRAY", "items": {"type": "STRING"}}),
    _fn("remember_note", "Save something important to remember long-term to stay on track: a goal, plan, project, deadline, decision, or what you agreed to do next.", ["note"], note=_s("One short sentence")),
    _fn("forget_note", "Remove a saved note that is done or no longer true (or 'all').", ["match"], match=_s("Words from the note, or 'all'")),
    _fn("search_memory", "Search your older conversations with the user.", ["query"], query=_s("What to look for")),
    _fn("forget_memory", "Forget all earlier conversations and notes. Only when the user asks."),
    _fn("break_status", "How long until the next computer break, or how much of the current one is left."),
    _fn("take_break_now", "Start a computer break now, as long as the user wants; it can't be cancelled. Only when the user asks.", minutes={"type": "INTEGER", "description": "1-120"}),
    _fn("schedule_break", "Plan a break for later at a time the user chooses; once planned it can't be cancelled.", ["minutes"],
        at_time=_s("24-hour HH:mm"), in_minutes={"type": "INTEGER"}, minutes={"type": "INTEGER", "description": "Length 1-120"}),
    _fn("set_breaks", "Turn automatic breaks on/off or change how often and how long they are.",
        enabled={"type": "BOOLEAN"}, every_minutes={"type": "INTEGER"}, break_minutes={"type": "INTEGER"}),
]


def main():
    if "--version" in sys.argv:
        print(VERSION)
        return 0
    lock_path = DATA_DIR / "notch.lock"
    import fcntl
    lock = open(lock_path, "w")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)        # only one notch at a time
    except OSError:
        print("Notch is already running.")
        return 0
    log(f"notch {VERSION} starting")
    Island()
    Gtk.main()
    return 0
