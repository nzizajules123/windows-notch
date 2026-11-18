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
import androidx.compose.foundation.layout.wrapContentSize
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

    var mode by remember { mutableStateOf(if (demo) Mode.Collapsed else Mode.Intro) }
    var peek by remember { mutableStateOf<IslandEvent?>(null) }
    var lastTouch by remember { mutableLongStateOf(0L) }
    val media = Island.media
    val playing = media?.playing == true
    val accent = media?.accent ?: Green
    val volume = peek as? IslandEvent.Volume
    val hasLyrics = Island.lyrics != null && media != null

    // ---- size of the island for each state
    val slot = if (playing) 48f else 46f
    val (tw, th, tr) = when (mode) {
        Mode.Intro -> Triple(pillH, pillH, pillH / 2)
        Mode.Collapsed -> if (volume != null) Triple(minOf(sw - 24f, 300f), pillH, pillH / 2) else Triple(camGap + 2 * slot, pillH, pillH / 2)
        Mode.Peek -> Triple(minOf(sw - 16f, 380f), pillH + 56f, 28f)
        Mode.Expanded -> Triple(minOf(sw - 12f, 440f), pillH + if (media == null) 132f else if (hasLyrics) 232f else 192f, 34f)
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
        if (mode == Mode.Intro || mode == Mode.Expanded) return@LaunchedEffect
        if (e is IslandEvent.Notification) Island.react(EyeMood.Surprised, 700)
        peek = e
        mode = if (e is IslandEvent.Volume) Mode.Collapsed else Mode.Peek
        delay(when (e) { is IslandEvent.Volume -> 1600; is IslandEvent.Charge -> 3400; else -> 4800 })
        peek = null
        if (mode == Mode.Peek) mode = Mode.Collapsed
    }
    // ---- close when touching outside, or after a few idle seconds
    var seenOutside by remember { mutableIntStateOf(Island.outsideTouches) }
    LaunchedEffect(Island.outsideTouches) {
        if (Island.outsideTouches != seenOutside) { seenOutside = Island.outsideTouches; if (mode == Mode.Expanded || mode == Mode.Peek) mode = Mode.Collapsed }
    }
    LaunchedEffect(mode, lastTouch) {
        if (mode == Mode.Expanded && !demo) { delay(7000); mode = Mode.Collapsed }
    }

    val open = mode == Mode.Expanded || mode == Mode.Peek
    Box(Modifier.padding(top = top.dp).padding(horizontal = if (open) 10.dp else 0.dp).padding(bottom = if (open) 16.dp else 0.dp).wrapContentSize(),
        contentAlignment = Alignment.TopCenter) {

        if (mode == Mode.Intro) {
            DropletIntro(pillH) { mode = Mode.Collapsed; Island.react(EyeMood.Happy, 1100) }
            return@Box
        }

        val shape = RoundedCornerShape(r.value.dp)
        Box(
            Modifier
                .size(w.value.dp, h.value.dp)
                .then(if (open) Modifier.shadow(18.dp, shape, ambientColor = Color.Black, spotColor = Color.Black) else Modifier)
                .clip(shape)
                .background(Color.Black)
                .border(0.8.dp, if (open) GlassStroke else Color.Transparent, shape)
                .clickable(remember { MutableInteractionSource() }, indication = null) {
                    lastTouch = SystemClock.elapsedRealtime()
                    if (mode != Mode.Expanded) { peek = null; mode = Mode.Expanded }
                },
        ) {
            when (mode) {
                Mode.Collapsed -> Collapsed(geo, pillH, camGap, media, accent, volume)
                Mode.Peek -> when (val p = peek) {
                    is IslandEvent.Notification -> NotificationCard(p, pillH)
                    is IslandEvent.Charge -> ChargeCard(p, pillH)
                    else -> Collapsed(geo, pillH, camGap, media, accent, null)
                }
                Mode.Expanded -> Expanded(pillH, media, accent) { lastTouch = SystemClock.elapsedRealtime() }
                Mode.Intro -> Unit
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
private fun Collapsed(geo: Geo, pillH: Float, camGap: Float, media: MediaInfo?, accent: Color, volume: IslandEvent.Volume?) {
    Row(Modifier.fillMaxSize().padding(horizontal = 9.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.weight(1f), contentAlignment = Alignment.CenterStart) {
            when {
                volume != null -> Icon(when { volume.muted -> Ico.VolumeOff; volume.level < 0.5f -> Ico.VolumeDown; else -> Ico.VolumeUp }, Fg, 17f)
                media?.art != null && media.playing -> Image(media.art, null, Modifier.size((pillH - 10).dp).clip(RoundedCornerShape(7.dp)), contentScale = ContentScale.Crop)
                geo.hasCutout -> Eyes(eyeSize = (pillH * 0.42f).dp, gap = 5.dp, iris = accent)
            }
        }
        Box(Modifier.width(camGap.dp), contentAlignment = Alignment.Center) {
            if (!geo.hasCutout && volume == null) Eyes(eyeSize = (pillH * 0.42f).dp, gap = 6.dp, iris = accent)
        }
        Box(Modifier.weight(1f), contentAlignment = Alignment.CenterEnd) {
            when {
                volume != null -> Row(verticalAlignment = Alignment.CenterVertically) {
                    LevelBar(volume.level, accent, 64f)
                    Text2("${(volume.level * 100).toInt()}", 11.5f, FgDim, Modifier.padding(start = 7.dp).width(24.dp), align = TextAlign.End)
                }
                media?.playing == true -> Equalizer(accent, true, pillH * 0.48f)
                else -> Battery(Island.battery, Island.charging)
            }
        }
    }
}

// =====================================================================================
//  Peek cards
// =====================================================================================
@Composable
private fun NotificationCard(n: IslandEvent.Notification, pillH: Float) {
    Row(Modifier.fillMaxSize().padding(start = 12.dp, end = 16.dp, top = (pillH - 2).dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(42.dp).clip(RoundedCornerShape(13.dp)).background(Glass).border(1.dp, GlassStroke, RoundedCornerShape(13.dp)), contentAlignment = Alignment.Center) {
            if (n.icon != null) Image(n.icon, null, Modifier.size(28.dp)) else Icon(Ico.Bell, Fg, 20f)
        }
        Column(Modifier.padding(start = 12.dp).weight(1f)) {
            Text2("${n.app}  ·  now", 10.5f, FgFaint, weight = FontWeight.SemiBold)
            Text2(n.title.ifBlank { n.app }, 14f, Fg, weight = FontWeight.SemiBold)
            if (n.text.isNotBlank()) Text2(n.text, 12.5f, FgDim)
        }
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
private fun Expanded(pillH: Float, media: MediaInfo?, accent: Color, touched: () -> Unit) {
    var now by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    LaunchedEffect(Unit) { while (true) { now = SystemClock.elapsedRealtime(); delay(200) } }

    Column(Modifier.fillMaxSize().padding(start = 18.dp, end = 18.dp, top = (pillH + 2).dp, bottom = 14.dp)) {
        AnimatedContent(media == null, transitionSpec = { (fadeIn(tween(250)) + scaleIn(initialScale = 0.96f)) togetherWith fadeOut(tween(150)) }, label = "exp") { idle ->
            if (idle) IdlePanel() else media?.let { MediaPanel(it, accent, now, touched) }
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
            Text2("Tap the eyes to poke them, double-tap to wink", 11f, FgFaint, Modifier.padding(top = 6.dp))
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
            IconButton2(Ico.Prev, 26f) { touched(); c?.transportControls?.skipToPrevious() }
            Spacer(Modifier.width(26.dp))
            Box(Modifier.size(50.dp).clip(CircleShape).background(Color.White).clickable { touched(); if (m.playing) c?.transportControls?.pause() else c?.transportControls?.play() },
                contentAlignment = Alignment.Center) {
                Icon(if (m.playing) Ico.Pause else Ico.Play, Color.Black, 26f)
            }
            Spacer(Modifier.width(26.dp))
            IconButton2(Ico.Next, 26f) { touched(); c?.transportControls?.skipToNext() }
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
        Text2("$pct", 11f, if (charging) Color(0xFF5EE082) else if (low) Color(0xFFFF5A4F) else FgDim, weight = FontWeight.SemiBold)
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
