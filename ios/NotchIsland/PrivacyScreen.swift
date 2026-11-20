import SwiftUI

/// The standby / privacy screen: a big clock over a heavy blur. Tap (or clap twice) to go back.
struct PrivacyScreen: View {
    @EnvironmentObject var m: IslandModel
    @EnvironmentObject var music: MusicModel

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            RadialGradient(colors: [Color(red: 0.25, green: 0.22, blue: 0.45).opacity(0.55), .clear],
                           center: .top, startRadius: 10, endRadius: 520)
                .ignoresSafeArea()
                .blur(radius: 40)

            TimelineView(.periodic(from: .now, by: 1)) { ctx in
                VStack(spacing: 10) {
                    EyesView(mood: m.mood, size: 22).padding(.bottom, 18)
                    Text(ctx.date, format: .dateTime.weekday(.wide).day().month(.wide))
                        .font(.title3.weight(.medium))
                        .foregroundColor(.white.opacity(0.75))
                    Text(ctx.date, format: .dateTime.hour().minute())
                        .font(.system(size: 92, weight: .bold, design: .rounded))
                        .monospacedDigit()
                        .foregroundColor(.white)
                    HStack(spacing: 6) {
                        Image(systemName: m.charging ? "battery.100.bolt" : "battery.75")
                        Text("\(m.battery)%").monospacedDigit()
                    }
                    .font(.headline)
                    .foregroundColor(m.charging ? .green : .white.opacity(0.75))

                    if music.playing, let t = music.title {
                        Label(t, systemImage: "music.note")
                            .font(.subheadline)
                            .foregroundColor(.white.opacity(0.7))
                            .lineLimit(1)
                            .padding(.top, 14)
                    }
                }
            }

            VStack {
                Spacer()
                Text(m.clapsOn ? "Tap or clap twice to go back" : "Tap to go back")
                    .font(.footnote)
                    .foregroundColor(.white.opacity(0.45))
                    .padding(.bottom, 30)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { m.privacy = false }
        .statusBarHidden()
    }
}
