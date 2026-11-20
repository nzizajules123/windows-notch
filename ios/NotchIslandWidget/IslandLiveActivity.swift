import ActivityKit
import SwiftUI
import WidgetKit

@main
struct NotchIslandWidgets: WidgetBundle {
    var body: some Widget {
        IslandLiveActivity()
    }
}

/// The island in the iPhone's Dynamic Island: eyes on the left, battery on the right; long-press to see what's
/// playing and your next to-do with a live countdown.
struct IslandLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: IslandAttributes.self) { context in
            LockScreenCard(state: context.state)
                .activityBackgroundTint(Color.black.opacity(0.82))
                .activitySystemActionForegroundColor(.white)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    MiniEyes(mood: context.state.mood, size: 15).padding(.leading, 8).padding(.top, 4)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    BatteryLabel(state: context.state).padding(.trailing, 8).padding(.top, 4)
                }
                DynamicIslandExpandedRegion(.center) {
                    if let now = context.state.nowPlaying {
                        Label(now, systemImage: "music.note").font(.caption).lineLimit(1).foregroundColor(.white)
                    } else {
                        Text("Notch Island").font(.caption).foregroundColor(.white.opacity(0.7))
                    }
                }
                DynamicIslandExpandedRegion(.bottom) {
                    TodoLine(state: context.state).padding(.horizontal, 8)
                }
            } compactLeading: {
                MiniEyes(mood: context.state.mood, size: 9).padding(.leading, 2)
            } compactTrailing: {
                Text("\(context.state.battery)%")
                    .font(.caption2.weight(.semibold))
                    .monospacedDigit()
                    .foregroundColor(context.state.batteryColor)
            } minimal: {
                MiniEyes(mood: context.state.mood, size: 6)
            }
            .keylineTint(.white)
        }
    }
}

struct BatteryLabel: View {
    let state: IslandAttributes.ContentState
    var body: some View {
        HStack(spacing: 4) {
            if state.charging { Image(systemName: "bolt.fill").font(.caption2) }
            Text("\(state.battery)%").font(.caption.weight(.semibold)).monospacedDigit()
        }
        .foregroundColor(state.batteryColor)
    }
}

struct TodoLine: View {
    let state: IslandAttributes.ContentState
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "checklist").foregroundColor(Color(red: 0.2, green: 0.6, blue: 1))
            if let t = state.todoTitle {
                Text(t).lineLimit(1).foregroundColor(.white)
                Spacer(minLength: 4)
                if let due = state.todoDue, due > Date() {
                    Text(timerInterval: Date()...due, countsDown: true)
                        .monospacedDigit()
                        .multilineTextAlignment(.trailing)
                        .frame(maxWidth: 70, alignment: .trailing)
                        .foregroundColor(.white.opacity(0.75))
                }
            } else {
                Text("Nothing due").foregroundColor(.white.opacity(0.6))
                Spacer()
            }
        }
        .font(.caption)
    }
}

/// The same island as a card on the lock screen.
struct LockScreenCard: View {
    let state: IslandAttributes.ContentState
    var body: some View {
        VStack(spacing: 10) {
            HStack {
                MiniEyes(mood: state.mood, size: 16)
                Spacer()
                if let now = state.nowPlaying {
                    Label(now, systemImage: "music.note").font(.caption).lineLimit(1).foregroundColor(.white)
                }
                Spacer()
                BatteryLabel(state: state)
            }
            TodoLine(state: state)
        }
        .padding(14)
    }
}
