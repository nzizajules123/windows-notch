package com.windowsnotch.island

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.View
import android.webkit.JavascriptInterface
import android.webkit.WebChromeClient
import android.webkit.WebView
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

data class YtVideo(val id: String, val title: String, val channel: String, val length: String) {
    val thumbUrl get() = "https://i.ytimg.com/vi/$id/mqdefault.jpg"
}

/**
 * Plays YouTube audio inside the island: YouTube's official embedded player in a tiny hidden WebView
 * that lives in the island's own window. The search results become the "up next" queue; videos whose
 * owners don't allow embedding are skipped.
 */
object NotchPlayer {
    const val PKG = "notch.youtube"
    private val main = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var web: WebView? = null
    private var ready = false
    private var queue = listOf<YtVideo>()
    private var index = -1
    private var t = 0.0
    private var d = 0.0
    private var state = -1          // -1 unstarted, 0 ended, 1 playing, 2 paused, 3 buffering
    private var art: ImageBitmap? = null
    private var accent = Color(0xFFFF453A)
    private var timeAt = SystemClock.elapsedRealtime()

    val current: YtVideo? get() = queue.getOrNull(index)

    /** The island's window: the hidden player is added to it when a song first plays. */
    var host: android.widget.FrameLayout? = null

    /** Creates the hidden player view on first use (it costs 50–100 MB, so not before a song is played). */
    @SuppressLint("SetJavaScriptEnabled")
    private fun ensureWeb() {
        if (web != null) return
        val h = host ?: return
        val w = WebView(h.context)
        w.settings.javaScriptEnabled = true
        w.settings.domStorageEnabled = true
        w.settings.mediaPlaybackRequiresUserGesture = false
        w.webChromeClient = WebChromeClient()
        w.addJavascriptInterface(Bridge(), "NotchBridge")
        w.alpha = 0.01f
        ready = false
        w.loadDataWithBaseURL("https://notch.local/", HTML, "text/html", "utf-8", null)
        h.addView(w, android.widget.FrameLayout.LayoutParams(2, 2))
        web = w
    }

    /** Frees the player's memory (low-RAM phones after a stop, and when the island turns off). */
    fun release() {
        val w = web ?: return
        web = null
        ready = false
        runCatching { (w.parent as? android.view.ViewGroup)?.removeView(w); w.destroy() }
    }

    fun play(list: List<YtVideo>, i: Int) {
        queue = list
        ensureWeb()
        playIndex(i)
    }

    private fun playIndex(i: Int) {
        if (i !in queue.indices) return
        index = i
        t = 0.0; d = 0.0; state = 3; timeAt = SystemClock.elapsedRealtime()
        art = null
        val v = queue[i]
        publish()
        loadArt(v)
        loadLyrics(v)
        js("load('${v.id}')")
    }

    fun playPause() {
        if (current == null) return
        val playing = state == 1 || state == 3
        js(if (playing) "cmd('pause')" else "cmd('play')")
        state = if (playing) 2 else 1
        publish()
    }

    fun next() { if (index + 1 < queue.size) playIndex(index + 1) }
    fun previous() { if (t > 4) seek(0.0) else if (index > 0) playIndex(index - 1) else seek(0.0) }
    fun seek(sec: Double) { js("cmd('seek', $sec)"); t = sec; timeAt = SystemClock.elapsedRealtime(); publish() }

    fun stop() {
        js("cmd('stop')")
        index = -1; state = -1
        Island.playerMedia = null
        // on a small phone, give the memory back once the music has stopped for a little while
        if (Device.lite) main.postDelayed({ if (index < 0) release() }, 60_000)
    }

    // before the player has loaded, load('…') is sent when it says "ready" (see onMessage)
    private fun js(code: String) = main.post { if (ready || !code.startsWith("load(")) web?.evaluateJavascript(code, null) }

    private fun publish() {
        val v = current ?: run { Island.playerMedia = null; return }
        Island.playerMedia = MediaInfo(
            key = "$PKG|${v.id}", title = v.title, artist = v.channel, app = "YouTube · island", packageName = PKG,
            art = art, accent = accent, durationMs = (d * 1000).toLong(), positionMs = (t * 1000).toLong(),
            positionAt = timeAt, speed = 1f, playing = state == 1 || state == 3,
        )
    }

    private fun loadArt(v: YtVideo) = scope.launch {
        val bmp = withContext(Dispatchers.IO) { download("https://i.ytimg.com/vi/${v.id}/hqdefault.jpg") } ?: return@launch
        if (current?.id != v.id) return@launch
        // hqdefault is 4:3 with black bars → crop the middle square
        val side = (bmp.height * 0.75f).toInt()
        val sq = runCatching { Bitmap.createBitmap(bmp, (bmp.width - side) / 2, (bmp.height - side) / 2, side, side) }.getOrDefault(bmp)
        art = sq.asImageBitmap()
        accent = Colors.accentOf(sq)
        publish()
    }

    private fun loadLyrics(v: YtVideo) = scope.launch {
        Island.lyrics = null
        if (!Island.lyricsEnabled) return@launch
        val r = withContext(Dispatchers.IO) { Lyrics.get(v.title, v.channel, 0, false) }
        if (current?.id == v.id) Island.lyrics = r
    }

    private class Bridge {
        @JavascriptInterface
        fun post(json: String) {
            Handler(Looper.getMainLooper()).post { onMessage(json) }
        }
    }

    private fun onMessage(json: String) {
        val o = runCatching { JSONObject(json) }.getOrNull() ?: return
        when (o.optString("type")) {
            "ready" -> { ready = true; current?.let { js("load('${it.id}')") } }
            "state" -> {
                val s = o.optInt("state")
                if (s == 0) { next(); return }
                state = s; publish()
            }
            "time" -> {
                t = o.optDouble("t"); d = o.optDouble("d")
                val s = o.optInt("s")
                state = s
                timeAt = SystemClock.elapsedRealtime()
                publish()
            }
            "error" -> {
                val code = o.optInt("code")
                if (code in setOf(2, 5, 100, 101, 150)) next()
            }
        }
    }

    // ------------------------------------------------------------------ search (no API key)
    /** YouTube results from the public search page (personal use; may need updating if YouTube changes it). */
    fun search(q: String, max: Int = 12): List<YtVideo> {
        val enc = URLEncoder.encode(q, "UTF-8")
        // "videos only" filter first; if YouTube serves a page we can't read, try the plain results page
        for (url in listOf("https://www.youtube.com/results?search_query=$enc&sp=EgIQAQ%253D%253D", "https://www.youtube.com/results?search_query=$enc")) {
            val r = fetchResults(url, max)
            if (r.isNotEmpty()) return r
        }
        // the page layout couldn't be read: ask YouTube's own search service (what youtube.com itself calls)
        return searchApi(q, max)
    }

    private fun searchApi(q: String, max: Int): List<YtVideo> = runCatching {
        val body = JSONObject()
            .put("context", JSONObject().put("client", JSONObject().put("clientName", "WEB").put("clientVersion", "2.20241120.01.00").put("hl", "en").put("gl", "US")))
            .put("query", q)
            .put("params", "EgIQAQ%3D%3D")   // videos only
            .toString()
        val c = URL("https://www.youtube.com/youtubei/v1/search?prettyPrint=false").openConnection() as HttpURLConnection
        c.requestMethod = "POST"; c.doOutput = true
        c.setRequestProperty("Content-Type", "application/json")
        c.setRequestProperty("Origin", "https://www.youtube.com")
        c.setRequestProperty("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36")
        c.connectTimeout = 8000; c.readTimeout = 10000
        val json = try { c.outputStream.use { it.write(body.toByteArray()) }; c.inputStream.bufferedReader().readText() } finally { c.disconnect() }
        val out = ArrayList<YtVideo>()
        walk(JSONObject(json), out, max)
        out
    }.getOrDefault(emptyList())

    private fun fetchResults(url: String, max: Int): List<YtVideo> = runCatching {
        val c = URL(url).openConnection() as HttpURLConnection
        c.setRequestProperty("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36")
        c.setRequestProperty("Accept-Language", "en-US,en;q=0.8")
        c.connectTimeout = 8000; c.readTimeout = 10000
        val html = try { c.inputStream.bufferedReader().readText() } finally { c.disconnect() }
        var start = html.indexOf("var ytInitialData = ")
        if (start < 0) start = html.indexOf("ytInitialData = ")
        if (start < 0) return emptyList()
        start = html.indexOf('{', start)
        val end = html.indexOf(";</script>", start)
        if (end < 0) return emptyList()
        val out = ArrayList<YtVideo>()
        walk(JSONObject(html.substring(start, end)), out, max)
        out
    }.getOrDefault(emptyList())

    private fun walk(e: Any?, out: MutableList<YtVideo>, max: Int) {
        if (out.size >= max) return
        when (e) {
            is JSONObject -> {
                val v = e.optJSONObject("videoRenderer")
                if (v != null && v.has("videoId")) {
                    val id = v.optString("videoId")
                    val len = lengthOf(v)
                    if (len.isNotEmpty() && out.none { it.id == id })
                        out.add(YtVideo(id, runs(v, "title"), runs(v, "ownerText").ifEmpty { runs(v, "longBylineText") }, len))
                    return
                }
                // YouTube's newer layout: lockupViewModel (contentId + lockupMetadataViewModel + a time badge)
                val lv = e.optJSONObject("lockupViewModel")
                if (lv != null && lv.has("contentId")) {
                    val id = lv.optString("contentId")
                    val isVideo = lv.optString("contentType", "LOCKUP_CONTENT_TYPE_VIDEO") == "LOCKUP_CONTENT_TYPE_VIDEO"
                    val meta = lv.optJSONObject("metadata")?.optJSONObject("lockupMetadataViewModel")
                    val title = meta?.optJSONObject("title")?.optString("content").orEmpty()
                    val len = badgeTime(lv)
                    if (isVideo && id.length == 11 && title.isNotEmpty() && len.isNotEmpty() && out.none { it.id == id })
                        out.add(YtVideo(id, title, meta?.let { firstMetadataText(it) }.orEmpty(), len))
                    return
                }
                val keys = e.keys()
                while (keys.hasNext()) walk(e.opt(keys.next()), out, max)
            }
            is JSONArray -> for (i in 0 until e.length()) walk(e.opt(i), out, max)
        }
    }

    /** Video length: "lengthText", else the time badge on the thumbnail. "LIVE" / none → live stream (skipped). */
    private fun lengthOf(v: JSONObject): String {
        runs(v, "lengthText").takeIf { it.isNotEmpty() }?.let { return it }
        val ov = v.optJSONArray("thumbnailOverlays") ?: return ""
        for (i in 0 until ov.length()) {
            val ts = ov.optJSONObject(i)?.optJSONObject("thumbnailOverlayTimeStatusRenderer") ?: continue
            if (ts.optString("style") == "LIVE") return ""
            val txt = runs(ts, "text")
            if (txt.isNotEmpty() && txt[0].isDigit()) return txt
        }
        return ""
    }

    /** The "3:40" time badge somewhere on a lockup's thumbnail ("" for live streams). */
    private fun badgeTime(e: Any?): String {
        when (e) {
            is JSONObject -> {
                e.optJSONObject("thumbnailBadgeViewModel")?.optString("text")?.let { if (it.isNotEmpty() && it[0].isDigit() && ':' in it) return it }
                val keys = e.keys()
                while (keys.hasNext()) badgeTime(e.opt(keys.next())).let { if (it.isNotEmpty()) return it }
            }
            is JSONArray -> for (i in 0 until e.length()) badgeTime(e.opt(i)).let { if (it.isNotEmpty()) return it }
        }
        return ""
    }

    /** The channel name: the first text in a lockup's metadata rows. */
    private fun firstMetadataText(meta: JSONObject): String {
        val rows = meta.optJSONObject("metadata")?.optJSONObject("contentMetadataViewModel")?.optJSONArray("metadataRows") ?: return ""
        for (i in 0 until rows.length()) {
            val parts = rows.optJSONObject(i)?.optJSONArray("metadataParts") ?: continue
            for (j in 0 until parts.length()) parts.optJSONObject(j)?.optJSONObject("text")?.optString("content")?.let { if (it.isNotEmpty()) return it }
        }
        return ""
    }

    private fun runs(v: JSONObject, prop: String): String {
        val t = v.optJSONObject(prop) ?: return ""
        return t.optJSONArray("runs")?.optJSONObject(0)?.optString("text") ?: t.optString("simpleText", "")
    }

    private val thumbs = HashMap<String, ImageBitmap?>()
    suspend fun thumb(v: YtVideo): ImageBitmap? {
        synchronized(thumbs) { if (thumbs.containsKey(v.id)) return thumbs[v.id] }
        val b = withContext(Dispatchers.IO) { download(v.thumbUrl) }?.asImageBitmap()
        synchronized(thumbs) { thumbs[v.id] = b }
        return b
    }

    private fun download(url: String): Bitmap? = runCatching {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 6000; c.readTimeout = 8000
        try { c.inputStream.use { BitmapFactory.decodeStream(it) } } finally { c.disconnect() }
    }.getOrNull()

    private const val HTML = """<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<style>html,body{margin:0;background:#000}</style></head><body><div id="p"></div>
<script>
const post = (m) => NotchBridge.post(JSON.stringify(m));
let player = null, ready = false, pending = null;
const tag = document.createElement('script'); tag.src = 'https://www.youtube.com/iframe_api'; document.head.appendChild(tag);
function onYouTubeIframeAPIReady() {
  player = new YT.Player('p', { width: 200, height: 112,
    playerVars: { autoplay: 1, controls: 0, playsinline: 1, rel: 0, origin: location.origin },
    events: {
      onReady: () => { ready = true; post({type:'ready'}); if (pending) { load(pending); pending = null; } },
      onStateChange: (e) => post({type:'state', state: e.data}),
      onError: (e) => post({type:'error', code: e.data}) } });
}
function load(id) { if (!ready) { pending = id; return; } player.loadVideoById(id); }
function cmd(c, v) { if (!ready) return;
  if (c === 'play') player.playVideo(); else if (c === 'pause') player.pauseVideo();
  else if (c === 'seek') player.seekTo(v, true); else if (c === 'stop') player.stopVideo(); }
setInterval(() => { if (ready && player.getCurrentTime) post({type:'time', t: player.getCurrentTime()||0, d: player.getDuration()||0, s: player.getPlayerState()}); }, 500);
</script></body></html>"""
}

/** Shared colour helper. */
object Colors {
    fun accentOf(b: Bitmap): Color {
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
