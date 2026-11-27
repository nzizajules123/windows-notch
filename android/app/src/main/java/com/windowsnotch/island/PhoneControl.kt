package com.windowsnotch.island

import android.accessibilityservice.AccessibilityService
import android.app.ActivityManager
import android.content.Context
import android.content.Intent
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.media.AudioManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.os.BatteryManager
import android.os.Build
import android.os.Environment
import android.os.StatFs
import android.provider.AlarmClock
import android.provider.Settings
import org.json.JSONArray
import org.json.JSONObject

/**
 * The AI's hands on the phone: volume, flashlight, Settings panels, back / recents / notifications / screenshot,
 * alarms and timers, the dialer, and the phone's health. Nothing here needs extra permissions.
 */
object PhoneControl {
    private fun str(d: String) = JSONObject().put("type", "STRING").put("description", d)
    private fun choice(d: String, vararg v: String) = JSONObject().put("type", "STRING").put("description", d).put("enum", JSONArray(v.toList()))
    private fun int(d: String) = JSONObject().put("type", "INTEGER").put("description", d)
    private fun tool(name: String, desc: String, vararg params: Pair<String, JSONObject>, required: List<String> = emptyList()): JSONObject {
        val o = JSONObject().put("name", name).put("description", desc)
        if (params.isNotEmpty()) {
            val props = JSONObject(); params.forEach { (k, v) -> props.put(k, v) }
            val p = JSONObject().put("type", "OBJECT").put("properties", props)
            if (required.isNotEmpty()) p.put("required", JSONArray(required))
            o.put("parameters", p)
        }
        return o
    }

    fun tools(): List<JSONObject> = listOf(
        tool("set_volume", "Change the phone's media volume: set a level, turn it up or down, mute or unmute.",
            "action" to choice("What to do", "set", "up", "down", "mute", "unmute", "get"), "level" to int("For 'set': 0-100. For up/down: how much (default 10)."),
            required = listOf("action")),
        tool("flashlight", "Turn the phone's flashlight (torch) on or off.", "on" to JSONObject().put("type", "BOOLEAN"), required = listOf("on")),
        tool("open_settings", "Open a phone settings panel: wifi, bluetooth, mobile data, internet, display (brightness), sound, battery, storage, apps, location, notifications, hotspot, airplane mode, date and time, language, security, accessibility, about phone.",
            "page" to str("Which, e.g. 'wifi' or 'battery'"), required = listOf("page")),
        tool("phone_action", "Do a system action: go back, show recent apps, pull down the notifications or quick settings, take a screenshot, or open the power menu.",
            "action" to choice("Which", "back", "recents", "notifications", "quick_settings", "screenshot", "power_menu"), required = listOf("action")),
        tool("set_alarm", "Set an alarm in the clock app.", "hour" to int("0-23"), "minute" to int("0-59"), "label" to str("Optional label"),
            required = listOf("hour", "minute")),
        tool("set_timer", "Start a countdown timer in the clock app.", "minutes" to int("How many minutes"), "seconds" to int("Extra seconds (optional)"),
            "label" to str("Optional label"), required = listOf("minutes")),
        tool("dial", "Open the phone app with a number ready to call (the user presses call).", "number" to str("The phone number"), required = listOf("number")),
        tool("phone_status", "Check the phone's health: battery, free memory, free storage, network, Android version (e.g. 'why is my phone slow?', 'how much space is left?')."),
    )

    /** Null = not one of these actions. */
    fun act(ctx: Context, name: String, a: JSONObject): String? {
        val island = IslandService.instance
        val starter: Context = island ?: ctx
        fun open(i: Intent) = starter.startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        return when (name) {
            "set_volume" -> {
                val am = ctx.getSystemService(AudioManager::class.java)
                val max = am.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)
                var pct = am.getStreamVolume(AudioManager.STREAM_MUSIC) * 100 / max
                val step = if (a.has("level")) a.optInt("level") else 10
                when (a.optString("action")) {
                    "get" -> return "The volume is at $pct%."
                    "mute" -> { am.adjustStreamVolume(AudioManager.STREAM_MUSIC, AudioManager.ADJUST_MUTE, 0); return "Muted." }
                    "unmute" -> { am.adjustStreamVolume(AudioManager.STREAM_MUSIC, AudioManager.ADJUST_UNMUTE, 0); return "Sound is back on." }
                    "up" -> pct = (pct + kotlin.math.abs(step)).coerceAtMost(100)
                    "down" -> pct = (pct - kotlin.math.abs(step)).coerceAtLeast(0)
                    else -> pct = a.optInt("level", pct).coerceIn(0, 100)
                }
                am.setStreamVolume(AudioManager.STREAM_MUSIC, Math.round(pct / 100f * max), AudioManager.FLAG_SHOW_UI)
                "The volume is now $pct%."
            }
            "flashlight" -> runCatching {
                val cm = ctx.getSystemService(CameraManager::class.java)
                val id = cm.cameraIdList.firstOrNull { cm.getCameraCharacteristics(it).get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true }
                    ?: return "This phone has no flashlight."
                val on = a.optBoolean("on")
                cm.setTorchMode(id, on)
                if (on) "Flashlight on 🔦" else "Flashlight off."
            }.getOrElse { "Couldn't switch the flashlight: ${it.message}" }
            "open_settings" -> {
                val p = a.optString("page").lowercase().replace("settings", "").trim()
                val action = when {
                    p.contains("wi") && p.contains("fi") -> if (Build.VERSION.SDK_INT >= 29) Settings.Panel.ACTION_WIFI else Settings.ACTION_WIFI_SETTINGS
                    p.contains("bluetooth") -> Settings.ACTION_BLUETOOTH_SETTINGS
                    p.contains("data") || p.contains("internet") || p.contains("network") ->
                        if (Build.VERSION.SDK_INT >= 29) Settings.Panel.ACTION_INTERNET_CONNECTIVITY else Settings.ACTION_WIRELESS_SETTINGS
                    p.contains("display") || p.contains("bright") || p.contains("screen") -> Settings.ACTION_DISPLAY_SETTINGS
                    p.contains("sound") || p.contains("volume") || p.contains("ring") -> Settings.ACTION_SOUND_SETTINGS
                    p.contains("battery") || p.contains("power") -> Intent.ACTION_POWER_USAGE_SUMMARY
                    p.contains("storage") || p.contains("space") -> Settings.ACTION_INTERNAL_STORAGE_SETTINGS
                    p.contains("app") -> Settings.ACTION_MANAGE_APPLICATIONS_SETTINGS
                    p.contains("location") || p.contains("gps") -> Settings.ACTION_LOCATION_SOURCE_SETTINGS
                    p.contains("notif") -> "android.settings.NOTIFICATION_SETTINGS"   // falls back to Settings below if missing
                    p.contains("hotspot") || p.contains("tether") -> Settings.ACTION_WIRELESS_SETTINGS
                    p.contains("airplane") || p.contains("flight") -> Settings.ACTION_AIRPLANE_MODE_SETTINGS
                    p.contains("date") || p.contains("time") -> Settings.ACTION_DATE_SETTINGS
                    p.contains("language") -> Settings.ACTION_LOCALE_SETTINGS
                    p.contains("security") || p.contains("lock") || p.contains("password") -> Settings.ACTION_SECURITY_SETTINGS
                    p.contains("access") -> Settings.ACTION_ACCESSIBILITY_SETTINGS
                    p.contains("about") || p.contains("phone") || p.contains("info") -> Settings.ACTION_DEVICE_INFO_SETTINGS
                    else -> Settings.ACTION_SETTINGS
                }
                runCatching { open(Intent(action)) }.getOrElse { open(Intent(Settings.ACTION_SETTINGS)) }
                "Opened ${if (action == Settings.ACTION_SETTINGS) "Settings" else a.optString("page")}."
            }
            "phone_action" -> {
                val svc = island ?: return "That needs the island turned on (step 1 in the app)."
                val (g, said) = when (a.optString("action")) {
                    "back" -> AccessibilityService.GLOBAL_ACTION_BACK to "Went back."
                    "recents" -> AccessibilityService.GLOBAL_ACTION_RECENTS to "Showing recent apps."
                    "notifications" -> AccessibilityService.GLOBAL_ACTION_NOTIFICATIONS to "Opened the notifications."
                    "quick_settings" -> AccessibilityService.GLOBAL_ACTION_QUICK_SETTINGS to "Opened quick settings."
                    "screenshot" -> if (Build.VERSION.SDK_INT >= 28) AccessibilityService.GLOBAL_ACTION_TAKE_SCREENSHOT to "Took a screenshot." else return "Screenshots need Android 9 or newer."
                    "power_menu" -> AccessibilityService.GLOBAL_ACTION_POWER_DIALOG to "Opened the power menu."
                    else -> return "Unknown action."
                }
                // a screenshot right away would include the island's own talk bubble: wait a moment
                android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({ svc.performGlobalAction(g) },
                    if (Build.VERSION.SDK_INT >= 28 && g == AccessibilityService.GLOBAL_ACTION_TAKE_SCREENSHOT) 700 else 0)
                said
            }
            "set_alarm" -> runCatching {
                val h = a.optInt("hour").coerceIn(0, 23); val m = a.optInt("minute").coerceIn(0, 59)
                open(Intent(AlarmClock.ACTION_SET_ALARM).putExtra(AlarmClock.EXTRA_HOUR, h).putExtra(AlarmClock.EXTRA_MINUTES, m)
                    .putExtra(AlarmClock.EXTRA_MESSAGE, a.optString("label").ifBlank { "Notch" }).putExtra(AlarmClock.EXTRA_SKIP_UI, true))
                "Alarm set for %02d:%02d.".format(h, m)
            }.getOrElse { "The clock app didn't accept the alarm: ${it.message}" }
            "set_timer" -> runCatching {
                val secs = a.optInt("minutes") * 60 + a.optInt("seconds", 0)
                if (secs <= 0) return "How long should the timer be?"
                open(Intent(AlarmClock.ACTION_SET_TIMER).putExtra(AlarmClock.EXTRA_LENGTH, secs)
                    .putExtra(AlarmClock.EXTRA_MESSAGE, a.optString("label").ifBlank { "Notch" }).putExtra(AlarmClock.EXTRA_SKIP_UI, true))
                "Timer started: ${secs / 60} min${if (secs % 60 > 0) " ${secs % 60} s" else ""}."
            }.getOrElse { "The clock app didn't accept the timer: ${it.message}" }
            "dial" -> {
                val n = a.optString("number").filter { it.isDigit() || it == '+' || it == '*' || it == '#' }
                if (n.isEmpty()) "That isn't a phone number." else { open(Intent(Intent.ACTION_DIAL, Uri.parse("tel:" + Uri.encode(n)))); "The number $n is ready in the phone app — press call." }
            }
            "phone_status" -> status(ctx)
            else -> null
        }
    }

    fun status(ctx: Context): String {
        val sb = StringBuilder()
        val bm = ctx.getSystemService(BatteryManager::class.java)
        sb.append("Battery ${bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)}%${if (bm.isCharging) ", charging" else ""}. ")
        val am = ctx.getSystemService(ActivityManager::class.java)
        val mi = ActivityManager.MemoryInfo().also { am.getMemoryInfo(it) }
        sb.append("Memory: %.1f GB free of %.1f GB%s. ".format(mi.availMem / 1e9, mi.totalMem / 1e9, if (mi.lowMemory) " (low!)" else ""))
        val st = StatFs(Environment.getDataDirectory().path)
        sb.append("Storage: %.1f GB free of %.0f GB. ".format(st.availableBytes / 1e9, st.totalBytes / 1e9))
        val cm = ctx.getSystemService(ConnectivityManager::class.java)
        val caps = cm.getNetworkCapabilities(cm.activeNetwork)
        sb.append(when {
            caps == null -> "Offline. "
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "On Wi-Fi. "
            caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "On mobile data. "
            else -> "Online. "
        })
        sb.append("Android ${Build.VERSION.RELEASE}${if (Device.goEdition) " (Go edition)" else ""}, ${Build.MANUFACTURER} ${Build.MODEL}.")
        return sb.toString()
    }
}
