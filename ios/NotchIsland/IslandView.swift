import SwiftUI

/// The island: a small black pill (time · eyes · battery, plus the lyric line while music plays) that opens into
/// Music, To-do and Control, like the Windows notch and the Android island.
struct IslandView: View {
    @EnvironmentObject var m: IslandModel
    @EnvironmentObject var music: MusicModel

    var body: some View {
        VStack(spacing: 8) {
            ZStack {
                RoundedRectangle(cornerRadius: m.expanded ? 34 : 22, style: .continuous)
                    .fill(Color.black)
                    .overlay(
                        RoundedRectangle(cornerRadius: m.expanded ? 34 : 22, style: .continuous)
                            .stroke(Color.white.opacity(0.08), lineWidth: 1)
                    )
                    .shadow(color: .black.opacity(0.5), radius: m.expanded ? 24 : 10, y: 6)

                if m.expanded { ExpandedIsland().transition(.opacity.combined(with: .scale(scale: 0.96, anchor: .top))) }
                else { CollapsedIsland().transition(.opacity) }
            }
            .frame(width: m.expanded ? nil : 236, height: m.expanded ? 380 : (lyric == nil ? 44 : 66))
            .frame(maxWidth: m.expanded ? .infinity : nil)
            .padding(.horizontal, m.expanded ? 10 : 0)
            .onTapGesture { if !m.expanded { m.expanded = true } }

            if let s = m.speech {
                Text(s)
                    .font(.footnote.weight(.semibold))
                    .padding(.horizontal, 14).padding(.vertical, 8)
                    .background(Capsule().fill(Color.white))
                    .foregroundColor(.black)
                    .transition(.scale(scale: 0.6, anchor: .top).combined(with: .opacity))
            }
        }
        .animation(.spring(response: 0.42, dampingFraction: 0.8), value: m.expanded)
        .animation(.spring(response: 0.42, dampingFraction: 0.8), value: lyric)
    }

    private var lyric: String? { m.expanded ? nil : music.currentLine() }
}

private struct CollapsedIsland: View {
    @EnvironmentObject var m: IslandModel
    @EnvironmentObject var music: MusicModel

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.5)) { ctx in
            VStack(spacing: 4) {
                HStack {
                    Text(ctx.date, format: .dateTime.hour().minute())
                        .font(.subheadline.weight(.semibold)).monospacedDigit()
                    Spacer()
                    EyesView(mood: m.mood, size: 13)
                        .onTapGesture { m.poke() }
                    Spacer()
                    if music.playing { Bars().frame(width: 22, height: 14) }
                    else { BatteryChip(level: m.battery, charging: m.charging) }
                }
                if let line = music.currentLine() {
                    Text(line)
                        .font(.caption.weight(.medium))
                        .foregroundColor(.white.opacity(0.85))
                        .lineLimit(1)
                        .id(line)
                        .transition(.opacity)
                }
            }
            .padding(.horizontal, 16)
            .foregroundColor(.white)
        }
    }
}

private struct BatteryChip: View {
    let level: Int
    let charging: Bool
    var body: some View {
        HStack(spacing: 4) {
            Text("\(level)%").font(.caption.weight(.semibold)).monospacedDigit()
            Image(systemName: charging ? "battery.100.bolt" : level <= 20 ? "battery.25" : "battery.75")
                .foregroundColor(charging ? .green : level <= 20 ? .red : .white)
        }
    }
}

/// Little equalizer bars while music plays.
private struct Bars: View {
    @State private var phase = false
    private let high: [CGFloat] = [14, 7, 11, 5]
    private let low: [CGFloat] = [5, 13, 6, 12]
    var body: some View {
        HStack(alignment: .bottom, spacing: 2.5) {
            ForEach(0..<4, id: \.self) { i in
                Capsule()
                    .fill(Color(red: 1, green: 0.37, blue: 0.6))
                    .frame(width: 3, height: phase ? high[i] : low[i])
            }
        }
        .onAppear {
            withAnimation(.easeInOut(duration: 0.45).repeatForever(autoreverses: true)) { phase = true }
        }
    }
}

// MARK: - Open island

private struct ExpandedIsland: View {
    @EnvironmentObject var m: IslandModel

    var body: some View {
        VStack(spacing: 12) {
            HStack(spacing: 6) {
                ForEach(IslandTab.allCases, id: \.self) { t in
                    Button { withAnimation(.easeInOut(duration: 0.2)) { m.tab = t } } label: {
                        Image(systemName: t.icon)
                            .font(.system(size: 14, weight: .semibold))
                            .frame(width: 38, height: 30)
                            .background(Capsule().fill(m.tab == t ? Color.white.opacity(0.16) : .clear))
                    }
                    .foregroundColor(m.tab == t ? .white : .white.opacity(0.55))
                }
                Spacer()
                EyesView(mood: m.mood, size: 11).onTapGesture { m.poke() }
                Spacer()
                Button { m.expanded = false } label: {
                    Image(systemName: "chevron.up").font(.system(size: 13, weight: .bold))
                        .frame(width: 30, height: 30)
                        .background(Circle().fill(Color.white.opacity(0.1)))
                }
                .foregroundColor(.white)
            }

            Group {
                switch m.tab {
                case .media: MediaTab()
                case .todo: TodoTab()
                case .control: ControlTab()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        }
        .padding(16)
        .foregroundColor(.white)
    }
}

private struct MediaTab: View {
    @EnvironmentObject var music: MusicModel

    var body: some View {
        if !music.authorized {
            VStack(spacing: 12) {
                Image(systemName: "music.note.list").font(.system(size: 36))
                Text("See what's playing in the Music app, with controls and synced lyrics.")
                    .multilineTextAlignment(.center).foregroundColor(.white.opacity(0.7))
                Button("Allow Music") { music.requestAccess() }
                    .buttonStyle(.borderedProminent).tint(.pink)
            }
            .padding(.top, 30)
        } else if let title = music.title {
            VStack(spacing: 14) {
                HStack(spacing: 14) {
                    Group {
                        if let art = music.art { Image(uiImage: art).resizable().scaledToFill() }
                        else { Color.white.opacity(0.1).overlay(Image(systemName: "music.note")) }
                    }
                    .frame(width: 84, height: 84)
                    .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
                    VStack(alignment: .leading, spacing: 4) {
                        Text(title).font(.headline).lineLimit(2)
                        Text(music.artist ?? "").font(.subheadline).foregroundColor(.white.opacity(0.6)).lineLimit(1)
                    }
                    Spacer()
                }
                HStack(spacing: 40) {
                    Button { music.previous() } label: { Image(systemName: "backward.fill") }
                    Button { music.toggle() } label: { Image(systemName: music.playing ? "pause.fill" : "play.fill").font(.system(size: 30)) }
                    Button { music.next() } label: { Image(systemName: "forward.fill") }
                }
                .font(.system(size: 22))
                .foregroundColor(.white)

                TimelineView(.periodic(from: .now, by: 0.4)) { _ in
                    Text(music.currentLine() ?? (music.lyrics.isEmpty ? " " : "♪"))
                        .font(.title3.weight(.semibold))
                        .multilineTextAlignment(.center)
                        .lineLimit(2)
                        .foregroundColor(Color(red: 1, green: 0.55, blue: 0.75))
                        .frame(maxWidth: .infinity)
                }
            }
        } else {
            VStack(spacing: 10) {
                Image(systemName: "play.circle").font(.system(size: 40)).foregroundColor(.white.opacity(0.5))
                Text("Nothing playing in Music").foregroundColor(.white.opacity(0.6))
                Text("iOS only lets apps see the Music app — not Spotify or YouTube.")
                    .font(.caption).foregroundColor(.white.opacity(0.4)).multilineTextAlignment(.center)
            }
            .padding(.top, 40)
        }
    }
}

private struct TodoTab: View {
    @EnvironmentObject var todos: TodoStore
    @State private var text = ""
    @State private var remind = false
    @State private var when = Date().addingTimeInterval(3600)

    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 8) {
                TextField("Add a to-do", text: $text)
                    .padding(.horizontal, 12).padding(.vertical, 9)
                    .background(RoundedRectangle(cornerRadius: 12).fill(Color.white.opacity(0.08)))
                    .submitLabel(.done)
                    .onSubmit(add)
                Button { remind.toggle() } label: {
                    Image(systemName: remind ? "alarm.fill" : "alarm")
                        .frame(width: 38, height: 38)
                        .background(Circle().fill(remind ? Color.orange.opacity(0.3) : Color.white.opacity(0.08)))
                }
                .foregroundColor(remind ? .orange : .white)
                Button(action: add) {
                    Image(systemName: "plus").font(.system(size: 15, weight: .bold))
                        .frame(width: 38, height: 38)
                        .background(Circle().fill(Color.blue))
                }
                .foregroundColor(.white)
            }
            if remind {
                DatePicker("Remind me", selection: $when, in: Date()..., displayedComponents: [.date, .hourAndMinute])
                    .font(.subheadline)
            }
            ScrollView {
                VStack(spacing: 6) {
                    if todos.items.isEmpty {
                        Text("Nothing to do 🎉").foregroundColor(.white.opacity(0.5)).padding(.top, 20)
                    }
                    ForEach(todos.items) { item in
                        HStack(spacing: 10) {
                            Button { todos.toggle(item.id) } label: {
                                Image(systemName: item.done ? "checkmark.circle.fill" : "circle")
                                    .font(.system(size: 20))
                                    .foregroundColor(item.done ? .green : .white.opacity(0.6))
                            }
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.title).strikethrough(item.done).foregroundColor(item.done ? .white.opacity(0.45) : .white)
                                if let due = item.due {
                                    Label(due.formatted(date: .abbreviated, time: .shortened), systemImage: "alarm")
                                        .font(.caption2)
                                        .foregroundColor(due < Date() && !item.done ? .red : .orange)
                                }
                            }
                            Spacer()
                            Button { todos.remove(item.id) } label: { Image(systemName: "xmark").font(.caption) }
                                .foregroundColor(.white.opacity(0.4))
                        }
                        .padding(.horizontal, 10).padding(.vertical, 8)
                        .background(RoundedRectangle(cornerRadius: 12).fill(Color.white.opacity(0.05)))
                    }
                }
            }
        }
    }

    private func add() {
        todos.add(text, due: remind ? when : nil)
        text = ""
        remind = false
    }
}

private struct ControlTab: View {
    @EnvironmentObject var m: IslandModel

    var body: some View {
        ScrollView {
            VStack(spacing: 10) {
                Button { m.expanded = false; m.privacy = true } label: {
                    Label("Privacy screen", systemImage: "lock.fill")
                        .font(.headline)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 12)
                        .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(
                            LinearGradient(colors: [Color(red: 0.36, green: 0.4, blue: 1), Color(red: 0.62, green: 0.35, blue: 1)],
                                           startPoint: .leading, endPoint: .trailing)))
                }
                .foregroundColor(.white)

                ToggleRow(icon: "hands.clap.fill", tint: .orange, title: "Claps 👏", detail: m.clapStatus, isOn: $m.clapsOn)
                ToggleRow(icon: "rectangle.topthird.inset.filled", tint: .blue, title: "Dynamic Island",
                          detail: m.liveSupported ? "Eyes, battery and your next to-do up top" : "Turn on Live Activities in Settings › Notch Island",
                          isOn: $m.liveOn)
                ToggleRow(icon: "bubble.left.fill", tint: .pink, title: "Eyes talk", detail: "Little speech bubbles", isOn: $m.talk)
            }
        }
    }
}

private struct ToggleRow: View {
    let icon: String
    let tint: Color
    let title: String
    let detail: String
    @Binding var isOn: Bool

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: icon)
                .font(.system(size: 14))
                .frame(width: 32, height: 32)
                .background(Circle().fill(tint))
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.subheadline.weight(.semibold))
                Text(detail).font(.caption).foregroundColor(.white.opacity(0.55)).lineLimit(2)
            }
            Spacer()
            Toggle("", isOn: $isOn).labelsHidden().tint(.green)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.white.opacity(0.06)))
    }
}
