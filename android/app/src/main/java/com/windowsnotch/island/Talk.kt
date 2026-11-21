package com.windowsnotch.island

import android.accessibilityservice.AccessibilityService
import android.annotation.SuppressLint
import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import android.util.Base64
import androidx.core.content.ContextCompat
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONArray
import org.json.JSONObject
import java.util.Calendar
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import kotlin.math.max
import kotlin.math.sqrt

/**
 * Starts / stops a live chat. A see-through activity is used because Android only lets an app use the
 * microphone in the background when it was started while the app was in front; it closes straight away.
 */
class TalkActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val go = {
            if (TalkService.running) stopService(Intent(this, TalkService::class.java))
            else ContextCompat.startForegroundService(this, Intent(this, TalkService::class.java))
            finish()
        }
        if (ContextCompat.checkSelfPermission(this, android.Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) go()
        else requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), 1)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED)
            ContextCompat.startForegroundService(this, Intent(this, TalkService::class.java))
        else Island.hint("Talking needs the microphone 🎙")
        finish()
    }

    companion object {
        /** Toggle the live chat (from the island's mic button, 4 claps or the setup screen). */
        fun toggle(ctx: Context) {
            if (Talk.key(ctx).isBlank()) {
                Island.hint("Add a Gemini key in the Notch Island app first 🔑")
                ctx.startActivity(Intent(ctx, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return
            }
            runCatching { ctx.startActivity(Intent(ctx, TalkActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
        }
    }
}

/** Settings + the memory of earlier chats (kept in this app's private storage only). */
object Talk {
    private const val PREFS = "island"
    fun key(c: Context) = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString("gemini_key", "") ?: ""
    fun setKey(c: Context, k: String) = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString("gemini_key", k.trim()).apply()
    fun voiceName(c: Context) = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString("gemini_voice", "Kore") ?: "Kore"

    fun remember(c: Context, you: String, notch: String) {
        val p = c.getSharedPreferences("talk_memory", Context.MODE_PRIVATE)
        val arr = runCatching { JSONArray(p.getString("items", "[]")) }.getOrDefault(JSONArray())
        arr.put(JSONObject().put("t", System.currentTimeMillis()).put("you", you.take(400)).put("notch", notch.take(600)))
        while (arr.length() > 60) arr.remove(0)
        p.edit().putString("items", arr.toString()).apply()
    }

    fun forget(c: Context) = c.getSharedPreferences("talk_memory", Context.MODE_PRIVATE).edit().clear().apply()

    /** Earlier exchanges as text for Gemini, newest last. */
    fun memory(c: Context, maxChars: Int = 4000): String {
        val arr = runCatching { JSONArray(c.getSharedPreferences("talk_memory", Context.MODE_PRIVATE).getString("items", "[]")) }.getOrDefault(JSONArray())
        val fmt = java.text.SimpleDateFormat("EEE d MMM HH:mm", java.util.Locale.getDefault())
        val lines = ArrayList<String>(); var total = 0
        for (i in arr.length() - 1 downTo 0) {
            val o = arr.getJSONObject(i)
            val line = "[${fmt.format(java.util.Date(o.optLong("t")))}] User: ${o.optString("you")}\n  Notch: ${o.optString("notch")}"
            if (total + line.length > maxChars) break
            lines.add(line); total += line.length
        }
        return lines.reversed().joinToString("\n")
    }
}

/**
 * A live voice conversation with Gemini (Live API, speech to speech) — the same notch you talk to on Windows.
 * The mic streams to Gemini; its spoken answer plays back with captions in the island. Gemini can act on the phone
 * (open apps, play music, control media, search, to-dos, lock). While it speaks the mic is muted so it never answers
 * itself; with music on, only clear speech passes. Ends after a while of silence. Nothing is recorded.
 */
class TalkService : Service() {

    companion object {
        @Volatile var running = false
            private set
        private const val CHANNEL = "talk"
        private val MODELS = listOf("gemini-3.1-flash-live-preview", "gemini-2.5-flash-native-audio-latest", "gemini-2.5-flash-native-audio-preview-12-2025")
        private const val MIC_RATE = 16000
        private const val OUT_RATE = 24000
    }

    private val main = Handler(Looper.getMainLooper())
    private val http = OkHttpClient.Builder().readTimeout(0, TimeUnit.MILLISECONDS).pingInterval(20, TimeUnit.SECONDS).build()
    private var ws: WebSocket? = null
    @Volatile private var stop = false
    private var record: AudioRecord? = null
    private var track: AudioTrack? = null
    private var focus: AudioFocusRequest? = null
    @Volatile private var lastActivity = SystemClock.elapsedRealtime()
    @Volatile private var speakingUntil = 0L
    private val outQueue = java.util.concurrent.LinkedBlockingQueue<ByteArray>()
    private var model: String? = null
    private var resumeHandle: String? = null
    private val modelLine = StringBuilder()
    private val userLine = StringBuilder()
    private var turnUser = ""
    private var musicFloor = 800.0
    private var gateUntil = 0L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (running) return START_NOT_STICKY
        try {
            if (Build.VERSION.SDK_INT >= 29) startForeground(8, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
            else startForeground(8, notification())
        } catch (e: Exception) {
            Island.hint("Couldn't start the microphone"); stopSelf(); return START_NOT_STICKY
        }
        running = true
        setState("connecting", null)
        thread(name = "talk-connect") { connect(null) }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        stop = true
        running = false
        runCatching { ws?.close(1000, "bye") }
        ws = null
        releaseAudio()
        setState("off", null)
        super.onDestroy()
    }

    private fun end(why: String? = null) {
        if (why != null) main.post { Island.hint(why) }
        main.post { stopSelf() }
    }

    private fun setState(s: String, line: String?) = main.post {
        Island.talkState = s
        if (line != null || s == "off" || s == "connecting") Island.talkLine = line
    }

    private fun notification(): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26 && nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Live talk", NotificationManager.IMPORTANCE_LOW).apply { setShowBadge(false) })
        val stopIt = PendingIntent.getActivity(this, 2, Intent(this, TalkActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val b = if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, CHANNEL) else @Suppress("DEPRECATION") Notification.Builder(this)
        return b.setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle("Talking with Notch 🎙")
            .setContentText("Tap to end the conversation")
            .setContentIntent(stopIt)
            .setOngoing(true)
            .build()
    }

    // ------------------------------------------------------------------ connection
    private fun connect(handle: String?) {
        val key = Talk.key(this)
        val m = model ?: pickModel(key).also { model = it }
        val req = Request.Builder()
            .url("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent")
            .header("x-goog-api-key", key)
            .build()
        ws = http.newWebSocket(req, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                val setup = JSONObject()
                    .put("model", "models/$m")
                    .put("generationConfig", JSONObject()
                        .put("responseModalities", JSONArray().put("AUDIO"))
                        .put("speechConfig", JSONObject().put("voiceConfig", JSONObject().put("prebuiltVoiceConfig", JSONObject().put("voiceName", Talk.voiceName(this@TalkService))))))
                    .put("systemInstruction", JSONObject().put("parts", JSONArray().put(JSONObject().put("text", prompt()))))
                    .put("inputAudioTranscription", JSONObject())
                    .put("outputAudioTranscription", JSONObject())
                    .put("contextWindowCompression", JSONObject().put("slidingWindow", JSONObject()))
                    .put("sessionResumption", if (handle != null) JSONObject().put("handle", handle) else JSONObject())
                    .put("tools", JSONArray().put(JSONObject().put("functionDeclarations", tools())))
                webSocket.send(JSONObject().put("setup", setup).toString())
            }
            override fun onMessage(webSocket: WebSocket, text: String) {
                runCatching { handle(JSONObject(text), resuming = handle != null) }.onFailure { android.util.Log.w("talk", it) }
            }
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                if (!stop && webSocket == ws) end(if (code == 1000 || reason.isBlank()) null else "Gemini: $reason")
            }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (stop || webSocket != ws) return
                val bad = response?.code == 401 || response?.code == 403 || response?.code == 400
                end(if (bad) "Gemini didn't accept the key 🔑" else "Couldn't reach Gemini — check the internet")
            }
        })
    }

    private fun pickModel(key: String): String = runCatching {
        val c = java.net.URL("https://generativelanguage.googleapis.com/v1beta/models?pageSize=200").openConnection() as java.net.HttpURLConnection
        c.setRequestProperty("x-goog-api-key", key); c.connectTimeout = 8000; c.readTimeout = 8000
        val arr = JSONObject(c.inputStream.bufferedReader().readText()).getJSONArray("models")
        val live = HashSet<String>()
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val g = o.optJSONArray("supportedGenerationMethods") ?: continue
            if ((0 until g.length()).any { g.getString(it) == "bidiGenerateContent" }) live.add(o.getString("name").removePrefix("models/"))
        }
        MODELS.firstOrNull { it in live } ?: live.firstOrNull { "native-audio" in it } ?: MODELS[0]
    }.getOrDefault(MODELS[0])

    private fun send(o: JSONObject) { ws?.send(o.toString()) }

    private fun handle(o: JSONObject, resuming: Boolean) {
        if (o.has("setupComplete")) {
            lastActivity = SystemClock.elapsedRealtime()
            if (record == null) startAudio()
            setState("listening", null)
            if (!resuming) send(userTurn("(The user just opened a live voice chat with you on their phone. Greet them in a few words.)"))
            return
        }
        o.optJSONObject("sessionResumptionUpdate")?.let { u ->
            if (u.optBoolean("resumable", true) && u.has("newHandle")) resumeHandle = u.getString("newHandle")
            return
        }
        if (o.has("goAway")) {
            val h = resumeHandle ?: run { end(); return }
            val old = ws; ws = null
            runCatching { old?.close(1000, "resume") }
            thread(name = "talk-resume") { connect(h) }
            return
        }
        o.optJSONObject("toolCall")?.optJSONArray("functionCalls")?.let { calls -> runTools(calls); return }
        val sc = o.optJSONObject("serverContent") ?: return

        if (sc.optBoolean("interrupted")) {
            outQueue.clear()
            track?.let { runCatching { it.pause(); it.flush(); it.play() } }
            speakingUntil = 0
            modelLine.setLength(0)
            setState("listening", null)
        }
        sc.optJSONObject("inputTranscription")?.optString("text")?.takeIf { it.isNotEmpty() }?.let {
            if (modelLine.isNotEmpty()) { modelLine.setLength(0); userLine.setLength(0) }
            userLine.append(it)
            lastActivity = SystemClock.elapsedRealtime()
            setState(Island.talkState, "🗣  " + tail(userLine.toString().trim()))
        }
        sc.optJSONObject("modelTurn")?.optJSONArray("parts")?.let { parts ->
            for (i in 0 until parts.length()) {
                val data = parts.getJSONObject(i).optJSONObject("inlineData")?.optString("data") ?: continue
                play(Base64.decode(data, Base64.DEFAULT))
            }
        }
        sc.optJSONObject("outputTranscription")?.optString("text")?.takeIf { it.isNotEmpty() }?.let {
            if (modelLine.isEmpty()) { turnUser = userLine.toString().trim(); userLine.setLength(0) }
            modelLine.append(it)
            setState("speaking", "✨  " + tail(modelLine.toString().trim()))
        }
        if (sc.optBoolean("turnComplete")) {
            val said = modelLine.toString().trim()
            if (said.isNotEmpty() && turnUser.isNotEmpty()) Talk.remember(this, turnUser, said)
            turnUser = ""
            modelLine.setLength(0)
        }
    }

    private fun userTurn(text: String) = JSONObject().put("clientContent", JSONObject()
        .put("turns", JSONArray().put(JSONObject().put("role", "user").put("parts", JSONArray().put(JSONObject().put("text", text)))))
        .put("turnComplete", true))

    private fun tail(t: String) = if (t.length <= 60) t else "…" + t.takeLast(58).trimStart()

    // ------------------------------------------------------------------ audio
    @SuppressLint("MissingPermission")
    private fun startAudio() {
        val am = getSystemService(AudioManager::class.java)
        // other apps' music ducks while we talk (most players lower their volume)
        if (Build.VERSION.SDK_INT >= 26) {
            focus = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_MAY_DUCK)
                .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ASSISTANT).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
                .build().also { am.requestAudioFocus(it) }
        }
        val outMin = AudioTrack.getMinBufferSize(OUT_RATE, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
        track = AudioTrack.Builder()
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ASSISTANT).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
            .setAudioFormat(AudioFormat.Builder().setSampleRate(OUT_RATE).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(max(outMin, OUT_RATE)).setTransferMode(AudioTrack.MODE_STREAM).build()
            .also { it.play() }
        // one writer keeps the chunks in order
        thread(name = "talk-out") {
            while (!stop) {
                val pcm = outQueue.poll(200, TimeUnit.MILLISECONDS) ?: continue
                runCatching { track?.write(pcm, 0, pcm.size) }
            }
        }

        val inMin = AudioRecord.getMinBufferSize(MIC_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val rec = runCatching {
            AudioRecord(MediaRecorder.AudioSource.VOICE_COMMUNICATION, MIC_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, max(inMin, 6400) * 2)
        }.getOrNull()
        if (rec == null || rec.state != AudioRecord.STATE_INITIALIZED) { rec?.release(); end("Couldn't open the microphone"); return }
        if (AcousticEchoCanceler.isAvailable()) runCatching { AcousticEchoCanceler.create(rec.audioSessionId)?.enabled = true }
        record = rec
        rec.startRecording()
        thread(name = "talk-mic") {
            val buf = ShortArray(1600)   // 100 ms
            val silence = ShortArray(1600)
            while (!stop) {
                val n = rec.read(buf, 0, buf.size)
                if (n <= 0) { Thread.sleep(20); continue }
                val now = SystemClock.elapsedRealtime()
                val talking = now < speakingUntil + 350
                val music = Island.media?.playing == true
                var src = if (talking) silence else buf
                if (!talking && music) {
                    val r = rms(buf, n)
                    if (r > max(1400.0, musicFloor * 2.2)) gateUntil = now + 1300
                    else musicFloor += (r - musicFloor) * (if (r < musicFloor) 0.2 else 0.05)
                    if (now >= gateUntil) src = silence
                }
                val bytes = ByteArray(n * 2)
                for (i in 0 until n) { val s = src[i].toInt(); bytes[2 * i] = (s and 0xFF).toByte(); bytes[2 * i + 1] = (s shr 8 and 0xFF).toByte() }
                send(JSONObject().put("realtimeInput", JSONObject().put("audio", JSONObject()
                    .put("data", Base64.encodeToString(bytes, Base64.NO_WRAP)).put("mimeType", "audio/pcm;rate=16000"))))
                if (!talking && src !== silence && rms(buf, n) > 600) lastActivity = now
                val limit = if (music) 15 * 60_000L else 75_000L
                if (now - lastActivity > limit) { end("Talk ended — quiet for a while"); break }
                if (Island.talkState == "speaking" && now > speakingUntil + 400) setState("listening", null)
            }
        }
    }

    private fun play(pcm: ByteArray) {
        val t = track ?: return
        lastActivity = SystemClock.elapsedRealtime()
        val ms = pcm.size / 2 * 1000L / OUT_RATE
        val now = SystemClock.elapsedRealtime()
        speakingUntil = max(speakingUntil, now) + ms
        outQueue.offer(pcm)
    }

    private fun rms(b: ShortArray, n: Int): Double { var s = 0.0; for (i in 0 until n) s += b[i].toDouble() * b[i]; return sqrt(s / max(1, n)) }

    private fun releaseAudio() {
        runCatching { record?.stop() }; record?.release(); record = null
        runCatching { track?.stop() }; track?.release(); track = null
        if (Build.VERSION.SDK_INT >= 26) focus?.let { getSystemService(AudioManager::class.java).abandonAudioFocusRequest(it) }
    }

    // ------------------------------------------------------------------ who the notch is + what it can do
    private fun prompt(): String {
        val now = java.text.SimpleDateFormat("EEEE d MMMM yyyy, HH:mm", java.util.Locale.getDefault()).format(java.util.Date())
        val m = Island.media
        val media = if (m != null) " Right now \"${m.title}\" by ${m.artist} is ${if (m.playing) "playing" else "paused"}." else ""
        val mem = Talk.memory(this)
        return "You are Notch, a cute little island with living eyes that sits at the top of the user's phone screen, around the camera. " +
            "You are their friendly companion: warm, playful, encouraging and a little funny. Speak naturally like a friend in a voice chat; " +
            "keep answers short (one to three sentences) unless asked for more. Reply in the language the user speaks. " +
            "You can act on the phone with your tools: open apps, search and play music, pause / skip music, search the web, open websites, " +
            "add to-dos with reminders, go to the home screen and lock the phone. When the user asks, call the tool straight away, then say in a few words what you did. " +
            "Android doesn't let you close other apps; offer to go to the home screen instead. You can't restart or turn off the phone. " +
            "Music may be playing quietly in the background: ignore song lyrics and only answer when the user speaks to you. " +
            "It is $now. The battery is at ${Island.battery}%${if (Island.charging) " and charging" else ""}.$media" +
            if (mem.isNotEmpty()) "\n\nYour earlier conversations with the user (oldest first). Use them to remember what you talked about; don't list them unless asked:\n$mem" else ""
    }

    private fun str(d: String) = JSONObject().put("type", "STRING").put("description", d)
    private fun fn(name: String, desc: String, vararg params: Pair<String, JSONObject>): JSONObject {
        val o = JSONObject().put("name", name).put("description", desc)
        if (params.isNotEmpty()) {
            val props = JSONObject(); val req = JSONArray()
            params.forEach { (k, v) -> props.put(k, v); req.put(k) }
            o.put("parameters", JSONObject().put("type", "OBJECT").put("properties", props).put("required", req))
        }
        return o
    }

    private fun tools() = JSONArray()
        .put(fn("open_app", "Open an app on the phone by its name (e.g. WhatsApp, YouTube, Camera, Spotify, Settings, Chrome).", "name" to str("The app's name as the user said it")))
        .put(fn("play_music", "Search YouTube for a song, artist or playlist and play the top result in the island's music player.", "query" to str("What to play")))
        .put(fn("media_control", "Control the music or video playing on the phone.", "action" to JSONObject().put("type", "STRING")
            .put("enum", JSONArray().put("play").put("pause").put("stop").put("next").put("previous"))))
        .put(fn("now_playing", "Find out what song is playing right now."))
        .put(fn("web_search", "Search the web in the browser.", "query" to str("The search")))
        .put(fn("open_website", "Open a website in the browser.", "url" to str("The address, e.g. youtube.com")))
        .put(fn("add_todo", "Add a task to the island's to-do list. Include any time in the text so a reminder is set (e.g. 'call mom at 5pm').", "text" to str("The task with its time, if any")))
        .put(fn("go_home", "Go to the home screen (closes what's in front)."))
        .put(fn("lock_phone", "Lock the phone. Only when the user clearly asks to lock it."))
        .put(fn("forget_memory", "Forget the earlier conversations the notch remembers. Only when the user asks."))

    private fun runTools(calls: JSONArray) {
        main.post {
            val out = JSONArray()
            for (i in 0 until calls.length()) {
                val c = calls.getJSONObject(i)
                val name = c.optString("name"); val args = c.optJSONObject("args") ?: JSONObject()
                val result = runCatching { act(name, args) }.getOrElse { "That didn't work: ${it.message}" }
                out.put(JSONObject().put("id", c.opt("id")).put("name", name).put("response", JSONObject().put("result", result)))
            }
            lastActivity = SystemClock.elapsedRealtime()
            send(JSONObject().put("toolResponse", JSONObject().put("functionResponses", out)))
        }
    }

    /** Does what Gemini asked (main thread); the text goes back so it can tell you what happened. */
    private fun act(name: String, a: JSONObject): String {
        val island = IslandService.instance
        val starter: Context = island ?: this
        fun open(i: Intent) = starter.startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        return when (name) {
            "open_app" -> {
                val want = a.optString("name").lowercase().replace(Regex("\\b(the|app)\\b"), " ").trim().replace(Regex("\\s+"), " ")
                val pm = packageManager
                val apps = pm.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), 0)
                    .map { it to it.loadLabel(pm).toString() }
                val pick = apps.firstOrNull { it.second.equals(want, true) }
                    ?: apps.filter { it.second.lowercase().startsWith(want) }.minByOrNull { it.second.length }
                    ?: apps.filter { want.length >= 3 && it.second.lowercase().contains(want) }.minByOrNull { it.second.length }
                    ?: apps.firstOrNull { want.length >= 4 && want.contains(it.second.lowercase()) }
                if (pick == null) "No app called \"${a.optString("name")}\" is on this phone."
                else {
                    open(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER).setClassName(pick.first.activityInfo.packageName, pick.first.activityInfo.name))
                    Island.react(EyeMood.Happy, 1000)
                    "Opened ${pick.second}."
                }
            }
            "play_music" -> {
                val q = a.optString("query").trim()
                if (q.isEmpty()) "Nothing to search for." else {
                    thread(name = "talk-yt") {
                        val list = NotchPlayer.search(q)
                        main.post { if (list.isNotEmpty()) NotchPlayer.play(list, 0) else Island.hint("No songs found for \"$q\"") }
                    }
                    "Searching YouTube and starting \"$q\" in the island's player. It plays quietly while you talk."
                }
            }
            "media_control" -> {
                val action = a.optString("action")
                val m = Island.media ?: return "Nothing is playing or paused right now."
                val own = Island.playerMedia != null && Island.media == Island.playerMedia
                val tc = Island.controller?.transportControls
                when (action) {
                    "next" -> { if (own) NotchPlayer.next() else tc?.skipToNext(); "Skipped to the next track." }
                    "previous" -> { if (own) NotchPlayer.previous() else tc?.skipToPrevious(); "Went back." }
                    "pause", "stop" -> { if (m.playing) { if (own) NotchPlayer.playPause() else tc?.pause() }; "Paused." }
                    else -> { if (!m.playing) { if (own) NotchPlayer.playPause() else tc?.play() }; "Playing \"${m.title}\"." }
                }
            }
            "now_playing" -> Island.media?.let { "\"${it.title}\" by ${it.artist} is ${if (it.playing) "playing" else "paused"}." } ?: "Nothing is playing."
            "web_search" -> { open(Intent(Intent.ACTION_VIEW, Uri.parse("https://www.google.com/search?q=" + Uri.encode(a.optString("query"))))); "Searched the web for \"${a.optString("query")}\"." }
            "open_website" -> {
                var u = a.optString("url").trim().replace(" ", "")
                if (!u.startsWith("http://") && !u.startsWith("https://")) u = "https://$u"
                val uri = Uri.parse(u)
                if (uri.host.isNullOrBlank()) "That doesn't look like a website address." else { open(Intent(Intent.ACTION_VIEW, uri)); "Opened ${uri.host}." }
            }
            "add_todo" -> {
                val (text, due) = Todo.parse(a.optString("text"))
                if (text.isBlank()) "The task was empty." else {
                    Todo.add(text, due)
                    Island.react(EyeMood.Happy, 1000)
                    if (due != null) {
                        val c = Calendar.getInstance().apply { timeInMillis = due }
                        "Added \"$text\" with a reminder at %02d:%02d.".format(c.get(Calendar.HOUR_OF_DAY), c.get(Calendar.MINUTE))
                    } else "Added \"$text\" to the to-do list."
                }
            }
            "go_home" -> { island?.performGlobalAction(AccessibilityService.GLOBAL_ACTION_HOME) ?: open(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)); "Went to the home screen." }
            "lock_phone" -> {
                if (island != null && Build.VERSION.SDK_INT >= 28) { main.postDelayed({ island.performGlobalAction(AccessibilityService.GLOBAL_ACTION_LOCK_SCREEN); stopSelf() }, 1800); "Locking the phone." }
                else "Locking needs the island turned on (Android 9 or newer)."
            }
            "forget_memory" -> { Talk.forget(this); "Forgot all earlier conversations." }
            else -> "Unknown action."
        }
    }
}
