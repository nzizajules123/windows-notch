import SwiftUI

/// The island's living eyes: they blink, glance around and pull faces.
struct EyesView: View {
    var mood: String
    var size: CGFloat = 16

    @State private var blink = false
    @State private var look = CGSize.zero
    private let timer = Timer.publish(every: 0.9, on: .main, in: .common).autoconnect()

    var body: some View {
        HStack(spacing: size * 0.6) {
            eye
            eye
        }
        .onReceive(timer) { _ in
            // blink now and then, glance somewhere else now and then
            if Int.random(in: 0..<5) == 0 {
                withAnimation(.easeIn(duration: 0.08)) { blink = true }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
                    withAnimation(.easeOut(duration: 0.1)) { blink = false }
                }
            }
            if Int.random(in: 0..<4) == 0 {
                withAnimation(.spring(response: 0.35, dampingFraction: 0.7)) {
                    look = CGSize(width: CGFloat.random(in: -0.22...0.22), height: CGFloat.random(in: -0.12...0.15))
                }
            }
        }
    }

    @ViewBuilder private var eye: some View {
        switch mood {
        case "love":
            Image(systemName: "heart.fill")
                .font(.system(size: size))
                .foregroundColor(Color(red: 1, green: 0.33, blue: 0.55))
                .transition(.scale)
        case "happy":
            Image(systemName: "chevron.up")
                .font(.system(size: size * 0.95, weight: .heavy))
                .foregroundColor(.white)
        case "sleepy":
            Capsule().fill(Color.white).frame(width: size, height: size * 0.25)
        default:
            let h = mood == "surprised" ? size * 1.4 : size * 1.2
            ZStack {
                Ellipse().fill(Color.white)
                Circle().fill(Color.black)
                    .frame(width: size * 0.52, height: size * 0.52)
                    .offset(x: look.width * size, y: look.height * size)
                Circle().fill(Color.white).frame(width: size * 0.16, height: size * 0.16)
                    .offset(x: look.width * size + size * 0.1, y: look.height * size - size * 0.1)
            }
            .frame(width: size, height: blink ? size * 0.12 : h)
            .clipShape(Ellipse())
        }
    }
}
