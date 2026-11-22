package com.windowsnotch.island

import android.app.KeyguardManager
import android.content.Context
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Full-screen curtains drawn by the island's accessibility service:
 *  • the privacy screen — a big clock over a dark screen; tap to close;
 *  • the break screen — a countdown you can't close early (taps are swallowed and answered with a warning);
 *    emergency only: press and hold anywhere for 5 seconds.
 */
object Curtain {
    private var view: View? = null
    private val main = Handler(Looper.getMainLooper())
    private var breakUntil = 0L
    private var shownAt = 0L
    private var lastWarn = 0L
    private var escapes = 0
    private lateinit var time: TextView
    private lateinit var date: TextView
    private lateinit var title: TextView
    private lateinit var remaining: TextView
    private lateinit var tip: TextView
    private lateinit var warn: TextView
    /** Called when a break ends by itself (true) or by emergency (false). */
    var onBreakEnded: ((finished: Boolean) -> Unit)? = null

    val showing get() = view != null
    val onBreak get() = view != null && breakUntil > 0

    private val tips = listOf(
        "Stand up and stretch your arms and back 🧘",
        "Look at something far away for 20 seconds 👀",
        "Drink a glass of water 💧",
        "Roll your shoulders and gently turn your neck",
        "Take five slow, deep breaths 😌",
        "Walk around for a minute 🚶",
    )
    private val warnings = listOf(
        "Not yet — the break isn't over 🙂",
        "Nice try! Your eyes need this 💙",
        "Still on break. Stretch while you wait 🧘",
        "Put the phone down — you've earned these minutes ☕",
    )

    fun showPrivacy(): Boolean = show(0L)

    fun startBreak(minutes: Int): Boolean = show(System.currentTimeMillis() + minutes * 60_000L)

    fun hide(force: Boolean = false): Boolean {
        if (onBreak && !force) return false
        val v = view ?: return true
        view = null
        breakUntil = 0
        main.removeCallbacksAndMessages(null)
        val svc = IslandService.instance ?: return true
        runCatching { svc.getSystemService(WindowManager::class.java).removeView(v) }
        return true
    }

    private fun show(until: Long): Boolean {
        val svc = IslandService.instance ?: return false
        if (view != null) { if (until > 0 && breakUntil == 0L) hide() else return true }
        breakUntil = until
        shownAt = System.currentTimeMillis()
        escapes = 0
        val ctx: Context = svc
        fun tv(size: Float, alpha: Int, bold: Boolean = false) = TextView(ctx).apply {
            textSize = size; setTextColor(Color.argb(alpha, 255, 255, 255)); gravity = Gravity.CENTER
            if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
        }
        date = tv(18f, 220); time = tv(76f, 245, true)
        title = tv(20f, 230, true).apply { text = "☕  Break time"; setPadding(0, 60, 0, 0) }
        remaining = tv(48f, 255, true).apply { setTextColor(Color.rgb(127, 216, 255)) }
        tip = tv(17f, 210).apply { setPadding(60, 18, 60, 0) }
        warn = tv(15f, 255, true).apply { setTextColor(Color.rgb(255, 176, 64)); setPadding(40, 28, 40, 0); alpha = 0f }
        val hint = tv(12f, 130).apply {
            text = if (until > 0) "The break ends by itself · emergency only: press and hold for 5 seconds" else "Tap to go back"
            setPadding(30, 0, 30, 60)
        }
        val column = LinearLayout(ctx).apply {
            orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER
            addView(date); addView(time)
            if (until > 0) { addView(title); addView(remaining); addView(tip); addView(warn) }
        }
        val root = object : FrameLayout(ctx) {
            override fun dispatchTouchEvent(ev: MotionEvent): Boolean { touched(ev); return true }   // nothing gets through
        }.apply {
            background = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM,
                intArrayOf(Color.rgb(28, 24, 52), Color.rgb(8, 8, 12), Color.rgb(4, 4, 6)))
            addView(column, FrameLayout.LayoutParams(-1, -1))
            addView(hint, FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM))
        }
        val p = WindowManager.LayoutParams(-1, -1, WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON,
            PixelFormat.TRANSLUCENT)
        if (Build.VERSION.SDK_INT >= 28) p.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        return runCatching {
            svc.getSystemService(WindowManager::class.java).addView(root, p)
            view = root
            root.alpha = 0f; root.animate().alpha(1f).setDuration(400).start()
            tick()
            true
        }.getOrDefault(false)
    }

    private var downAt = 0L

    private fun touched(ev: MotionEvent) {
        if (breakUntil == 0L) { if (ev.action == MotionEvent.ACTION_UP) hide(); return }
        when (ev.action) {
            MotionEvent.ACTION_DOWN -> downAt = SystemClock.elapsedRealtime()
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                if (SystemClock.elapsedRealtime() - downAt >= 5000) { emergency(); return }
                downAt = 0
                triedToLeave()
            }
        }
    }

    private fun emergency() {
        hide(force = true)
        onBreakEnded?.invoke(false)
    }

    private fun triedToLeave() {
        val ms = breakUntil - System.currentTimeMillis()
        val rem = "%d:%02d".format(ms / 60000, (ms / 1000) % 60)
        warn.text = warnings[escapes % warnings.size] + "  ·  $rem left"
        warn.animate().cancel(); warn.alpha = 1f
        warn.animate().alpha(0f).setStartDelay(3000).setDuration(600).start()
        warn.animate().translationX(0f)
        warn.translationX = -18f; warn.animate().translationX(0f).setDuration(300).start()
        if (SystemClock.elapsedRealtime() - lastWarn < 8000) return
        lastWarn = SystemClock.elapsedRealtime()
        escapes++
        val note = "(The user just tried to leave the break screen early (attempt $escapes); $rem of the break are left. " +
            (if (escapes == 1) "Kindly tell them it isn't over yet and why the break matters." else "Be a little firmer but still friendly; suggest something to do instead.") + ")"
        if (TalkService.running) TalkService.tell(note) else Island.hint(warnings[(escapes - 1) % warnings.size])
    }

    private fun tick() {
        val v = view ?: return
        val now = System.currentTimeMillis()
        time.text = SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(now))
        date.text = SimpleDateFormat("EEEE d MMMM", Locale.getDefault()).format(Date(now))
        if (breakUntil > 0) {
            val ms = breakUntil - now
            if (ms <= 0) { hide(force = true); onBreakEnded?.invoke(true); return }
            remaining.text = "%d:%02d".format(ms / 60000, (ms / 1000) % 60)
            tip.text = tips[(((now - shownAt) / 20_000) % tips.size).toInt()]
        }
        if (view === v) main.postDelayed({ tick() }, 1000)
    }
}

/** Healthy breaks on the phone: after 2 hours of screen time, a 5-minute break you can't skip. */
object Breaks {
    private var usedMs = 0L
    private var awayMs = 0L
    private var warned = false
    private var lastTick = 0L

    fun enabled(c: Context) = c.getSharedPreferences("island", Context.MODE_PRIVATE).getBoolean("breaks", false)
    fun everyMin(c: Context) = c.getSharedPreferences("island", Context.MODE_PRIVATE).getInt("break_every", 120)
    fun lengthMin(c: Context) = c.getSharedPreferences("island", Context.MODE_PRIVATE).getInt("break_len", 5)

    fun set(c: Context, on: Boolean? = null, every: Int? = null, length: Int? = null) {
        c.getSharedPreferences("island", Context.MODE_PRIVATE).edit().apply {
            on?.let { putBoolean("breaks", it) }
            every?.let { putInt("break_every", it.coerceIn(30, 240)) }
            length?.let { putInt("break_len", it.coerceIn(1, 30)) }
        }.apply()
        usedMs = 0; warned = false
    }

    fun untilNext(c: Context) = (everyMin(c) * 60_000L - usedMs).coerceAtLeast(0)

    /** Called every few seconds: counts unlocked screen time; time with the screen off counts as rest. */
    fun tick(c: Context) {
        val now = SystemClock.elapsedRealtime()
        val dt = if (lastTick == 0L) 0 else (now - lastTick).coerceIn(0, 60_000)
        lastTick = now
        // a break you planned for a set time: starts then (once the phone is in use), whatever the schedule switch says
        val p = c.getSharedPreferences("island", Context.MODE_PRIVATE)
        val at = p.getLong("break_at", 0)
        if (at > 0 && System.currentTimeMillis() >= at && !Curtain.onBreak &&
            c.getSystemService(PowerManager::class.java).isInteractive && !c.getSystemService(KeyguardManager::class.java).isKeyguardLocked) {
            val len = p.getInt("break_at_len", lengthMin(c))
            p.edit().remove("break_at").apply()
            start(c, "scheduled", len)
            return
        }
        if (!enabled(c) || Curtain.onBreak) return
        val inUse = c.getSystemService(PowerManager::class.java).isInteractive &&
            !c.getSystemService(KeyguardManager::class.java).isKeyguardLocked
        if (!inUse) {
            awayMs += dt
            if (awayMs >= lengthMin(c) * 60_000L) { usedMs = 0; warned = false }
            return
        }
        awayMs = 0
        usedMs += dt
        val total = everyMin(c) * 60_000L
        if (!warned && usedMs >= total - 60_000) { warned = true; Island.hint("Break in 1 minute ☕"); Island.react(EyeMood.Surprised, 900) }
        if (usedMs >= total) start(c, "time")
    }

    /** Plans a break at [atMillis] for [minutes]; returns false if one is already planned (planned breaks can't be changed). */
    fun schedule(c: Context, atMillis: Long, minutes: Int): Boolean {
        val p = c.getSharedPreferences("island", Context.MODE_PRIVATE)
        if (p.getLong("break_at", 0) > System.currentTimeMillis()) return false
        p.edit().putLong("break_at", atMillis).putInt("break_at_len", minutes.coerceIn(1, 120)).apply()
        return true
    }

    fun planned(c: Context): Pair<Long, Int>? {
        val p = c.getSharedPreferences("island", Context.MODE_PRIVATE)
        val at = p.getLong("break_at", 0)
        return if (at > System.currentTimeMillis()) at to p.getInt("break_at_len", 5) else null
    }

    fun start(c: Context, why: String, minutes: Int? = null): Boolean {
        if (Curtain.onBreak) return true
        val mins = (minutes ?: lengthMin(c)).coerceIn(1, 120)
        if (!Curtain.startBreak(mins)) return false
        usedMs = 0; warned = false
        TalkService.keepAlive = true
        Curtain.onBreakEnded = { finished ->
            TalkService.keepAlive = false
            if (finished) {
                Island.react(EyeMood.Happy, 1600)
                if (TalkService.running) TalkService.tell("(The break is over. Welcome them back in one cheerful sentence.)")
                else Island.hint("Break's over — welcome back! 💪")
            } else {
                usedMs = (everyMin(c) - 30).coerceAtLeast(0) * 60_000L   // another break in half an hour
                if (TalkService.running) TalkService.tell("(The user ended the break early with the emergency press. Say you hope everything is okay, in one sentence.)")
            }
        }
        val reason = when (why) {
            "time" -> "the user has been on their phone for about ${everyMin(c) / 60.0} hours, so a $mins-minute break has just started"
            "scheduled" -> "the $mins-minute break the user planned earlier has just started"
            else -> "the user asked for a $mins-minute break, and it has just started"
        }
        val note = "(Break time: $reason. " +
            "Their screen is covered until it's over and they can't end it early. Tell them warmly, suggest a couple of things to do (stretch, water, rest the eyes, a short walk), " +
            "and keep them company. If they ask to end the break early, kindly say no — you have no way to end it.)"
        if (Talk.key(c).isNotBlank()) TalkActivity.toggle(c, note) else Island.hint("Time for a $mins-minute break ☕")
        return true
    }
}

/** Now and then the island starts a chat by itself: news about your hobbies, or a bit of history. */
object Proactive {
    fun enabled(c: Context) = c.getSharedPreferences("island", Context.MODE_PRIVATE).getBoolean("proactive", true)

    fun maybe(c: Context) {
        if (!enabled(c) || Talk.key(c).isBlank() || TalkService.running || Curtain.showing) return
        val cal = java.util.Calendar.getInstance()
        val hour = cal.get(java.util.Calendar.HOUR_OF_DAY)
        if (hour < 9 || hour >= 21) return
        val p = c.getSharedPreferences("island", Context.MODE_PRIVATE)
        val today = cal.get(java.util.Calendar.DAY_OF_YEAR)
        if (p.getInt("proactive_day", -1) != today) p.edit().putInt("proactive_day", today).putInt("proactive_count", 0).apply()
        if (p.getInt("proactive_count", 0) >= 3 || System.currentTimeMillis() - p.getLong("proactive_last", 0) < 3 * 3600_000L) return
        val inUse = c.getSystemService(PowerManager::class.java).isInteractive && !c.getSystemService(KeyguardManager::class.java).isKeyguardLocked
        if (!inUse || Island.media?.playing == true) return
        p.edit().putLong("proactive_last", System.currentTimeMillis()).putInt("proactive_count", p.getInt("proactive_count", 0) + 1).apply()
        val name = Talk.userName(c).ifBlank { "the user" }
        val interests = Talk.interests(c)
        val topic = if (interests.isNotEmpty() && (0..2).random() != 0)
            "something new and interesting about one of their interests (${interests.joinToString(", ")}) — if you can search Google, find a real update from the last few days; otherwise share a fun fact or tip"
        else "a short, fascinating 'on this day in history' fact for today"
        TalkActivity.toggle(c, "(You are starting this chat yourself — $name didn't ask. Greet them by name in a few words, then share $topic. " +
            "Two or three sentences, then ask a light question to keep the conversation going. If they're busy, keep it short.)")
    }
}
