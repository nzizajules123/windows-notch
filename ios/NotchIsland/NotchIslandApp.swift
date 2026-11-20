import SwiftUI

@main
struct NotchIslandApp: App {
    @StateObject private var model = IslandModel.shared
    @Environment(\.scenePhase) private var phase

    var body: some Scene {
        WindowGroup {
            HomeView()
                .environmentObject(model)
                .environmentObject(model.music)
                .environmentObject(model.todos)
                .preferredColorScheme(.dark)
        }
        .onChange(of: phase) { p in
            switch p {
            case .active: model.becameActive()
            case .background: model.wentToBackground()
            default: break
            }
        }
    }
}

/// The app screen: the island at the top (under the real Dynamic Island) and a short guide.
struct HomeView: View {
    @EnvironmentObject var m: IslandModel

    var body: some View {
        ZStack(alignment: .top) {
            LinearGradient(colors: [Color(red: 0.10, green: 0.10, blue: 0.14), Color(red: 0.03, green: 0.03, blue: 0.05)],
                           startPoint: .top, endPoint: .bottom)
                .ignoresSafeArea()

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    Color.clear.frame(height: m.expanded ? 400 : 110)
                    Text("Notch Island").font(.largeTitle.bold())
                    Text("Living eyes, music with lyrics, to-dos with reminders and clap control. Leave the app and the island stays in your Dynamic Island.")
                        .foregroundColor(.secondary)
                    Guide(icon: "hand.tap", title: "Tap the island", text: "Opens Music, To-do and Control. Tap the eyes to poke them.")
                    Guide(icon: "rectangle.topthird.inset.filled", title: "Dynamic Island", text: "Eyes and battery stay up top when you leave. Long-press it to see your next to-do with a live countdown.")
                    Guide(icon: "hands.clap", title: "Clap control", text: "Control → Claps. While the app is open: clap twice for the privacy screen, three times to play or pause music.")
                    Guide(icon: "lock.rectangle", title: "Privacy screen", text: "A big clock over a blur, for when someone's looking. Tap it, or clap twice, to go back.")
                }
                .padding(.horizontal, 20)
                .padding(.bottom, 40)
            }

            IslandView()
                .padding(.top, 4)
        }
        .animation(.spring(response: 0.45, dampingFraction: 0.82), value: m.expanded)
        .fullScreenCover(isPresented: $m.privacy) {
            PrivacyScreen().environmentObject(m).environmentObject(m.music)
        }
    }
}

private struct Guide: View {
    let icon: String, title: String, text: String
    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: icon).font(.title3).frame(width: 30).foregroundColor(Color(red: 0.35, green: 0.62, blue: 1))
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.headline)
                Text(text).font(.subheadline).foregroundColor(.secondary)
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 20, style: .continuous).fill(Color.white.opacity(0.06)))
    }
}
