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
    private var clapsOn by mutableStateOf(false)

    /** Asks for the microphone (and, on Android 13+, the small "listening" notification), then starts listening. */
    private val askMic = registerForActivityResult(androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions()) { res ->
        val ok = res[android.Manifest.permission.RECORD_AUDIO] == true || ClapService.hasMic(this)
        setClaps(ok)
        if (!ok) android.widget.Toast.makeText(this, "Clap control needs the microphone", android.widget.Toast.LENGTH_LONG).show()
    }

    private fun setClaps(on: Boolean) {
        clapsOn = on
        getSharedPreferences("island", MODE_PRIVATE).edit().putBoolean("claps", on).apply()
        ClapService.sync(this)
    }

    private fun toggleClaps(on: Boolean) {
        if (!on || ClapService.hasMic(this)) { setClaps(on); return }
        val perms = mutableListOf(android.Manifest.permission.RECORD_AUDIO)
        if (android.os.Build.VERSION.SDK_INT >= 33) perms += android.Manifest.permission.POST_NOTIFICATIONS
        askMic.launch(perms.toTypedArray())
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Device.init(this)
        Todo.init(this)
        Voice.init(this)
        Updates.check(this)
        if (intent?.getBooleanExtra("install_update", false) == true) Updates.install(this)
        enableEdgeToEdge()
        val prefs = getSharedPreferences("island", MODE_PRIVATE)
        Island.lyricsEnabled = prefs.getBoolean("lyrics", true)
        Island.talk = prefs.getBoolean("talk", true)
        setContent {
            Setup(
                overlayOn = overlayOn,
                listenerOn = listenerOn,
                openAccessibility = { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) },
                openListener = { startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) },
                openAppInfo = { startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName"))) },
                lyrics = Island.lyricsEnabled,
                setLyrics = { Island.lyricsEnabled = it; prefs.edit().putBoolean("lyrics", it).apply() },
                setTalk = { prefs.edit().putBoolean("talk", it).apply() },
                claps = clapsOn,
                setClaps = ::toggleClaps,
                geminiKey = Talk.key(this),
                saveKey = { Talk.setKey(this, it) },
                startTalk = { TalkActivity.toggle(this) },
                install = { Updates.install(this) },
                checkUpdates = { Updates.check(this, force = true) },
            )
        }
    }

    override fun onResume() {
        super.onResume()
        val enabled = Settings.Secure.getString(contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES).orEmpty()
        overlayOn = enabled.split(':').any { ComponentName.unflattenFromString(it)?.packageName == packageName }
        listenerOn = NotificationManagerCompat.getEnabledListenerPackages(this).contains(packageName)
        clapsOn = getSharedPreferences("island", MODE_PRIVATE).getBoolean("claps", false) && ClapService.hasMic(this)
        ClapService.sync(this)   // (re)start listening while the app is in front: Android allows the microphone from here
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
    setTalk: (Boolean) -> Unit,
    claps: Boolean, setClaps: (Boolean) -> Unit,
    geminiKey: String, saveKey: (String) -> Unit, startTalk: () -> Unit,
    install: () -> Unit, checkUpdates: () -> Unit,
) {
    var lyricsOn by androidx.compose.runtime.remember { mutableStateOf(lyrics) }
    Column(
        Modifier.fillMaxSize().background(Bg).statusBarsPadding().verticalScroll(rememberScrollState()).padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Text("Notch Island", color = Fg, fontSize = 28.sp, fontWeight = FontWeight.Bold)
        Text("Living eyes, media with lyrics, notifications and charging animations around your front camera.", color = Dim, fontSize = 14.sp)

        // a new version is downloaded and verified: one tap to install
        Updates.readyVersion?.let { v ->
            Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Color(0xFF1E3A2A)).clickable(onClick = install).padding(16.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Update ready · v$v ✨", color = Fg, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                    Text("Tap to install. Android will ask you to confirm.", color = Dim, fontSize = 12.5.sp)
                }
                Text("Install", color = Green, fontSize = 14.sp, fontWeight = FontWeight.Bold)
            }
        }

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
        if (Device.goEdition) {
            // Android Go (Tecno Pop, Itel A-series…): there is no Notification access to give; the island's
            // accessibility service shows music and notifications instead
            Step(2, "Media & notifications", "Your phone runs Android Go, which doesn't offer Notification access — that's fine: " +
                "once step 1 is on, the island shows what's playing (artwork, controls, lyrics) and your notifications through it.",
                overlayOn, "Open Accessibility", openAccessibility)
        } else {
            Step(2, "Allow media & notifications", "Lets the island show what's playing (artwork, controls, lyrics) and pop up your notifications.", listenerOn, "Open Notification access", openListener)
        }
        if (Device.lite) Text(
            "Lite mode is on for this phone (${"%.1f".format(Device.ramGb)} GB of memory): lighter animations, smaller pictures, and the music player only loads when you play a song.",
            color = Faint, fontSize = 12.sp, modifier = Modifier.padding(horizontal = 4.dp),
        )

        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Card).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Synced lyrics", color = Fg, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                Text("Karaoke lyrics from LRCLIB while music plays", color = Dim, fontSize = 12.5.sp)
            }
            Switch(lyricsOn, { lyricsOn = it; setLyrics(it) }, colors = SwitchDefaults.colors(checkedTrackColor = Green))
        }
        var talkOn by androidx.compose.runtime.remember { mutableStateOf(Island.talk) }
        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Card).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Eyes talk", color = Fg, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                Text("Little speech bubbles: hi, hello, reactions and friendly nudges", color = Dim, fontSize = 12.5.sp)
            }
            Switch(talkOn, {
                talkOn = it; Island.talk = it; setTalk(it)
            }, colors = SwitchDefaults.colors(checkedTrackColor = Green))
        }
        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Card).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Clap control 👏", color = Fg, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                Text("Clap twice to lock the phone, three times for the power menu. Uses the microphone; ignored while music plays, during calls and when locked. Nothing is recorded.", color = Dim, fontSize = 12.5.sp)
                if (claps && !overlayOn) Text("Turn on the island (step 1) so claps can lock the phone.", color = Color(0xFFFF9F0A), fontSize = 12.sp, modifier = Modifier.padding(top = 4.dp))
            }
            Switch(claps, setClaps, colors = SwitchDefaults.colors(checkedTrackColor = Green))
        }
        // live talk with Gemini
        var key by androidx.compose.runtime.remember { mutableStateOf(geminiKey) }
        var shown by androidx.compose.runtime.remember { mutableStateOf(false) }
        Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Card).padding(16.dp)) {
            Text("Talk with Notch 🎙", color = Fg, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
            Text("A real spoken conversation (Gemini Live). Ask it to open apps, play or pause music, search, add to-dos or lock the phone. " +
                 "It remembers earlier chats. Start it with the mic in the open island, or clap four times.", color = Dim, fontSize = 12.5.sp)
            androidx.compose.foundation.text.BasicTextField(
                value = key, onValueChange = { key = it }, singleLine = true,
                textStyle = androidx.compose.ui.text.TextStyle(color = Fg, fontSize = 14.sp),
                cursorBrush = androidx.compose.ui.graphics.SolidColor(Fg),
                visualTransformation = if (shown) androidx.compose.ui.text.input.VisualTransformation.None else androidx.compose.ui.text.input.PasswordVisualTransformation(),
                modifier = Modifier.padding(top = 12.dp).fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(Color(0x14FFFFFF)).padding(12.dp),
                decorationBox = { inner -> if (key.isEmpty()) Text("Gemini API key (aistudio.google.com)", color = Faint, fontSize = 14.sp); inner() },
            )
            Row(Modifier.padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.clip(RoundedCornerShape(12.dp)).background(Color.White).clickable { saveKey(key); startTalk() }.padding(horizontal = 16.dp, vertical = 10.dp)) {
                    Text(if (key.isBlank()) "Save" else "Save & talk", color = Color.Black, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
                }
                Text(if (shown) "Hide key" else "Show key", color = Dim, fontSize = 13.sp, modifier = Modifier.padding(start = 14.dp).clickable { shown = !shown })
            }
            Text("The key stays in this app on your phone only.", color = Faint, fontSize = 11.5.sp, modifier = Modifier.padding(top = 8.dp))
        }

        // what the island does by itself
        val ctx0 = androidx.compose.ui.platform.LocalContext.current
        @Composable fun Toggle(title: String, body: String, initial: Boolean, onChange: (Boolean) -> Unit) {
            var on by androidx.compose.runtime.remember { mutableStateOf(initial) }
            Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Card).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(title, color = Fg, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                    Text(body, color = Dim, fontSize = 12.5.sp)
                }
                Switch(on, { on = it; onChange(it) }, colors = SwitchDefaults.colors(checkedTrackColor = Green))
            }
        }
        Toggle("Reminders talk ⏰", "When a to-do is due, the island starts a chat, tells you the task and gives ideas", Talk.flag(ctx0, "talk_reminders", true)) { Talk.setFlag(ctx0, "talk_reminders", it) }
        Toggle("Island starts chats 💬", "Now and then (max 3 a day, 9:00–21:00): news about your hobbies or a bit of history", Proactive.enabled(ctx0)) { Talk.setFlag(ctx0, "proactive", it) }
        Toggle("Healthy breaks ☕", "After 2 hours of screen time, a 5-minute break you can't skip (emergency: press and hold 5 s)", Breaks.enabled(ctx0)) { Breaks.set(ctx0, on = it) }

        var voiceOn by androidx.compose.runtime.remember { mutableStateOf(Voice.enabled) }
        val ctx = androidx.compose.ui.platform.LocalContext.current
        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Card).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Voice 🔊", color = Fg, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                Text("The eyes say their speech bubbles out loud with your phone's voice (quiet while music plays)", color = Dim, fontSize = 12.5.sp)
            }
            Switch(voiceOn, { voiceOn = it; Voice.set(ctx, it); if (it) { Island.talk = true; setTalk(true) } }, colors = SwitchDefaults.colors(checkedTrackColor = Green))
        }

        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(Card).clickable(onClick = checkUpdates).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Updates", color = Fg, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                Text(Updates.status.ifEmpty { "v${BuildConfig.VERSION_NAME} · new versions download by themselves; tap to check now" }, color = Dim, fontSize = 12.5.sp)
            }
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
