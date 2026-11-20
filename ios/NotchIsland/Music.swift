import MediaPlayer
import UIKit

/// What's playing in the Music app (iOS only lets apps see the system Music player, not Spotify or YouTube),
/// with play controls and synced lyrics from LRCLIB.
@MainActor
final class MusicModel: ObservableObject {
    @Published private(set) var authorized = MPMediaLibrary.authorizationStatus() == .authorized
    @Published private(set) var title: String?
    @Published private(set) var artist: String?
    @Published private(set) var art: UIImage?
    @Published private(set) var playing = false
    @Published private(set) var duration: TimeInterval = 0
    @Published private(set) var lyrics: [(time: TimeInterval, line: String)] = []

    private let player = MPMusicPlayerController.systemMusicPlayer
    private var lyricsKey = ""
    private var observers: [NSObjectProtocol] = []

    init() {
        if authorized { startWatching() }
    }

    func requestAccess() {
        MPMediaLibrary.requestAuthorization { status in
            Task { @MainActor in
                self.authorized = status == .authorized
                if self.authorized { self.startWatching() }
            }
        }
    }

    private func startWatching() {
        guard observers.isEmpty else { return }
        player.beginGeneratingPlaybackNotifications()
        let nc = NotificationCenter.default
        for name in [Notification.Name.MPMusicPlayerControllerNowPlayingItemDidChange,
                     Notification.Name.MPMusicPlayerControllerPlaybackStateDidChange] {
            observers.append(nc.addObserver(forName: name, object: player, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.refresh() }
            })
        }
        refresh()
    }

    func refresh() {
        guard authorized else { return }
        let item = player.nowPlayingItem
        title = item?.title
        artist = item?.artist
        duration = item?.playbackDuration ?? 0
        art = item?.artwork?.image(at: CGSize(width: 300, height: 300))
        playing = player.playbackState == .playing
        loadLyrics()
    }

    var position: TimeInterval { player.currentPlaybackTime.isFinite ? player.currentPlaybackTime : 0 }

    func toggle() {
        guard authorized else { return }
        if player.playbackState == .playing { player.pause() } else { player.play() }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in self?.refresh() }
    }
    func next() { player.skipToNextItem() }
    func previous() { player.currentPlaybackTime > 3 ? player.skipToBeginning() : player.skipToPreviousItem() }

    /// The lyric line being sung right now.
    func currentLine() -> String? {
        guard playing, !lyrics.isEmpty else { return nil }
        let t = position + 0.25
        return lyrics.last(where: { $0.time <= t })?.line
    }

    private func loadLyrics() {
        guard let title, let artist else { lyrics = []; lyricsKey = ""; return }
        let key = title + "|" + artist
        if key == lyricsKey { return }
        lyricsKey = key
        lyrics = []
        let dur = Int(duration.rounded())
        Task {
            let found = await Lyrics.fetch(title: title, artist: artist, duration: dur)
            if self.lyricsKey == key { self.lyrics = found }
        }
    }
}

/// Synced lyrics from LRCLIB (free, no account): "[mm:ss.xx] line".
enum Lyrics {
    static func fetch(title: String, artist: String, duration: Int) async -> [(time: TimeInterval, line: String)] {
        var c = URLComponents(string: "https://lrclib.net/api/get")!
        c.queryItems = [
            URLQueryItem(name: "track_name", value: title),
            URLQueryItem(name: "artist_name", value: artist),
        ]
        if duration > 0 { c.queryItems?.append(URLQueryItem(name: "duration", value: String(duration))) }
        guard let url = c.url else { return [] }
        var req = URLRequest(url: url)
        req.setValue("NotchIsland iOS", forHTTPHeaderField: "User-Agent")
        guard let (data, resp) = try? await URLSession.shared.data(for: req),
              (resp as? HTTPURLResponse)?.statusCode == 200,
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let synced = obj["syncedLyrics"] as? String else { return [] }
        return parse(synced)
    }

    static func parse(_ text: String) -> [(time: TimeInterval, line: String)] {
        var out: [(time: TimeInterval, line: String)] = []
        for raw in text.split(separator: "\n") {
            let s = String(raw)
            guard s.hasPrefix("["), let close = s.firstIndex(of: "]") else { continue }
            let stamp = s[s.index(after: s.startIndex)..<close]
            let parts = stamp.split(separator: ":")
            guard parts.count == 2, let m = Double(parts[0]), let sec = Double(parts[1]) else { continue }
            let line = s[s.index(after: close)...].trimmingCharacters(in: .whitespaces)
            out.append((m * 60 + sec, line.isEmpty ? "♪" : line))
        }
        return out
    }
}
