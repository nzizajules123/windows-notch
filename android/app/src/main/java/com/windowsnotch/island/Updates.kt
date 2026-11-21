package com.windowsnotch.island

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.speech.tts.TextToSpeech
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.FileProvider
import org.json.JSONArray
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.Locale

/**
 * Keeps the app up to date from the project's GitHub releases (tags "android-v…"). When a newer version is out it
 * downloads the APK, checks its SHA-256 against what GitHub published, and asks Android to install it. Android always
 * shows its own "Install update?" screen, so nothing is installed without you tapping Install.
 */
object Updates {
    private const val REPO = "nzizajules123/windows-notch"
    private const val CHANNEL = "updates"

    /** A downloaded, verified update ready to install (shown on the setup screen), and its version. */
    var readyApk by mutableStateOf<File?>(null)
        private set
    var readyVersion by mutableStateOf<String?>(null)
        private set
    var status by mutableStateOf("")
        private set
    @Volatile private var busy = false

    /** Checks at most every 6 hours unless [force]; runs on a background thread. */
    fun check(ctx: Context, force: Boolean = false) {
        val prefs = ctx.getSharedPreferences("island", Context.MODE_PRIVATE)
        val last = prefs.getLong("update_checked", 0)
        if (busy || (!force && System.currentTimeMillis() - last < 6 * 3600_000L)) return
        busy = true
        Thread {
            try {
                status = "Checking for updates…"
                prefs.edit().putLong("update_checked", System.currentTimeMillis()).apply()
                val c = URL("https://api.github.com/repos/$REPO/releases?per_page=30").openConnection() as HttpURLConnection
                c.setRequestProperty("Accept", "application/vnd.github+json")
                c.setRequestProperty("User-Agent", "NotchIsland-Updater")
                c.connectTimeout = 10_000; c.readTimeout = 15_000
                val arr = JSONArray(c.inputStream.bufferedReader().readText())
                // newest Android release
                var best: org.json.JSONObject? = null
                var bestV: List<Int> = emptyList()
                for (i in 0 until arr.length()) {
                    val r = arr.getJSONObject(i)
                    if (r.optBoolean("draft") || r.optBoolean("prerelease")) continue
                    val tag = r.optString("tag_name")
                    if (!tag.startsWith("android-v")) continue
                    val v = parse(tag.removePrefix("android-v"))
                    if (best == null || newer(v, bestV)) { best = r; bestV = v }
                }
                val current = parse(BuildConfig.VERSION_NAME)
                if (best == null || !newer(bestV, current)) { status = "Up to date · v${BuildConfig.VERSION_NAME}"; return@Thread }
                val version = bestV.joinToString(".")
                val assets = best.getJSONArray("assets")
                var url: String? = null; var digest: String? = null; var size = 0L
                for (i in 0 until assets.length()) {
                    val a = assets.getJSONObject(i)
                    if (!a.optString("name").endsWith(".apk")) continue
                    url = a.optString("browser_download_url"); digest = a.optString("digest").takeIf { it.startsWith("sha256:") }; size = a.optLong("size")
                    break
                }
                if (url == null || !url.startsWith("https://github.com/$REPO/releases/download/")) { status = "v$version is out"; return@Thread }

                status = "Downloading v$version…"
                val dir = File(ctx.cacheDir, "updates").apply { mkdirs(); listFiles()?.forEach { it.delete() } }
                val apk = File(dir, "NotchIsland-$version.apk")
                val d = URL(url).openConnection() as HttpURLConnection
                d.instanceFollowRedirects = true
                d.setRequestProperty("User-Agent", "NotchIsland-Updater")
                val md = MessageDigest.getInstance("SHA-256")
                d.inputStream.use { input -> apk.outputStream().use { out ->
                    val buf = ByteArray(1 shl 16)
                    while (true) { val n = input.read(buf); if (n < 0) break; out.write(buf, 0, n); md.update(buf, 0, n) }
                } }
                val hash = md.digest().joinToString("") { "%02x".format(it) }
                val ok = if (digest != null) hash.equals(digest.removePrefix("sha256:"), true) else size <= 0 || apk.length() == size
                if (!ok) { apk.delete(); status = "Update download didn't match — will try later"; return@Thread }

                readyApk = apk
                readyVersion = version
                status = "v$version is ready to install"
                notifyReady(ctx, version)
            } catch (e: Exception) {
                status = "Couldn't check for updates"
            } finally { busy = false }
        }.start()
    }

    /** Opens Android's installer for the downloaded update (Android asks before installing). */
    fun install(ctx: Context) {
        val apk = readyApk ?: return
        if (Build.VERSION.SDK_INT >= 26 && !ctx.packageManager.canRequestPackageInstalls()) {
            // first time: let Notch Island install updates (Settings → Install unknown apps)
            ctx.startActivity(Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${ctx.packageName}"))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            return
        }
        val uri = FileProvider.getUriForFile(ctx, "${ctx.packageName}.files", apk)
        ctx.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK))
    }

    private fun notifyReady(ctx: Context, version: String) {
        val nm = ctx.getSharedPreferences("island", Context.MODE_PRIVATE)
        if (nm.getString("update_notified", null) == version) return
        nm.edit().putString("update_notified", version).apply()
        val mgr = ctx.getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26 && mgr.getNotificationChannel(CHANNEL) == null)
            mgr.createNotificationChannel(NotificationChannel(CHANNEL, "Updates", NotificationManager.IMPORTANCE_DEFAULT))
        val open = PendingIntent.getActivity(ctx, 3, Intent(ctx, MainActivity::class.java).putExtra("install_update", true)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val b = if (Build.VERSION.SDK_INT >= 26) android.app.Notification.Builder(ctx, CHANNEL) else @Suppress("DEPRECATION") android.app.Notification.Builder(ctx)
        runCatching {
            mgr.notify(9, b.setSmallIcon(android.R.drawable.stat_sys_download_done)
                .setContentTitle("Notch Island v$version is ready ✨")
                .setContentText("Tap to install the update")
                .setContentIntent(open).setAutoCancel(true).build())
        }
        Island.hint("Update ready · v$version ✨")
    }

    private fun parse(v: String) = v.split('.').map { it.takeWhile(Char::isDigit).toIntOrNull() ?: 0 }
    private fun newer(a: List<Int>, b: List<Int>): Boolean {
        for (i in 0 until maxOf(a.size, b.size)) {
            val x = a.getOrElse(i) { 0 }; val y = b.getOrElse(i) { 0 }
            if (x != y) return x > y
        }
        return false
    }
}

/**
 * The eyes' voice: speech bubbles read out loud with the phone's own text-to-speech (no account needed).
 * Quiet while music plays or during a live chat.
 */
object Voice {
    private var tts: TextToSpeech? = null
    private var ready = false
    var enabled = false
        private set

    fun init(ctx: Context) {
        enabled = ctx.getSharedPreferences("island", Context.MODE_PRIVATE).getBoolean("voice", false)
        if (enabled) start(ctx)
        Island.speaker = { speak(it) }
    }

    fun set(ctx: Context, on: Boolean) {
        enabled = on
        ctx.getSharedPreferences("island", Context.MODE_PRIVATE).edit().putBoolean("voice", on).apply()
        if (on) start(ctx) else { tts?.stop() }
    }

    private fun start(ctx: Context) {
        if (tts != null) return
        tts = TextToSpeech(ctx.applicationContext) { st ->
            ready = st == TextToSpeech.SUCCESS
            if (ready) tts?.language = Locale.getDefault()
        }
    }

    private fun speak(text: String) {
        if (!enabled || !ready || Island.talking || Island.media?.playing == true) return
        val clean = text.filter { !Character.isSurrogate(it) && Character.getType(it) != Character.OTHER_SYMBOL.toInt() }
            .replace("…", "...").replace(Regex("\\s+"), " ").trim()
        if (clean.none { it.isLetter() }) return
        tts?.speak(clean, TextToSpeech.QUEUE_FLUSH, null, "eyes")
    }
}
