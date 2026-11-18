package com.windowsnotch.island

import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.text.Normalizer

data class LyricLine(val timeMs: Long, val text: String)

class LyricsResult(val lines: List<LyricLine>, val match: String) {
    fun indexAt(posMs: Long): Int {
        var idx = -1
        for (i in lines.indices) if (lines[i].timeMs <= posMs + 250) idx = i else break
        return idx
    }

    /** When the karaoke fill of a line should be complete (not dragged over instrumental gaps). */
    fun lineEnd(idx: Int): Long {
        val start = lines[idx].timeMs
        val next = if (idx + 1 < lines.size) lines[idx + 1].timeMs else start + 5000
        val natural = ((maxOf(1.4, lines[idx].text.length * 0.075) + 0.6) * 1000).toLong()
        return start + minOf(next - start, natural)
    }
}

/** Time-synced lyrics from LRCLIB, matched carefully so videos don't all get the same song. */
object Lyrics {
    private val cache = HashMap<String, LyricsResult?>()
    private val stamp = Regex("""\[(\d+):(\d+(?:\.\d+)?)]""")

    fun get(rawTitle: String, rawArtist: String, durationMs: Long, fromMusicApp: Boolean): LyricsResult? {
        if (rawTitle.isBlank()) return null
        val (title, artist) = normalize(rawTitle, rawArtist, fromMusicApp)
        if (title.isEmpty()) return null
        val key = "$title|$artist".lowercase()
        synchronized(cache) { if (cache.containsKey(key)) return cache[key] }

        val dur = durationMs / 1000.0
        var result: LyricsResult? = null
        runCatching {
            if (artist.isNotEmpty()) {
                var q = "track_name=${e(title)}&artist_name=${e(artist)}"
                if (fromMusicApp && dur > 5) q += "&duration=${dur.toInt()}"
                result = fetch("https://lrclib.net/api/get?$q")?.let { JSONObject(it) }
                    ?.takeIf { accept(it, title, artist, dur, fromMusicApp) }?.let { parse(it) }
                if (result == null)
                    result = search("https://lrclib.net/api/search?track_name=${e(title)}&artist_name=${e(artist)}", title, artist, dur, fromMusicApp)
            }
            if (result == null)
                result = search("https://lrclib.net/api/search?q=${e("$title $artist".trim())}", title, artist, dur, fromMusicApp)
            if (result == null && !fromMusicApp && artist.isNotEmpty() && dur > 30)
                result = search("https://lrclib.net/api/search?track_name=${e(title)}", title, "", dur, fromMusicApp)
        }
        synchronized(cache) { cache[key] = result }
        return result
    }

    fun normalize(title: String, artist: String, fromMusicApp: Boolean): Pair<String, String> {
        val ic = RegexOption.IGNORE_CASE
        var a = artist.replace(Regex("""\s*(VEVO|-\s*Topic|Official|TV)\s*$""", ic), "").trim()
        if (Regex("^(YouTube|Chrome|Google Chrome|Unknown.*)$", ic).matches(a)) a = ""
        var t = title
        if (fromMusicApp) {
            t = t.replace(Regex("""\s+-\s+(.*(remaster|live|version|edit|mix|mono|stereo|demo|bonus).*)$""", ic), "")
            t = t.replace(Regex("""\s*[(\[](feat|ft|with)\.?\s[^)\]]*[)\]]""", ic), "")
            return t.trim() to a
        }
        t = t.replace(Regex("""\s+-\s+YouTube( Music)?$""", ic), "")
        t = t.replace(Regex("""\s*[(\[{【][^)\]}】]*[)\]}】]"""), "")
        t = t.replace(Regex("""\s*(\||//|#).*$"""), "")
        t = t.replace(Regex("""\b(official\s+)?(music\s+|lyrics?\s+|visuali[sz]er\s+)?(video|audio|clip)\b""", ic), "")
        t = t.replace(Regex("""\b(lyrics?|paroles|letra|hd|hq|4k|mv|clip officiel)\b""", ic), "")
        t = t.replace(Regex("""\s+(ft\.?|feat\.?|featuring)\s+[^-–—]*""", ic), " ")
        Regex("""^\s*(.+?)\s+[-–—~]\s+(.+?)\s*$""").find(t)?.let { a = it.groupValues[1].trim(); t = it.groupValues[2].trim() }
        t = t.replace(Regex("[\"“”«»]"), "").replace(Regex("""\s{2,}"""), " ").trim(' ', '-', '–', '—', ':', '.')
        return t to a
    }

    private fun e(s: String) = URLEncoder.encode(s, "UTF-8")

    private fun fetch(url: String): String? {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 8000
        c.readTimeout = 8000
        c.setRequestProperty("User-Agent", "NotchIsland/1.0")
        return try {
            if (c.responseCode !in 200..299) null else c.inputStream.bufferedReader().readText()
        } finally { c.disconnect() }
    }

    private fun search(url: String, title: String, artist: String, dur: Double, fromMusicApp: Boolean): LyricsResult? {
        val arr = fetch(url)?.let { JSONArray(it) } ?: return null
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            if (!accept(o, title, artist, dur, fromMusicApp)) continue
            parse(o)?.let { return it }
        }
        return null
    }

    private fun norm(s: String): String {
        val d = Normalizer.normalize(s, Normalizer.Form.NFD).replace(Regex("\\p{Mn}+"), "")
        return d.lowercase().map { if (it.isLetterOrDigit()) it else ' ' }.joinToString("").replace(Regex("\\s+"), " ").trim()
    }

    private fun similar(a0: String, b0: String): Boolean {
        val a = norm(a0); val b = norm(b0)
        if (a.isEmpty() || b.isEmpty()) return false
        if (a == b || a.contains(b) || b.contains(a)) return true
        val ta = a.split(' ').toSet(); val tb = b.split(' ').toSet()
        return ta.intersect(tb).size >= maxOf(1.0, minOf(ta.size, tb.size) * 0.6)
    }

    private fun accept(o: JSONObject, title: String, artist: String, dur: Double, fromMusicApp: Boolean): Boolean {
        if (!similar(o.optString("trackName"), title)) return false
        val artistOk = artist.isEmpty() || similar(o.optString("artistName"), artist)
        var durOk = true
        if (dur > 30 && o.has("duration")) {
            val diff = o.optDouble("duration") - dur
            durOk = if (fromMusicApp) kotlin.math.abs(diff) <= 4 else diff <= 6 && diff >= -60
        }
        return if (artist.isEmpty()) durOk else artistOk && (durOk || fromMusicApp)
    }

    private fun parse(o: JSONObject): LyricsResult? {
        val synced = o.optString("syncedLyrics", "")
        if (synced.isBlank() || synced == "null") return null
        val lines = ArrayList<LyricLine>()
        for (raw in synced.split('\n')) {
            val ms = stamp.findAll(raw).toList()
            if (ms.isEmpty()) continue
            var text = stamp.replace(raw, "").replace(Regex("""<\d+:\d+(?:\.\d+)?>"""), "").replace(Regex("""\s{2,}"""), " ").trim()
            if (text.isEmpty()) text = "♪"
            for (m in ms) {
                val t = m.groupValues[1].toLong() * 60_000 + (m.groupValues[2].toDouble() * 1000).toLong()
                lines.add(LyricLine(t, text))
            }
        }
        lines.sortBy { it.timeMs }
        return if (lines.isEmpty()) null else LyricsResult(lines, "\"${o.optString("trackName")}\" by \"${o.optString("artistName")}\"")
    }
}
