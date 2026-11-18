package com.windowsnotch.island

import android.app.PendingIntent
import android.app.RemoteInput
import android.app.SearchManager
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.MediaStore
import org.json.JSONArray
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/** Replying to chats through the messaging app's own notification "Reply" action. */
object Chats {
    fun reply(ctx: Context, t: ChatThread, text: String): Boolean {
        val action = t.reply ?: return false
        val inputs = action.remoteInputs ?: return false
        val intent = Intent()
        val results = Bundle().apply { inputs.forEach { putCharSequence(it.resultKey, text) } }
        RemoteInput.addResultsToIntent(inputs, intent, results)
        return try {
            action.actionIntent.send(ctx, 0, intent)
            t.messages.add(ChatMsg("You", text, System.currentTimeMillis(), true))
            t.unread = 0
            t.updated = System.currentTimeMillis()
            true
        } catch (e: PendingIntent.CanceledException) {
            t.reply = null   // the conversation's notification is gone; open the app instead
            false
        }
    }

    fun openApp(ctx: Context, t: ChatThread) {
        ctx.packageManager.getLaunchIntentForPackage(t.pkg)?.let {
            it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            runCatching { ctx.startActivity(it) }
        }
    }
}

/** YouTube / YouTube Music: search suggestions and "play the top result". */
object YouTube {
    private const val MUSIC = "com.google.android.apps.youtube.music"
    private const val YT = "com.google.android.youtube"

    /** YouTube's own search-box suggestions. */
    fun suggestions(q: String): List<String> = runCatching {
        val c = URL("https://suggestqueries.google.com/complete/search?client=firefox&ds=yt&q=" + URLEncoder.encode(q, "UTF-8"))
            .openConnection() as HttpURLConnection
        c.connectTimeout = 5000; c.readTimeout = 5000
        try {
            val arr = JSONArray(c.inputStream.bufferedReader().readText()).getJSONArray(1)
            (0 until minOf(arr.length(), 7)).map { arr.getString(it) }
        } finally { c.disconnect() }
    }.getOrDefault(emptyList())

    /** Plays the top result in YouTube Music; falls back to YouTube search, then the browser. */
    fun play(ctx: Context, q: String) {
        val i = Intent(MediaStore.INTENT_ACTION_MEDIA_PLAY_FROM_SEARCH)
            .setPackage(MUSIC)
            .putExtra(SearchManager.QUERY, q)
            .putExtra(MediaStore.EXTRA_MEDIA_FOCUS, "vnd.android.cursor.item/*")
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        try { ctx.startActivity(i) } catch (_: ActivityNotFoundException) { search(ctx, q) } catch (_: SecurityException) { search(ctx, q) }
    }

    fun search(ctx: Context, q: String) {
        val i = Intent(Intent.ACTION_SEARCH).setPackage(YT).putExtra(SearchManager.QUERY, q).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        try { ctx.startActivity(i) } catch (_: Exception) {
            runCatching {
                ctx.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://m.youtube.com/results?search_query=" + Uri.encode(q))).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
        }
    }
}
