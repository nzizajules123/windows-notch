package com.windowsnotch.island

import android.Manifest
import android.accessibilityservice.AccessibilityService
import android.app.KeyguardManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import androidx.core.content.ContextCompat
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt

/**
 * Finds hand claps in 16 kHz mono sound. It's the same detector as the Windows notch: a clap is a sudden, loud,
 * bright burst that fades within ~0.1 s, and claps in a steady rhythm (0.12–0.75 s apart) form a sequence.
 */
class ClapAnalyzer(
    private val onClap: (Int) -> Unit,
    private val onSequence: (count: Int, spanMs: Int) -> Unit,
) {
    companion object {
        const val RATE = 16000
        private const val FRAME = RATE / 100
        const val MIN_GAP = 120; const val MAX_GAP = 750; const val END_GAP = 800
    }

    var strictness = 1.0
    private val buf = ShortArray(FRAME)
    private var fill = 0
    private var frameNo = 0L
    private var floor = 0.003
    private var prevRms = 0.0
    private var burstStart = -1L
    private var burstPeak = 0.0; private var burstZcr = 0.0; private var burstBase = 0.0
    private var burstDecayed = false
    private val claps = ArrayList<Long>()

    fun feed(samples: ShortArray, n: Int) {
        for (i in 0 until n) {
            buf[fill++] = samples[i]
            if (fill == FRAME) { analyze(); fill = 0 }
        }
    }

    private fun analyze() {
        var sum = 0.0; var peak = 0.0; var crossings = 0
        for (i in buf.indices) {
            val v = buf[i] / 32768.0
            sum += v * v
            peak = max(peak, abs(v))
            if (i > 0 && (buf[i] >= 0) != (buf[i - 1] >= 0)) crossings++
        }
        val rms = sqrt(sum / buf.size)
        val zcr = crossings.toDouble() / buf.size
        val now = frameNo++

        if (burstStart >= 0) {
            val age = now - burstStart
            if (age <= 2) { burstPeak = max(burstPeak, rms); burstZcr = max(burstZcr, zcr) }
            else if (rms < max(burstPeak * 0.22, burstBase * 1.5)) burstDecayed = true
            if (burstDecayed || age >= 14) {
                if (burstDecayed && age <= 13 && burstZcr >= 0.08) clap(burstStart)
                burstStart = -1
            }
        } else {
            val need = max(min(floor, 0.04) * 6, 0.025) * strictness
            if (rms > need && peak > 0.12 * strictness && rms > prevRms * 2.5) {
                burstStart = now; burstPeak = rms; burstZcr = zcr; burstBase = prevRms; burstDecayed = false
            } else {
                val k = if (rms < floor) 0.08 else 0.02
                floor = (floor + (rms - floor) * k).coerceIn(0.0005, 0.2)
            }
        }
        prevRms = rms
        if (claps.isNotEmpty() && (now - claps.last()) * 10 >= END_GAP) {
            val n = claps.size
            val span = ((claps.last() - claps.first()) * 10).toInt()
            claps.clear()
            onSequence(n, span)
        }
    }

    private fun clap(at: Long) {
        if (claps.isNotEmpty()) {
            val gap = (at - claps.last()) * 10
            if (gap < MIN_GAP) return
            if (gap > MAX_GAP) claps.clear()
            else if (claps.size >= 2) {
                val prevGap = (claps.last() - claps[claps.size - 2]) * 10
                if (gap > prevGap * 2.2 || gap * 2.2 < prevGap) claps.clear()
            }
        }
        claps.add(at)
        onClap(claps.size)
    }
}

/**
 * Listens to the microphone while clap control is on. It's a foreground service: Android only lets apps use the
 * microphone in the background that way, and it shows a small "Listening for claps" notification.
 * 👏👏 locks the phone and 👏👏👏 opens the power menu (Android doesn't let apps restart the phone).
 * Both go through the island's accessibility service. Sound is analysed live and never recorded.
 */
class ClapService : Service() {

    companion object {
        private const val CHANNEL = "claps"
        @Volatile var running = false
            private set

        fun hasMic(ctx: Context) =
            ContextCompat.checkSelfPermission(ctx, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

        /** Starts listening if clap control is on (and the microphone is allowed). Safe to call often. */
        fun sync(ctx: Context) {
            val on = ctx.getSharedPreferences("island", Context.MODE_PRIVATE).getBoolean("claps", false)
            if (on && hasMic(ctx)) {
                if (!running) runCatching { ContextCompat.startForegroundService(ctx, Intent(ctx, ClapService::class.java)) }
            } else if (running) ctx.stopService(Intent(ctx, ClapService::class.java))
        }
    }

    private val main = Handler(Looper.getMainLooper())
    @Volatile private var stop = false
    private var thread: Thread? = null
    private var cooldownUntil = 0L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (!hasMic(this)) { stopSelf(); return START_NOT_STICKY }
        val n = notification()
        try {
            if (Build.VERSION.SDK_INT >= 29) startForeground(7, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
            else startForeground(7, n)
        } catch (e: Exception) {
            // Android refuses to start the microphone from the background; it starts next time the app is opened
            stopSelf(); return START_NOT_STICKY
        }
        if (thread == null) {
            stop = false
            thread = Thread(::listen, "claps").apply { isDaemon = true; start() }
        }
        running = true
        return START_STICKY
    }

    override fun onDestroy() {
        stop = true
        thread?.join(1000)
        thread = null
        running = false
        super.onDestroy()
    }

    private fun notification(): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26 && nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Clap control", NotificationManager.IMPORTANCE_MIN).apply { setShowBadge(false) })
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val b = if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, CHANNEL) else @Suppress("DEPRECATION") Notification.Builder(this)
        return b.setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle("Listening for claps 👏")
            .setContentText("Clap twice to lock · three times for the power menu")
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    @Suppress("MissingPermission")
    private fun listen() {
        val min = AudioRecord.getMinBufferSize(ClapAnalyzer.RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val rec = try {
            AudioRecord(MediaRecorder.AudioSource.MIC, ClapAnalyzer.RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, max(min, 3200) * 2)
        } catch (e: Exception) { null }
        if (rec == null || rec.state != AudioRecord.STATE_INITIALIZED) { rec?.release(); main.post { stopSelf() }; return }

        val analyzer = ClapAnalyzer(
            onClap = { n -> main.post { onClap(n) } },
            onSequence = { n, span -> main.post { onSequence(n, span) } },
        )
        val buf = ShortArray(800)
        rec.startRecording()
        try {
            while (!stop) {
                val n = rec.read(buf, 0, buf.size)
                if (n > 0) analyzer.feed(buf, n) else if (n < 0) Thread.sleep(50)
            }
        } finally {
            runCatching { rec.stop() }
            rec.release()
        }
    }

    /** Times claps must be ignored: phone locked, sound playing or a call, just acted, island not running. */
    private fun blocker(): String? {
        if (SystemClock.elapsedRealtime() < cooldownUntil) return "cooldown"
        if (getSystemService(KeyguardManager::class.java).isKeyguardLocked) return "locked"
        val am = getSystemService(AudioManager::class.java)
        if (am.isMusicActive) return "media playing"
        if (am.mode != AudioManager.MODE_NORMAL) return "call"
        return null
    }

    private fun onClap(n: Int) {
        if (blocker() != null) return
        Island.hint(when (n) { 1 -> "👏  Clap again to lock"; 2 -> "👏👏  Locking…"; else -> "👏👏👏  Power menu" })
    }

    private fun onSequence(n: Int, span: Int) {
        if (n !in 2..3 || blocker() != null) return
        val island = IslandService.instance
        if (island == null) { Island.hint("Turn on the island to use claps"); return }
        cooldownUntil = SystemClock.elapsedRealtime() + 4000
        if (n == 2) {
            if (Build.VERSION.SDK_INT >= 28) island.performGlobalAction(AccessibilityService.GLOBAL_ACTION_LOCK_SCREEN)
            else Island.hint("Locking needs Android 9 or newer")
        } else island.performGlobalAction(AccessibilityService.GLOBAL_ACTION_POWER_DIALOG)
    }
}
