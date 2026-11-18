package com.windowsnotch.island

import android.media.session.MediaController
import android.os.SystemClock
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
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

sealed interface IslandEvent {
    data class Notification(val app: String, val title: String, val text: String, val icon: ImageBitmap?) : IslandEvent
    data class Charge(val charging: Boolean, val percent: Int) : IslandEvent
    data class Volume(val level: Float, val muted: Boolean) : IslandEvent
}

/** Shared, observable state between the services and the island UI (all on the main thread). */
object Island {
    var media by mutableStateOf<MediaInfo?>(null)
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

    fun post(e: IslandEvent) {
        event = e
        eventId++
    }

    fun react(m: EyeMood, ms: Long = 1600) {
        mood = m
        moodUntil = SystemClock.elapsedRealtime() + ms
    }
}
