from __future__ import annotations

"""What's playing, from any Linux player that speaks MPRIS (Spotify, Firefox, Chrome, VLC, Rhythmbox, mpv…)."""
import time

from gi.repository import Gio, GLib

PLAYER = "org.mpris.MediaPlayer2.Player"
PATH = "/org/mpris/MediaPlayer2"


class Track:
    def __init__(self, bus_name, identity, meta, status, position_us):
        self.bus_name = bus_name
        self.app = identity or bus_name.split(".")[-1].capitalize()
        self.title = _first(meta.get("xesam:title")) or ""
        self.artist = _first(meta.get("xesam:artist")) or ""
        self.art_url = meta.get("mpris:artUrl") or ""
        self.length = (meta.get("mpris:length") or 0) / 1e6
        self.playing = status == "Playing"
        self._pos = position_us / 1e6
        self._at = time.monotonic()

    @property
    def position(self) -> float:
        p = self._pos + (time.monotonic() - self._at if self.playing else 0)
        return min(p, self.length) if self.length else p

    @property
    def key(self):
        return f"{self.bus_name}|{self.title}|{self.artist}"


def _first(v):
    if isinstance(v, (list, tuple)):
        return v[0] if v else ""
    return v


class Media:
    def __init__(self):
        self.bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        self.current: Track | None = None

    def _call(self, name, iface, method, args=None, reply=None, timeout=500):
        return self.bus.call_sync(name, PATH, iface, method, args, reply, Gio.DBusCallFlags.NONE, timeout, None)

    def _players(self):
        try:
            names = self.bus.call_sync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
                                       "ListNames", None, GLib.VariantType("(as)"), Gio.DBusCallFlags.NONE, 500, None).unpack()[0]
        except GLib.Error:
            return []
        return [n for n in names if n.startswith("org.mpris.MediaPlayer2.")]

    def _read(self, name):
        try:
            props = self._call(name, "org.freedesktop.DBus.Properties", "GetAll", GLib.Variant("(s)", (PLAYER,)),
                               GLib.VariantType("(a{sv})")).unpack()[0]
            try:
                ident = self._call(name, "org.freedesktop.DBus.Properties", "Get",
                                   GLib.Variant("(ss)", ("org.mpris.MediaPlayer2", "Identity")), GLib.VariantType("(v)")).unpack()[0]
            except GLib.Error:
                ident = ""
            return Track(name, ident, props.get("Metadata") or {}, props.get("PlaybackStatus", ""), props.get("Position") or 0)
        except GLib.Error:
            return None

    def refresh(self) -> Track | None:
        """Picks the player that's playing (else the one we showed, else any)."""
        tracks = [t for t in (self._read(n) for n in self._players()) if t and t.title]
        playing = [t for t in tracks if t.playing]
        keep = [t for t in tracks if self.current and t.bus_name == self.current.bus_name]
        self.current = (playing or keep or tracks or [None])[0]
        return self.current

    def _player(self, method):
        if self.current:
            try:
                self._call(self.current.bus_name, PLAYER, method)
            except GLib.Error:
                pass

    def play_pause(self):
        self._player("PlayPause")

    def play(self):
        self._player("Play")

    def pause(self):
        self._player("Pause")

    def next(self):
        self._player("Next")

    def previous(self):
        self._player("Previous")
