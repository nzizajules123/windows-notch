package com.windowsnotch.island

import android.media.session.MediaController
import android.os.SystemClock
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap

enum class EyeMood { Neutral, Happy, Surprised, Sleepy, Sad, Love, Angry }

data class MediaInfo(
    val key: String,
    val title: String,
    val artist: String,
    val app: String,
    val packageName: String,
    val art: ImageBitmap?,
    val accent: Color,
    val durationMs: Long,
    val positionMs: Long,
    val positionAt: Long,        // SystemClock.elapsedRealtime() when positionMs was reported
    val speed: Float,
    val playing: Boolean,
) {
    fun livePosition(): Long {
        if (!playing) return positionMs
        val p = positionMs + ((SystemClock.elapsedRealtime() - positionAt) * speed).toLong()
        return if (durationMs > 0) p.coerceIn(0, durationMs) else p.coerceAtLeast(0)
    }
}

class ChatMsg(val from: String, val text: String, val time: Long, val mine: Boolean)

/** One WhatsApp / Instagram / … conversation, built from its notifications. */
class ChatThread(val key: String, val app: String, val pkg: String, val title: String) {
    val messages = mutableStateListOf<ChatMsg>()
    var icon by mutableStateOf<ImageBitmap?>(null)
    var avatar by mutableStateOf<ImageBitmap?>(null)
    var unread by mutableIntStateOf(0)
    var updated by mutableLongStateOf(System.currentTimeMillis())
    /** The notification's own "Reply" action (RemoteInput) — how replies are sent. */
    @Volatile var reply: android.app.Notification.Action? = null
    val canReply get() = reply != null
}

sealed interface IslandEvent {
    data class Notification(val app: String, val title: String, val text: String, val icon: ImageBitmap?, val chat: ChatThread? = null, val pkg: String = "") : IslandEvent
    data class Charge(val charging: Boolean, val percent: Int) : IslandEvent
    data class Volume(val level: Float, val muted: Boolean) : IslandEvent
    /** A to-do whose reminder time has come. */
    data class Reminder(val item: TodoItem) : IslandEvent
}

/** A notification kept for the Alerts tab. */
class NotifEntry(val n: IslandEvent.Notification, val time: Long = System.currentTimeMillis())

/** Shared, observable state between the services and the island UI (all on the main thread). */
object Island {
    /** What the phone's media sessions say is playing. */
    var sysMedia by mutableStateOf<MediaInfo?>(null)
    /** The island's own YouTube player. */
    var playerMedia by mutableStateOf<MediaInfo?>(null)
    /** Shown in the island: the island player while it plays (or nothing else does), else the phone's media. */
    val media: MediaInfo?
        get() {
            val p = playerMedia
            val s = sysMedia
            return if (p != null && (p.playing || s?.playing != true)) p else s
        }
    var lyrics by mutableStateOf<LyricsResult?>(null)
    var battery by mutableIntStateOf(100)
    var charging by mutableStateOf(false)

    var event by mutableStateOf<IslandEvent?>(null)
        private set
    var eventId by mutableLongStateOf(0L)
        private set

    /** Face the eyes should pull, and until when (elapsedRealtime). */
    var mood by mutableStateOf(EyeMood.Neutral)
        private set
    var moodUntil by mutableLongStateOf(0L)
        private set

    /** Bumped when the user touches outside the open island. */
    var outsideTouches by mutableIntStateOf(0)

    var listenerConnected by mutableStateOf(false)
    var overlayRunning by mutableStateOf(false)
    var lyricsEnabled by mutableStateOf(true)

    @Volatile var controller: MediaController? = null

    /** Recent conversations, newest first. */
    val chats = mutableStateListOf<ChatThread>()
    /** Conversation to open in the island (set by tapping a message pop-up or a chat row). */
    var openChat by mutableStateOf<ChatThread?>(null)
    /** True while a text field in the island has focus → the overlay must accept the keyboard. */
    var typing by mutableStateOf(false)

    /** Every notification (newest first, last 60) for the Alerts tab, and whether some are unseen. */
    val history = mutableStateListOf<NotifEntry>()
    var unseenAlerts by mutableStateOf(false)

    fun post(e: IslandEvent) {
        if (e is IslandEvent.Notification) {
            history.add(0, NotifEntry(e))
            while (history.size > 60) history.removeAt(history.size - 1)
            unseenAlerts = true
        }
        event = e
        eventId++
    }

    /** What the eyes are saying right now (speech bubble), and a counter so repeats still pop. */
    var speech by mutableStateOf<String?>(null)
        private set
    var speechId by mutableLongStateOf(0L)
        private set
    var talk by mutableStateOf(true)

    fun say(text: String) {
        if (!talk) return
        speech = text
        speechId++
    }

    /** A short status bubble that shows even when "Eyes talk" is off (e.g. clap feedback). */
    fun hint(text: String) {
        speech = text
        speechId++
    }

    fun pick(vararg s: String) = s.random()

    fun greeting(): String {
        val h = java.util.Calendar.getInstance().get(java.util.Calendar.HOUR_OF_DAY)
        return when {
            h < 5 -> pick("Up late? 🌙", "Night owl mode 🦉")
            h < 12 -> pick("Good morning ☀️", "Rise and shine ☀️")
            h < 17 -> pick("Good afternoon 🌤", "Hey! Good afternoon 👋")
            h < 22 -> pick("Good evening 🌆", "Evening! 👋")
            else -> pick("Late one tonight? 🌙", "Good night soon? 🌙")
        }
    }

    fun react(m: EyeMood, ms: Long = 1600) {
        mood = m
        moodUntil = SystemClock.elapsedRealtime() + ms
    }
}
