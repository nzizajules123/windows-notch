package com.windowsnotch.island

import android.app.ActivityManager
import android.content.Context

/**
 * What kind of phone this is. Small phones (Android Go like the Tecno Pop 10C, or under ~3 GB of RAM) get "lite" mode:
 * the hidden YouTube player only loads when a song plays and is freed after, idle animations rest, covers are kept small.
 * On Android Go, Notification access can't be granted at all, so music and alerts come through the accessibility service.
 */
object Device {
    /** Android Go edition (the system says "low RAM device"): Notification access isn't available. */
    @Volatile var goEdition = false
        private set

    /** Lighter everything: Go phones and phones with less than ~3 GB of RAM. */
    @Volatile var lite = false
        private set

    @Volatile var ramGb = 0f
        private set

    fun init(ctx: Context) {
        val am = ctx.getSystemService(ActivityManager::class.java) ?: return
        goEdition = am.isLowRamDevice
        val mi = ActivityManager.MemoryInfo()
        am.getMemoryInfo(mi)
        ramGb = mi.totalMem / 1_073_741_824f
        lite = goEdition || mi.totalMem < 3_200_000_000L
    }

    /** Largest side, in pixels, for album covers kept in memory. */
    val artSize get() = if (lite) 256 else 512
}
