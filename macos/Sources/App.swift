// HW Widget for macOS - the menu-bar pill (same design as Windows and GNOME, compacted to the 22 pt menu bar),
// the details card shown as the first item of the menu, and the settings.
import AppKit

// MARK: - Drawing

extension RGB {
    var ns: NSColor { NSColor(srgbRed: r, green: g, blue: b, alpha: a) }
}

enum Pill {
    static let h: CGFloat = 22, pad: CGFloat = 5, cell: CGFloat = 35
    static func size(_ cells: Int) -> NSSize { NSSize(width: pad * 2 + cell * CGFloat(cells), height: h) }
}

enum Card {
    static let w: CGFloat = 300, rowH: CGFloat = 46, headH: CGFloat = 40, footH: CGFloat = 28
    static func size(_ rows: Int) -> NSSize { NSSize(width: w, height: headH + rowH * CGFloat(rows) + footH) }
}

private func rainbow(_ alpha: Double) -> NSGradient {
    let c = Colors.rainbow.map { $0.with(alpha: alpha).ns }
    let locs = (0..<c.count).map { CGFloat($0) / CGFloat(c.count - 1) }
    return NSGradient(colors: c, atLocations: locs, colorSpace: .sRGB)!
}

/// Fills a rounded-rect outline of the given stroke width with a horizontal gradient (macOS 13: no NSBezierPath.cgPath).
private func ring(_ r: NSRect, radius: CGFloat, width: CGFloat, _ g: NSGradient) {
    guard let cg = NSGraphicsContext.current?.cgContext else { return }
    cg.saveGState()
    cg.addPath(CGPath(roundedRect: r, cornerWidth: radius, cornerHeight: radius, transform: nil))
    cg.setLineWidth(width)
    cg.replacePathWithStrokedPath()
    cg.clip()
    g.draw(in: r.insetBy(dx: -width, dy: -width), angle: 0)
    cg.restoreGState()
}

/// Draws text centred vertically in the rect; align 0 left, 0.5 centre, 1 right. Flipped coordinates.
private func text(_ s: String, size: CGFloat, weight: NSFont.Weight, color: RGB, rect: NSRect, align: CGFloat = 0.5) {
    let font = NSFont.monospacedDigitSystemFont(ofSize: size, weight: weight)
    let para = NSMutableParagraphStyle()
    para.lineBreakMode = .byTruncatingTail
    para.alignment = align == 0 ? .left : align == 1 ? .right : .center
    let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color.ns, .paragraphStyle: para]
    let h = ceil(font.ascender - font.descender)
    NSAttributedString(string: s, attributes: attrs).draw(with: NSRect(x: rect.minX, y: rect.midY - h / 2, width: rect.width, height: h),
                                                         options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine])
}

private func bar(_ r: NSRect, _ frac: Double, _ a: RGB, _ b: RGB, _ p: Palette) {
    let track = NSBezierPath(roundedRect: r, xRadius: r.height / 2, yRadius: r.height / 2)
    p.track.with(alpha: frac.isFinite ? p.track.a : p.track.a / 2).ns.setFill()
    track.fill()
    guard frac.isFinite else { return }
    let fw = max(min(r.width, r.height), r.width * CGFloat(max(0, min(1, frac))))
    let fill = NSBezierPath(roundedRect: NSRect(x: r.minX, y: r.minY, width: fw, height: r.height), xRadius: r.height / 2, yRadius: r.height / 2)
    NSGradient(starting: a.ns, ending: b.ns)!.draw(in: fill, angle: 0)
}

/// Draws the pill at 1 unit = 1 pt into a flipped context.
func drawPill(_ cells: [Cell], _ p: Palette, hover: Bool = false) {
    let W = Pill.size(cells.count).width, H = Pill.h
    let outline = NSBezierPath(roundedRect: NSRect(x: 0.75, y: 0.75, width: W - 1.5, height: H - 1.5), xRadius: (H - 1.5) / 2, yRadius: (H - 1.5) / 2)
    NSGradient(starting: p.bg1.ns, ending: p.bg2.ns)!.draw(in: outline, angle: 90)
    if hover { (p.dark ? RGB(1, 1, 1, 0.07) : RGB(0.12, 0.16, 0.23, 0.04)).ns.setFill(); outline.fill() }

    NSGraphicsContext.saveGraphicsState()
    outline.addClip()
    for (i, c) in cells.enumerated() {
        let a = c.level == 2 ? (p.dark ? 0.13 : 0.12) : c.level == 1 ? (p.dark ? 0.09 : 0.07) : (p.dark ? 0.04 : 0.025)
        let tint = c.level == 2 ? p.crit : c.level == 1 ? p.warn : c.bar2
        let cx = Pill.pad + CGFloat(i) * Pill.cell - (i == 0 ? Pill.pad : 0)
        let cw = Pill.cell + (i == 0 || i == cells.count - 1 ? Pill.pad : 0)
        NSGradient(starting: tint.with(alpha: 0).ns, ending: tint.with(alpha: a).ns)!.draw(in: NSRect(x: cx, y: 0, width: cw, height: H), angle: 90)
    }
    NSGraphicsContext.restoreGraphicsState()

    p.edge.ns.setStroke()
    outline.lineWidth = 2
    outline.stroke()
    // Rainbow border: stroke as a path, clip to it, fill with the gradient.
    ring(NSRect(x: 0.75, y: 0.75, width: W - 1.5, height: H - 1.5), radius: (H - 1.5) / 2, width: 1.2, rainbow(hover ? 1 : p.borderAlpha))

    for (i, c) in cells.enumerated() {
        let x = Pill.pad + CGFloat(i) * Pill.cell
        if i > 0 {
            p.sep.ns.setStroke()
            let l = NSBezierPath(); l.lineWidth = 1
            l.move(to: NSPoint(x: x, y: 6)); l.line(to: NSPoint(x: x, y: H - 6)); l.stroke()
        }
        text(c.label, size: 6.4, weight: .bold, color: c.labelColor, rect: NSRect(x: x, y: 2.2, width: Pill.cell, height: 7))
        text(c.unknown ? "\u{2013}" : c.value, size: 9.6, weight: .bold, color: c.unknown ? p.sub.with(alpha: 0.6) : c.valueColor,
             rect: NSRect(x: x - 2, y: 8.2, width: Pill.cell + 4, height: 10))
        bar(NSRect(x: x + 6, y: 18, width: Pill.cell - 12, height: 1.7), c.frac, c.bar1, c.bar2, p)
    }
}

func drawCard(_ rows: [CardRow], _ p: Palette, footer: String) {
    let S = Card.size(rows.count)
    let outline = NSBezierPath(roundedRect: NSRect(x: 0.5, y: 0.5, width: S.width - 1, height: S.height - 1), xRadius: 14, yRadius: 14)
    NSGradient(starting: p.card1.ns, ending: p.card2.ns)!.draw(in: outline, angle: 90)
    p.edge.ns.setStroke(); outline.lineWidth = 2; outline.stroke()
    ring(NSRect(x: 0.5, y: 0.5, width: S.width - 1, height: S.height - 1), radius: 14, width: 1.3, rainbow(p.dark ? 0.63 : 0.78))

    let x: CGFloat = 16, w = S.width - 32
    let logo = NSBezierPath(ovalIn: NSRect(x: x, y: 19, width: 12, height: 12))
    NSGradient(starting: RGB(hex: "22D3EE").ns, ending: RGB(hex: "F472B6").ns)!.draw(in: logo, angle: 45)
    text("Hardware", size: 14, weight: .bold, color: p.text, rect: NSRect(x: x + 20, y: 13, width: 150, height: 24), align: 0)
    text("Live", size: 10.5, weight: .regular, color: p.sub, rect: NSRect(x: x + w - 120, y: 13, width: 120, height: 24), align: 1)

    var y = Card.headH
    for r in rows {
        let dot = NSBezierPath(ovalIn: NSRect(x: x, y: y + 8, width: 9, height: 9))
        if let d = r.dot { d.ns.setFill(); dot.fill() } else { NSGradient(starting: r.a.ns, ending: r.b.ns)!.draw(in: dot, angle: 45) }
        text(r.title, size: 12.5, weight: .semibold, color: p.text, rect: NSRect(x: x + 16, y: y + 3, width: w - 96, height: 19), align: 0)
        text(r.value, size: 13, weight: .bold, color: r.valueColor, rect: NSRect(x: x + w - 96, y: y + 3, width: 96, height: 19), align: 1)
        text(r.sub, size: 10.5, weight: .regular, color: p.sub, rect: NSRect(x: x + 16, y: y + 21, width: w - 16, height: 14), align: 0)
        bar(NSRect(x: x + 16, y: y + 37, width: w - 16, height: 3), r.frac, r.a, r.b, p)
        y += Card.rowH
    }
    text(footer, size: 10, weight: .regular, color: p.sub.with(alpha: 0.85), rect: NSRect(x: x, y: y + 4, width: w, height: 18), align: 0)
}

/// Renders into a bitmap at the given scale (points -> pixels). Used for the status item and the test renders.
func renderImage(size: NSSize, scale: CGFloat, background: RGB? = nil, _ draw: () -> Void) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(ceil(size.width * scale)), pixelsHigh: Int(ceil(size.height * scale)),
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    // rep.size is set only after drawing: set before, AppKit would already scale points to pixels and the
    // scaleBy below would apply twice on Retina.
    NSGraphicsContext.saveGraphicsState()
    let ctx = NSGraphicsContext(bitmapImageRep: rep)!
    NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx.cgContext, flipped: true)
    ctx.cgContext.translateBy(x: 0, y: size.height * scale)
    ctx.cgContext.scaleBy(x: scale, y: -scale)
    if let background { background.ns.setFill(); NSRect(origin: .zero, size: size).fill() }
    draw()
    NSGraphicsContext.restoreGraphicsState()
    rep.size = size
    return rep
}

// MARK: - Settings

extension Settings {
    static let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/HW Widget")
    static let file = dir.appendingPathComponent("settings.json")

    static func load() -> Settings {
        guard let d = try? Data(contentsOf: file), let s = try? JSONDecoder().decode(Settings.self, from: d) else { return Settings() }
        return s.validated()
    }

    func save() {
        try? FileManager.default.createDirectory(at: Settings.dir, withIntermediateDirectories: true)
        if let d = try? JSONEncoder().encode(self) { try? d.write(to: Settings.file) }
    }
}

let launchAgent = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/LaunchAgents/ai.solarity.hw-widget.plist")

func setOpenAtLogin(_ on: Bool) {
    if !on { try? FileManager.default.removeItem(at: launchAgent); return }
    let plist: [String: Any] = ["Label": "ai.solarity.hw-widget", "ProgramArguments": [Bundle.main.executablePath ?? CommandLine.arguments[0]],
                                "RunAtLoad": true, "ProcessType": "Interactive"]
    try? FileManager.default.createDirectory(at: launchAgent.deletingLastPathComponent(), withIntermediateDirectories: true)
    (plist as NSDictionary).write(to: launchAgent, atomically: true)
}

// MARK: - App

final class CardView: NSView {
    var rows: [CardRow] = []
    var palette = Palette.darkTheme
    var footer = ""
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: NSRect) { drawCard(rows, palette, footer: footer) }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    var item: NSStatusItem!
    let sampler = Sampler()
    var settings = Settings.load()
    var snap = Snapshot()
    var timer: Timer?
    let card = CardView(frame: NSRect(origin: .zero, size: Card.size(6)))

    func applicationDidFinishLaunching(_ n: Notification) {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.autosaveName = "HWWidget"
        item.button?.imagePosition = .imageOnly
        item.button?.toolTip = "HW Widget"
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu
        snap = sampler.sample()
        render()
        restartTimer()
        DistributedNotificationCenter.default().addObserver(self, selector: #selector(themeChanged), name: Notification.Name("AppleInterfaceThemeChangedNotification"), object: nil)
        NotificationCenter.default.addObserver(forName: NSWindow.didChangeOcclusionStateNotification, object: nil, queue: .main) { [weak self] n in
            guard let self, (n.object as? NSWindow) === self.item.button?.window else { return }
            self.scheduleFit()
        }
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            self?.retryFullPill()
        }
        scheduleFit()
    }

    @objc func themeChanged() { render() }

    // MARK: Fitting the menu bar

    /// Cells shown in the pill; fewer than all of them only while the full pill would be hidden by macOS.
    var pillFit = PillFit(preferred: 0)
    private var fitWork: DispatchWorkItem?
    private var lastFullRetry = Date()

    /// Checks after the menu bar settles, so a transient state (a full-screen app, a screen change) is ignored.
    func scheduleFit(after delay: TimeInterval = 1.5) {
        fitWork?.cancel()
        let w = DispatchWorkItem { [weak self] in self?.fit() }
        fitWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: w)
    }

    func fit() {
        // The item is also occluded while the menu bar itself is hidden: nothing to fix then.
        guard NSMenu.menuBarVisible(), let w = item.button?.window else { return }
        let visible = w.occlusionState.contains(.visible)
        var slack: Double?
        if visible, let notch = w.screen?.auxiliaryTopRightArea { slack = Double(w.frame.minX - notch.minX) }
        guard pillFit.update(visible: visible, slack: slack, cellWidth: Double(Pill.cell)) else { return }
        render()
        scheduleFit()
    }

    /// The screen changed, or (without a notch, where the free room is unknown) every 30 minutes:
    /// try the full pill again; it is trimmed again if macOS hides it.
    func retryFullPill() {
        lastFullRetry = Date()
        guard pillFit.count < pillFit.preferred else { return }
        pillFit.reset()
        render()
        scheduleFit()
    }

    var isDark: Bool { NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua }

    func restartTimer() {
        timer?.invalidate()
        // Common modes: keeps ticking while the menu is open, so the card stays live.
        let t = Timer(timeInterval: settings.interval, repeats: true) { [weak self] _ in self?.tick() }
        t.tolerance = settings.interval * 0.1
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func tick() {
        snap = sampler.sample()
        render()
        if card.window?.isVisible == true { updateCard() }
        if pillFit.count < pillFit.preferred {
            // Room can open up when another app quits; with a notch the gap is measured, so this costs no flicker.
            if item.button?.window?.screen?.auxiliaryTopRightArea != nil { fit() }
            else if Date().timeIntervalSince(lastFullRetry) > 1800 { retryFullPill() }
        }
    }

    func cells() -> [Cell] {
        buildCells(snap, fahrenheit: settings.fahrenheit, fourth: Fourth(rawValue: settings.fourth) ?? .auto,
                   download: settings.download, palette: .darkTheme)
    }

    func render() {
        let all = cells()
        if all.count != pillFit.preferred {
            // First render, or the download cell was switched on or off: start from the full pill.
            pillFit = PillFit(preferred: all.count)
            scheduleFit()
        }
        let c = Array(all.prefix(pillFit.count))
        let size = Pill.size(c.count)
        let scale = item.button?.window?.backingScaleFactor ?? 2
        let rep = renderImage(size: size, scale: scale) { drawPill(c, .darkTheme) }
        let img = NSImage(size: size)
        img.addRepresentation(rep)
        item.button?.image = img
        item.button?.setAccessibilityLabel(accessibleText(all))
    }

    func updateCard() {
        let p: Palette = isDark ? .darkTheme : .lightTheme
        card.rows = cardRows(snap, fahrenheit: settings.fahrenheit, palette: p)
        card.palette = p
        card.footer = pillFit.count < pillFit.preferred ? "Menu bar full: pill shows \(pillFit.count) of \(pillFit.preferred) metrics"
                                   : "Updates every \(Int(settings.interval)) s \u{00B7} no admin rights needed"
        card.setFrameSize(Card.size(card.rows.count))
        card.needsDisplay = true
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        handlers.removeAll()
        updateCard()
        let cardItem = NSMenuItem()
        cardItem.view = card
        menu.addItem(cardItem)
        menu.addItem(.separator())
        menu.addItem(choice("Refresh every", [(1.0, "1 second"), (2.0, "2 seconds"), (5.0, "5 seconds")].map { ($0.0 == settings.interval, $0.1, $0.0 as Any) }) { [weak self] v in
            self?.settings.interval = v as! Double; self?.restartTimer()
        })
        menu.addItem(choice("Temperature unit", [(!settings.fahrenheit, "Celsius (\u{00B0}C)", false as Any), (settings.fahrenheit, "Fahrenheit (\u{00B0}F)", true as Any)]) { [weak self] v in
            self?.settings.fahrenheit = v as! Bool
        })
        let fourth: [(Fourth, String)] = [(.auto, "Automatic (GPU, else disk)"), (.gpu, "GPU"), (.disk, "Disk")]
        menu.addItem(choice("Fourth metric", fourth.map { ($0.0.rawValue == settings.fourth, $0.1, $0.0.rawValue as Any) }) { [weak self] v in
            self?.settings.fourth = v as! String
        })
        let down = action("Show download speed", #selector(toggleDownload))
        down.state = settings.download ? .on : .off
        menu.addItem(down)
        let login = action("Open at Login", #selector(toggleLogin))
        login.state = FileManager.default.fileExists(atPath: launchAgent.path) ? .on : .off
        menu.addItem(login)
        menu.addItem(action("Open Activity Monitor", #selector(openActivityMonitor)))
        menu.addItem(.separator())
        menu.addItem(action("Quit HW Widget", #selector(quit)))
    }

    private var handlers: [ObjectIdentifier: (Any) -> Void] = [:]

    func choice(_ title: String, _ options: [(Bool, String, Any)], _ apply: @escaping (Any) -> Void) -> NSMenuItem {
        let parent = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        let sub = NSMenu()
        for (on, label, value) in options {
            let it = NSMenuItem(title: label, action: #selector(pick(_:)), keyEquivalent: "")
            it.target = self
            it.state = on ? .on : .off
            it.representedObject = value
            handlers[ObjectIdentifier(it)] = apply
            sub.addItem(it)
        }
        parent.submenu = sub
        return parent
    }

    @objc func pick(_ it: NSMenuItem) {
        guard let apply = handlers[ObjectIdentifier(it)], let v = it.representedObject else { return }
        apply(v)
        settings.save()
        render()
    }

    func action(_ title: String, _ sel: Selector) -> NSMenuItem {
        let it = NSMenuItem(title: title, action: sel, keyEquivalent: "")
        it.target = self
        return it
    }

    @objc func toggleDownload() { settings.download.toggle(); settings.save(); render() }
    @objc func toggleLogin() { setOpenAtLogin(!FileManager.default.fileExists(atPath: launchAgent.path)) }
    @objc func openActivityMonitor() { NSWorkspace.shared.open(URL(fileURLWithPath: "/System/Applications/Utilities/Activity Monitor.app")) }
    @objc func quit() { NSApp.terminate(nil) }
}
