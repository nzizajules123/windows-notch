package com.windowsnotch.island

import android.app.Notification
import android.content.ComponentName
import android.graphics.Bitmap
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.core.graphics.drawable.toBitmap
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Reads now-playing media sessions (title, artwork, position, controls) and incoming notifications. */
class NotifListener : NotificationListenerService() {

    private val main = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var msm: MediaSessionManager? = null
    private var controller: MediaController? = null
    private var lastArtKey = ""
    private var lastArt: Pair<androidx.compose.ui.graphics.ImageBitmap?, Color> = null to Color(0xFF30D158)
    private var lyricsKey = ""

    private val sessionsListener = MediaSessionManager.OnActiveSessionsChangedListener { pick(it) }

    private val callback = object : MediaController.Callback() {
        override fun onMetadataChanged(metadata: MediaMetadata?) = push()
        override fun onPlaybackStateChanged(state: PlaybackState?) {
            // another app may have started playing → re-pick the "best" session
            if (state?.state == PlaybackState.STATE_PLAYING && controller?.playbackState?.state != PlaybackState.STATE_PLAYING) refresh()
            push()
        }
        override fun onSessionDestroyed() = refresh()
    }

    override fun onListenerConnected() {
        Island.listenerConnected = true
        val m = getSystemService(MediaSessionManager::class.java)
        msm = m
        val me = ComponentName(this, NotifListener::class.java)
        runCatching {
            m.addOnActiveSessionsChangedListener(sessionsListener, me, main)
            pick(m.getActiveSessions(me))
        }
    }

    override fun onListenerDisconnected() {
        Island.listenerConnected = false
        runCatching { msm?.removeOnActiveSessionsChangedListener(sessionsListener) }
        controller?.unregisterCallback(callback)
        controller = null
        Island.controller = null
        Island.media = null
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun refresh() {
        val m = msm ?: return
        runCatching { pick(m.getActiveSessions(ComponentName(this, NotifListener::class.java))) }
    }

    private fun pick(list: List<MediaController>?) {
        val sessions = list.orEmpty()
        val best = sessions.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING } ?: sessions.firstOrNull()
        if (best?.sessionToken != controller?.sessionToken) {
            controller?.unregisterCallback(callback)
            controller = best
            best?.registerCallback(callback, main)
        }
        Island.controller = controller
        push()
    }

    private fun push() {
        val c = controller
        val md = c?.metadata
        if (c == null || md == null) { Island.media = null; Island.lyrics = null; return }
        val title = md.getString(MediaMetadata.METADATA_KEY_TITLE) ?: md.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE) ?: ""
        if (title.isBlank()) { Island.media = null; return }
        val artist = md.getString(MediaMetadata.METADATA_KEY_ARTIST) ?: md.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST)
            ?: md.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE) ?: ""
        val pkg = c.packageName
        val app = runCatching { packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString() }.getOrDefault(pkg)
        val ps = c.playbackState
        val key = "$pkg|$title|$artist"

        if (key != lastArtKey) {
            lastArtKey = key
            val bmp: Bitmap? = md.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART) ?: md.getBitmap(MediaMetadata.METADATA_KEY_ART)
                ?: md.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
            lastArt = (bmp?.asImageBitmap()) to (bmp?.let { accentOf(it) } ?: Color(0xFF30D158))
        }

        Island.media = MediaInfo(
            key = key, title = title, artist = artist, app = app, packageName = pkg,
            art = lastArt.first, accent = lastArt.second,
            durationMs = md.getLong(MediaMetadata.METADATA_KEY_DURATION),
            positionMs = ps?.position ?: 0L,
            positionAt = ps?.lastPositionUpdateTime?.takeIf { it > 0 } ?: SystemClock.elapsedRealtime(),
            speed = ps?.playbackSpeed?.takeIf { it > 0f } ?: 1f,
            playing = ps?.state == PlaybackState.STATE_PLAYING,
        )
        loadLyrics(title, artist, md.getLong(MediaMetadata.METADATA_KEY_DURATION), pkg)
    }

    private fun loadLyrics(title: String, artist: String, durationMs: Long, pkg: String) {
        val key = "$title|$artist"
        if (key == lyricsKey) return
        lyricsKey = key
        Island.lyrics = null
        if (!Island.lyricsEnabled) return
        val musicApp = pkg.contains("spotify") || pkg.contains("music") || pkg.contains("deezer") || pkg.contains("tidal")
        scope.launch {
            val r = withContext(Dispatchers.IO) { Lyrics.get(title, artist, durationMs, musicApp) }
            if (lyricsKey == key) Island.lyrics = r
        }
    }

    /** The most vivid colour of the artwork (tints the eyes' irises and the progress bar). */
    private fun accentOf(b: Bitmap): Color {
        val s = runCatching { Bitmap.createScaledBitmap(b, 12, 12, true) }.getOrNull() ?: return Color(0xFF30D158)
        var best = 0; var bestScore = -1f
        for (x in 0 until 12) for (y in 0 until 12) {
            val p = s.getPixel(x, y)
            val hsv = FloatArray(3)
            android.graphics.Color.colorToHSV(p, hsv)
            val score = hsv[1] * (0.35f + hsv[2]) * if (hsv[2] < 0.25f) 0.2f else 1f
            if (score > bestScore) { bestScore = score; best = p }
        }
        val hsv = FloatArray(3)
        android.graphics.Color.colorToHSV(best, hsv)
        if (hsv[1] < 0.2f) return Color(0xFFDADDE3)
        hsv[1] = hsv[1].coerceAtLeast(0.55f); hsv[2] = hsv[2].coerceAtLeast(0.85f)
        return Color(android.graphics.Color.HSVToColor(hsv))
    }

    // ------------------------------------------------------------------ notifications
    override fun onNotificationPosted(sbn: StatusBarNotification) {
        val n = sbn.notification ?: return
        if (sbn.packageName == packageName || sbn.isOngoing) return
        if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return
        if (n.category == Notification.CATEGORY_TRANSPORT || n.category == Notification.CATEGORY_PROGRESS) return
        if (n.extras.containsKey(Notification.EXTRA_MEDIA_SESSION)) return
        val title = n.extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        val text = (n.extras.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: n.extras.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty()
        if (title.isBlank() && text.isBlank()) return
        val pm = packageManager
        val app = runCatching { pm.getApplicationLabel(pm.getApplicationInfo(sbn.packageName, 0)).toString() }.getOrDefault(sbn.packageName)
        val icon = runCatching { pm.getApplicationIcon(sbn.packageName).toBitmap(96, 96).asImageBitmap() }.getOrNull()
        main.post { Island.post(IslandEvent.Notification(app, title, text.replace('\n', ' '), icon)) }
    }
}
