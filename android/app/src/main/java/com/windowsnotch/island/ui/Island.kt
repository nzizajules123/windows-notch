package com.windowsnotch.island.ui

import android.os.SystemClock
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.EaseInCubic
import androidx.compose.animation.core.EaseInQuad
import androidx.compose.animation.core.EaseOutQuad
import androidx.compose.animation.core.EaseOutQuint
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.StartOffset
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.wrapContentSize
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.zIndex
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.offset
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.windowsnotch.island.EyeMood
import com.windowsnotch.island.Geo
import com.windowsnotch.island.Island
import com.windowsnotch.island.IslandEvent
import com.windowsnotch.island.MediaInfo
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

private enum class Mode { Intro, Collapsed, Peek, Expanded }

private val Fg = Color(0xFFF5F5F7)
private val FgDim = Color(0xB3F5F5F7)
private val FgFaint = Color(0x73F5F5F7)
private val Glass = Color(0x1FFFFFFF)
private val GlassStroke = Color(0x26FFFFFF)
private val Green = Color(0xFF30D158)

@Composable
fun IslandRoot(geo: Geo, demo: Boolean = false) {
    if (geo.landscape && !demo) { Box(Modifier.size(1.dp)); return }

    val pillH = if (geo.hasCutout) (geo.cutoutHeightDp + 10f).coerceIn(28f, 40f) else 30f
    val top = if (demo) 0f else if (geo.hasCutout) (geo.cutoutTopDp - (pillH - geo.cutoutHeightDp) / 2f).coerceAtLeast(2f) else 6f
    val camGap = if (geo.hasCutout) geo.cutoutWidthDp + 12f else 40f
    val sw = geo.screenWidthDp

    val ctx = androidx.compose.ui.platform.LocalContext.current
    var mode by remember { mutableStateOf(if (demo) Mode.Collapsed else Mode.Intro) }
    var peek by remember { mutableStateOf<IslandEvent?>(null) }
    var lastTouch by remember { mutableLongStateOf(0L) }
    var tab by remember { mutableIntStateOf(0) }          // 0 media · 1 chats · 2 search · 3 alerts · 4 to-do
    val media = Island.media
    val playing = media?.playing == true
    val accent = media?.accent ?: Green
    val volume = peek as? IslandEvent.Volume
    val hasLyrics = Island.lyrics != null && media != null
    /** Notifications that arrived while the current pop-up was up (newest first) → "View all · N". */
    val stack = remember { androidx.compose.runtime.mutableStateListOf<IslandEvent.Notification>() }

    // the current lyric line, shown under the small pill while music plays
    var lyric by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(playing, Island.lyrics, media?.key) {
        if (!playing || Island.lyrics == null) { lyric = null; return@LaunchedEffect }
        while (true) {
            val m = Island.media; val ly = Island.lyrics
            lyric = if (m != null && ly != null && m.playing) ly.indexAt(m.livePosition()).let { i ->
                if (i >= 0) ly.lines[i].text.takeIf { it.isNotBlank() && it != "♪" } else null
            } else null
            delay(250)
        }
    }

    // ---- size of the island for each state
    val slot = when {
        playing -> 48f
        geo.hasCutout -> 76f      // time + eyes | battery %
        else -> 56f
    }
    // live talk captions win over lyrics under the pill
    val talkLine = if (Island.talking) (Island.talkLine ?: if (Island.talkState == "connecting") "🎙  Connecting…" else "🎙  Listening…") else null
    val ticker = (talkLine != null || lyric != null) && volume == null
    val (tw, th, tr) = when (mode) {
        Mode.Intro -> Triple(pillH, pillH, pillH / 2)
        Mode.Collapsed -> when {
            volume != null -> Triple(minOf(sw - 24f, 300f), pillH, pillH / 2)
            ticker -> Triple(maxOf(camGap + 2 * slot, minOf(sw - 24f, 330f)), pillH + 24f, 18f)
            else -> Triple(camGap + 2 * slot, pillH, pillH / 2)
        }
        Mode.Peek -> Triple(minOf(sw - 16f, 390f), pillH + if (peek is IslandEvent.Notification || peek is IslandEvent.Reminder) 96f else 56f, 28f)
        Mode.Expanded -> Triple(minOf(sw - 12f, 440f), pillH + 44f + when {
            tab != 0 -> 290f
            media == null -> 132f
            hasLyrics -> 232f
            else -> 192f
        }, 34f)
    }
    val w = remember { Animatable(pillH) }
    val h = remember { Animatable(pillH) }
    val r = remember { Animatable(pillH / 2) }
    LaunchedEffect(tw, th, tr) {
        launch { w.animateTo(tw, if (mode == Mode.Collapsed) spring(0.5f, 260f) else spring(0.68f, 280f)) }
        launch { h.animateTo(th, spring(0.72f, 260f)) }
        launch { r.animateTo(tr, tween(320)) }
    }

    // ---- events → peek / volume HUD
    LaunchedEffect(Island.eventId) {
        val e = Island.event ?: return@LaunchedEffect
        if (mode == Mode.Intro) return@LaunchedEffect
        // while open only reminders interrupt (notifications are kept in the Alerts tab)
        if (mode == Mode.Expanded && (e !is IslandEvent.Reminder || Island.typing)) return@LaunchedEffect
        if (e is IslandEvent.Notification) {
            Island.react(EyeMood.Surprised, 700)
            if (peek !is IslandEvent.Notification) stack.clear()
            stack.add(0, e)
        }
        peek = e
        mode = if (e is IslandEvent.Volume) Mode.Collapsed else Mode.Peek
        delay(when (e) {
            is IslandEvent.Volume -> 1600
            is IslandEvent.Charge -> 3400
            is IslandEvent.Reminder -> 20_000
            else -> if (stack.size > 1) 6500 else 4800
        })
        peek = null
        stack.clear()
        if (mode == Mode.Peek) mode = Mode.Collapsed
    }
    // ---- close when touching outside, or after a few idle seconds
    var seenOutside by remember { mutableIntStateOf(Island.outsideTouches) }
    LaunchedEffect(Island.outsideTouches) {
        if (Island.outsideTouches != seenOutside) {
            seenOutside = Island.outsideTouches
            // while typing, touches on the keyboard count as "outside" — ignore them
            if (!Island.typing && (mode == Mode.Expanded || mode == Mode.Peek)) mode = Mode.Collapsed
        }
    }
    LaunchedEffect(mode, lastTouch, Island.typing, tab) {
        if (mode == Mode.Expanded && !demo && !Island.typing) { delay(if (tab == 0) 7000 else 20000); mode = Mode.Collapsed }
    }
    // a message pop-up (or the setup screen) asked to open a conversation
    LaunchedEffect(Island.openChat) {
        if (Island.openChat != null && mode != Mode.Intro) { tab = 1; peek = null; mode = Mode.Expanded }
    }
    LaunchedEffect(mode) {
        if (mode != Mode.Expanded) { Island.typing = false; Island.openChat = null }
    }

    val open = mode == Mode.Expanded || mode == Mode.Peek
    Box(Modifier.padding(top = top.dp).padding(horizontal = if (open) 10.dp else 0.dp).padding(bottom = if (open) 16.dp else 0.dp).wrapContentSize(),
        contentAlignment = Alignment.TopCenter) {

        if (mode == Mode.Intro) {
            DropletIntro(pillH) { mode = Mode.Collapsed; Island.react(EyeMood.Happy, 1100) }
            return@Box
        }

        // the eyes' speech bubble pops out under the pill
        var bubble by remember { mutableStateOf<String?>(null) }
        LaunchedEffect(Island.speechId) {
            val s = Island.speech ?: return@LaunchedEffect
            bubble = s
            delay(2700)
            bubble = null
        }
        val shape = RoundedCornerShape(r.value.dp)
        // smoked glass washed with the colour of what's playing (not flat black)
        val bg = if (open) Brush.verticalGradient(listOf(lerpC(Color(0xFF16161B), accent, 0.17f), Color(0xFF111115), Color(0xFF0C0C0F)))
                 else Brush.horizontalGradient(listOf(lerpC(Color(0xFF17171C), accent, 0.24f), Color(0xFF0E0E12), Color(0xFF0E0E12), lerpC(Color(0xFF17171C), accent, 0.14f)))
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Box(
            Modifier
                .size(w.value.dp, h.value.dp)
                .shadow(if (open) 18.dp else 6.dp, shape, ambientColor = Color.Black, spotColor = Color.Black)
                .clip(shape)
                .background(bg)
                .border(0.8.dp, if (open) GlassStroke else Color(0x1CFFFFFF), shape)
                .clickable(remember { MutableInteractionSource() }, indication = null) {
                    lastTouch = SystemClock.elapsedRealtime()
                    if (mode != Mode.Expanded) {
                        val chat = (peek as? IslandEvent.Notification)?.chat
                        tab = when {
                            peek is IslandEvent.Reminder -> 4
                            chat != null -> 1
                            stack.size > 1 -> 3
                            media == null && Island.chats.any { it.unread > 0 } -> 1
                            else -> 0
                        }
                        if (chat != null) Island.openChat = chat
                        peek = null
                        mode = Mode.Expanded
                    }
                },
        ) {
            when (mode) {
                Mode.Collapsed -> Collapsed(geo, pillH, camGap, media, accent, volume, if (ticker) (talkLine ?: lyric) else null)
                Mode.Peek -> when (val p = peek) {
                    is IslandEvent.Reminder -> ReminderCard(
                        p.item, pillH,
                        onDone = { if (!p.item.done) com.windowsnotch.island.Todo.toggle(p.item); Island.react(EyeMood.Happy, 1200); peek = null; mode = Mode.Collapsed },
                        onSnooze = { com.windowsnotch.island.Todo.snooze(p.item, 10 * 60_000L); peek = null; mode = Mode.Collapsed },
                        onDismiss = { peek = null; mode = Mode.Collapsed },
                    )
                    is IslandEvent.Notification -> NotificationCard(
                        p, pillH,
                        more = stack.size - 1,
                        onViewAll = { tab = 3; peek = null; stack.clear(); mode = Mode.Expanded },
                        onReply = { p.chat?.let { Island.openChat = it } },
                        onOpen = {
                            ctx.packageManager.getLaunchIntentForPackage(p.pkg)?.let {
                                it.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK); runCatching { ctx.startActivity(it) }
                            }
                            peek = null; mode = Mode.Collapsed
                        },
                        onDismiss = { peek = null; mode = Mode.Collapsed },
                    )
                    is IslandEvent.Charge -> ChargeCard(p, pillH)
                    else -> Collapsed(geo, pillH, camGap, media, accent, null, null)
                }
                Mode.Expanded -> Expanded(pillH, media, accent, tab, { tab = it; lastTouch = SystemClock.elapsedRealtime() }, { mode = Mode.Collapsed }) { lastTouch = SystemClock.elapsedRealtime() }
                Mode.Intro -> Unit
            }
        }
        androidx.compose.animation.AnimatedVisibility(
            visible = bubble != null && mode == Mode.Collapsed && !playing,   // the eyes step aside while music plays
            enter = fadeIn(tween(160)) + scaleIn(spring(0.5f, 500f), initialScale = 0.5f, transformOrigin = androidx.compose.ui.graphics.TransformOrigin(0.5f, 0f)),
            exit = fadeOut(tween(180)) + androidx.compose.animation.scaleOut(targetScale = 0.85f),
        ) {
            SpeechBubble(bubble ?: "")
        }
        }
    }
}

// =====================================================================================
//  Launch intro: a droplet falls, bounces like a ball, and is pulled up into the island
// =====================================================================================
@Composable
private fun DropletIntro(pillH: Float, done: () -> Unit) {
    val y = remember { Animatable(-30f) }
    val sx = remember { Animatable(0.84f) }
    val sy = remember { Animatable(1.22f) }
    val floor = 78f
    LaunchedEffect(Unit) {
        suspend fun splat(x: Float, yy: Float) {
            sx.snapTo(x); sy.snapTo(yy)
            launch { sx.animateTo(1f, spring(0.35f, 700f)) }
            launch { sy.animateTo(1f, spring(0.35f, 700f)) }
        }
        delay(250)
        y.animateTo(floor, tween(430, easing = EaseInQuad)); splat(1.42f, 0.62f)
        y.animateTo(floor - 28f, tween(220, easing = EaseOutQuad))
        y.animateTo(floor, tween(210, easing = EaseInQuad)); splat(1.25f, 0.78f)
        y.animateTo(floor - 10f, tween(115, easing = EaseOutQuad))
        y.animateTo(floor, tween(110, easing = EaseInQuad)); splat(1.1f, 0.9f)
        delay(160)
        launch { sx.animateTo(0.74f, tween(280, easing = EaseInCubic)) }
        launch { sy.animateTo(1.45f, tween(280, easing = EaseInCubic)) }
        y.animateTo(0f, tween(280, easing = EaseInCubic))
        done()
    }
    Canvas(Modifier.size(width = 60.dp, height = (floor + 34f).dp)) {
        val d = pillH * 0.68f * density
        val c = Offset(size.width / 2, y.value * density + d / 2)
        scale(sx.value, sy.value, pivot = c) {
            drawCircle(Color.Black.copy(alpha = 0.25f), d / 2 + 3 * density, c + Offset(0f, 3 * density))
            drawCircle(Color.Black, d / 2, c)
            drawCircle(Color.White.copy(alpha = 0.2f), d / 2, c, style = Stroke(0.8f * density))
            drawOval(Color.White.copy(alpha = 0.45f), topLeft = c + Offset(-d * 0.3f, -d * 0.36f), size = Size(d * 0.34f, d * 0.22f))
        }
    }
}

// =====================================================================================
//  Collapsed pill: eyes | camera hole | battery  (artwork + equalizer while playing)
// =====================================================================================
@Composable
private fun Collapsed(geo: Geo, pillH: Float, camGap: Float, media: MediaInfo?, accent: Color, volume: IslandEvent.Volume?, lyric: String?) {
    val playing = media?.playing == true
    var clock by remember { mutableStateOf("") }
    LaunchedEffect(Unit) { while (true) { clock = SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date()); delay(5000) } }
    // while music plays the pill is about the song: the eyes step aside, lyrics run underneath
    val eyesA by androidx.compose.animation.core.animateFloatAsState(if (playing || volume != null) 0f else 1f, tween(260), label = "eyesA")
  Column(Modifier.fillMaxSize()) {
    Row(Modifier.fillMaxWidth().height(pillH.dp).padding(horizontal = 9.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.weight(1f), contentAlignment = Alignment.CenterStart) {
            when {
                volume != null -> Icon(when { volume.muted -> Ico.VolumeOff; volume.level < 0.5f -> Ico.VolumeDown; else -> Ico.VolumeUp }, Fg, 17f)
                media?.art != null && playing -> Image(media.art, null, Modifier.size((pillH - 10).dp).clip(RoundedCornerShape(7.dp)), contentScale = ContentScale.Crop)
                playing -> Icon(Ico.Music, accent, 15f)
                else -> Row(verticalAlignment = Alignment.CenterVertically) {
                    Text2(clock, 12f, Fg, Modifier.padding(start = 3.dp), weight = FontWeight.SemiBold)
                    if (geo.hasCutout && eyesA > 0.01f) Box(Modifier.padding(start = 7.dp).graphicsLayer { alpha = eyesA }) {
                        Eyes(eyeSize = (pillH * 0.38f).dp, gap = 4.dp, iris = accent)
                    }
                }
            }
        }
        Box(Modifier.width(camGap.dp), contentAlignment = Alignment.Center) {
            if (!geo.hasCutout && eyesA > 0.01f) Box(Modifier.graphicsLayer { alpha = eyesA }) { Eyes(eyeSize = (pillH * 0.42f).dp, gap = 6.dp, iris = accent) }
        }
        Box(Modifier.weight(1f), contentAlignment = Alignment.CenterEnd) {
            when {
                volume != null -> Row(verticalAlignment = Alignment.CenterVertically) {
                    LevelBar(volume.level, accent, 64f)
                    Text2("${(volume.level * 100).toInt()}", 11.5f, FgDim, Modifier.padding(start = 7.dp).width(24.dp), align = TextAlign.End)
                }
                playing -> Equalizer(accent, true, pillH * 0.48f)
                else -> Battery(Island.battery, Island.charging)
            }
        }
    }
    // karaoke line under the pill
    if (lyric != null) AnimatedContent(lyric, transitionSpec = {
        (fadeIn(tween(260)) + androidx.compose.animation.slideInVertically { it / 2 }) togetherWith fadeOut(tween(120))
    }, label = "pillLyric", modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp)) { line ->
        Row(Modifier.fillMaxWidth().padding(bottom = 6.dp), horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
            Text2("♪", 11f, accent, Modifier.padding(end = 6.dp), weight = FontWeight.Bold)
            Text2(line, 12f, lerpC(Color.White, accent, 0.25f), weight = FontWeight.SemiBold)
        }
    }
  }
}

// =====================================================================================
//  Peek cards
// =====================================================================================
@Composable
private fun NotificationCard(n: IslandEvent.Notification, pillH: Float, more: Int, onViewAll: () -> Unit, onReply: () -> Unit, onOpen: () -> Unit, onDismiss: () -> Unit) {
    // tint everything with the app's own colour
    val tint = remember(n) { n.icon?.let { runCatching { com.windowsnotch.island.Colors.accentOf(it.asAndroidBitmap()) }.getOrNull() } ?: Green }
    val left = remember(n) { Animatable(1f) }
    LaunchedEffect(n) { left.animateTo(0f, tween(if (more > 0) 6500 else 4800, easing = LinearEasing)) }
    val iconPop = remember(n) { Animatable(0.4f) }
    LaunchedEffect(n) { iconPop.animateTo(1f, spring(0.42f, 380f)) }

    Box(Modifier.fillMaxSize()) {
        // soft glow behind the icon
        Canvas(Modifier.size(150.dp).offset((-40).dp, (pillH - 50).dp)) {
            drawCircle(Brush.radialGradient(listOf(tint.copy(alpha = 0.35f), Color.Transparent)))
        }
        Column(Modifier.fillMaxSize().padding(start = 14.dp, end = 10.dp, top = (pillH - 2).dp, bottom = 10.dp)) {
            Row(verticalAlignment = Alignment.Top) {
                Box(
                    Modifier.size(44.dp).graphicsLayer { scaleX = iconPop.value; scaleY = iconPop.value }
                        .clip(RoundedCornerShape(14.dp)).background(Color(0x1AFFFFFF)).border(1.dp, Color(0x2BFFFFFF), RoundedCornerShape(14.dp)),
                    contentAlignment = Alignment.Center,
                ) {
                    if (n.icon != null) Image(n.icon, null, Modifier.size(28.dp)) else Icon(Ico.Bell, Fg, 20f)
                }
                Column(Modifier.padding(start = 12.dp).weight(1f)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text2(n.app, 11f, lerpC(tint, Color.White, 0.25f), weight = FontWeight.SemiBold)
                        Text2("  ·  now", 11f, FgFaint)
                        if (more > 0) {
                            Spacer(Modifier.weight(1f))
                            Box(Modifier.clip(RoundedCornerShape(9.dp)).background(Color(0x2EFFFFFF)).clickable(onClick = onViewAll).padding(horizontal = 8.dp, vertical = 2.dp)) {
                                Text2("View all · ${more + 1}", 10.5f, Fg, weight = FontWeight.SemiBold)
                            }
                        }
                    }
                    Text2(n.title.ifBlank { n.app }, 14.5f, Fg, weight = FontWeight.SemiBold)
                    if (n.text.isNotBlank()) androidx.compose.material3.Text(
                        n.text, color = FgDim, fontSize = 12.5.sp, maxLines = 2, overflow = TextOverflow.Ellipsis, lineHeight = 16.sp,
                    )
                }
                Box(Modifier.size(28.dp).clip(CircleShape).clickable(onClick = onDismiss), contentAlignment = Alignment.Center) {
                    Text2("✕", 12f, FgDim)
                }
            }
            Spacer(Modifier.weight(1f))
            Row(Modifier.padding(start = 56.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                if (n.chat != null) Chip("↩  Reply", onReply)
                if (n.pkg.isNotEmpty()) Chip(if (n.app.length <= 14) "Open ${n.app}" else "Open", onOpen)
            }
        }
        // countdown before it closes
        Box(Modifier.align(Alignment.BottomCenter).padding(horizontal = 24.dp, vertical = 6.dp).fillMaxWidth().height(2.5.dp).clip(CircleShape).background(Color(0x12FFFFFF))) {
            Box(Modifier.fillMaxWidth(left.value).height(2.5.dp).clip(CircleShape).background(tint))
        }
    }
}

@Composable
private fun Chip(label: String, onClick: () -> Unit) =
    Box(Modifier.clip(RoundedCornerShape(11.dp)).background(Color(0x26FFFFFF)).clickable(onClick = onClick).padding(horizontal = 11.dp, vertical = 5.dp)) {
        Text2(label, 11.5f, Fg, weight = FontWeight.SemiBold)
    }

private val Orange = Color(0xFFFF9F0A)

/** A to-do reminder: ✓ Done or Snooze 10 min; stays 20 s. */
@Composable
private fun ReminderCard(item: com.windowsnotch.island.TodoItem, pillH: Float, onDone: () -> Unit, onSnooze: () -> Unit, onDismiss: () -> Unit) {
    val left = remember(item) { Animatable(1f) }
    LaunchedEffect(item) { left.animateTo(0f, tween(20_000, easing = LinearEasing)) }
    val ring = rememberInfiniteTransition(label = "ring")
    val wobble by ring.animateFloat(-14f, 14f, infiniteRepeatable(tween(90, easing = LinearEasing), RepeatMode.Reverse), label = "wobble")
    val pause by ring.animateFloat(0f, 1f, infiniteRepeatable(tween(1400, easing = LinearEasing)), label = "pause")
    Box(Modifier.fillMaxSize()) {
        Canvas(Modifier.size(150.dp).offset((-40).dp, (pillH - 50).dp)) {
            drawCircle(Brush.radialGradient(listOf(Orange.copy(alpha = 0.38f), Color.Transparent)))
        }
        Column(Modifier.fillMaxSize().padding(start = 14.dp, end = 10.dp, top = (pillH - 2).dp, bottom = 10.dp)) {
            Row(verticalAlignment = Alignment.Top) {
                Box(
                    Modifier.size(44.dp).clip(RoundedCornerShape(14.dp)).background(Orange.copy(alpha = 0.2f)).border(1.dp, Orange.copy(alpha = 0.35f), RoundedCornerShape(14.dp)),
                    contentAlignment = Alignment.Center,
                ) {
                    // the alarm clock rings in short bursts
                    Box(Modifier.graphicsLayer { rotationZ = if (pause < 0.35f) wobble else 0f }) { Icon(Ico.Alarm, Orange, 22f) }
                }
                Column(Modifier.padding(start = 12.dp).weight(1f)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text2("REMINDER", 11f, lerpC(Orange, Color.White, 0.2f), weight = FontWeight.SemiBold)
                        Text2("  ·  now", 11f, FgFaint)
                    }
                    Text2(item.text, 14.5f, Fg, weight = FontWeight.SemiBold)
                    val at = item.due?.let { SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(it)) } ?: ""
                    Text2("Due $at  ·  from your to-do list", 12.5f, FgDim)
                }
                Box(Modifier.size(28.dp).clip(CircleShape).clickable(onClick = onDismiss), contentAlignment = Alignment.Center) { Text2("✕", 12f, FgDim) }
            }
            Spacer(Modifier.weight(1f))
            Row(Modifier.padding(start = 56.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Chip("✓  Done", onDone)
                Chip("Snooze 10 min", onSnooze)
            }
        }
        Box(Modifier.align(Alignment.BottomCenter).padding(horizontal = 24.dp, vertical = 6.dp).fillMaxWidth().height(2.5.dp).clip(CircleShape).background(Color(0x12FFFFFF))) {
            Box(Modifier.fillMaxWidth(left.value).height(2.5.dp).clip(CircleShape).background(Orange))
        }
    }
}

@Composable
private fun SpeechBubble(text: String) {
    Column(Modifier.padding(top = 3.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        Canvas(Modifier.size(14.dp, 7.dp)) {
            val p = Path().apply {
                moveTo(0f, size.height); cubicTo(size.width * 0.3f, size.height, size.width * 0.38f, 0f, size.width / 2, 0f)
                cubicTo(size.width * 0.62f, 0f, size.width * 0.7f, size.height, size.width, size.height); close()
            }
            drawPath(p, Color(0xFA1E1E24))
        }
        Box(
            Modifier.shadow(10.dp, RoundedCornerShape(15.dp)).clip(RoundedCornerShape(15.dp)).background(Color(0xFA1E1E24))
                .border(1.dp, Color(0x2EFFFFFF), RoundedCornerShape(15.dp)).padding(horizontal = 13.dp, vertical = 6.dp),
        ) { Text2(text, 12.5f, Fg, weight = FontWeight.SemiBold) }
    }
}

@Composable
private fun ChargeCard(c: IslandEvent.Charge, pillH: Float) {
    val fill = remember(c) { Animatable(if (c.charging) 0f else 1f) }
    val bolt = remember(c) { Animatable(if (c.charging) 0f else 1f) }
    val count = remember(c) { Animatable(if (c.charging) (c.percent - 25).coerceAtLeast(0).toFloat() else c.percent.toFloat()) }
    LaunchedEffect(c) {
        launch { delay(180); fill.animateTo(c.percent / 100f, tween(1100, easing = EaseOutQuint)) }
        launch { bolt.animateTo(if (c.charging) 1f else 0f, if (c.charging) spring(0.35f, 300f) else tween(420, easing = EaseInCubic)) }
        launch { count.animateTo(c.percent.toFloat(), tween(1100, easing = EaseOutQuint)) }
    }
    val rings = rememberInfiniteTransition(label = "rings")
    val r1 by rings.animateFloat(0f, 1f, infiniteRepeatable(tween(1100, easing = LinearEasing), RepeatMode.Restart), label = "r1")
    val r2 by rings.animateFloat(0f, 1f, infiniteRepeatable(tween(1100, easing = LinearEasing), RepeatMode.Restart, StartOffset(450)), label = "r2")
    val low = !c.charging && c.percent <= 20
    val fillColor = when { c.charging -> Green; low -> Color(0xFFFF453A); c.percent <= 35 -> Color(0xFFFFB020); else -> Color(0xFFF2F4F7) }

    Row(Modifier.fillMaxSize().padding(start = 10.dp, end = 18.dp, top = (pillH - 4).dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Canvas(Modifier.size(66.dp, 46.dp)) {
            val cx = size.width / 2; val cy = size.height / 2
            if (c.charging) for (p in floatArrayOf(r1, r2)) {
                drawCircle(Green.copy(alpha = (1f - p) * 0.9f), (19f + 22f * p) * density, Offset(cx, cy), style = Stroke(1.4f * density))
            }
            val bw = 44 * density; val bh = 22 * density
            val left = cx - bw / 2 - 1.5f * density; val topY = cy - bh / 2
            drawRoundRect(Color.White.copy(alpha = 0.08f), Offset(left, topY), Size(bw, bh), CornerRadius(6.5f * density))
            drawRoundRect(Color.White.copy(alpha = 0.6f), Offset(left, topY), Size(bw, bh), CornerRadius(6.5f * density), style = Stroke(1.4f * density))
            drawRoundRect(Color.White.copy(alpha = 0.6f), Offset(left + bw + 1.2f * density, cy - 4 * density), Size(2.6f * density, 8 * density), CornerRadius(1.3f * density))
            val inset = 3.8f * density
            drawRoundRect(fillColor, Offset(left + inset, topY + inset), Size(((bw - 2 * inset) * fill.value).coerceAtLeast(2f), bh - 2 * inset), CornerRadius(3.5f * density))
            if (bolt.value > 0.01f) {
                val b = Path().apply { moveTo(5.6f, 0f); lineTo(0.6f, 6.6f); lineTo(3.9f, 6.6f); lineTo(3.1f, 11f); lineTo(8.2f, 4.2f); lineTo(4.8f, 4.2f); close() }
                val s = 1.45f * density * bolt.value
                translate(left + bw / 2 - 4.4f * s, cy - 5.5f * s) {
                    scale(s, s, pivot = Offset.Zero) {
                        drawPath(b, Color.White)
                        drawPath(b, Color(0xCC0B3D1C), style = Stroke(0.6f))
                    }
                }
            }
        }
        Column(Modifier.padding(start = 10.dp).weight(1f)) {
            Text2(if (c.charging) "Charging" else "On battery", 14f, Fg, weight = FontWeight.SemiBold)
            Text2(if (c.charging) "Charger connected" else "Running on battery power", 11.5f, FgDim)
        }
        Text2("${count.value.toInt()}%", 24f, if (c.charging) Color(0xFF5EE082) else if (low) Color(0xFFFF5A4F) else Fg, weight = FontWeight.SemiBold)
    }
}

// =====================================================================================
//  Expanded: media (artwork, progress, controls, karaoke lyrics) or a friendly idle face
// =====================================================================================
@Composable
private fun Expanded(pillH: Float, media: MediaInfo?, accent: Color, tab: Int, setTab: (Int) -> Unit, close: () -> Unit, touched: () -> Unit) {
    var now by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    LaunchedEffect(Unit) { while (true) { now = SystemClock.elapsedRealtime(); delay(200) } }

    Column(Modifier.fillMaxSize().padding(start = 16.dp, end = 16.dp, top = (pillH + 2).dp, bottom = 14.dp)) {
        // Media · Chats · Search · Alerts · To-do
        Row(Modifier.fillMaxWidth().padding(bottom = 10.dp), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            TabPill(Ico.Music, "Media", tab == 0) { setTab(0) }
            TabPill(Ico.Chat, "Chats", tab == 1, badge = Island.chats.any { it.unread > 0 }) { setTab(1) }
            TabPill(Ico.Search, "Search", tab == 2) { setTab(2) }
            TabPill(Ico.Bell, "Alerts", tab == 3, badge = Island.unseenAlerts, badgeColor = Color(0xFFFF453A)) { setTab(3) }
            TabPill(Ico.Todo, "To-do", tab == 4, badge = com.windowsnotch.island.Todo.items.any { it.overdue() || (it.due != null && !it.done && it.due!! - System.currentTimeMillis() < 3600_000) },
                badgeColor = Color(0xFFFFB020)) { setTab(4) }
            Spacer(Modifier.weight(1f))
            // talk with Notch (Gemini Live)
            val ctx = androidx.compose.ui.platform.LocalContext.current
            val live = Island.talking
            Box(
                Modifier.size(30.dp).clip(CircleShape)
                    .background(if (live) Color(0x33FF453A) else Glass)
                    .border(1.dp, if (live) Color(0x66FF453A) else GlassStroke, CircleShape)
                    .clickable { com.windowsnotch.island.TalkActivity.toggle(ctx); close() },
                contentAlignment = Alignment.Center,
            ) { Icon(if (live) Ico.Close else Ico.Mic, if (live) Color(0xFFFF453A) else Fg, 15f) }
        }
        AnimatedContent(tab, transitionSpec = { (fadeIn(tween(220)) + scaleIn(initialScale = 0.97f)) togetherWith fadeOut(tween(120)) }, label = "tab") { t ->
            when (t) {
                1 -> ChatsPanel(touched)
                2 -> SearchPanel(touched) { setTab(0) }
                3 -> AlertsPanel(touched, close)
                4 -> TodoPanel(touched)
                else -> AnimatedContent(media == null, transitionSpec = { (fadeIn(tween(250)) + scaleIn(initialScale = 0.96f)) togetherWith fadeOut(tween(150)) }, label = "exp") { idle ->
                    if (idle) IdlePanel() else media?.let { MediaPanel(it, accent, now, touched) }
                }
            }
        }
    }
}

@Composable
private fun TabPill(icon: ImageVector, label: String, selected: Boolean, badge: Boolean = false, badgeColor: Color = Green, onClick: () -> Unit) {
    Row(
        Modifier.clip(RoundedCornerShape(12.dp)).background(if (selected) Glass else Color.Transparent)
            .border(1.dp, if (selected) GlassStroke else Color.Transparent, RoundedCornerShape(12.dp))
            .clickable(onClick = onClick).padding(horizontal = 10.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box {
            Icon(icon, if (selected) Fg else FgFaint, 15f)
            if (badge) Box(Modifier.align(Alignment.TopEnd).size(6.dp).clip(CircleShape).background(badgeColor))
        }
        if (selected) Text2(label, 12f, Fg, Modifier.padding(start = 6.dp), weight = FontWeight.SemiBold)
    }
}

// =====================================================================================
//  Chats: WhatsApp / Instagram … conversations from notifications, reply inline
// =====================================================================================
@Composable
private fun ChatsPanel(touched: () -> Unit) {
    val open = Island.openChat
    if (open != null) { ChatView(open, touched); return }
    if (Island.chats.isEmpty()) {
        Column(Modifier.fillMaxWidth().padding(top = 40.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            Box(Modifier.size(46.dp).clip(CircleShape).background(Glass), contentAlignment = Alignment.Center) { Icon(Ico.Chat, Fg, 20f) }
            Text2("No messages yet", 14.5f, Fg, Modifier.padding(top = 10.dp), weight = FontWeight.SemiBold)
            Text2("WhatsApp and Instagram messages will show up here", 11.5f, FgDim)
        }
        return
    }
    androidx.compose.foundation.lazy.LazyColumn(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        items(Island.chats.size) { i ->
            val t = Island.chats[i]
            Row(
                Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Color(0x0FFFFFFF))
                    .clickable { touched(); t.unread = 0; Island.openChat = t }.padding(10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Avatar(t, 36f)
                Column(Modifier.padding(start = 10.dp).weight(1f)) {
                    Text2(t.title, 13.5f, Fg, weight = FontWeight.SemiBold)
                    val last = t.messages.lastOrNull()
                    Text2(if (last == null) "" else (if (last.mine) "You: " else "") + last.text, 12f, FgDim)
                }
                Column(horizontalAlignment = Alignment.End) {
                    Text2(t.app, 10f, FgFaint)
                    if (t.unread > 0) Box(Modifier.padding(top = 4.dp).clip(CircleShape).background(Green).padding(horizontal = 6.dp, vertical = 1.dp)) {
                        Text2("${t.unread}", 10f, Color.Black, weight = FontWeight.Bold)
                    }
                }
            }
        }
    }
}

@Composable
private fun Avatar(t: com.windowsnotch.island.ChatThread, sizeDp: Float) {
    Box(Modifier.size(sizeDp.dp)) {
        Box(Modifier.fillMaxSize().clip(CircleShape).background(Color(0x26FFFFFF)), contentAlignment = Alignment.Center) {
            val a = t.avatar
            if (a != null) Image(a, null, Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
            else Text2(t.title.take(1).uppercase(), (sizeDp * 0.4f), Fg, weight = FontWeight.Bold)
        }
        t.icon?.let { Image(it, null, Modifier.size((sizeDp * 0.42f).dp).align(Alignment.BottomEnd)) }
    }
}

@Composable
private fun ChatView(t: com.windowsnotch.island.ChatThread, touched: () -> Unit) {
    val ctx = androidx.compose.ui.platform.LocalContext.current
    var text by remember(t) { mutableStateOf("") }
    var status by remember(t) { mutableStateOf("") }
    val list = androidx.compose.foundation.lazy.rememberLazyListState()
    LaunchedEffect(t.messages.size) { if (t.messages.isNotEmpty()) list.animateScrollToItem(t.messages.size - 1) }
    LaunchedEffect(t) { t.unread = 0 }

    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(30.dp).clip(CircleShape).clickable { touched(); Island.typing = false; Island.openChat = null }, contentAlignment = Alignment.Center) { Icon(Ico.Back, Fg, 17f) }
            Avatar(t, 26f)
            Text2(t.title, 14f, Fg, Modifier.padding(start = 8.dp).weight(1f), weight = FontWeight.SemiBold)
            Text2(t.app, 10.5f, FgFaint)
        }
        androidx.compose.foundation.lazy.LazyColumn(Modifier.weight(1f).padding(vertical = 6.dp), state = list, verticalArrangement = Arrangement.spacedBy(5.dp)) {
            items(t.messages.size) { i ->
                val m = t.messages[i]
                Box(Modifier.fillMaxWidth(), contentAlignment = if (m.mine) Alignment.CenterEnd else Alignment.CenterStart) {
                    Column(
                        Modifier.widthIn(max = 280.dp).clip(RoundedCornerShape(15.dp)).background(if (m.mine) Color(0xFF1F8A3E) else Color(0x24FFFFFF))
                            .padding(horizontal = 11.dp, vertical = 6.dp),
                    ) {
                        androidx.compose.material3.Text(m.text, color = Fg, fontSize = 13.sp)
                        Text2(SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(m.time)), 9.5f, FgFaint, Modifier.align(Alignment.End))
                    }
                }
            }
        }
        if (status.isNotEmpty()) Text2(status, 10.5f, FgFaint, Modifier.padding(start = 6.dp, bottom = 4.dp))
        val send = {
            val msg = text.trim()
            if (msg.isNotEmpty()) {
                touched()
                if (t.canReply && com.windowsnotch.island.Chats.reply(ctx, t, msg)) { status = "✓ Sent to ${t.title}"; text = ""; Island.react(EyeMood.Happy, 1200) }
                else { status = "Opening ${t.app} — the reply option has expired"; com.windowsnotch.island.Chats.openApp(ctx, t) }
            }
        }
        InputRow(text, { text = it; touched() }, if (t.canReply) "Reply to ${t.title}…" else "Reply (opens ${t.app})…", Ico.Send, send)
    }
}

// =====================================================================================
//  Search: YouTube suggestions, Play = top result in YouTube Music
// =====================================================================================
/** Results survive switching tabs. */
private object SearchState {
    var q by mutableStateOf("")
    var results by mutableStateOf(listOf<com.windowsnotch.island.YtVideo>())
    var loading by mutableStateOf(false)
    var searched by mutableStateOf("")
}

@Composable
private fun SearchPanel(touched: () -> Unit, playStarted: () -> Unit) {
    val ctx = androidx.compose.ui.platform.LocalContext.current
    val s = SearchState
    LaunchedEffect(s.q) {
        val q = s.q.trim()
        if (q.isEmpty() || q == s.searched) return@LaunchedEffect
        delay(450)
        s.loading = true
        val r = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { com.windowsnotch.island.NotchPlayer.search(q) }
        s.loading = false
        if (s.q.trim() == q) { s.results = r; s.searched = q }
    }
    val play = { i: Int ->
        touched(); Island.typing = false
        Island.react(EyeMood.Happy, 1200)
        com.windowsnotch.island.NotchPlayer.play(s.results, i)
        playStarted()
    }
    Column(Modifier.fillMaxSize()) {
        InputRow(s.q, { s.q = it; touched() }, "Search a song — it plays right here", Ico.Search) { if (s.results.isNotEmpty()) play(0) }
        when {
            s.loading && s.results.isEmpty() -> Text2("Searching YouTube…", 11.5f, FgFaint, Modifier.padding(top = 18.dp).align(Alignment.CenterHorizontally))
            s.results.isEmpty() -> Column(Modifier.padding(top = 18.dp).align(Alignment.CenterHorizontally), horizontalAlignment = Alignment.CenterHorizontally) {
                Text2(if (s.searched.isEmpty()) "Type a song — results play inside the island" else "No songs found", 11.5f, FgFaint)
                if (s.q.isNotBlank()) Box(Modifier.padding(top = 8.dp).clip(RoundedCornerShape(10.dp)).background(Color(0x26FFFFFF))
                    .clickable { touched(); Island.typing = false; com.windowsnotch.island.YouTube.play(ctx, s.q.trim()) }.padding(horizontal = 10.dp, vertical = 4.dp)) {
                    Text2("Open in YouTube Music instead", 11f, Fg)
                }
            }
        }
        androidx.compose.foundation.lazy.LazyColumn(Modifier.padding(top = 8.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
            items(s.results.size) { i ->
                val v = s.results[i]
                val playingNow = com.windowsnotch.island.NotchPlayer.current?.id == v.id && Island.playerMedia != null
                var thumb by remember(v.id) { mutableStateOf<androidx.compose.ui.graphics.ImageBitmap?>(null) }
                LaunchedEffect(v.id) { thumb = com.windowsnotch.island.NotchPlayer.thumb(v) }
                Row(
                    Modifier.fillMaxWidth().clip(RoundedCornerShape(13.dp)).background(Color(0x0FFFFFFF)).clickable { play(i) }.padding(6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Box(Modifier.size(72.dp, 41.dp).clip(RoundedCornerShape(9.dp)).background(Glass)) {
                        thumb?.let { Image(it, null, Modifier.fillMaxSize(), contentScale = ContentScale.Crop) }
                        Box(Modifier.align(Alignment.BottomEnd).padding(3.dp).clip(RoundedCornerShape(5.dp)).background(Color(0xCC000000)).padding(horizontal = 4.dp)) {
                            Text2(v.length, 9.5f, Fg, weight = FontWeight.SemiBold)
                        }
                        if (playingNow) Box(Modifier.fillMaxSize().background(Color(0x99000000)), contentAlignment = Alignment.Center) { Equalizer(Color.White, Island.playerMedia?.playing == true, 14f) }
                    }
                    Column(Modifier.padding(start = 10.dp).weight(1f)) {
                        Text2(v.title, 12.5f, Fg, weight = FontWeight.SemiBold)
                        Text2("${v.channel}  ·  ${v.length}", 11f, FgDim)
                    }
                    Box(Modifier.padding(start = 6.dp).size(30.dp).clip(CircleShape).background(Color.White), contentAlignment = Alignment.Center) {
                        Icon(Ico.Play, Color.Black, 16f)
                    }
                }
            }
        }
    }
}

/**
 * A pill text field. The overlay window normally can't take the keyboard, so the first tap switches the
 * island into typing mode (window becomes focusable), then focuses the field and shows the keyboard.
 */
@Composable
private fun InputRow(value: String, onChange: (String) -> Unit, hint: String, actionIcon: ImageVector, onAction: () -> Unit) {
    val focus = remember { androidx.compose.ui.focus.FocusRequester() }
    val kb = androidx.compose.ui.platform.LocalSoftwareKeyboardController.current
    val fm = androidx.compose.ui.platform.LocalFocusManager.current
    LaunchedEffect(Island.typing) {
        if (Island.typing) { delay(120); runCatching { focus.requestFocus() }; kb?.show() } else { fm.clearFocus(); kb?.hide() }
    }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(
            Modifier.weight(1f).height(38.dp).clip(RoundedCornerShape(19.dp)).background(Color(0x1AFFFFFF)).border(1.dp, GlassStroke, RoundedCornerShape(19.dp))
                .clickable(enabled = !Island.typing) { Island.typing = true }.padding(horizontal = 14.dp),
            contentAlignment = Alignment.CenterStart,
        ) {
            if (value.isEmpty()) Text2(hint, 13f, FgFaint)
            androidx.compose.foundation.text.BasicTextField(
                value, onChange, Modifier.fillMaxWidth().focusRequester(focus),
                enabled = Island.typing, singleLine = true,
                textStyle = TextStyle(color = Fg, fontSize = 13.sp),
                cursorBrush = androidx.compose.ui.graphics.SolidColor(Fg),
                keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(imeAction = androidx.compose.ui.text.input.ImeAction.Send),
                keyboardActions = androidx.compose.foundation.text.KeyboardActions(onSend = { onAction() }, onDone = { onAction() }, onSearch = { onAction() }),
            )
        }
        Box(Modifier.padding(start = 8.dp).size(38.dp).clip(CircleShape).background(Color.White).clickable { onAction() }, contentAlignment = Alignment.Center) {
            Icon(actionIcon, Color.Black, 18f)
        }
    }
}

// =====================================================================================
//  Alerts: every notification, unfolding out of a stack like an iPhone's "View all"
// =====================================================================================
private fun ago(t: Long, now: Long): String {
    val s = (now - t) / 1000
    return when {
        s < 60 -> "now"
        s < 3600 -> "${s / 60}m ago"
        s < 86400 -> "${s / 3600}h ago"
        else -> SimpleDateFormat("EEE HH:mm", Locale.getDefault()).format(Date(t))
    }
}

@Composable
private fun AlertsPanel(touched: () -> Unit, close: () -> Unit) {
    val ctx = androidx.compose.ui.platform.LocalContext.current
    val list = Island.history
    var clearing by remember { mutableStateOf(false) }
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) { Island.unseenAlerts = false; while (true) { now = System.currentTimeMillis(); delay(20_000) } }
    LaunchedEffect(clearing) { if (clearing) { delay(260L + minOf(list.size, 8) * 40L); list.clear(); clearing = false } }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(start = 2.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text2(if (list.isEmpty()) "Notifications" else "${list.size} notification${if (list.size == 1) "" else "s"}", 12.5f, Fg, Modifier.weight(1f), weight = FontWeight.SemiBold)
            if (list.isNotEmpty()) Chip("Clear all") { touched(); clearing = true }
        }
        if (list.isEmpty()) {
            Column(Modifier.fillMaxWidth().padding(top = 34.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Box(Modifier.size(46.dp).clip(CircleShape).background(Glass), contentAlignment = Alignment.Center) { Icon(Ico.Bell, Fg, 20f) }
                Text2("You're all caught up", 14.5f, Fg, Modifier.padding(top = 10.dp), weight = FontWeight.SemiBold)
                Text2("Notifications you get will be kept here", 11.5f, FgDim)
            }
            return@Column
        }
        androidx.compose.foundation.lazy.LazyColumn(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            items(list.size, key = { System.identityHashCode(list[it]) }) { i ->
                val e = list[i]
                // starts tucked under the card above it, then springs down into place (staggered)
                val y = remember { Animatable(if (i < 8) -i * 66f else 0f) }
                val a = remember { Animatable(if (i < 3) 1f else 0f) }
                val s = remember { Animatable(if (i < 8) 1f - minOf(i, 3) * 0.04f else 1f) }
                LaunchedEffect(Unit) {
                    delay(60L + minOf(i, 8) * 55L)
                    launch { y.animateTo(0f, spring(0.62f, 230f)) }
                    launch { s.animateTo(1f, spring(0.7f, 260f)) }
                    a.animateTo(1f, tween(280))
                }
                val out by androidx.compose.animation.core.animateFloatAsState(if (clearing) 460f else 0f,
                    tween(280, delayMillis = minOf(i, 8) * 40, easing = EaseInCubic), label = "out")
                Row(
                    Modifier.zIndex(100f - i).graphicsLayer {
                        translationY = y.value * density; translationX = out * density
                        alpha = a.value; scaleX = s.value
                    }.fillMaxWidth().clip(RoundedCornerShape(15.dp)).background(Color(0x14FFFFFF))
                        .border(1.dp, Color(0x14FFFFFF), RoundedCornerShape(15.dp))
                        .clickable {
                            touched()
                            val chat = e.n.chat
                            if (chat != null) { chat.unread = 0; Island.openChat = chat }
                            else ctx.packageManager.getLaunchIntentForPackage(e.n.pkg)?.let {
                                it.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK); runCatching { ctx.startActivity(it) }; close()
                            }
                        }.padding(10.dp),
                    verticalAlignment = Alignment.Top,
                ) {
                    Box(Modifier.size(34.dp).clip(RoundedCornerShape(10.dp)).background(Color(0x24FFFFFF)), contentAlignment = Alignment.Center) {
                        if (e.n.icon != null) Image(e.n.icon, null, Modifier.size(22.dp)) else Icon(Ico.Bell, Fg, 15f)
                    }
                    Column(Modifier.padding(start = 10.dp).weight(1f)) {
                        Row {
                            Text2(e.n.app.uppercase(), 10.5f, Color(0x99F5F5F7), Modifier.weight(1f), weight = FontWeight.SemiBold)
                            Text2(ago(e.time, now), 10f, FgFaint)
                        }
                        Text2(e.n.title.ifBlank { e.n.app }, 13f, Fg, weight = FontWeight.SemiBold)
                        if (e.n.text.isNotBlank()) androidx.compose.material3.Text(e.n.text, color = FgDim, fontSize = 12.sp, maxLines = 2, overflow = TextOverflow.Ellipsis, lineHeight = 15.sp)
                    }
                }
            }
        }
    }
}

// =====================================================================================
//  To-do with reminders: "call mom at 5pm", "stretch in 20 min", "pay rent tomorrow"
// =====================================================================================
private object TodoState {
    var q by mutableStateOf("")
    var chipDue by mutableStateOf<Long?>(null)
}

@Composable
private fun TodoPanel(touched: () -> Unit) {
    val s = TodoState
    val todo = com.windowsnotch.island.Todo
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) { while (true) { now = System.currentTimeMillis(); delay(15_000) } }
    val parsed = remember(s.q) { todo.parse(s.q) }
    val due = parsed.second ?: s.chipDue
    val add = {
        val (text, d) = todo.parse(s.q)
        if (text.isNotEmpty()) {
            touched()
            todo.add(text, d ?: s.chipDue)
            s.q = ""; s.chipDue = null
            Island.react(EyeMood.Happy, 1200)
        }
    }
    Column(Modifier.fillMaxSize()) {
        InputRow(s.q, { s.q = it; touched() }, "Add a task… try “call mom at 5pm”", Ico.Add) { add() }
        Row(
            Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(top = 8.dp, bottom = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically,
        ) {
            if (due != null) Box(Modifier.clip(RoundedCornerShape(10.dp)).background(Color(0x33FFB020)).padding(horizontal = 8.dp, vertical = 4.dp)) {
                Text2("⏰ " + com.windowsnotch.island.TodoItem("", "", false, due, false, 0).dueText(now), 11f, Color(0xFFFFC85A), weight = FontWeight.SemiBold)
            }
            @Composable fun chip(label: String, ms: () -> Long?) = Box(
                Modifier.clip(RoundedCornerShape(11.dp)).background(Color(0x1FFFFFFF)).clickable { touched(); s.chipDue = ms() }.padding(horizontal = 10.dp, vertical = 4.dp),
            ) { Text2(label, 11f, Fg) }
            chip("+15 min") { System.currentTimeMillis() + 15 * 60_000L }
            chip("+1 hour") { System.currentTimeMillis() + 3600_000L }
            chip("Tonight") {
                val c = java.util.Calendar.getInstance().apply { set(java.util.Calendar.HOUR_OF_DAY, 20); set(java.util.Calendar.MINUTE, 0); set(java.util.Calendar.SECOND, 0) }
                if (c.timeInMillis > System.currentTimeMillis()) c.timeInMillis else System.currentTimeMillis() + 30 * 60_000L
            }
            chip("Tomorrow 9:00") {
                java.util.Calendar.getInstance().apply { add(java.util.Calendar.DAY_OF_YEAR, 1); set(java.util.Calendar.HOUR_OF_DAY, 9); set(java.util.Calendar.MINUTE, 0); set(java.util.Calendar.SECOND, 0) }.timeInMillis
            }
            chip("No reminder") { null }
        }
        if (todo.items.isEmpty()) {
            Column(Modifier.fillMaxWidth().padding(top = 26.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text2("✨ Nothing to do", 14.5f, Fg, weight = FontWeight.SemiBold)
                Text2("Add a task above — the island reminds you when it's time", 11.5f, FgDim)
            }
            return@Column
        }
        androidx.compose.foundation.lazy.LazyColumn(verticalArrangement = Arrangement.spacedBy(5.dp)) {
            items(todo.items.size, key = { todo.items[it].id }) { i ->
                val t = todo.items[i]
                val pop = remember { Animatable(1f) }
                val scope = androidx.compose.runtime.rememberCoroutineScope()
                Row(
                    Modifier.animateItem().fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(Color(0x0FFFFFFF))
                        .border(1.dp, Color(0x14FFFFFF), RoundedCornerShape(12.dp)).padding(horizontal = 10.dp, vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    // round check
                    Box(
                        Modifier.size(22.dp).graphicsLayer { scaleX = pop.value; scaleY = pop.value }.clip(CircleShape)
                            .background(if (t.done) Green else Color.Transparent).border(1.6.dp, if (t.done) Green else Color(0x66FFFFFF), CircleShape)
                            .clickable {
                                touched()
                                scope.launch { pop.snapTo(0.6f); pop.animateTo(1f, spring(0.35f, 500f)) }
                                val finishing = !t.done
                                t.done = !t.done
                                if (finishing) Island.react(EyeMood.Happy, 1200)
                                scope.launch { delay(if (finishing) 380 else 0); todo.sort(); todo.save() }
                            },
                        contentAlignment = Alignment.Center,
                    ) { if (t.done) Icon(Ico.Check, Color.Black, 14f) }
                    androidx.compose.material3.Text(
                        t.text, Modifier.padding(start = 11.dp, end = 8.dp).weight(1f).graphicsLayer { alpha = if (t.done) 0.45f else 1f },
                        color = Fg, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis,
                        textDecoration = if (t.done) androidx.compose.ui.text.style.TextDecoration.LineThrough else null,
                    )
                    if (t.due != null && !t.done) {
                        val over = t.overdue(now)
                        val soon = !over && t.due!! - now < 3600_000
                        val col = when { over -> Color(0xFFFF5A4F); soon -> Color(0xFFFFB020); else -> FgDim }
                        Row(Modifier.clip(RoundedCornerShape(9.dp)).background(if (over) Color(0x26FF453A) else Color(0x1AFFFFFF)).padding(horizontal = 7.dp, vertical = 2.dp),
                            verticalAlignment = Alignment.CenterVertically) {
                            Icon(Ico.Alarm, col, 10f)
                            Text2(t.dueText(now), 10.5f, col, Modifier.padding(start = 4.dp), weight = FontWeight.SemiBold)
                        }
                    }
                    Box(Modifier.padding(start = 4.dp).size(26.dp).clip(CircleShape).clickable { touched(); todo.remove(t) }, contentAlignment = Alignment.Center) {
                        Icon(Ico.Close, FgFaint, 12f)
                    }
                }
            }
        }
    }
}

@Composable
private fun IdlePanel() {
    Row(Modifier.fillMaxWidth().padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(96.dp, 64.dp).clip(RoundedCornerShape(20.dp)).background(Color(0xFF0F0F12)), contentAlignment = Alignment.Center) {
            Eyes(eyeSize = 28.dp, gap = 12.dp, interactive = true, lidColor = Color(0xFF0F0F12))
        }
        Column(Modifier.padding(start = 16.dp)) {
            Text2(SimpleDateFormat("EEEE d MMMM", Locale.getDefault()).format(Date()), 15f, Fg, weight = FontWeight.SemiBold)
            Text2("Nothing playing  ·  ${Island.battery}%${if (Island.charging) " charging" else ""}", 12.5f, FgDim)
            // the eyes' latest words show here while the island is open
            var said by remember { mutableStateOf<String?>(null) }
            LaunchedEffect(Island.speechId) { said = Island.speech; delay(2600); said = null }
            Text2(said ?: "Tap the eyes to poke them, double-tap to wink", if (said != null) 13f else 11f,
                if (said != null) Fg else FgFaint, Modifier.padding(top = 6.dp), weight = if (said != null) FontWeight.SemiBold else FontWeight.Normal)
        }
    }
}

@Composable
private fun MediaPanel(m: MediaInfo, accent: Color, tick: Long, touched: () -> Unit) {
    val c = Island.controller
    check(tick >= 0) // `tick` changes every 200 ms so the progress and lyrics keep moving
    Column {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(58.dp).clip(RoundedCornerShape(15.dp)).background(Glass), contentAlignment = Alignment.Center) {
                if (m.art != null) Image(m.art, null, Modifier.fillMaxSize(), contentScale = ContentScale.Crop) else Icon(Ico.Music, FgDim, 24f)
            }
            Column(Modifier.padding(start = 14.dp).weight(1f)) {
                Text2(m.title, 15.5f, Fg, weight = FontWeight.SemiBold)
                Text2(m.artist.ifBlank { m.app }, 12.5f, FgDim)
                Text2(m.app, 10.5f, FgFaint, weight = FontWeight.SemiBold)
            }
            Equalizer(accent, m.playing, 20f)
        }
        // progress
        val pos = m.livePosition()
        val frac = if (m.durationMs > 0) (pos.toFloat() / m.durationMs).coerceIn(0f, 1f) else 0f
        Row(Modifier.padding(top = 14.dp), verticalAlignment = Alignment.CenterVertically) {
            Text2(fmt(pos), 10.5f, FgFaint, Modifier.width(36.dp))
            Box(Modifier.weight(1f).height(5.dp).clip(CircleShape).background(Color(0x26FFFFFF))) {
                Box(Modifier.fillMaxWidth(frac).height(5.dp).clip(CircleShape).background(accent))
            }
            Text2(if (m.durationMs > 0) fmt(m.durationMs) else "--:--", 10.5f, FgFaint, Modifier.width(36.dp), align = TextAlign.End)
        }
        // controls
        Row(Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
            val own = m.packageName == com.windowsnotch.island.NotchPlayer.PKG   // the island's own YouTube player
            IconButton2(Ico.Prev, 26f) { touched(); if (own) com.windowsnotch.island.NotchPlayer.previous() else c?.transportControls?.skipToPrevious() }
            Spacer(Modifier.width(26.dp))
            Box(Modifier.size(50.dp).clip(CircleShape).background(Color.White).clickable {
                touched()
                if (own) com.windowsnotch.island.NotchPlayer.playPause() else if (m.playing) c?.transportControls?.pause() else c?.transportControls?.play()
            }, contentAlignment = Alignment.Center) {
                Icon(if (m.playing) Ico.Pause else Ico.Play, Color.Black, 26f)
            }
            Spacer(Modifier.width(26.dp))
            IconButton2(Ico.Next, 26f) { touched(); if (own) com.windowsnotch.island.NotchPlayer.next() else c?.transportControls?.skipToNext() }
        }
        // karaoke lyrics
        Island.lyrics?.let { ly ->
            val idx = ly.indexAt(pos)
            val line = if (idx >= 0) ly.lines[idx].text else ""
            val start = if (idx >= 0) ly.lines[idx].timeMs else 0L
            val end = if (idx >= 0) ly.lineEnd(idx) else 1L
            val p = ((pos - start).toFloat() / (end - start).coerceAtLeast(1)).coerceIn(0f, 1f)
            AnimatedContent(line, transitionSpec = { (fadeIn(tween(300)) + androidx.compose.animation.slideInVertically { it / 2 }) togetherWith fadeOut(tween(120)) }, label = "lyric") { text ->
                val brush = Brush.horizontalGradient(
                    0f to Color.White, (p * 0.98f) to lerpC(Color.White, accent, 0.4f), (p * 0.98f + 0.02f).coerceAtMost(1f) to Color(0x80FFFFFF), 1f to Color(0x80FFFFFF),
                )
                androidx.compose.material3.Text(
                    if (text == "♪" || text.isBlank()) "♪" else text,
                    modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
                    style = TextStyle(brush = brush, fontSize = 16.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center),
                    maxLines = 2, overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

// =====================================================================================
//  Small pieces
// =====================================================================================
@Composable
private fun Equalizer(color: Color, playing: Boolean, height: Float) {
    val t = rememberInfiniteTransition(label = "eq")
    val bars = listOf(380, 520, 300, 460).mapIndexed { i, d ->
        t.animateFloat(0.25f, 1f, infiniteRepeatable(tween(d, easing = LinearEasing), RepeatMode.Reverse, StartOffset(i * 90)), label = "b$i")
    }
    Row(Modifier.height(height.dp), verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(2.5.dp)) {
        bars.forEach { b ->
            val v = if (playing) b.value else 0.22f
            Box(Modifier.width(3.dp).height((height * v).dp).clip(RoundedCornerShape(1.5.dp)).background(color))
        }
    }
}

@Composable
private fun Battery(pct: Int, charging: Boolean) {
    val low = !charging && pct <= 20
    val color = when { charging -> Green; low -> Color(0xFFFF453A); pct <= 35 -> Color(0xFFFFB020); else -> Color.White }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text2("$pct%", 11f, if (charging) Color(0xFF5EE082) else if (low) Color(0xFFFF5A4F) else FgDim, weight = FontWeight.SemiBold)
        Canvas(Modifier.padding(start = 4.dp).size(24.dp, 12.dp)) {
            val d = density
            drawRoundRect(Color.White.copy(alpha = 0.55f), Offset(0f, 0f), Size(21 * d, 12 * d), CornerRadius(3.6f * d), style = Stroke(1.1f * d))
            drawRoundRect(Color.White.copy(alpha = 0.55f), Offset(22 * d, 4 * d), Size(1.8f * d, 4 * d), CornerRadius(1f * d))
            drawRoundRect(color, Offset(2.2f * d, 2.2f * d), Size(((21 - 4.4f) * pct / 100f).coerceAtLeast(2f) * d, 7.6f * d), CornerRadius(2f * d))
        }
    }
}

@Composable
private fun LevelBar(level: Float, color: Color, widthDp: Float) {
    val v = remember { Animatable(level) }
    LaunchedEffect(level) { v.animateTo(level, tween(220)) }
    Box(Modifier.width(widthDp.dp).height(5.dp).clip(CircleShape).background(Color(0x26FFFFFF))) {
        Box(Modifier.fillMaxWidth(v.value.coerceIn(0f, 1f)).height(5.dp).clip(CircleShape).background(color))
    }
}

@Composable
private fun Icon(v: ImageVector, tint: Color, sizeDp: Float) =
    Image(v, null, Modifier.size(sizeDp.dp), colorFilter = ColorFilter.tint(tint))

@Composable
private fun IconButton2(v: ImageVector, sizeDp: Float, onClick: () -> Unit) =
    Box(Modifier.size((sizeDp + 18).dp).clip(CircleShape).clickable(onClick = onClick), contentAlignment = Alignment.Center) { Icon(v, Fg, sizeDp) }

@Composable
private fun Text2(text: String, sizeSp: Float, color: Color, modifier: Modifier = Modifier, weight: FontWeight = FontWeight.Normal, align: TextAlign = TextAlign.Start) =
    androidx.compose.material3.Text(text, modifier, color = color, fontSize = sizeSp.sp, fontWeight = weight, maxLines = 1, overflow = TextOverflow.Ellipsis, textAlign = align)

private fun fmt(ms: Long): String { val s = ms / 1000; return "%d:%02d".format(s / 60, s % 60) }

private fun lerpC(a: Color, b: Color, t: Float) = androidx.compose.ui.graphics.lerp(a, b, t)
