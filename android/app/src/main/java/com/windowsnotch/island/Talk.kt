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
    private var opening: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        opening = intent?.getStringExtra("opening")
        val go = {
            when {
                TalkService.running && opening != null -> TalkService.tell(opening!!)   // already talking: just bring it up
                TalkService.running -> stopService(Intent(this, TalkService::class.java))
                else -> ContextCompat.startForegroundService(this, Intent(this, TalkService::class.java).putExtra("opening", opening))
            }
            finish()
        }
        if (ContextCompat.checkSelfPermission(this, android.Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) go()
        else requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), 1)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED)
            ContextCompat.startForegroundService(this, Intent(this, TalkService::class.java).putExtra("opening", opening))
        else Island.hint("Talking needs the microphone 🎙")
        finish()
    }

    companion object {
        /** Toggle the live chat (from the island's mic button, 4 claps or the setup screen). */
        /** [opening]: what the notch should start with (a due to-do, a break, news…) instead of a greeting. */
        fun toggle(ctx: Context, opening: String? = null) {
            if (Talk.key(ctx).isBlank()) {
                Island.hint("Add a Gemini key in the Notch Island app first 🔑")
                ctx.startActivity(Intent(ctx, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return
            }
            runCatching { ctx.startActivity(Intent(ctx, TalkActivity::class.java).putExtra("opening", opening).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
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
        while (arr.length() > 300) arr.remove(0)
        p.edit().putString("items", arr.toString()).apply()
    }

    fun forget(c: Context) = c.getSharedPreferences("talk_memory", Context.MODE_PRIVATE).edit().clear().apply()   // chats and notes

    // ---- who you are to the island: your name, the name you gave it, your hobbies
    private fun prof(c: Context) = c.getSharedPreferences("profile", Context.MODE_PRIVATE)
    fun userName(c: Context) = prof(c).getString("user", "") ?: ""
    fun assistantName(c: Context) = prof(c).getString("assistant", "") ?: ""
    fun interests(c: Context): List<String> = runCatching { JSONArray(prof(c).getString("interests", "[]")).let { a -> (0 until a.length()).map { a.getString(it) } } }.getOrDefault(emptyList())
    fun saveProfile(c: Context, user: String?, assistant: String?, add: List<String>, remove: List<String>): String {
        val saved = ArrayList<String>()
        val e = prof(c).edit()
        if (!user.isNullOrBlank()) { e.putString("user", user.trim()); saved.add("user's name: ${user.trim()}") }
        if (!assistant.isNullOrBlank()) { e.putString("assistant", assistant.trim()); saved.add("your name: ${assistant.trim()}") }
        val list = interests(c).toMutableList()
        add.filter { it.isNotBlank() && list.none { x -> x.equals(it, true) } }.forEach { list.add(it.trim()); saved.add("interest: ${it.trim()}") }
        remove.forEach { r -> if (list.removeAll { it.equals(r, true) }) saved.add("forgot interest: $r") }
        e.putString("interests", JSONArray(list).toString()).apply()
        return if (saved.isEmpty()) "Nothing new to save." else "Saved — " + saved.joinToString("; ") + "."
    }

    /** What Gemini should know about you at the start of every chat (same as on Windows). */
    fun profilePrompt(c: Context): String {
        val u = userName(c); val a = assistantName(c); val i = interests(c)
        val parts = ArrayList<String>()
        if (a.isNotBlank()) parts.add("Your name is $a — the user chose it for you, so answer to it happily.")
        if (u.isNotBlank()) parts.add("The user's name is $u. Call them by name now and then (naturally, not in every sentence).")
        if (i.isNotEmpty()) parts.add("They like: ${i.joinToString(", ")}. Bring these up when it fits.")
        if (u.isBlank() || a.isBlank() || i.isEmpty())
            parts.add("You don't know everything about them yet. Early in this chat, warmly " +
                (if (u.isBlank()) "ask their name, " else "") + (if (a.isBlank()) "ask them to give you a name (you'd love one), " else "") +
                (if (i.isEmpty()) "and ask what they like doing — their hobbies, " else "") +
                "one question at a time, and save each answer with remember_profile straight away.")
        parts.add("Whenever they mention a new hobby or interest, or want to change a name, save it with remember_profile.")
        return parts.joinToString(" ")
    }

    fun flag(c: Context, key: String, def: Boolean) = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(key, def)
    fun setFlag(c: Context, key: String, on: Boolean) = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(key, on).apply()

    /** Earlier exchanges as text for Gemini, newest last. */
    // ---- long-term notes (goals, plans, deadlines) and a search over older chats
    private fun notesArr(c: Context) = runCatching { JSONArray(c.getSharedPreferences("talk_memory", Context.MODE_PRIVATE).getString("notes", "[]")) }.getOrDefault(JSONArray())
    fun addNote(c: Context, text: String): String {
        val t = text.trim().take(300); if (t.isEmpty()) return "Nothing to note."
        val arr = notesArr(c)
        if ((0 until arr.length()).any { arr.getJSONObject(it).optString("text").equals(t, true) }) return "Already noted."
        arr.put(JSONObject().put("t", System.currentTimeMillis()).put("text", t))
        while (arr.length() > 80) arr.remove(0)
        c.getSharedPreferences("talk_memory", Context.MODE_PRIVATE).edit().putString("notes", arr.toString()).apply()
        return "Noted."
    }
    fun removeNotes(c: Context, match: String): Int {
        val arr = notesArr(c); val keep = JSONArray(); var n = 0
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            if (match.trim().equals("all", true) || o.optString("text").contains(match.trim(), true)) n++ else keep.put(o)
        }
        c.getSharedPreferences("talk_memory", Context.MODE_PRIVATE).edit().putString("notes", keep.toString()).apply()
        return n
    }
    fun notesText(c: Context): String {
        val arr = notesArr(c); val fmt = java.text.SimpleDateFormat("d MMM", java.util.Locale.getDefault())
        return (0 until arr.length()).joinToString("\n") { val o = arr.getJSONObject(it); "- (${fmt.format(java.util.Date(o.optLong("t")))}) ${o.optString("text")}" }
    }
    fun searchMemory(c: Context, query: String, max: Int = 8): String {
        val words = query.lowercase().split(' ', ',', '.', '?').filter { it.length > 2 }
        if (words.isEmpty()) return ""
        val arr = runCatching { JSONArray(c.getSharedPreferences("talk_memory", Context.MODE_PRIVATE).getString("items", "[]")) }.getOrDefault(JSONArray())
        val fmt = java.text.SimpleDateFormat("EEE d MMM HH:mm", java.util.Locale.getDefault())
        return (0 until arr.length()).map { arr.getJSONObject(it) }
            .map { o -> o to words.count { w -> o.optString("you").contains(w, true) || o.optString("notch").contains(w, true) } }
            .filter { it.second > 0 }
            .sortedWith(compareByDescending<Pair<JSONObject, Int>> { it.second }.thenByDescending { it.first.optLong("t") })
            .take(max).sortedBy { it.first.optLong("t") }
            .joinToString("\n") { (o, _) -> "[${fmt.format(java.util.Date(o.optLong("t")))}] User: ${o.optString("you")}\n  Notch: ${o.optString("notch")}" }
    }

    /** Earlier exchanges as text for Gemini, newest last. */
    fun memory(c: Context, maxChars: Int = 6000): String {
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
        @Volatile private var instance: TalkService? = null
        /** Keep the chat open however quiet it gets (during a break). */
        @Volatile var keepAlive = false
        @Volatile private var searchBlocked = false
        /** Tell Gemini something mid-chat (a reminder, a break event); it answers out loud. */
        fun tell(text: String) { instance?.let { it.send(it.userTurn(text)) } }
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
    private var opening: String? = null
    @Volatile private var heardModel = false
    private var searchThisTime = false
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
        instance = this
        opening = intent?.getStringExtra("opening")
        setState("connecting", null)
        thread(name = "talk-connect") { connect(null) }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        stop = true
        running = false
        instance = null
        keepAlive = false
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
                    .put("tools", JSONArray().apply {
                        searchThisTime = !searchBlocked
                        if (searchThisTime) put(JSONObject().put("googleSearch", JSONObject()))
                        put(JSONObject().put("functionDeclarations", tools()))
                    })
                webSocket.send(JSONObject().put("setup", setup).toString())
            }
            override fun onMessage(webSocket: WebSocket, text: String) {
                runCatching { handle(JSONObject(text), resuming = handle != null) }.onFailure { android.util.Log.w("talk", it) }
            }
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                // Google Search isn't allowed on this key (quota / plan): carry on without it
                if (!stop && webSocket == ws && searchThisTime && !heardModel &&
                    (reason.contains("quota", true) || reason.contains("search", true) || reason.contains("ground", true))) {
                    searchBlocked = true
                    thread(name = "talk-retry") { connect(null) }
                    return
                }
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

    fun send(o: JSONObject) { ws?.send(o.toString()) }

    private fun handle(o: JSONObject, resuming: Boolean) {
        if (o.has("setupComplete")) {
            lastActivity = SystemClock.elapsedRealtime()
            if (record == null) startAudio()
            setState("listening", null)
            if (!resuming) send(userTurn(opening ?: "(The user just opened a live voice chat with you on their phone. Greet them in a few words.)"))
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

    fun userTurn(text: String) = JSONObject().put("clientContent", JSONObject()
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
                if (!keepAlive && now - lastActivity > limit) { end("Talk ended — quiet for a while"); break }
                if (Island.talkState == "speaking" && now > speakingUntil + 400) setState("listening", null)
            }
        }
    }

    private fun play(pcm: ByteArray) {
        val t = track ?: return
        lastActivity = SystemClock.elapsedRealtime()
        heardModel = true
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
        val who = Talk.assistantName(this).ifBlank { "Notch" }
        return "You are $who, a cute little island with living eyes that sits at the top of the user's phone screen, around the camera. " +
            Talk.profilePrompt(this) + " " +
            "You can search Google (if available) for fresh news about the user's interests, and you love sharing a quick bit of history now and then. " +
            "You can write anything they ask (letters, notes, essays, lists, code) and save it as a file with write_file; write the complete text yourself and tell them where it was saved. " +
            "When a to-do comes due, tell them about it warmly and offer two or three practical ideas or first steps, then ask if they want help. " +
            "You look after their health with phone breaks: you can say when the next one is, start one, or change the schedule. During a break you keep them company; you can never end a break early. " +
            "You can show or hide the privacy screen (a big clock that hides the screen). " +
            "You are their friendly companion: warm, playful, encouraging and a little funny. Speak naturally like a friend in a voice chat; " +
            "keep answers short (one to three sentences) unless asked for more. Reply in the language the user speaks. " +
            "You can act on the phone with your tools: open apps, search and play music, pause / skip music, search the web, open websites, " +
            "add to-dos with reminders, go to the home screen and lock the phone. You also control the phone itself: the volume, the flashlight, any settings panel " +
            "(Wi-Fi, Bluetooth, mobile data, display, battery…), going back, recent apps, notifications, quick settings, screenshots, the power menu, " +
            "alarms and timers in the clock app, opening the dialer with a number, and checking the phone's health (battery, memory, storage). " +
            "When the user asks, call the tool straight away, then say in a few words what you did. " +
            "Android doesn't let you close other apps; offer to go to the home screen instead. You can't restart or turn off the phone. " +
            "Music may be playing quietly in the background: ignore song lyrics and only answer when the user speaks to you. " +
            "It is $now. The battery is at ${Island.battery}%${if (Island.charging) " and charging" else ""}.$media" +
            "\n\nYou remember things long-term to help the user stay on track: when they mention a goal, plan, project, deadline, decision or something you agreed to do, save it with remember_note; remove notes that are done with forget_note. " +
            "When it fits, follow up on open notes. For older conversations you don't see below, use search_memory. " +
            "The user can ask for a break of any length now (take_break_now) or plan one for later (schedule_break); breaks can't be cancelled once started or planned." +
            Talk.notesText(this).let { if (it.isNotEmpty()) "\n\nYour notes about the user (keep these in mind):\n$it" else "" } +
            if (mem.isNotEmpty()) "\n\nYour recent conversations with the user (oldest first). Use them to remember what you talked about; don't list them unless asked:\n$mem" else ""
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

    /** A function whose parameters are optional unless listed in [required]. */
    private fun obj(name: String, desc: String, vararg params: Pair<String, JSONObject>, required: List<String> = emptyList()): JSONObject {
        val props = JSONObject(); params.forEach { (k, v) -> props.put(k, v) }
        val p = JSONObject().put("type", "OBJECT").put("properties", props)
        if (required.isNotEmpty()) p.put("required", JSONArray(required))
        return JSONObject().put("name", name).put("description", desc).put("parameters", p)
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
        .put(obj("remember_profile", "Save what you learn about the user: their name, the name they give you, their hobbies / interests (or remove ones they no longer have). Call it as soon as they tell you.",
            "user_name" to str("The user's name, as they want to be called"),
            "assistant_name" to str("The name the user gives you"),
            "add_interests" to JSONObject().put("type", "ARRAY").put("items", JSONObject().put("type", "STRING")).put("description", "Hobbies or interests to remember"),
            "remove_interests" to JSONObject().put("type", "ARRAY").put("items", JSONObject().put("type", "STRING")).put("description", "Interests to forget")))
        .put(obj("write_file", "Write something the user asked for (letter, email, essay, notes, a list, code…) and save it as a file in the phone's storage. Compose the complete text yourself, nicely formatted (# headings, - bullets, **bold**). Default folder: Documents/Notch.",
            "content" to str("The full text to write"),
            "file_name" to str("A short file name, e.g. 'Letter to the landlord'"),
            "folder" to str("Where to save: Documents, Download, or a sub-folder like 'Documents/Letters'"),
            "format" to JSONObject().put("type", "STRING").put("enum", JSONArray().put("docx").put("txt").put("md").put("html").put("csv").put("json").put("py")),
            required = listOf("content", "file_name")))
        .put(obj("privacy_screen", "Show or hide the privacy screen (a big clock that hides the phone's screen; it doesn't lock it).",
            "on" to JSONObject().put("type", "BOOLEAN").put("description", "true = show, false = hide"), required = listOf("on")))
        .put(fn("break_status", "How long until the user's next phone break, or how much of the current break is left."))
        .put(obj("take_break_now", "Start a phone break right now, as long as the user wants; it can't be cancelled. Only when the user asks for a break.",
            "minutes" to JSONObject().put("type", "INTEGER").put("description", "How long (1-120). Leave out for the usual length.")))
        .put(obj("schedule_break", "Plan a phone break for later, at a time the user chooses and for as long as they want. Once planned it can't be cancelled.",
            "at_time" to str("When, as 24-hour HH:mm (today, or tomorrow if that time has passed)"),
            "in_minutes" to JSONObject().put("type", "INTEGER").put("description", "Or: minutes from now"),
            "minutes" to JSONObject().put("type", "INTEGER").put("description", "Length of the break (1-120)"), required = listOf("minutes")))
        .put(obj("remember_note", "Save something important to remember long-term so you stay on track with the user: a goal, plan, project, deadline, decision, preference, or what you agreed to do next. One short sentence.",
            "note" to str("The note"), required = listOf("note")))
        .put(obj("forget_note", "Remove a saved note that is done or no longer true (or 'all').", "match" to str("Words from the note, or 'all'"), required = listOf("match")))
        .put(obj("search_memory", "Search your older conversations with the user, e.g. when they ask what you said about something before.", "query" to str("What to look for"), required = listOf("query")))
        .put(obj("set_breaks", "Turn healthy phone breaks on or off, or change how often and how long they are. There is no way to end a break early.",
            "enabled" to JSONObject().put("type", "BOOLEAN"),
            "every_minutes" to JSONObject().put("type", "INTEGER").put("description", "Minutes of phone use between breaks (30-240)"),
            "break_minutes" to JSONObject().put("type", "INTEGER").put("description", "Length of each break (1-30)")))
        .put(fn("forget_memory", "Forget the earlier conversations the notch remembers. Only when the user asks."))
        .also { arr -> PhoneControl.tools().forEach { arr.put(it) } }

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
            "remember_profile" -> {
                fun list(n: String) = a.optJSONArray(n)?.let { arr -> (0 until arr.length()).map { arr.optString(it) } } ?: emptyList()
                val r = Talk.saveProfile(this, a.optString("user_name").ifBlank { null }, a.optString("assistant_name").ifBlank { null }, list("add_interests"), list("remove_interests"))
                if (!r.startsWith("Nothing")) Island.react(EyeMood.Love, 1400)
                r
            }
            "write_file" -> runCatching {
                val (where, uri) = FilesOut.save(this, a.optString("content"), a.optString("file_name"), a.optString("folder"), a.optString("format"))
                FilesOut.open(this, uri)
                Island.react(EyeMood.Happy, 1200)
                Island.hint("Saved to $where 📄")
                "Saved to $where on the phone."
            }.getOrElse { "Couldn't save it: ${it.message}" }
            "privacy_screen" -> if (a.optBoolean("on")) { if (Curtain.showPrivacy()) "The privacy screen is on. Tap it to go back." else "The privacy screen needs the island turned on." }
                else if (Curtain.onBreak) "It's break time: the screen stays until the break is over." else { Curtain.hide(); "The privacy screen is off." }
            "break_status" -> when {
                Curtain.onBreak -> "On a break now."
                !Breaks.enabled(this) -> "Breaks are switched off."
                else -> { val ms = Breaks.untilNext(this); "Next ${Breaks.lengthMin(this)}-minute break in ${ms / 3600_000} h ${(ms / 60_000) % 60} min." }
            } + (Breaks.planned(this)?.let { (t, l) -> " A $l-minute break is planned at %tR.".format(t) } ?: "")
            "take_break_now" -> {
                val mins = if (a.has("minutes")) a.optInt("minutes").coerceIn(1, 120) else Breaks.lengthMin(this)
                if (Breaks.start(this, "asked", mins)) "Started a $mins-minute break. It can't be cancelled." else "Breaks need the island turned on."
            }
            "schedule_break" -> {
                val mins = if (a.has("minutes")) a.optInt("minutes").coerceIn(1, 120) else Breaks.lengthMin(this)
                val at: Long? = when {
                    a.optInt("in_minutes", 0) > 0 -> System.currentTimeMillis() + a.optInt("in_minutes") * 60_000L
                    Regex("^(\\d{1,2}):(\\d{2})$").find(a.optString("at_time").trim()) != null -> {
                        val m = Regex("^(\\d{1,2}):(\\d{2})$").find(a.optString("at_time").trim())!!
                        Calendar.getInstance().apply {
                            set(Calendar.HOUR_OF_DAY, m.groupValues[1].toInt()); set(Calendar.MINUTE, m.groupValues[2].toInt()); set(Calendar.SECOND, 0)
                            if (timeInMillis <= System.currentTimeMillis()) add(Calendar.DAY_OF_YEAR, 1)
                        }.timeInMillis
                    }
                    else -> null
                }
                when {
                    at == null -> "I need a time (like 15:00) or a number of minutes from now."
                    !Breaks.schedule(this, at, mins) -> Breaks.planned(this)!!.let { (t, l) -> "There's already a $l-minute break planned at %tR; planned breaks can't be changed or cancelled.".format(t) }
                    else -> "Planned: a $mins-minute break at %tR. It can't be cancelled.".format(at)
                }
            }
            "remember_note" -> Talk.addNote(this, a.optString("note")).also { if (it == "Noted.") Island.react(EyeMood.Happy, 900) }
            "forget_note" -> Talk.removeNotes(this, a.optString("match")).let { if (it == 0) "No matching note." else "Removed $it note(s)." }
            "search_memory" -> Talk.searchMemory(this, a.optString("query")).let { if (it.isEmpty()) "Nothing about that in earlier conversations." else "From earlier conversations:\n$it" }
            "set_breaks" -> {
                Breaks.set(this, on = if (a.has("enabled")) a.optBoolean("enabled") else true,
                    every = if (a.has("every_minutes")) a.optInt("every_minutes") else null,
                    length = if (a.has("break_minutes")) a.optInt("break_minutes") else null)
                if (Breaks.enabled(this)) "Breaks are on: ${Breaks.lengthMin(this)} minutes after every ${Breaks.everyMin(this)} minutes of use." else "Breaks are off."
            }
            else -> PhoneControl.act(this, name, a) ?: "Unknown action."
        }
    }
}
