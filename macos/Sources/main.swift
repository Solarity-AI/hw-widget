// Entry point. With no arguments it runs the menu-bar app. Test modes:
//   --self-test            model and parser checks, no hardware access
//   --probe <seconds>      prints one live reading per 2 s (CPU, RAM, temperatures, GPU, disk, network)
//   --render <dir>         writes PNGs of the pill and the card for normal, hot and unknown readings
//   --version
import AppKit

let args = CommandLine.arguments

func argValue(_ name: String) -> String? {
    guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
    return args[i + 1]
}

if args.contains("--version") {
    print(appVersion)
    exit(0)
}

if args.contains("--self-test") {
    let r = coreSelfTest()
    print("\(r.passed) passed, \(r.failed) failed")
    exit(r.failed == 0 ? 0 : 1)
}

if let secs = argValue("--probe").flatMap(Double.init) {
    let s = Sampler()
    _ = s.sample()
    let end = Date().addingTimeInterval(secs)
    while Date() < end {
        Thread.sleep(forTimeInterval: 2)
        let t0 = Date()
        let r = s.sample()
        let ms = Int(Date().timeIntervalSince(t0) * 1000)
        let gpu = r.gpus.map { "\($0.name) \(Fmt.pct($0.percent))" }.joined(separator: ", ")
        print("cpu \(Fmt.pct(r.cpuPercent)) temp \(Fmt.tempLong(r.cpuTempC, false)) [\(r.cpuTempSource)\(r.thermalState)] ram \(Fmt.pct(r.ramPercent)) "
            + "gpu \(gpu.isEmpty ? dash : gpu) gpuTemp \(Fmt.tempLong(r.gpuTempC, false)) disk \(Fmt.pct(r.diskPercent)) "
            + "down \(Fmt.rate(r.netDownBps)) up \(Fmt.rate(r.netUpBps)) (\(ms) ms)")
    }
    exit(0)
}

if let dir = argValue("--render") {
    try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
    var normal = Snapshot()
    normal.cpuName = "Apple M3 Pro"; normal.cpuTempC = 61; normal.cpuTempSource = "2 die sensors"; normal.cpuPercent = 23
    normal.ramPercent = 58; normal.ramUsedGB = 10.4; normal.ramTotalGB = 18; normal.diskPercent = 4; normal.netDownBps = 182e3; normal.netUpBps = 21e3
    normal.gpus = [GpuInfo(name: "Apple M3 Pro", percent: 12, tempC: 51, memUsedGB: 1.2)]; normal.gpuPercent = 12; normal.gpuTempC = 51
    var hot = normal
    hot.cpuTempC = 93; hot.cpuPercent = 96; hot.ramPercent = 91; hot.gpuPercent = 97; hot.gpus[0].percent = 97
    for (name, snap) in [("normal", normal), ("hot", hot), ("unknown", Snapshot())] {
        let c = buildCells(snap, download: true)
        let size = Pill.size(c.count)
        let bar = NSSize(width: size.width + 24, height: 24)
        for scale: CGFloat in [1, 2] {
            let rep = renderImage(size: bar, scale: scale, background: RGB(0.12, 0.12, 0.13)) {
                NSGraphicsContext.current?.cgContext.translateBy(x: 12, y: 1)
                drawPill(c, .darkTheme)
            }
            try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: "\(dir)/mac-pill-\(name)-\(Int(scale * 100)).png"))
        }
        for (theme, p) in [("dark", Palette.darkTheme), ("light", Palette.lightTheme)] {
            let rows = cardRows(snap, palette: p)
            let rep = renderImage(size: Card.size(rows.count), scale: 2) { drawCard(rows, p, footer: "Updates every 2 s \u{00B7} no admin rights needed") }
            try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: "\(dir)/mac-card-\(theme)-\(name).png"))
        }
    }
    print("rendered to \(dir)")
    exit(0)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
