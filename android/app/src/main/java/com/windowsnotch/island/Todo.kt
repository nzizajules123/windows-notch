package com.windowsnotch.island

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale
import java.util.UUID

class TodoItem(val id: String, text: String, done: Boolean, due: Long?, notified: Boolean, val created: Long) {
    var text by mutableStateOf(text)
    var done by mutableStateOf(done)
    var due by mutableStateOf(due)
    var notified by mutableStateOf(notified)

    fun overdue(now: Long = System.currentTimeMillis()) = due != null && !done && due!! < now

    /** "in 12 min", "today 17:30", "tomorrow 09:00", "overdue 5 min"… */
    fun dueText(now: Long = System.currentTimeMillis()): String {
        val d = due ?: return ""
        if (done) return ""
        val span = d - now
        val hm = SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(d))
        if (span < 0) {
            val late = -span / 60000
            return if (late < 60) "overdue ${late.coerceAtLeast(1)} min" else "overdue · $hm"
        }
        if (span < 3600_000) return "in ${((span + 59_999) / 60000).coerceAtLeast(1)} min"
        val day = dayIndex(d) - dayIndex(now)
        return when (day) {
            0L -> "today $hm"
            1L -> "tomorrow $hm"
            else -> SimpleDateFormat("EEE d MMM HH:mm", Locale.getDefault()).format(Date(d))
        }
    }

    private fun dayIndex(t: Long): Long {
        val c = Calendar.getInstance().apply { timeInMillis = t }
        return c.get(Calendar.YEAR) * 400L + c.get(Calendar.DAY_OF_YEAR)
    }

    fun toJson() = JSONObject().put("id", id).put("text", text).put("done", done).put("due", due ?: -1L)
        .put("notified", notified).put("created", created)
}

/** The island's to-do list with reminders (saved in the app's preferences). */
object Todo {
    val items = mutableStateListOf<TodoItem>()
    private var ctx: Context? = null

    fun init(c: Context) {
        if (ctx != null) return
        ctx = c.applicationContext
        val raw = ctx!!.getSharedPreferences("island", Context.MODE_PRIVATE).getString("todo", null) ?: return
        runCatching {
            val a = JSONArray(raw)
            for (i in 0 until a.length()) {
                val o = a.getJSONObject(i)
                items.add(TodoItem(o.getString("id"), o.getString("text"), o.optBoolean("done"),
                    o.optLong("due", -1L).takeIf { it > 0 }, o.optBoolean("notified"), o.optLong("created")))
            }
        }
        sort()
    }

    fun save() {
        val a = JSONArray()
        items.forEach { a.put(it.toJson()) }
        ctx?.getSharedPreferences("island", Context.MODE_PRIVATE)?.edit()?.putString("todo", a.toString())?.apply()
    }

    fun add(text: String, due: Long?): TodoItem {
        val t = TodoItem(UUID.randomUUID().toString(), text, false, due, false, System.currentTimeMillis())
        items.add(t); sort(); save()
        return t
    }

    fun toggle(t: TodoItem) { t.done = !t.done; sort(); save() }
    fun remove(t: TodoItem) { items.remove(t); save() }
    fun snooze(t: TodoItem, ms: Long) { t.due = System.currentTimeMillis() + ms; t.notified = false; sort(); save() }

    /** Open tasks first (soonest reminder first), finished ones last. */
    fun sort() {
        val sorted = items.sortedWith(compareBy<TodoItem>({ it.done }, { it.due ?: Long.MAX_VALUE }, { it.created }))
        if (sorted != items.toList()) { items.clear(); items.addAll(sorted) }
    }

    /** Reminders that just became due (each fires once). */
    fun dueNow(): List<TodoItem> {
        val now = System.currentTimeMillis()
        val due = items.filter { !it.done && !it.notified && it.due != null && it.due!! <= now }
        due.forEach { it.notified = true }
        if (due.isNotEmpty()) save()
        return due
    }

    // ------------------------------------------------------------ "call mom at 5pm", "stretch in 20 min", "report tomorrow"
    fun parse(input: String): Pair<String, Long?> {
        var text = input.trim()
        val now = Calendar.getInstance()
        var due: Calendar? = null
        var day: Calendar? = null
        val ic = RegexOption.IGNORE_CASE
        fun today() = Calendar.getInstance().apply { set(Calendar.HOUR_OF_DAY, 0); set(Calendar.MINUTE, 0); set(Calendar.SECOND, 0); set(Calendar.MILLISECOND, 0) }

        Regex("""\bin\s+(\d+(?:[.,]\d+)?)\s*(m|min|mins|minutes?|h|hr|hrs|hours?)\b""", ic).find(text)?.let { m ->
            val n = m.groupValues[1].replace(',', '.').toDouble()
            val ms = if (m.groupValues[2].startsWith("h", true)) n * 3600_000 else n * 60_000
            due = Calendar.getInstance().apply { timeInMillis += ms.toLong() }
            text = text.removeRange(m.range)
        }
        Regex("""\btomorrow\b""", ic).find(text)?.let { m -> day = today().apply { add(Calendar.DAY_OF_YEAR, 1) }; text = text.removeRange(m.range) }
        Regex("""\btonight\b""", ic).find(text)?.let { m ->
            if (due == null) {
                due = today().apply { set(Calendar.HOUR_OF_DAY, 20) }
                if (due!!.before(now)) due = Calendar.getInstance().apply { add(Calendar.MINUTE, 30) }
                text = text.removeRange(m.range)
            }
        }
        val at = Regex("""\b(?:at|@)\s*(\d{1,2})(?:[:.h](\d{2}))?\s*(am|pm)?\b""", ic).find(text)
            ?: Regex("""\b(\d{1,2})(?:[:.](\d{2}))?\s*(am|pm)\b""", ic).find(text)
        if (at != null && due == null) {
            var h = at.groupValues[1].toInt()
            val min = at.groupValues[2].toIntOrNull() ?: 0
            val ap = at.groupValues[3].lowercase()
            if (ap == "pm" && h < 12) h += 12
            if (ap == "am" && h == 12) h = 0
            if (h < 24 && min < 60) {
                val d = ((day?.clone() as Calendar?) ?: today()).apply { set(Calendar.HOUR_OF_DAY, h); set(Calendar.MINUTE, min) }
                if (day == null && !d.after(now)) d.add(Calendar.DAY_OF_YEAR, 1)
                due = d
                text = text.removeRange(at.range)
            }
        }
        if (due == null && day != null) due = day!!.apply { set(Calendar.HOUR_OF_DAY, 9) }
        text = text.replace(Regex("""\s{2,}"""), " ").trim(' ', ',', '.', '-')
        if (text.isNotEmpty()) text = text[0].uppercaseChar() + text.substring(1)
        return text to due?.timeInMillis
    }
}
