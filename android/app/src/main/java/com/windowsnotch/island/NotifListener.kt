package com.windowsnotch.island

import android.app.Notification
import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSession
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.core.app.NotificationCompat
import androidx.core.graphics.drawable.toBitmap
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Now playing: follows one media session (title, artwork, position, controls) and publishes it to the island.
 * Fed by the notification listener's session list, or — on Android Go phones, where Notification access can't be
 * granted — by the media session token inside a music app's notification, which the accessibility service sees.
 */
object MediaHub {
    private val main = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var app: Context? = null
    private var controller: MediaController? = null
    private var lastArtKey = ""
    private var lastArt: Pair<androidx.compose.ui.graphics.ImageBitmap?, Color> = null to Color(0xFF30D158)
    private var lyricsKey = ""

    private val callback = object : MediaController.Callback() {
        override fun onMetadataChanged(metadata: MediaMetadata?) = push()
        override fun onPlaybackStateChanged(state: PlaybackState?) {
            onStateChanged?.invoke(state)
            push()
        }
        override fun onSessionDestroyed() {
            onDestroyed?.invoke()
            use(null)
        }
    }

    /** Set by the notification listener: another session may have started playing / this one ended → re-pick. */
    var onStateChanged: ((PlaybackState?) -> Unit)? = null
    var onDestroyed: (() -> Unit)? = null

    val current get() = controller

    /** Follow this session (or none). */
    fun use(ctx: Context?, c: MediaController?) {
        if (ctx != null) app = ctx.applicationContext
        if (c?.sessionToken != controller?.sessionToken) {
            controller?.unregisterCallback(callback)
            controller = c
            c?.registerCallback(callback, main)
        }
        Island.controller = controller
        push()
    }

    fun use(c: MediaController?) = use(null, c)

    /** Android Go: a music app's notification carries its media session token. */
    fun useToken(ctx: Context, token: MediaSession.Token) {
        if (token == controller?.sessionToken) return
        val c = runCatching { MediaController(ctx, token) }.getOrNull() ?: return
        // keep following what's actually playing: don't switch away from a playing session to a paused one
        val playingNow = controller?.playbackState?.state == PlaybackState.STATE_PLAYING
        if (playingNow && c.playbackState?.state != PlaybackState.STATE_PLAYING) return
        use(ctx, c)
    }

    fun clear() {
        controller?.unregisterCallback(callback)
        controller = null
        Island.controller = null
        Island.sysMedia = null
    }

    private fun push() {
        val c = controller
        val md = c?.metadata
        val ctx = app
        if (c == null || md == null) { Island.sysMedia = null; if (Island.playerMedia == null) Island.lyrics = null; return }
        val title = md.getString(MediaMetadata.METADATA_KEY_TITLE) ?: md.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE) ?: ""
        if (title.isBlank()) { Island.sysMedia = null; return }
        val artist = md.getString(MediaMetadata.METADATA_KEY_ARTIST) ?: md.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST)
            ?: md.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE) ?: ""
        val pkg = c.packageName
        val label = ctx?.let { x -> runCatching { x.packageManager.getApplicationLabel(x.packageManager.getApplicationInfo(pkg, 0)).toString() }.getOrNull() } ?: pkg
        val ps = c.playbackState
        val key = "$pkg|$title|$artist"

        if (key != lastArtKey) {
            lastArtKey = key
            val raw: Bitmap? = md.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART) ?: md.getBitmap(MediaMetadata.METADATA_KEY_ART)
                ?: md.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
            val bmp = raw?.let { small(it) }
            lastArt = (bmp?.asImageBitmap()) to (bmp?.let { accentOf(it) } ?: Color(0xFF30D158))
        }

        Island.sysMedia = MediaInfo(
            key = key, title = title, artist = artist, app = label, packageName = pkg,
            art = lastArt.first, accent = lastArt.second,
            durationMs = md.getLong(MediaMetadata.METADATA_KEY_DURATION),
            positionMs = ps?.position ?: 0L,
            positionAt = ps?.lastPositionUpdateTime?.takeIf { it > 0 } ?: SystemClock.elapsedRealtime(),
            speed = ps?.playbackSpeed?.takeIf { it > 0f } ?: 1f,
            playing = ps?.state == PlaybackState.STATE_PLAYING,
        )
        loadLyrics(title, artist, md.getLong(MediaMetadata.METADATA_KEY_DURATION), pkg)
    }

    /** Covers can be 1000+ px (4 MB each): keep a small copy, smaller still on low-RAM phones. */
    private fun small(b: Bitmap): Bitmap {
        val max = Device.artSize
        val side = maxOf(b.width, b.height)
        if (side <= max) return b
        val s = max.toFloat() / side
        return runCatching { Bitmap.createScaledBitmap(b, (b.width * s).toInt().coerceAtLeast(1), (b.height * s).toInt().coerceAtLeast(1), true) }.getOrDefault(b)
    }

    private fun loadLyrics(title: String, artist: String, durationMs: Long, pkg: String) {
        val key = "$title|$artist"
        if (key == lyricsKey) return
        lyricsKey = key
        Island.lyrics = null
        if (!Island.lyricsEnabled) return
        val musicApp = pkg.contains("spotify") || pkg.contains("music") || pkg.contains("deezer") || pkg.contains("tidal") || pkg.contains("boomplay")
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
}

/** Incoming notifications and chats (WhatsApp, Instagram, Telegram, SMS…), from the listener or the accessibility service. */
object Notifs {
    private val main = Handler(Looper.getMainLooper())
    private val recent = ArrayDeque<String>()

    /** Handles one posted notification. ongoing = it's a persistent one (music, downloads…). */
    fun handle(ctx: Context, pkg: String, n: Notification, ongoing: Boolean) {
        if (pkg == ctx.packageName || ongoing) return
        if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return
        if (n.category == Notification.CATEGORY_TRANSPORT || n.category == Notification.CATEGORY_PROGRESS) return
        if (n.extras.containsKey(Notification.EXTRA_MEDIA_SESSION)) return
        val title = n.extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        val text = (n.extras.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: n.extras.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty()
        if (title.isBlank() && text.isBlank()) return
        // the accessibility service and the listener can both see the same notification: show it once
        val sig = "$pkg|$title|$text"
        synchronized(recent) {
            if (sig in recent) return
            recent.addLast(sig)
            while (recent.size > 30) recent.removeFirst()
        }
        val pm = ctx.packageManager
        val app = runCatching { pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString() }.getOrDefault(pkg)
        val iconSize = if (Device.lite) 64 else 96
        val icon = runCatching { pm.getApplicationIcon(pkg).toBitmap(iconSize, iconSize).asImageBitmap() }.getOrNull()

        // chats: WhatsApp, Instagram, Telegram, Messenger, Signal, SMS… anything with an inline "Reply" action
        val replyAction = n.actions?.firstOrNull { a -> a.remoteInputs?.any { it.allowFreeFormInput } == true }
        val isChat = pkg in MESSENGERS || n.category == Notification.CATEGORY_MESSAGE || replyAction != null
        val avatar = runCatching { n.getLargeIcon()?.loadDrawable(ctx)?.toBitmap(iconSize, iconSize)?.asImageBitmap() }.getOrNull()
        val style = if (isChat) runCatching { NotificationCompat.MessagingStyle.extractMessagingStyleFromNotification(n) }.getOrNull() else null

        main.post {
            val chat = if (isChat) upsertChat(pkg, app, title, text, style, replyAction, icon, avatar) else null
            Island.post(IslandEvent.Notification(chat?.app ?: app, chat?.title ?: title, text.replace('\n', ' '), icon, chat, pkg))
        }
    }

    private fun upsertChat(
        pkg: String, app: String, title: String, text: String, style: NotificationCompat.MessagingStyle?,
        reply: Notification.Action?, icon: androidx.compose.ui.graphics.ImageBitmap?, avatar: androidx.compose.ui.graphics.ImageBitmap?,
    ): ChatThread {
        val name = (style?.conversationTitle?.toString() ?: title).ifBlank { app }
            .replace(Regex("""\s*\(\d+ (messages|new messages)\)$"""), "")
        val key = "$pkg|$name"
        val t = Island.chats.firstOrNull { it.key == key } ?: ChatThread(key, app, pkg, name).also { Island.chats.add(0, it) }
        if (Island.chats.indexOf(t) > 0) { Island.chats.remove(t); Island.chats.add(0, t) }
        t.icon = icon
        if (avatar != null) t.avatar = avatar
        if (reply != null) t.reply = reply
        val incoming = style?.messages?.takeLast(8)?.map {
            ChatMsg(it.person?.name?.toString() ?: name, it.text?.toString().orEmpty(), it.timestamp, false)
        }?.filter { it.text.isNotBlank() } ?: listOf(ChatMsg(name, text, System.currentTimeMillis(), false))
        var added = 0
        for (m in incoming) {
            if (t.messages.none { !it.mine && it.text == m.text && kotlin.math.abs(it.time - m.time) < 120_000 }) { t.messages.add(m); added++ }
        }
        val keep = if (Device.lite) 25 else 40
        while (t.messages.size > keep) t.messages.removeAt(0)
        t.unread += added
        t.updated = System.currentTimeMillis()
        while (Island.chats.size > (if (Device.lite) 15 else 25)) Island.chats.removeAt(Island.chats.lastIndex)
        return t
    }

    /** The media session inside a notification (music apps put it there), or null. */
    @Suppress("DEPRECATION")
    fun mediaToken(n: Notification): MediaSession.Token? =
        if (Build.VERSION.SDK_INT >= 33) n.extras.getParcelable(Notification.EXTRA_MEDIA_SESSION, MediaSession.Token::class.java)
        else n.extras.getParcelable(Notification.EXTRA_MEDIA_SESSION)

    private val MESSENGERS = setOf(
        "com.whatsapp", "com.whatsapp.w4b", "com.instagram.android", "com.instagram.barcelona",
        "org.telegram.messenger", "com.facebook.orca", "org.thoughtcrime.securesms", "com.google.android.apps.messaging",
        "com.snapchat.android", "com.discord", "com.facebook.lite", "com.facebook.mlite", "com.transsion.message",
    )
}

/** Reads now-playing media sessions and incoming notifications (on phones that allow Notification access). */
class NotifListener : NotificationListenerService() {

    private val main = Handler(Looper.getMainLooper())
    private var msm: MediaSessionManager? = null

    private val sessionsListener = MediaSessionManager.OnActiveSessionsChangedListener { pick(it) }

    override fun onListenerConnected() {
        Island.listenerConnected = true
        val m = getSystemService(MediaSessionManager::class.java)
        msm = m
        val me = ComponentName(this, NotifListener::class.java)
        MediaHub.onStateChanged = { state ->
            // another app may have started playing → re-pick the "best" session
            if (state?.state == PlaybackState.STATE_PLAYING) refresh()
        }
        MediaHub.onDestroyed = { main.post { refresh() } }
        runCatching {
            m.addOnActiveSessionsChangedListener(sessionsListener, me, main)
            pick(m.getActiveSessions(me))
        }
    }

    override fun onListenerDisconnected() {
        Island.listenerConnected = false
        runCatching { msm?.removeOnActiveSessionsChangedListener(sessionsListener) }
        MediaHub.onStateChanged = null
        MediaHub.onDestroyed = null
        MediaHub.clear()
    }

    private fun refresh() {
        val m = msm ?: return
        runCatching { pick(m.getActiveSessions(ComponentName(this, NotifListener::class.java))) }
    }

    private fun pick(list: List<MediaController>?) {
        val sessions = list.orEmpty()
        val best = sessions.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING } ?: sessions.firstOrNull()
        MediaHub.use(this, best)
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        val n = sbn.notification ?: return
        Notifs.handle(this, sbn.packageName, n, sbn.isOngoing)
    }
}
