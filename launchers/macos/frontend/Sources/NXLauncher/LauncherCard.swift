import SwiftUI
import AppKit
import NXCore
import NXModel

// The pieces every card of the Builds and Settings pages is made of: a titled group, a row, a divider, a disclosure row.
// They are the library's and the launch options' look (LNPANDY/NX2Apple#387, step 8).

extension StepState {
    package var color: Color {
        switch self {
        case .pending: return .gray
        case .running: return .yellow
        case .done: return .green
        case .failed: return .red
        }
    }
}

/// The big title of a page with a note under it.
package struct CardPageHeading: View {
    let title: String
    let note: String

    package init(_ title: String, note: String) {
        self.title = title
        self.note = note
    }

    package var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.manrope(44, .heavy)).tracking(-1.3)
            Text(note).font(.manrope(15)).foregroundStyle(.secondary)
        }
    }
}

/// A titled group: a small tracked label over one card of rows.
package struct CardGroup<Content: View>: View {
    let label: String
    let footnote: String
    let content: Content

    package init(_ label: String, footnote: String = "", @ViewBuilder _ content: () -> Content) {
        self.label = label
        self.footnote = footnote
        self.content = content()
    }

    package var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(label).font(.manrope(15, .semibold)).foregroundStyle(.secondary)
                .padding(.leading, 2)
            VStack(spacing: 0) { content }
                .background(LauncherPalette.surface.opacity(0.75), in: RoundedRectangle(cornerRadius: 14))
                .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(Color.white.opacity(0.06)))
            if !footnote.isEmpty {
                Text(footnote).font(.manrope(12)).foregroundStyle(.secondary).padding(.leading, 2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

/// One line of a card: a status dot (its place kept when there is none, so every title starts on one line),
/// the title (with a disclosure chevron beside it when `open` is given), a secondary detail, and its controls.
/// Its buttons and dropdowns are all LauncherControl.width wide, so they line up in one column across the cards.
package struct CardRow<Trailing: View>: View {
    let title: String
    let detail: String
    let detail2: String
    let dot: Color?
    let help: String
    let open: Bool?
    let trailing: Trailing

    package init(_ title: String, detail: String = "", detail2: String = "", dot: Color? = nil, help: String = "", open: Bool? = nil,
                 @ViewBuilder trailing: () -> Trailing) {
        self.title = title
        self.detail = detail
        self.detail2 = detail2
        self.dot = dot
        self.help = help
        self.open = open
        self.trailing = trailing()
    }

    package var body: some View {
        HStack(spacing: 12) {
            Circle().fill(dot ?? .clear).frame(width: 7, height: 7)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(title).font(.manrope(14)).fixedSize(horizontal: false, vertical: true)
                    if let open {
                        Image(systemName: "chevron.right").font(.system(size: 10, weight: .bold)).foregroundStyle(.secondary)
                            .rotationEffect(.degrees(open ? 90 : 0))
                    }
                }
                if !detail.isEmpty {
                    // A path is technical text: the system's monospaced face, on one line, cut in the middle when
                    // too long. Everything else is prose in Manrope and wraps, so no word is ever cut.
                    if detail.hasPrefix("/") || detail.hasPrefix("~") {
                        PathLink(detail, help: help)
                    } else {
                        Text(detail).font(.manrope(12.5)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true).help(help.isEmpty ? detail : help)
                    }
                }
                if !detail2.isEmpty {
                    if detail2.hasPrefix("/") || detail2.hasPrefix("~") { PathLink(detail2) }
                    else { Text(detail2).font(.mono(12)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle).help(detail2) }
                }
            }
            Spacer(minLength: 12)
            trailing.launcherRowControls()
        }
        .padding(.horizontal, 18).padding(.vertical, 13)
        .frame(minHeight: 52)
    }
}

/// A path shown on a card: monospaced, one line, cut in the middle. When it exists on disk it is a link — a click opens the folder
/// in Finder (a file is selected in its folder), so nobody has to copy the path by hand. A path that is not there (a disk not
/// plugged in) stays plain text.
package struct PathLink: View {
    let path: String
    let help: String
    @State private var hovering = false

    package init(_ path: String, help: String = "") { self.path = path; self.help = help }

    private var url: URL { URL(fileURLWithPath: (path as NSString).expandingTildeInPath) }

    package var body: some View {
        var isDir: ObjCBool = false
        let exists = FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir)
        let text = Text(path).font(.mono(12)).lineLimit(1).truncationMode(.middle)
        if exists {
            Button {
                if isDir.boolValue { NSWorkspace.shared.open(url) } else { NSWorkspace.shared.activateFileViewerSelecting([url]) }
            } label: {
                text.underline(hovering).foregroundStyle(hovering ? Color.primary : Color.secondary)
            }
            .buttonStyle(.plain)
            .onHover { inside in
                hovering = inside
                if inside { NSCursor.pointingHand.push() } else { NSCursor.pop() }
            }
            .help(help.isEmpty ? path : help)
            .accessibilityHint(Text(path))
        } else {
            text.foregroundStyle(.secondary).help(help.isEmpty ? path : help)
        }
    }
}

package struct CardDivider: View {
    package init() {}
    package var body: some View { Divider().opacity(0.25).padding(.leading, 18) }
}

/// A row that opens the rows under it.
package struct CardDisclosure: View {
    let title: String
    let detail: String
    let isOpen: Bool
    let toggle: () -> Void

    package init(_ title: String, detail: String = "", isOpen: Bool, toggle: @escaping () -> Void) {
        self.title = title
        self.detail = detail
        self.isOpen = isOpen
        self.toggle = toggle
    }

    package var body: some View {
        Button(action: { withAnimation(.easeInOut(duration: 0.18)) { toggle() } }) {
            CardRow(title, detail: detail, open: isOpen) { EmptyView() }
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

/// The scrolling column of a page of cards. A controller's focus on one of its `set.` targets scrolls into view.
package struct LauncherPageScroll<Content: View>: View {
    let content: Content
    @EnvironmentObject private var padFocus: PadFocus

    package init(@ViewBuilder _ content: () -> Content) { self.content = content() }

    package var body: some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 34) { content }
                    .padding(.horizontal, 40).padding(.top, 40).padding(.bottom, 48)
                    .frame(maxWidth: 900)
                    .frame(maxWidth: .infinity)
            }
            .onChange(of: padFocus.focused) { id in   // a controller's focus on this page scrolls into view
                if let id, id.hasPrefix("set.") { withAnimation(.easeInOut(duration: 0.2)) { proxy.scrollTo(id, anchor: .center) } }
            }
        }
        .buttonStyle(LauncherSecondaryButtonStyle())
    }
}

package func revealInFinder(_ path: String) {
    NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
}
