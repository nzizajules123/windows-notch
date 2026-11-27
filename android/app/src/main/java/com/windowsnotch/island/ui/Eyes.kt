package com.windowsnotch.island.ui

import android.os.SystemClock
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.keyframes
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.graphics.drawscope.rotate
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.windowsnotch.island.EyeMood
import com.windowsnotch.island.Island
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.random.Random

/**
 * Two little 3D eyes: shaded eyeballs, an iris in the artwork colour that slides and foreshortens,
 * a fixed light reflection and real eyelids. They blink, glance around, bob to music and pull faces.
 * With [interactive]: tap = poke (4 quick pokes = angry), double tap = wink.
 */
@Composable
fun Eyes(
    modifier: Modifier = Modifier,
    eyeSize: Dp = 13.dp,
    gap: Dp = 7.dp,
    iris: Color = Color(0xFF30D158),
    interactive: Boolean = false,
    lidColor: Color = Color.Black,
) {
    val blink = remember { Animatable(0f) }
    val wink = remember { Animatable(0f) }
    val lookX = remember { Animatable(0f) }
    val lookY = remember { Animatable(0f) }
    val pop = remember { Animatable(1f) }
    val shake = remember { Animatable(0f) }
    var localMood by remember { mutableStateOf<EyeMood?>(null) }
    var localUntil by remember { mutableLongStateOf(0L) }
    var pokes by remember { mutableIntStateOf(0) }
    var lastPoke by remember { mutableLongStateOf(0L) }

    // blinking
    LaunchedEffect(Unit) {
        while (true) {
            delay(Random.nextLong(2200, 5600))
            repeat(if (Random.nextFloat() < 0.18f) 2 else 1) {
                blink.animateTo(1f, tween(70))
                blink.animateTo(0f, tween(130))
                delay(90)
            }
        }
    }
    // glancing around
    LaunchedEffect(Unit) {
        while (true) {
            delay(Random.nextLong(1600, 4200))
            val (x, y) = if (Random.nextFloat() < 0.45f) 0f to 0f else Random.nextInt(-3, 4).toFloat() to (if (Random.nextFloat() < 0.3f) Random.nextInt(-1, 2).toFloat() else 0f)
            launch { lookX.animateTo(x, spring(0.55f, 300f)) }
            lookY.animateTo(y, spring(0.55f, 300f))
        }
    }
    // hop whenever something happens (notification, charger, volume)
    LaunchedEffect(Island.eventId) {
        if (Island.eventId == 0L) return@LaunchedEffect
        pop.snapTo(0.82f)
        pop.animateTo(1f, spring(0.38f, 500f))
    }

    // which face: a local poke/wink wins, then a global reaction, then the base mood
    var now by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    LaunchedEffect(Island.moodUntil, localUntil) {
        now = SystemClock.elapsedRealtime()
        val until = maxOf(Island.moodUntil, localUntil)
        if (until > now) { delay(until - now + 20); now = SystemClock.elapsedRealtime() }
    }
    val base = if (!Island.charging && Island.battery <= 15) EyeMood.Sleepy else EyeMood.Neutral
    val mood = when {
        localMood != null && now < localUntil -> localMood!!
        now < Island.moodUntil -> Island.mood
        else -> base
    }

    val upper by animateFloatAsState(when (mood) { EyeMood.Sleepy -> 0.5f; EyeMood.Sad -> 0.38f; EyeMood.Angry -> 0.4f; else -> 0f }, tween(240), label = "upper")
    val lower by animateFloatAsState(when (mood) { EyeMood.Happy -> 0.58f; EyeMood.Love -> 0.3f; else -> 0f }, tween(240), label = "lower")
    val tilt by animateFloatAsState(when (mood) { EyeMood.Sad -> 20f; EyeMood.Angry -> -22f; else -> 0f }, tween(240), label = "tilt")
    val pupil by animateFloatAsState(when (mood) { EyeMood.Surprised -> 0.65f; EyeMood.Angry -> 0.75f; else -> 1f }, spring(0.5f, 300f), label = "pupil")
    val ball by animateFloatAsState(if (mood == EyeMood.Surprised) 1.16f else 1f, spring(0.4f, 400f), label = "ball")
    val heart by animateFloatAsState(if (mood == EyeMood.Love) 1f else 0f, spring(0.45f, 350f), label = "heart")
    // eyebrows: lift (fraction of the eye radius, negative = up) and inner-end tilt (degrees)
    val browLift by animateFloatAsState(when (mood) {
        EyeMood.Surprised -> -0.5f; EyeMood.Happy -> -0.22f; EyeMood.Love -> -0.3f; EyeMood.Sad -> -0.12f
        EyeMood.Angry -> 0.2f; EyeMood.Sleepy -> 0.28f; else -> 0f
    }, spring(0.5f, 300f), label = "browLift")
    val browTilt by animateFloatAsState(when (mood) {
        EyeMood.Sad -> -18f; EyeMood.Angry -> 20f; EyeMood.Happy -> -6f; EyeMood.Love -> -8f; else -> 0f
    }, spring(0.5f, 300f), label = "browTilt")
    val irisColor = if (mood == EyeMood.Angry) lerp(iris, Color(0xFFFF3B30), 0.6f) else iris

    // bob to the music
    // (the animation only runs while music plays: an endless animation redraws 60 times a second, which costs battery)
    val playing = Island.media?.playing == true
    val bob = if (playing && !com.windowsnotch.island.Device.lite) {
        val bobT = rememberInfiniteTransition(label = "bob")
        bobT.animateFloat(-1f, 1f, infiniteRepeatable(tween(430, easing = LinearEasing), RepeatMode.Reverse), label = "bobY").value
    } else 0f

    val scope = androidx.compose.runtime.rememberCoroutineScope()
    var mod = modifier.size(eyeSize * 2 + gap + 4.dp, eyeSize + eyeSize * 0.6f + 6.dp)
    if (interactive) mod = mod.pointerInput(Unit) {
        detectTapGestures(
            onDoubleTap = {
                scope.launch { wink.animateTo(1f, tween(90)); delay(520); wink.animateTo(0f, tween(160)) }
                localMood = EyeMood.Love; localUntil = SystemClock.elapsedRealtime() + 1300
                Island.say(Island.pick("Hehe 😉", "Love you too ❤️", "You're the best 💖"))
            },
            onTap = {
                val t = SystemClock.elapsedRealtime()
                pokes = if (t - lastPoke < 2200) pokes + 1 else 1
                lastPoke = t
                scope.launch { blink.animateTo(1f, tween(60)); delay(240); blink.animateTo(0f, tween(150)) }
                scope.launch {
                    shake.animateTo(0f, keyframes { durationMillis = 420; -2.2f at 60; 2.2f at 130; -1.6f at 200; 1.2f at 270; 0f at 420 })
                }
                scope.launch { pop.snapTo(0.82f); pop.animateTo(1f, spring(0.4f, 500f)) }
                if (pokes >= 4) { localMood = EyeMood.Angry; localUntil = t + 2200; pokes = 0; Island.say(Island.pick("Stop it! 😤", "Hey, not cool 😠")) }
                else { localMood = EyeMood.Surprised; localUntil = t + 950; Island.say(Island.pick("Ouch!", "Boop! 👉", "Hey! 😳", "That tickles 😆", "Hehe")) }
            },
        )
    }

    Canvas(mod) {
        val r = eyeSize.toPx() / 2
        val g = gap.toPx()
        val cy = size.height - r - 3 * density + (if (playing) bob * 0.9f * density else 0f)
        val cx0 = size.width / 2 - g / 2 - r + shake.value * density
        val look = Offset(lookX.value, lookY.value)
        scale(pop.value, pop.value) {
            for (i in 0..1) {
                val c = Offset(cx0 + i * (2 * r + g), cy)
                val closed = maxOf(upper, blink.value, if (i == 1) wink.value else 0f)
                drawEye(c, r * ball, look, closed, lower, if (i == 0) tilt else -tilt, pupil, heart, irisColor, lidColor)
                // eyebrow: a soft arc above the eye, tilted around its inner end
                val by = c.y - r * 1.55f + browLift * r
                val inner = if (i == 0) Offset(c.x + r * 0.9f, by) else Offset(c.x - r * 0.9f, by)
                rotate(if (i == 0) browTilt else -browTilt, pivot = inner) {
                    val brow = Path().apply {
                        moveTo(c.x - r * 0.9f, by + r * 0.18f)
                        quadraticBezierTo(c.x, by - r * 0.32f, c.x + r * 0.9f, by + r * 0.18f)
                    }
                    drawPath(brow, Color(0xE6F2F4F8).copy(alpha = if (mood == EyeMood.Sleepy) 0.55f else 0.92f),
                        style = Stroke(width = r * 0.26f, cap = androidx.compose.ui.graphics.StrokeCap.Round))
                }
            }
        }
    }
}

private fun lighter(c: Color, t: Float) = lerp(c, Color.White, t)
private fun darker(c: Color, t: Float) = lerp(c, Color.Black, t)

private fun DrawScope.drawEye(
    c: Offset, r: Float, look: Offset, upper: Float, lower: Float, tilt: Float,
    pupil: Float, heart: Float, iris: Color, lid: Color,
) {
    val clip = Path().apply { addOval(Rect(c, r)) }
    clipPath(clip) {
        // sclera: lit from the upper-left, darker toward the rim
        drawCircle(
            Brush.radialGradient(
                0f to Color.White, 0.5f to Color(0xFFEEF1F6), 0.88f to Color(0xFFB4BDCB), 1f to Color(0xFF8E98A8),
                center = c + Offset(-r * 0.28f, -r * 0.34f), radius = r * 1.3f,
            ), r, c,
        )
        // iris + pupil (slides and flattens toward the rim)
        val ic = c + Offset(look.x * r * 0.21f, look.y * r * 0.22f)
        val ir = r * 0.57f
        scale(1f - abs(look.x) / 4f * 0.2f, 1f - abs(look.y) / 3f * 0.15f, pivot = ic) {
            drawCircle(Brush.radialGradient(listOf(lighter(iris, 0.35f), darker(iris, 0.45f)), center = ic, radius = ir), ir, ic)
            drawCircle(Color(0x8C050A14), ir, ic, style = Stroke(r * 0.1f))
            if (heart < 1f) drawCircle(Color(0xFF06070A).copy(alpha = 1f - heart), ir * 0.48f * pupil, ic)
            if (heart > 0f) {
                val hs = ir * 1.05f * heart
                val p = Path().apply {
                    moveTo(0f, 0.35f); cubicTo(-0.9f, -0.25f, -0.5f, -0.95f, 0f, -0.45f)
                    cubicTo(0.5f, -0.95f, 0.9f, -0.25f, 0f, 0.35f); close()
                }
                translate(ic.x, ic.y + hs * 0.1f) { scale(hs, hs, pivot = Offset.Zero) { drawPath(p, Color(0xFFFF3D71)) } }
            }
        }
        // specular highlight — stays put while the iris moves → reads as a sphere
        drawOval(Color.White.copy(alpha = 0.93f), topLeft = c + Offset(-r * 0.62f, -r * 0.68f), size = Size(r * 0.52f, r * 0.38f))
        drawCircle(Color.White.copy(alpha = 0.6f), r * 0.11f, c + Offset(r * 0.42f, r * 0.42f))
        // eyelids (same colour as the island)
        if (upper > 0.001f) rotate(tilt, pivot = c) {
            val line = c.y - r + 2 * r * upper
            drawRect(lid, topLeft = Offset(c.x - r * 2, c.y - r * 3), size = Size(r * 4, line - (c.y - r * 3) - r * 0.3f))
            drawOval(lid, topLeft = Offset(c.x - r * 1.7f, line - r * 1.2f), size = Size(r * 3.4f, r * 1.2f))
        }
        if (lower > 0.001f) {
            val line = c.y + r - 2 * r * lower
            drawOval(lid, topLeft = Offset(c.x - r * 1.6f, line), size = Size(r * 3.2f, r * 3f))
        }
    }
}
