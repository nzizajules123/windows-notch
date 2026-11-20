import Foundation
import UserNotifications

struct TodoItem: Codable, Identifiable, Equatable {
    var id = UUID()
    var title: String
    var done = false
    var due: Date?
}

/// To-dos with optional reminders (a normal iPhone notification at the chosen time). Saved on the phone.
@MainActor
final class TodoStore: ObservableObject {
    @Published private(set) var items: [TodoItem] = []
    private let key = "todos"

    init() {
        if let data = UserDefaults.standard.data(forKey: key),
           let list = try? JSONDecoder().decode([TodoItem].self, from: data) {
            items = list
        }
    }

    /// The next open to-do with a reminder still ahead, else the first open one.
    var next: TodoItem? {
        let open = items.filter { !$0.done }
        let upcoming = open.filter { ($0.due ?? .distantPast) > Date() }.sorted { $0.due! < $1.due! }
        return upcoming.first ?? open.first
    }

    func add(_ title: String, due: Date?) {
        let t = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return }
        let item = TodoItem(title: t, due: due)
        items.insert(item, at: 0)
        save()
        if let due, due > Date() { schedule(item, at: due) }
    }

    func toggle(_ id: UUID) {
        guard let i = items.firstIndex(where: { $0.id == id }) else { return }
        items[i].done.toggle()
        if items[i].done { unschedule(id) }
        else if let due = items[i].due, due > Date() { schedule(items[i], at: due) }
        save()
    }

    func remove(_ id: UUID) {
        items.removeAll { $0.id == id }
        unschedule(id)
        save()
    }

    func clearDone() {
        items.filter(\.done).forEach { unschedule($0.id) }
        items.removeAll(where: \.done)
        save()
    }

    private func save() {
        if let data = try? JSONEncoder().encode(items) { UserDefaults.standard.set(data, forKey: key) }
    }

    private func schedule(_ item: TodoItem, at date: Date) {
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert, .sound, .badge]) { ok, _ in
            guard ok else { return }
            let content = UNMutableNotificationContent()
            content.title = "⏰ " + item.title
            content.body = "Reminder from Notch Island"
            content.sound = .default
            let comps = Calendar.current.dateComponents([.year, .month, .day, .hour, .minute], from: date)
            let req = UNNotificationRequest(identifier: item.id.uuidString, content: content,
                                            trigger: UNCalendarNotificationTrigger(dateMatching: comps, repeats: false))
            center.add(req)
        }
    }

    private func unschedule(_ id: UUID) {
        UNUserNotificationCenter.current().removePendingNotificationRequests(withIdentifiers: [id.uuidString])
    }
}
