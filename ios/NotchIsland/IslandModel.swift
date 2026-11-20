import ActivityKit
import Combine
import SwiftUI
import UIKit

enum IslandTab: Int, CaseIterable {
    case media, todo, control
    var icon: String {
        switch self {
        case .media: return "music.note"
        case .todo: return "checklist"
        case .control: return "switch.2"
        }
    }
}

/// Everything the island shows and does. The views watch it.
@MainActor
final class IslandModel: ObservableObject {
    static let shared = IslandModel()

    let music = MusicModel()
    let todos = TodoStore()
    private let claps = ClapListener()

    @Published var expanded = false
    @Published var tab: IslandTab = .media
    @Published var privacy = false
    @Published private(set) var battery = 100
    @Published private(set) var charging = false
    @Published private(set) var mood = "neutral"
    @Published private(set) var speech: String?
    @Published private(set) var clapStatus = "Off"

    @Published var clapsOn: Bool {
        didSet { UserDefaults.standard.set(clapsOn, forKey: "claps"); syncClaps() }
    }
    @Published var liveOn: Bool {
        didSet { UserDefaults.standard.set(liveOn, forKey: "live"); syncLive() }
    }
    @Published var talk: Bool {
        didSet { UserDefaults.standard.set(talk, forKey: "talk") }
    }

    private var active = false
    private var clapCooldown = Date.distantPast
    private var speechTask: Task<Void, Never>?
    private var moodTask: Task<Void, Never>?
    private var lastLive: IslandAttributes.ContentState?
    private var subs: Set<AnyCancellable> = []

    private init() {
        let d = UserDefaults.standard
        clapsOn = d.bool(forKey: "claps")
        liveOn = d.object(forKey: "live") as? Bool ?? true
        talk = d.object(forKey: "talk") as? Bool ?? true

        UIDevice.current.isBatteryMonitoringEnabled = true
        readBattery()
        let nc = NotificationCenter.default
        for name in [UIDevice.batteryLevelDidChangeNotification, UIDevice.batteryStateDidChangeNotification] {
            nc.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.batteryChanged() }
            }
        }
        claps.onClap = { [weak self] n in self?.clapHeard(n) }
        claps.onSequence = { [weak self] n, ms in self?.clapSequence(n, ms) }
        // keep the Dynamic Island in step with the music and the to-do list ($ values arrive before they're stored)
        music.objectWillChange.merge(with: todos.objectWillChange)
            .debounce(for: .milliseconds(300), scheduler: RunLoop.main)
            .sink { [weak self] _ in self?.syncLive() }
            .store(in: &subs)
    }

    // ------------------------------------------------------------------ app in front / in background
    func becameActive() {
        active = true
        music.refresh()
        syncClaps()
        syncLive()
        say(greeting())
    }

    func wentToBackground() {
        active = false
        syncClaps()          // the microphone is only used while the app is open
        syncLive()
    }

    // ------------------------------------------------------------------ battery
    private func readBattery() {
        let level = UIDevice.current.batteryLevel
        battery = level < 0 ? 100 : Int((level * 100).rounded())
        let s = UIDevice.current.batteryState
        charging = s == .charging || s == .full
    }

    private func batteryChanged() {
        let was = charging
        readBattery()
        if charging != was {
            react(charging ? "love" : "surprised", seconds: 2)
            say(charging ? "Yum, power ⚡" : "Unplugged! I'll be careful 🔋")
        }
        syncLive()
    }

    // ------------------------------------------------------------------ eyes, bubbles
    func say(_ text: String, force: Bool = false) {
        guard talk || force else { return }
        speechTask?.cancel()
        withAnimation(.spring(response: 0.4, dampingFraction: 0.75)) { speech = text }
        speechTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            guard !Task.isCancelled else { return }
            withAnimation(.easeOut(duration: 0.25)) { self?.speech = nil }
        }
    }

    func react(_ m: String, seconds: Double = 1.6) {
        moodTask?.cancel()
        withAnimation(.spring()) { mood = m }
        moodTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            guard !Task.isCancelled else { return }
            withAnimation(.spring()) { self?.mood = "neutral" }
            self?.syncLive()
        }
        syncLive()
    }

    func poke() {
        react(["happy", "surprised", "love"].randomElement()!, seconds: 1.2)
        say(["Hey! 👀", "That tickles 😆", "Hi there 👋"].randomElement()!)
    }

    private func greeting() -> String {
        let h = Calendar.current.component(.hour, from: Date())
        switch h {
        case ..<5: return "Up late? 🌙"
        case ..<12: return "Good morning ☀️"
        case ..<17: return "Good afternoon 🌤"
        case ..<22: return "Good evening 🌆"
        default: return "Good night soon? 🌙"
        }
    }

    // ------------------------------------------------------------------ claps
    private func syncClaps() {
        if clapsOn && active {
            guard !claps.running else { return }
            clapStatus = "Starting…"
            claps.start { [weak self] err in
                guard let self else { return }
                self.clapStatus = err ?? "Listening · 👏👏 privacy · 👏👏👏 play/pause"
            }
        } else {
            claps.stop()
            clapStatus = clapsOn ? "Paused while the app is closed" : "Off"
        }
    }

    private func clapHeard(_ n: Int) {
        guard Date() >= clapCooldown else { return }
        switch n {
        case 1: say(privacy ? "👏  Clap again to unlock" : "👏  Clap again for privacy", force: true)
        case 2: say(privacy ? "👏👏  Unlocking…" : "👏👏  Privacy screen…  (once more = play/pause)", force: true)
        default: say("👏👏👏  Play / pause", force: true)
        }
    }

    private func clapSequence(_ n: Int, _ ms: Int) {
        guard (2...3).contains(n), Date() >= clapCooldown else { return }
        clapCooldown = Date().addingTimeInterval(2.5)
        if n == 2 {
            withAnimation(.easeInOut(duration: 0.35)) { privacy.toggle() }
            if privacy { expanded = false }
        } else {
            music.toggle()
            react("happy", seconds: 1.2)
        }
    }

    // ------------------------------------------------------------------ Dynamic Island (Live Activity)
    var liveState: IslandAttributes.ContentState {
        let next = todos.next
        var now: String?
        if music.playing, let t = music.title { now = [t, music.artist].compactMap { $0 }.joined(separator: " · ") }
        return .init(battery: battery, charging: charging, mood: mood,
                     todoTitle: next?.title, todoDue: next?.due, nowPlaying: now)
    }

    var liveSupported: Bool { ActivityAuthorizationInfo().areActivitiesEnabled }

    /// Starts, updates or ends the Live Activity that puts the island in the Dynamic Island.
    func syncLive() {
        let running = Activity<IslandAttributes>.activities
        guard liveOn, liveSupported else {
            lastLive = nil
            for a in running { Task { await a.end(nil, dismissalPolicy: .immediate) } }
            return
        }
        let state = liveState
        if let a = running.first {
            guard state != lastLive else { return }
            lastLive = state
            Task { await a.update(ActivityContent(state: state, staleDate: nil)) }
        } else if active {
            // a Live Activity can only be started while the app is open
            lastLive = state
            _ = try? Activity.request(attributes: IslandAttributes(name: "island"),
                                      content: ActivityContent(state: state, staleDate: nil),
                                      pushType: nil)
        }
    }
}
