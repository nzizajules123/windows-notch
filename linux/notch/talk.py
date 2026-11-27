from __future__ import annotations

"""Live voice conversation with Gemini (Live API, speech to speech) — the same notch you talk to on Windows.
The mic streams to Gemini; it answers out loud with captions; it can act on the PC through tools. While it speaks the
mic sends silence (half-duplex) so it never answers itself; with music on, only clear speech passes. Gemini closes each
connection after about ten minutes: the chat then carries on in a new connection, with the conversation remembered."""
import asyncio
import base64
import json
import math
import array
import threading
import time
import urllib.request

from .audio import Mic, Speaker
from .config import log

try:
    import websockets
except ImportError:          # install.sh installs python3-websockets
    websockets = None

MODELS = ["gemini-3.1-flash-live-preview", "gemini-2.5-flash-native-audio-latest", "gemini-2.5-flash-native-audio-preview-12-2025"]
URL = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"
GREETING = "(The user just opened a live voice chat with you. Greet them in a few words.)"


def _rms(pcm: bytes) -> float:
    a = array.array("h", pcm[: len(pcm) - len(pcm) % 2])
    return math.sqrt(sum(x * x for x in a) / max(1, len(a)))


class Talk:
    """state: "off" | "connecting" | "listening" | "speaking". Callbacks run on the talk thread — the UI marshals them."""

    def __init__(self, key_fn, voice_fn):
        self.key_fn, self.voice_fn = key_fn, voice_fn
        self.state = "off"
        self.on_state = lambda s, msg=None: None
        self.on_caption = lambda text: None
        self.on_exchange = lambda you, notch: None
        self.tools = []                       # function declarations
        self.handler = None                   # (name, args) -> (result, end_after_reply)
        self.music_playing = lambda: False
        self.keep_alive = False
        # QA: typed messages instead of the microphone (tests the AI and its actions without speaking)
        self.text_only = False
        self.script: list[str] = []
        self._loop = None
        self._ws = None
        self._stop = threading.Event()
        self._model = None
        self._search_blocked = False
        self._handle = None                   # session resumption handle
        self._end_after_reply = False

    @property
    def active(self):
        return self.state != "off"

    def _set(self, s, msg=None):
        self.state = s
        self.on_state(s, msg)

    def start(self, prompt: str, opening: str | None = None):
        if self.active or not self.key_fn():
            return
        if websockets is None:
            self._set("off", "Live talk needs python3-websockets (run install.sh again)")
            return
        self._stop.clear()
        self._handle = None
        self._end_after_reply = False
        self._set("connecting")
        threading.Thread(target=lambda: asyncio.run(self._run(prompt, opening)), daemon=True, name="talk").start()

    def stop(self, why=None):
        if not self.active:
            return
        log("live talk: stopped" + (f" ({why})" if why else ""))
        self._stop.set()
        if self._loop and self._ws:
            asyncio.run_coroutine_threadsafe(self._ws.close(), self._loop)

    def tell(self, text: str):
        """Says something to Gemini mid-chat (a reminder, a break event); it answers out loud."""
        if self._loop and self._ws:
            asyncio.run_coroutine_threadsafe(self._send(self._turn(text)), self._loop)

    # ------------------------------------------------------------------ internals
    @staticmethod
    def _turn(text):
        return {"clientContent": {"turns": [{"role": "user", "parts": [{"text": text}]}], "turnComplete": True}}

    async def _send(self, obj):
        try:
            await self._ws.send(json.dumps(obj))
        except Exception:
            pass

    def _pick_model(self, key):
        if self._model:
            return self._model
        try:
            req = urllib.request.Request("https://generativelanguage.googleapis.com/v1beta/models?pageSize=200", headers={"x-goog-api-key": key})
            with urllib.request.urlopen(req, timeout=8) as r:
                models = json.loads(r.read())["models"]
            live = {m["name"].removeprefix("models/") for m in models if "bidiGenerateContent" in m.get("supportedGenerationMethods", [])}
            self._model = next((m for m in MODELS if m in live), None) or next((m for m in live if "native-audio" in m), MODELS[0])
        except Exception:
            self._model = MODELS[0]
        return self._model

    def _setup(self, prompt, key, use_search):
        tools = ([{"googleSearch": {}}] if use_search else []) + [{"functionDeclarations": self.tools}]
        return {"setup": {
            "model": "models/" + self._pick_model(key),
            "generationConfig": {"responseModalities": ["AUDIO"],
                                 "speechConfig": {"voiceConfig": {"prebuiltVoiceConfig": {"voiceName": self.voice_fn()}}}},
            "systemInstruction": {"parts": [{"text": prompt}]},
            "inputAudioTranscription": {}, "outputAudioTranscription": {},
            "contextWindowCompression": {"slidingWindow": {}},
            "sessionResumption": {"handle": self._handle} if self._handle else {},
            "tools": tools}}

    @staticmethod
    def _connect(key):
        # websockets >= 13 calls it additional_headers; older versions (Ubuntu 22.04 / 24.04) extra_headers
        try:
            major = int(str(getattr(websockets, "__version__", "0")).split(".")[0])
        except ValueError:
            major = 0
        if major >= 13:
            return websockets.connect(URL, additional_headers={"x-goog-api-key": key}, max_size=None)
        return websockets.connect(URL, extra_headers={"x-goog-api-key": key}, max_size=None)

    async def _run(self, prompt, opening):
        self._loop = asyncio.get_running_loop()
        key = self.key_fn()
        speaker = Speaker(24000)
        error = None
        resumed = False
        try:
            for _ in range(12):            # a long chat: each connection lasts ~10 minutes
                use_search = not self._search_blocked
                outcome, reason = None, ""
                try:
                    async with self._connect(key) as ws:
                        self._ws = ws
                        await ws.send(json.dumps(self._setup(prompt, key, use_search)))
                        log(f"live talk: connected ({self._model})" + (" — carrying on the conversation" if resumed else ""))
                        outcome = await self._session(ws, None if resumed else opening, speaker, resumed)
                        reason = getattr(ws, "close_reason", "") or ""
                except websockets.exceptions.ConnectionClosed as e:
                    reason = str(e)
                if self._stop.is_set():
                    break
                # Google Search isn't allowed on this key (quota / plan): carry on without it
                if use_search and outcome != "heard" and any(w in reason.lower() for w in ("quota", "search", "ground")):
                    log("live talk: Google Search not available on this key — continuing without it")
                    self._search_blocked = True
                    continue
                if outcome == "go_away" and self._handle:
                    log("live talk: reconnecting (session continues)")
                    resumed = True
                    self._set("connecting")
                    continue
                if outcome == "go_away":
                    log("live talk: stopped (session time limit)")
                elif reason and outcome is None:
                    error = "Gemini: " + reason[:120]
                break
        except Exception as e:
            if not self._stop.is_set():
                log(f"live talk: {e}")
                error = "Couldn't reach Gemini — check the key and internet"
        finally:
            self._ws = None
            speaker.stop()
            self._set("off", error)

    async def _session(self, ws, opening, speaker, resumed):
        """Returns "go_away" (time limit: reconnect), "heard" (Gemini answered at least once) or None."""
        queue = asyncio.Queue()
        last_activity = [time.monotonic()]
        gate = {"until": 0.0, "floor": 800.0}
        model_line, user_line, turn_user = [], [], [""]
        outcome = None
        mic = None

        def on_block(pcm):
            now = time.monotonic()
            talking = speaker.playing or now < speaker.until + 0.35
            src = pcm
            if talking:
                src = bytes(len(pcm))
            elif self.music_playing():
                r = _rms(pcm)
                if r > max(1400, gate["floor"] * 2.2):
                    gate["until"] = now + 1.3
                else:
                    gate["floor"] += (r - gate["floor"]) * (0.2 if r < gate["floor"] else 0.05)
                if now >= gate["until"]:
                    src = bytes(len(pcm))
            if src is pcm and _rms(pcm) > 600:
                last_activity[0] = now
            self._loop.call_soon_threadsafe(queue.put_nowait, src)

        async def pump():
            while not self._stop.is_set():
                pcm = await queue.get()
                await self._send({"realtimeInput": {"audio": {"data": base64.b64encode(pcm).decode(), "mimeType": "audio/pcm;rate=16000"}}})
                limit = 15 * 60 if self.music_playing() else 75
                if not self.keep_alive and time.monotonic() - last_activity[0] > limit:
                    log("live talk: stopped (quiet for a while)")
                    self._stop.set()
                    await ws.close()
                    return
                if self.state == "speaking" and not speaker.playing:
                    self._set("listening")

        async def typed():
            # QA: nothing to stream; the next typed message goes out when an answer is complete
            while not self._stop.is_set():
                await asyncio.sleep(0.5)

        pump_task = None
        try:
            async for raw in ws:
                msg = json.loads(raw)
                if "setupComplete" in msg:
                    if self.text_only:
                        pump_task = asyncio.create_task(typed())
                    else:
                        mic = Mic(16000, on_block)
                        if not mic.start():
                            self._stop.set()
                            self._set("off", "No microphone tool found (install pulseaudio-utils or pipewire)")
                            return outcome
                        pump_task = asyncio.create_task(pump())
                    self._set("listening")
                    if not resumed:
                        first = opening or (self.script.pop(0) if self.script else GREETING)
                        if self.text_only:
                            log(f"live talk (typed): {first}")
                        await self._send(self._turn(first))
                    continue
                ru = msg.get("sessionResumptionUpdate")
                if ru is not None:
                    if ru.get("newHandle") and ru.get("resumable", True) is not False:
                        self._handle = ru["newHandle"]
                    continue
                if "goAway" in msg:
                    return "go_away"
                if "toolCall" in msg:
                    asyncio.create_task(self._tools(msg["toolCall"].get("functionCalls", []), last_activity))
                    continue
                sc = msg.get("serverContent")
                if not sc:
                    continue
                if sc.get("interrupted"):
                    speaker.clear()
                    model_line.clear()
                    self._set("listening")
                it = (sc.get("inputTranscription") or {}).get("text")
                if it:
                    if model_line:
                        model_line.clear(); user_line.clear()
                    user_line.append(it)
                    last_activity[0] = time.monotonic()
                    self.on_caption("🗣  " + "".join(user_line).strip()[-70:])
                for part in (sc.get("modelTurn") or {}).get("parts", []):
                    data = (part.get("inlineData") or {}).get("data")
                    if data:
                        if not self.text_only:
                            speaker.write(base64.b64decode(data))
                        outcome = "heard"
                        last_activity[0] = time.monotonic()
                        if self.state != "speaking":
                            self._set("speaking")
                ot = (sc.get("outputTranscription") or {}).get("text")
                if ot:
                    if not model_line:
                        turn_user[0] = "".join(user_line).strip(); user_line.clear()
                    model_line.append(ot)
                    self.on_caption("✨  " + "".join(model_line).strip()[-70:])
                if sc.get("turnComplete"):
                    said = "".join(model_line).strip()
                    if self.text_only:
                        log(f"live talk (answer): {said}")
                        self._set("listening")
                    if said and turn_user[0]:
                        self.on_exchange(turn_user[0], said)
                    turn_user[0] = ""
                    model_line.clear()
                    if self._end_after_reply:
                        # e.g. "lock the PC": say the short reply, then end the chat
                        while speaker.playing:
                            await asyncio.sleep(0.1)
                        log("live talk: stopped (action done)")
                        self._stop.set()
                        await ws.close()
                        break
                    if self.text_only:
                        if self.script:
                            nxt = self.script.pop(0)
                            log(f"live talk (typed): {nxt}")
                            await self._send(self._turn(nxt))
                        else:
                            self._stop.set()
                            await ws.close()
                            break
        finally:
            if pump_task:
                pump_task.cancel()
            if mic:
                mic.stop()
        return outcome

    async def _tools(self, calls, last_activity):
        out = []
        for c in calls:
            name, args = c.get("name", ""), c.get("args") or {}
            try:
                if self.handler:
                    result, end = await asyncio.get_running_loop().run_in_executor(None, self.handler, name, args)
                    if end:
                        self._end_after_reply = True
                else:
                    result = "Not available."
            except Exception as e:
                result = f"That didn't work: {e}"
            log(f"live talk: action {name} → {result}")
            out.append({"id": c.get("id"), "name": name, "response": {"result": result}})
        last_activity[0] = time.monotonic()
        await self._send({"toolResponse": {"functionResponses": out}})
