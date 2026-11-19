package com.windowsnotch.island

import android.accessibilityservice.AccessibilityService
import android.annotation.SuppressLint
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.res.Configuration
import android.graphics.PixelFormat
import android.media.AudioManager
import android.os.BatteryManager
import android.os.Build
import android.view.Gravity
import android.view.MotionEvent
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.snapshotFlow
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import androidx.compose.ui.platform.ComposeView
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import androidx.lifecycle.setViewTreeLifecycleOwner
import androidx.savedstate.SavedStateRegistry
import androidx.savedstate.SavedStateRegistryController
import androidx.savedstate.SavedStateRegistryOwner
import androidx.savedstate.setViewTreeSavedStateRegistryOwner
import com.windowsnotch.island.ui.IslandRoot

/** Where the island sits: around the front camera cut-out (dp), plus screen info. */
data class Geo(
    val screenWidthDp: Float,
    val cutoutWidthDp: Float,     // 0 when the phone has no cut-out
    val cutoutHeightDp: Float,
    val cutoutTopDp: Float,
    val statusBarDp: Float,
    val landscape: Boolean,
) {
    val hasCutout get() = cutoutWidthDp > 0f && cutoutWidthDp < screenWidthDp * 0.5f
}

/**
 * Accessibility service whose only job is to draw the island overlay (TYPE_ACCESSIBILITY_OVERLAY can
 * sit over the status bar and the camera hole) and to watch battery / volume.
 */
class IslandService : AccessibilityService() {

    private var view: android.view.View? = null
    private var params: WindowManager.LayoutParams? = null
    private val owner = OverlayOwner()
    private val geo = mutableStateOf(Geo(400f, 0f, 0f, 0f, 24f, false))
    private val wm get() = getSystemService(WindowManager::class.java)
    private var lastVolume = -1
    private var lastPlugged: Boolean? = null

    override fun onServiceConnected() {
        super.onServiceConnected()
        Island.lyricsEnabled = getSharedPreferences("island", MODE_PRIVATE).getBoolean("lyrics", true)
        Island.talk = getSharedPreferences("island", MODE_PRIVATE).getBoolean("talk", true)
        android.os.Handler(mainLooper).postDelayed({ Island.say(Island.greeting()) }, 3600)   // after the droplet intro
        owner.start()
        Todo.init(this)
        // to-do reminders: checked every 15 s; anything that came due while the island was off fires on start
        owner.lifecycleScope.launch {
            kotlinx.coroutines.delay(4000)   // after the droplet intro
            while (true) {
                Todo.dueNow().forEach {
                    Island.post(IslandEvent.Reminder(it))
                    Island.react(EyeMood.Surprised, 900)
                    kotlinx.coroutines.delay(400)
                }
                kotlinx.coroutines.delay(15_000)
            }
        }
        addOverlay()
        registerReceivers()
        Island.overlayRunning = true
        // while a text field in the island is focused, the window must accept the keyboard
        owner.lifecycleScope.launch {
            snapshotFlow { Island.typing }.collect { typing -> setFocusable(typing) }
        }
    }

    private fun setFocusable(on: Boolean) {
        val p = params ?: return
        val v = view ?: return
        p.flags = if (on) p.flags and WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE.inv()
                  else p.flags or WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
        p.softInputMode = WindowManager.LayoutParams.SOFT_INPUT_ADJUST_NOTHING or
            (if (on) WindowManager.LayoutParams.SOFT_INPUT_STATE_VISIBLE else WindowManager.LayoutParams.SOFT_INPUT_STATE_HIDDEN)
        runCatching { wm.updateViewLayout(v, p) }
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) = Unit
    override fun onInterrupt() = Unit

    override fun onDestroy() {
        Island.overlayRunning = false
        runCatching { unregisterReceiver(receiver) }
        view?.let { runCatching { wm.removeView(it) } }
        view = null
        owner.stop()
        super.onDestroy()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        updateGeo()
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun addOverlay() {
        val p = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R)
                layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
            else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P)
                layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
            y = 0
        }
        params = p
        val compose = ComposeView(this).apply { setContent { IslandRoot(geo = geo.value) } }
        // container: Back closes the keyboard / conversation instead of being lost
        val v = object : android.widget.FrameLayout(this) {
            override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
                if (event.keyCode == android.view.KeyEvent.KEYCODE_BACK && (Island.typing || Island.openChat != null)) {
                    if (event.action == android.view.KeyEvent.ACTION_UP) {
                        if (Island.typing) Island.typing = false else Island.openChat = null
                    }
                    return true
                }
                return super.dispatchKeyEvent(event)
            }
        }.apply {
            addView(compose)
            // the island's own YouTube player: a tiny, invisible web view living in this window
            addView(NotchPlayer.createView(this@IslandService).apply { alpha = 0.01f }, android.widget.FrameLayout.LayoutParams(2, 2))
            setViewTreeLifecycleOwner(owner)
            setViewTreeSavedStateRegistryOwner(owner)
            setOnTouchListener { _, ev ->
                if (ev.action == MotionEvent.ACTION_OUTSIDE) Island.outsideTouches++
                false
            }
        }
        view = v
        updateGeo()
        wm.addView(v, p)
    }

    private fun updateGeo() {
        val dm = resources.displayMetrics
        val d = dm.density
        val landscape = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
        var widthPx = dm.widthPixels
        var cut: android.graphics.Rect? = null
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val m = wm.currentWindowMetrics
            widthPx = m.bounds.width()
            cut = m.windowInsets.displayCutout?.boundingRects?.firstOrNull { it.top < 40 * d && it.width() < widthPx / 2 }
        }
        val sbId = resources.getIdentifier("status_bar_height", "dimen", "android")
        val statusBar = if (sbId > 0) resources.getDimensionPixelSize(sbId) / d else 24f
        geo.value = Geo(
            screenWidthDp = widthPx / d,
            cutoutWidthDp = (cut?.width() ?: 0) / d,
            cutoutHeightDp = (cut?.height() ?: 0) / d,
            cutoutTopDp = (cut?.top ?: 0) / d,
            statusBarDp = statusBar,
            landscape = landscape,
        )
        // keep the island centred on the camera hole (it isn't always in the middle)
        params?.let { p ->
            p.x = if (cut != null) cut.centerX() - widthPx / 2 else 0
            view?.let { runCatching { wm.updateViewLayout(it, p) } }
        }
    }

    // ------------------------------------------------------------------ battery & volume
    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            when (i.action) {
                Intent.ACTION_BATTERY_CHANGED -> {
                    val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
                    val scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, 100)
                    if (level >= 0 && scale > 0) Island.battery = level * 100 / scale
                    val plugged = i.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0
                    Island.charging = plugged
                    if (lastPlugged == null) lastPlugged = plugged
                }
                Intent.ACTION_POWER_CONNECTED, Intent.ACTION_POWER_DISCONNECTED -> {
                    val on = i.action == Intent.ACTION_POWER_CONNECTED
                    if (lastPlugged == on) return
                    lastPlugged = on
                    Island.charging = on
                    Island.post(IslandEvent.Charge(on, Island.battery))
                    Island.react(if (on) EyeMood.Love else EyeMood.Surprised, 1800)
                    android.os.Handler(mainLooper).postDelayed({
                        Island.say(if (on) Island.pick("Yum, power ⚡", "Charging up! 🔋") else Island.pick("Unplugged! I'll be careful 🔋", "Running free 🏃"))
                    }, 4200)
                }
                "android.media.VOLUME_CHANGED_ACTION" -> {
                    if (i.getIntExtra("android.media.EXTRA_VOLUME_STREAM_TYPE", -1) != AudioManager.STREAM_MUSIC) return
                    val am = getSystemService(AudioManager::class.java)
                    val v = am.getStreamVolume(AudioManager.STREAM_MUSIC)
                    if (v == lastVolume) return
                    val first = lastVolume < 0
                    lastVolume = v
                    if (first) return
                    val max = am.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)
                    Island.post(IslandEvent.Volume(v.toFloat() / max, v == 0))
                    when {
                        v == 0 -> Island.react(EyeMood.Sleepy, 1400)
                        v == max -> Island.react(EyeMood.Surprised, 900)
                    }
                }
            }
        }
    }

    private fun registerReceivers() {
        val f = IntentFilter().apply {
            addAction(Intent.ACTION_BATTERY_CHANGED)
            addAction(Intent.ACTION_POWER_CONNECTED)
            addAction(Intent.ACTION_POWER_DISCONNECTED)
            addAction("android.media.VOLUME_CHANGED_ACTION")
        }
        ContextCompat.registerReceiver(this, receiver, f, ContextCompat.RECEIVER_EXPORTED)
        lastVolume = getSystemService(AudioManager::class.java).getStreamVolume(AudioManager.STREAM_MUSIC)
    }
}

/** Lifecycle + saved-state owner so a ComposeView can live in a service window. */
class OverlayOwner : LifecycleOwner, SavedStateRegistryOwner {
    private val registry = LifecycleRegistry(this)
    private val saved = SavedStateRegistryController.create(this)
    override val lifecycle: Lifecycle get() = registry
    override val savedStateRegistry: SavedStateRegistry get() = saved.savedStateRegistry

    fun start() {
        saved.performRestore(null)
        registry.currentState = Lifecycle.State.RESUMED
    }

    fun stop() {
        registry.currentState = Lifecycle.State.DESTROYED
    }
}
