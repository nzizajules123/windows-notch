package com.windowsnotch.island

import android.content.ComponentName
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.app.NotificationManagerCompat
import com.windowsnotch.island.ui.IslandRoot

/** Setup screen: turn the island on, allow media & notification access, preview the eyes. */
class MainActivity : ComponentActivity() {

    private var overlayOn by mutableStateOf(false)
    private var listenerOn by mutableStateOf(false)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val prefs = getSharedPreferences("island", MODE_PRIVATE)
        Island.lyricsEnabled = prefs.getBoolean("lyrics", true)
        setContent {
            Setup(
                overlayOn = overlayOn,
                listenerOn = listenerOn,
                openAccessibility = { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) },
                openListener = { startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) },
                openAppInfo = { startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName"))) },
                lyrics = Island.lyricsEnabled,
                setLyrics = { Island.lyricsEnabled = it; prefs.edit().putBoolean("lyrics", it).apply() },
            )
        }
    }

    override fun onResume() {
        super.onResume()
        val enabled = Settings.Secure.getString(contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES).orEmpty()
        overlayOn = enabled.split(':').any { ComponentName.unflattenFromString(it)?.packageName == packageName }
        listenerOn = NotificationManagerCompat.getEnabledListenerPackages(this).contains(packageName)
    }
}

private val Bg = Color(0xFF0B0B0F)
private val Card = Color(0xFF16161B)
private val Fg = Color(0xFFF5F5F7)
private val Dim = Color(0xB3F5F5F7)
private val Faint = Color(0x73F5F5F7)
private val Green = Color(0xFF30D158)

@Composable
private fun Setup(
    overlayOn: Boolean, listenerOn: Boolean,
    openAccessibility: () -> Unit, openListener: () -> Unit, openAppInfo: () -> Unit,
    lyrics: Boolean, setLyrics: (Boolean) -> Unit,
) {
    var lyricsOn by androidx.compose.runtime.remember { mutableStateOf(lyrics) }
    Column(
        Modifier.fillMaxSize().background(Bg).statusBarsPadding().verticalScroll(rememberScrollState()).padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Text("Notch Island", color = Fg, fontSize = 28.sp, fontWeight = FontWeight.Bold)
        Text("Living eyes, media with lyrics, notifications and charging animations around your front camera.", color = Dim, fontSize = 14.sp)

        // live preview of the pill (tap the eyes in the open view to poke them)
        Box(Modifier.fillMaxWidth().clip(RoundedCornerShape(22.dp)).background(Color(0xFF2A2D35)).padding(vertical = 26.dp), contentAlignment = Alignment.TopCenter) {
            IslandRoot(Geo(360f, 0f, 0f, 0f, 24f, false), demo = true)
        }
        Text("Preview — tap it to open", color = Faint, fontSize = 12.sp, modifier = Modifier.align(Alignment.CenterHorizontally))

        Step(1, "Turn on the island", "Settings → Accessibility → Notch Island → On. It only draws the island; it doesn't read your screen.", overlayOn, "Open Accessibility", openAccessibility)
        Text(
            "Can't switch it on? Android blocks this for apps installed from a file. Open App info → ⋮ → \"Allow restricted settings\", then try again.",
            color = Faint, fontSize = 12.sp, modifier = Modifier.clickable(onClick = openAppInfo).padding(horizontal = 4.dp),
        )
        Step(2, "Allow media & notifications", "Lets the island show what's playing (artwork, controls, lyrics) and pop up your notifications.", listenerOn, "Open Notification access", openListener)

        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Card).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Synced lyrics", color = Fg, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                Text("Karaoke lyrics from LRCLIB while music plays", color = Dim, fontSize = 12.5.sp)
            }
            Switch(lyricsOn, { lyricsOn = it; setLyrics(it) }, colors = SwitchDefaults.colors(checkedTrackColor = Green))
        }
        Spacer(Modifier.height(8.dp))
        Text("Tap the island to open it · tap outside to close · the eyes react to notifications, volume and your charger.", color = Faint, fontSize = 12.sp)
    }
}

@Composable
private fun Step(n: Int, title: String, body: String, done: Boolean, action: String, onClick: () -> Unit) {
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Card).padding(16.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier.size(26.dp).clip(CircleShape).background(if (done) Green else Color(0x26FFFFFF)),
                contentAlignment = Alignment.Center,
            ) { Text(if (done) "✓" else "$n", color = if (done) Color.Black else Fg, fontSize = 13.sp, fontWeight = FontWeight.Bold) }
            Text(title, color = Fg, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(start = 12.dp).weight(1f))
            if (done) Text("On", color = Green, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
        }
        Text(body, color = Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp))
        if (!done) Box(
            Modifier.padding(top = 12.dp).clip(RoundedCornerShape(12.dp)).background(Color.White).clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 10.dp),
        ) { Text(action, color = Color.Black, fontSize = 14.sp, fontWeight = FontWeight.SemiBold) }
    }
}
