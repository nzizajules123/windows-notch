import AVFoundation

/// Finds hand claps in microphone sound. It's the same detector as the Windows and Android notches: a clap is a
/// sudden, loud, bright burst that fades within ~0.1 s, and claps in a steady rhythm (0.12–0.75 s apart) form a
/// sequence. Works at any sample rate (iPhones record at 44.1 or 48 kHz).
final class ClapAnalyzer {
    private static let minGap = 120, maxGap = 750, endGap = 800

    var onClap: ((Int) -> Void)?
    var onSequence: ((Int, Int) -> Void)?
    var strictness = 1.0

    private let frame: Int
    private let zcrNeed: Double
    private var buf: [Float]
    private var fill = 0
    private var frameNo: Int64 = 0
    private var floor = 0.003
    private var prevRms = 0.0
    private var burstStart: Int64 = -1
    private var burstPeak = 0.0, burstZcr = 0.0, burstBase = 0.0
    private var burstDecayed = false
    private var claps: [Int64] = []

    init(rate: Double) {
        frame = max(16, Int(rate / 100))                // 10 ms frames
        zcrNeed = 0.08 * 16000 / max(rate, 8000)        // same "brightness" at any sample rate
        buf = [Float](repeating: 0, count: frame)
    }

    func feed(_ p: UnsafePointer<Float>, count: Int) {
        for i in 0..<count {
            buf[fill] = p[i]
            fill += 1
            if fill == frame { analyze(); fill = 0 }
        }
    }

    private func analyze() {
        var sum = 0.0, peak = 0.0
        var crossings = 0
        for i in 0..<frame {
            let v = Double(buf[i])
            sum += v * v
            peak = max(peak, abs(v))
            if i > 0 && (buf[i] >= 0) != (buf[i - 1] >= 0) { crossings += 1 }
        }
        let rms = (sum / Double(frame)).squareRoot()
        let zcr = Double(crossings) / Double(frame)
        let now = frameNo
        frameNo += 1

        if burstStart >= 0 {
            let age = now - burstStart
            if age <= 2 {
                burstPeak = max(burstPeak, rms)
                burstZcr = max(burstZcr, zcr)
            } else if rms < max(burstPeak * 0.22, burstBase * 1.5) {
                burstDecayed = true
            }
            if burstDecayed || age >= 14 {
                if burstDecayed && age <= 13 && burstZcr >= zcrNeed { clap(at: burstStart) }
                burstStart = -1
            }
        } else {
            let need = max(min(floor, 0.04) * 6, 0.025) * strictness
            if rms > need && peak > 0.12 * strictness && rms > prevRms * 2.5 {
                burstStart = now; burstPeak = rms; burstZcr = zcr; burstBase = prevRms; burstDecayed = false
            } else {
                let k = rms < floor ? 0.08 : 0.02
                floor = min(max(floor + (rms - floor) * k, 0.0005), 0.2)
            }
        }
        prevRms = rms

        if let last = claps.last, (now - last) * 10 >= Int64(Self.endGap) {
            let n = claps.count
            let span = Int((last - claps[0]) * 10)
            claps.removeAll()
            onSequence?(n, span)
        }
    }

    private func clap(at: Int64) {
        if let last = claps.last {
            let gap = (at - last) * 10
            if gap < Int64(Self.minGap) { return }       // the echo of the same clap
            if gap > Int64(Self.maxGap) {
                claps.removeAll()
            } else if claps.count >= 2 {
                let prevGap = (last - claps[claps.count - 2]) * 10
                if Double(gap) > Double(prevGap) * 2.2 || Double(gap) * 2.2 < Double(prevGap) { claps.removeAll() }
            }
        }
        claps.append(at)
        onClap?(claps.count)
    }
}

/// Listens to the microphone while the app is open. Nothing is recorded: each block is analysed and dropped.
final class ClapListener {
    private let engine = AVAudioEngine()
    private(set) var running = false
    /// Called on the main thread.
    var onClap: ((Int) -> Void)?
    var onSequence: ((Int, Int) -> Void)?

    /// Asks for the microphone if needed and starts listening. The completion gets an error text, or nil.
    func start(_ done: @escaping (String?) -> Void) {
        AVAudioSession.sharedInstance().requestRecordPermission { ok in
            DispatchQueue.main.async {
                guard ok else { done("Microphone is off for Notch Island — allow it in Settings"); return }
                done(self.begin())
            }
        }
    }

    private func begin() -> String? {
        if running { return nil }
        do {
            let s = AVAudioSession.sharedInstance()
            // keep other apps' music playing while we listen
            try s.setCategory(.playAndRecord, mode: .default, options: [.mixWithOthers, .defaultToSpeaker, .allowBluetoothA2DP])
            try s.setActive(true)
            let input = engine.inputNode
            let fmt = input.outputFormat(forBus: 0)
            guard fmt.sampleRate > 0, fmt.channelCount > 0 else { return "No microphone found" }
            let a = ClapAnalyzer(rate: fmt.sampleRate)
            a.onClap = { [weak self] n in DispatchQueue.main.async { self?.onClap?(n) } }
            a.onSequence = { [weak self] n, ms in DispatchQueue.main.async { self?.onSequence?(n, ms) } }
            input.installTap(onBus: 0, bufferSize: 2048, format: fmt) { buffer, _ in
                guard let ch = buffer.floatChannelData?[0] else { return }
                a.feed(ch, count: Int(buffer.frameLength))
            }
            engine.prepare()
            try engine.start()
            running = true
            return nil
        } catch {
            return "Couldn't start the microphone"
        }
    }

    func stop() {
        guard running else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        running = false
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }
}
