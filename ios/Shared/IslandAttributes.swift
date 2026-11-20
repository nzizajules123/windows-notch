import ActivityKit
import SwiftUI

/// What the Dynamic Island shows while you're outside the app (a Live Activity).
struct IslandAttributes: ActivityAttributes {
    public struct ContentState: Codable, Hashable {
        var battery: Int
        var charging: Bool
        /// "neutral", "happy", "sleepy", "love", "surprised"
        var mood: String
        var todoTitle: String?
        var todoDue: Date?
        var nowPlaying: String?
    }
    var name: String
}

/// The island's eyes, small enough for the Dynamic Island (shared by the app and the widget).
struct MiniEyes: View {
    var mood: String
    var size: CGFloat

    var body: some View {
        HStack(spacing: size * 0.55) {
            eye
            eye
        }
    }

    @ViewBuilder private var eye: some View {
        switch mood {
        case "love":
            Image(systemName: "heart.fill")
                .font(.system(size: size * 0.95))
                .foregroundColor(Color(red: 1, green: 0.33, blue: 0.55))
        case "sleepy":
            Capsule().fill(Color.white).frame(width: size, height: size * 0.28)
        case "happy":
            Image(systemName: "chevron.up")
                .font(.system(size: size * 0.9, weight: .heavy))
                .foregroundColor(.white)
        default:
            ZStack {
                Ellipse().fill(Color.white).frame(width: size, height: mood == "surprised" ? size * 1.35 : size * 1.15)
                Circle().fill(Color.black).frame(width: size * 0.5, height: size * 0.5).offset(x: size * 0.08, y: size * 0.05)
            }
        }
    }
}

extension IslandAttributes.ContentState {
    var batteryColor: Color {
        if charging { return Color(red: 0.19, green: 0.82, blue: 0.35) }
        if battery <= 20 { return Color(red: 1, green: 0.27, blue: 0.23) }
        return .white
    }
}
