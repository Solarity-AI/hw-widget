// HW Widget for macOS - shared model: formatting, thresholds, colours, the 4 menu-bar cells, the card rows,
// pure sensor helpers and the self-test. Foundation only, so it also builds and tests on Linux (swift:6.0 Docker).
import Foundation

let appVersion = "1.0.0"
let dash = "\u{2014}"

// MARK: - Colours

struct RGB: Equatable {
    var r: Double, g: Double, b: Double, a: Double = 1

    init(_ r: Double, _ g: Double, _ b: Double, _ a: Double = 1) {
        self.r = r; self.g = g; self.b = b; self.a = a
    }

    init(hex: String, alpha: Double = 1) {
        let s = hex.hasPrefix("#") ? String(hex.dropFirst()) : hex
        let v = UInt32(s, radix: 16) ?? 0
        self.init(Double((v >> 16) & 0xFF) / 255, Double((v >> 8) & 0xFF) / 255, Double(v & 0xFF) / 255, alpha)
    }

    var hex: String {
        func c(_ x: Double) -> String { String(format: "%02X", Int((max(0, min(1, x)) * 255).rounded())) }
        return "#" + c(r) + c(g) + c(b)
    }

    func with(alpha: Double) -> RGB { RGB(r, g, b, alpha) }

    func mix(_ o: RGB, _ t: Double) -> RGB {
        let t = max(0, min(1, t))
        return RGB(r + (o.r - r) * t, g + (o.g - g) * t, b + (o.b - b) * t, a)
    }

    func lighten(_ t: Double) -> RGB { mix(RGB(1, 1, 1), t) }
    func darken(_ t: Double) -> RGB { mix(RGB(0, 0, 0), t) }
}

enum Colors {
    static let cpu = (RGB(hex: "38BDF8"), RGB(hex: "818CF8"))
    static let ram = (RGB(hex: "C084FC"), RGB(hex: "F472B6"))
    static let gpu = (RGB(hex: "4ADE80"), RGB(hex: "2DD4BF"))
    static let disk = (RGB(hex: "FBBF24"), RGB(hex: "F97316"))
    static let net = (RGB(hex: "60A5FA"), RGB(hex: "A78BFA"))
    static let hot = RGB(hex: "F43F5E")
    static let warm = RGB(hex: "F59E0B")
    static let rainbow = ["22D3EE", "60A5FA", "A78BFA", "F472B6", "FB923C", "FACC15", "34D399"].map { RGB(hex: $0) }
}

struct Palette {
    let bg1, bg2, text, sub, track, sep, warn, crit, card1, card2, edge: RGB
    let borderAlpha: Double
    let dark: Bool

    static let darkTheme = Palette(
        bg1: RGB(hex: "2A2A40"), bg2: RGB(hex: "18182A"), text: RGB(hex: "F8FAFC"), sub: RGB(hex: "A5ABC3"),
        track: RGB(1, 1, 1, 0.14), sep: RGB(1, 1, 1, 0.12), warn: RGB(hex: "FCD34D"), crit: RGB(hex: "FF8FA3"),
        card1: RGB(hex: "26263B"), card2: RGB(hex: "15151F"), edge: RGB(0, 0, 0, 0.27), borderAlpha: 0.69, dark: true)
    static let lightTheme = Palette(
        bg1: RGB(hex: "FFFFFF"), bg2: RGB(hex: "EEF1F8"), text: RGB(hex: "111827"), sub: RGB(hex: "5B6478"),
        track: RGB(0.07, 0.09, 0.15, 0.18), sep: RGB(0.07, 0.09, 0.15, 0.12), warn: RGB(hex: "B45309"), crit: RGB(hex: "E11D48"),
        card1: RGB(hex: "FFFFFF"), card2: RGB(hex: "F3F5FB"), edge: RGB(0.06, 0.09, 0.16, 0.13), borderAlpha: 0.92, dark: false)
}

private let rampT: [Double] = [35, 50, 65, 75, 85, 95]
private let rampC = ["22D3EE", "34D399", "A3E635", "FACC15", "FB923C", "F43F5E"].map { RGB(hex: $0) }

func tempColor(_ c: Double) -> RGB {
    guard c.isFinite else { return RGB(hex: "94A3B8") }
    if c <= rampT[0] { return rampC[0] }
    for i in 1..<rampT.count where c <= rampT[i] {
        return rampC[i - 1].mix(rampC[i], (c - rampT[i - 1]) / (rampT[i] - rampT[i - 1]))
    }
    return rampC[rampC.count - 1]
}

// MARK: - Thresholds and formatting

enum Thresholds {
    static let temp = (80.0, 90.0), cpu = (75.0, 90.0), ram = (80.0, 90.0), gpu = (85.0, 95.0), disk = (80.0, 95.0)
}

/// Judged on the whole number shown, so 74.6 % reads "75%" and is amber, never "75%" in white.
func level(_ v: Double, _ th: (Double, Double)) -> Int {
    guard v.isFinite else { return 0 }
    let v = v.rounded()
    return v >= th.1 ? 2 : v >= th.0 ? 1 : 0
}

enum Fmt {
    static func temp(_ c: Double, _ f: Bool) -> String {
        guard c.isFinite else { return dash }
        return "\(Int((f ? c * 9 / 5 + 32 : c).rounded()))\u{00B0}"
    }

    static func tempLong(_ c: Double, _ f: Bool) -> String { c.isFinite ? temp(c, f) + (f ? "F" : "C") : dash }

    static func pct(_ p: Double) -> String { p.isFinite ? "\(Int(max(0, min(100, p)).rounded()))%" : dash }

    static func rate(_ bps: Double) -> String {
        guard bps.isFinite else { return dash }
        let units = ["B/s", "KB/s", "MB/s", "GB/s"]
        var v = max(0, bps), i = 0
        while v >= 1000 && i < units.count - 1 { v /= 1024; i += 1 }
        return (v < 10 && i > 0 ? String(format: "%.1f", v) : "\(Int(v.rounded()))") + " " + units[i]
    }

    static func gb(_ g: Double) -> String {
        guard g.isFinite else { return dash }
        return (g < 10 ? String(format: "%.1f", g) : "\(Int(g.rounded()))") + " GB"
    }
}

// MARK: - Snapshot, cells and rows

struct GpuInfo: Equatable {
    var name = "", percent = Double.nan, tempC = Double.nan, memUsedGB = Double.nan
}

struct Snapshot {
    var cpuName = "", cpuPercent = Double.nan, cpuTempC = Double.nan, cpuTempSource = ""
    var thermalState = ""            // "Nominal", "Fair", "Serious", "Critical" when no numeric sensor is readable
    var ramPercent = Double.nan, ramUsedGB = Double.nan, ramTotalGB = Double.nan
    var gpus: [GpuInfo] = []
    var gpuPercent = Double.nan, gpuTempC = Double.nan
    var diskPercent = Double.nan, netDownBps = Double.nan, netUpBps = Double.nan
}

enum Fourth: String, CaseIterable { case auto, gpu, disk }

struct Cell {
    var label: String, value: String, frac: Double
    var bar1: RGB, bar2: RGB, labelColor: RGB, valueColor: RGB, level: Int
    var unknown: Bool { value == dash }
}

private func labelTint(_ accent: RGB, _ p: Palette) -> RGB { p.dark ? accent.lighten(0.2) : accent.darken(0.22) }
private func barTone(_ c: RGB, _ p: Palette) -> RGB { p.dark ? c : c.darken(0.1) }
private func valueColor(_ lvl: Int, _ p: Palette) -> RGB { lvl == 2 ? p.crit : lvl == 1 ? p.warn : p.text }

func showsGpu(_ s: Snapshot, _ fourth: Fourth) -> Bool {
    switch fourth {
    case .gpu: return true
    case .disk: return false
    case .auto: return !s.gpus.isEmpty && s.gpuPercent.isFinite
    }
}

private func metric(_ label: String, _ v: Double, _ th: (Double, Double), _ c: (RGB, RGB), _ p: Palette) -> Cell {
    let lvl = level(v, th)
    return Cell(label: label, value: Fmt.pct(v), frac: v.isFinite ? v / 100 : .nan,
                bar1: barTone(lvl == 2 ? Colors.warm : c.0, p), bar2: barTone(lvl == 2 ? Colors.hot : lvl == 1 ? Colors.warm : c.1, p),
                labelColor: labelTint(c.0.mix(c.1, 0.4), p), valueColor: valueColor(lvl, p), level: lvl)
}

func buildCells(_ s: Snapshot, fahrenheit: Bool = false, fourth: Fourth = .auto, palette p: Palette = .darkTheme) -> [Cell] {
    var tc = s.cpuTempC, label = "TEMP"
    if !tc.isFinite && s.gpuTempC.isFinite { tc = s.gpuTempC; label = "GPU\u{00B0}" }
    let tcol = tempColor(tc), tl = level(tc, Thresholds.temp)
    var cells = [Cell(label: label, value: Fmt.temp(tc, fahrenheit), frac: tc.isFinite ? (tc - 30) / 70 : .nan,
                      bar1: barTone(tempColor(35), p), bar2: barTone(tcol, p), labelColor: labelTint(tcol, p),
                      valueColor: tl > 0 ? valueColor(tl, p) : (p.dark ? tcol.lighten(0.45) : tcol.darken(0.45)), level: tl)]
    if !tc.isFinite && !s.thermalState.isEmpty {
        // No readable sensor: show macOS's own thermal state instead of a dash.
        let st = ["Nominal": (0, "OK", 45.0), "Fair": (0, "FAIR", 70.0), "Serious": (1, "HOT", 85.0), "Critical": (2, "CRIT", 98.0)][s.thermalState]
        if let st {
            let col = tempColor(st.2)
            cells[0] = Cell(label: "TEMP", value: st.1, frac: (st.2 - 30) / 70, bar1: barTone(tempColor(35), p), bar2: barTone(col, p),
                            labelColor: labelTint(col, p), valueColor: st.0 > 0 ? valueColor(st.0, p) : (p.dark ? col.lighten(0.45) : col.darken(0.45)),
                            level: st.0)
        }
    }
    cells.append(metric("CPU", s.cpuPercent, Thresholds.cpu, Colors.cpu, p))
    cells.append(metric("RAM", s.ramPercent, Thresholds.ram, Colors.ram, p))
    cells.append(showsGpu(s, fourth) ? metric("GPU", s.gpuPercent, Thresholds.gpu, Colors.gpu, p)
                                     : metric("DISK", s.diskPercent, Thresholds.disk, Colors.disk, p))
    return cells
}

struct CardRow {
    var title: String, sub: String, value: String, frac: Double
    var a: RGB, b: RGB, dot: RGB? = nil, valueColor: RGB
}

/// Busy rows turn their bar amber (warm) or amber-to-red (critical), matching the pill.
private func heat(_ r: CardRow, _ lvl: Int) -> CardRow {
    var r = r
    if lvl == 2 { r.a = Colors.warm; r.b = Colors.hot } else if lvl == 1 { r.b = Colors.warm }
    return r
}

func cardRows(_ s: Snapshot, fahrenheit: Bool = false, palette p: Palette = .darkTheme) -> [CardRow] {
    var rows: [CardRow] = []
    let tc = tempColor(s.cpuTempC), tl = level(s.cpuTempC, Thresholds.temp)
    let tsub = s.cpuTempC.isFinite ? "CPU \u{00B7} \(s.cpuTempSource.isEmpty ? "sensor" : s.cpuTempSource)"
        : s.thermalState.isEmpty ? "No CPU temperature sensor found" : "Thermal state: \(s.thermalState)"
    rows.append(CardRow(title: "Temperature", sub: tsub, value: s.cpuTempC.isFinite ? Fmt.tempLong(s.cpuTempC, fahrenheit) : (s.thermalState.isEmpty ? dash : s.thermalState),
                        frac: (s.cpuTempC - 30) / 70, a: tempColor(35), b: tc, dot: tc,
                        valueColor: tl > 0 ? valueColor(tl, p) : (p.dark ? tc.lighten(0.35) : tc.darken(0.45))))
    rows.append(heat(CardRow(title: "CPU", sub: s.cpuName.isEmpty ? "Processor" : s.cpuName, value: Fmt.pct(s.cpuPercent), frac: s.cpuPercent / 100,
                        a: Colors.cpu.0, b: Colors.cpu.1, valueColor: valueColor(level(s.cpuPercent, Thresholds.cpu), p)), level(s.cpuPercent, Thresholds.cpu)))
    let msub = s.ramUsedGB.isFinite ? "\(Fmt.gb(s.ramUsedGB).replacingOccurrences(of: " GB", with: "")) of \(Fmt.gb(s.ramTotalGB)) in use" : "Usage unavailable"
    rows.append(heat(CardRow(title: "Memory", sub: msub, value: Fmt.pct(s.ramPercent), frac: s.ramPercent / 100,
                        a: Colors.ram.0, b: Colors.ram.1, valueColor: valueColor(level(s.ramPercent, Thresholds.ram), p)), level(s.ramPercent, Thresholds.ram)))
    for g in s.gpus {
        var parts: [String] = []
        if g.tempC.isFinite { parts.append(Fmt.tempLong(g.tempC, fahrenheit)) }
        if g.memUsedGB.isFinite { parts.append(Fmt.gb(g.memUsedGB) + " in use") }
        parts.append(g.name.isEmpty ? "GPU" : g.name)
        rows.append(heat(CardRow(title: "GPU", sub: parts.joined(separator: " \u{00B7} "), value: Fmt.pct(g.percent), frac: g.percent / 100,
                            a: Colors.gpu.0, b: Colors.gpu.1, valueColor: valueColor(level(g.percent, Thresholds.gpu), p)), level(g.percent, Thresholds.gpu)))
    }
    rows.append(heat(CardRow(title: "Disk", sub: "Busiest drive, active time", value: Fmt.pct(s.diskPercent), frac: s.diskPercent / 100,
                        a: Colors.disk.0, b: Colors.disk.1, valueColor: valueColor(level(s.diskPercent, Thresholds.disk), p)), level(s.diskPercent, Thresholds.disk)))
    rows.append(CardRow(title: "Network", sub: "\u{2191} \(Fmt.rate(s.netUpBps)) upload", value: "\u{2193} \(Fmt.rate(s.netDownBps))",
                        frac: s.netDownBps.isFinite ? log10(1 + s.netDownBps) / log10(1 + 125e6) : .nan,
                        a: Colors.net.0, b: Colors.net.1, valueColor: p.text))
    return rows
}

// MARK: - Pure sensor helpers (the Mach/IOKit readers in Sampler.swift feed these)

/// CPU busy percent between two (user, system, idle, nice) tick samples.
func cpuPercent(_ a: [UInt64]?, _ b: [UInt64]) -> Double {
    guard let a, a.count == 4, b.count == 4 else { return .nan }
    let d = zip(a, b).map { Double(counterDelta($0, $1)) }   // the tick counters are 32-bit and wrap
    let total = d.reduce(0, +)
    return total > 0 ? max(0, min(100, (total - d[2]) / total * 100)) : .nan
}

/// Activity Monitor's "Memory Used": app memory (internal - purgeable) + wired + compressed.
func memoryUsed(pageSize: UInt64, internalPages: UInt64, purgeable: UInt64, wired: UInt64, compressed: UInt64) -> UInt64 {
    (internalPages - min(purgeable, internalPages) + wired + compressed) * pageSize
}

/// Busy percent of the busiest drive from cumulative read+write service time (nanoseconds) per drive.
func diskBusy(_ prev: [String: UInt64]?, _ cur: [String: UInt64], ms: Double) -> Double {
    guard let prev, ms > 0 else { return .nan }
    var best = Double.nan
    for (k, v) in cur {
        guard let p = prev[k], v >= p else { continue }
        let pct = min(100, Double(v - p) / 1e6 / ms * 100)
        best = best.isNaN ? pct : max(best, pct)
    }
    return best
}

/// Delta of a 32-bit counter (if_data bytes, CPU ticks). A drop of more than half the range is a wrap; a
/// smaller drop is a counter that restarted (adapter re-plugged, driver reset), which counts from 0.
func counterDelta(_ prev: UInt64, _ cur: UInt64, wrap: UInt64 = 1 << 32) -> UInt64 {
    if cur >= prev { return cur - prev }
    return prev - cur > wrap / 2 ? cur + wrap - prev : cur
}

/// Bytes per interface name, as read from getifaddrs.
typealias NetCounters = [String: (rx: UInt64, tx: UInt64)]

/// Bytes moved between 2 samples, counted per interface so an interface that appears or goes away
/// (a USB or Thunderbolt adapter unplugged) adds nothing instead of looking like a counter wrap.
func netDelta(_ prev: NetCounters, _ cur: NetCounters) -> (rx: UInt64, tx: UInt64) {
    var rx: UInt64 = 0, tx: UInt64 = 0
    for (name, c) in cur {
        guard let p = prev[name] else { continue }
        rx += counterDelta(p.rx, c.rx); tx += counterDelta(p.tx, c.tx)
    }
    return (rx, tx)
}

/// Interfaces that carry real traffic: Ethernet/Wi-Fi (en*), not loopback, VPN tunnels, AWDL or bridges.
func countsInterface(_ name: String) -> Bool { name.hasPrefix("en") }

/// CPU temperature from Apple Silicon HID sensor names (degrees C). Prefers die sensors ("PMU tdie*"),
/// then the CPU cluster sensors ("pACC/eACC MTR Temp Sensor*"). Mean, not max: single sensors spike.
func pickAppleSiliconCpuTemp(_ sensors: [(name: String, c: Double)]) -> (c: Double, source: String)? {
    let ok = sensors.filter { $0.c > 5 && $0.c < 125 }
    func mean(_ s: [(name: String, c: Double)]) -> Double { s.map(\.c).reduce(0, +) / Double(s.count) }
    let die = ok.filter { $0.name.lowercased().contains("tdie") }
    if !die.isEmpty { return (mean(die), "\(die.count) die sensors") }
    let cluster = ok.filter { let n = $0.name.lowercased(); return (n.contains("pacc") || n.contains("eacc")) && n.contains("mtr") }
    if !cluster.isEmpty { return (mean(cluster), "\(cluster.count) CPU cluster sensors") }
    return nil
}

func pickAppleSiliconGpuTemp(_ sensors: [(name: String, c: Double)]) -> Double {
    let g = sensors.filter { $0.name.uppercased().contains("GPU") && $0.c > 5 && $0.c < 125 }
    return g.isEmpty ? .nan : g.map(\.c).reduce(0, +) / Double(g.count)
}

/// Decodes an SMC value of type "sp78" (signed 7.8 fixed point, big-endian) or "flt " (Float32, little-endian).
func smcDecode(type: String, bytes: [UInt8]) -> Double {
    switch type {
    case "sp78" where bytes.count >= 2:
        return Double(Int16(bitPattern: UInt16(bytes[0]) << 8 | UInt16(bytes[1]))) / 256
    case "flt " where bytes.count >= 4:
        return Double(Float(bitPattern: UInt32(bytes[0]) | UInt32(bytes[1]) << 8 | UInt32(bytes[2]) << 16 | UInt32(bytes[3]) << 24))
    default:
        return .nan
    }
}

func fourCC(_ s: String) -> UInt32 { s.utf8.prefix(4).reduce(0) { $0 << 8 | UInt32($1) } }

/// Celsius by default everywhere; Fahrenheit only when chosen in the menu.
let defaultFahrenheit = false

// MARK: - Settings (stored as JSON by App.swift)

struct Settings: Codable {
    var interval: Double = 2
    var fahrenheit = defaultFahrenheit
    var fourth = Fourth.auto.rawValue

    /// Out-of-range values fall back to their defaults.
    func validated() -> Settings {
        var s = self
        if ![1, 2, 5].contains(s.interval) { s.interval = 2 }
        if Fourth(rawValue: s.fourth) == nil { s.fourth = Fourth.auto.rawValue }
        return s
    }
}

extension Settings {
    /// A missing key (an older file, a hand edit) keeps its default instead of resetting every setting.
    init(from decoder: Decoder) throws {
        self.init()
        let c = try decoder.container(keyedBy: CodingKeys.self)
        interval = (try? c.decodeIfPresent(Double.self, forKey: .interval)) ?? interval
        fahrenheit = (try? c.decodeIfPresent(Bool.self, forKey: .fahrenheit)) ?? fahrenheit
        fourth = (try? c.decodeIfPresent(String.self, forKey: .fourth)) ?? fourth
    }
}

// MARK: - Self-test (runs on macOS with --self-test and on Linux through tests/main.swift)

func coreSelfTest() -> (passed: Int, failed: Int) {
    var passed = 0, failed = 0
    func check(_ name: String, _ ok: Bool) {
        if ok { passed += 1; print("PASS \(name)") } else { failed += 1; print("FAIL \(name)") }
    }
    check("fmt: 20 C -> 68 F", Fmt.temp(20, true) == "68\u{00B0}")
    check("fmt: NaN temp is a dash", Fmt.temp(.nan, false) == dash)
    // TC-135: the colour matches the number shown.
    check("TC-135 level: 74.6 % CPU shows 75% in amber", Fmt.pct(74.6) == "75%" && level(74.6, Thresholds.cpu) == 1)
    check("TC-135 level: 89.6 % RAM shows 90% in red", Fmt.pct(89.6) == "90%" && level(89.6, Thresholds.ram) == 2)
    // TC-137: a counter that restarts is not a 4 GB wrap; a real wrap still counts.
    check("TC-137 net: restarted counter adds only its new bytes", netDelta(["en0": (5_000_000, 0)], ["en0": (1_000, 0)]).rx == 1_000)
    check("TC-137 net: 32-bit wrap still counted", counterDelta(4_294_967_000, 200) == 496)
    // TC-139: a wrapped 32-bit tick counter is not a reset.
    check("TC-139 cpu: tick wrap gives 5 %", abs(cpuPercent([100_000, 50_000, 4_294_964_266, 0], [100_080, 50_080, 10, 0]) - 5) < 0.01)
    // TC-141: a settings file without a key keeps the other settings.
    let partial = try? JSONDecoder().decode(Settings.self, from: Data(#"{"interval":5,"fahrenheit":true}"#.utf8))
    check("TC-141 settings: missing key keeps the others", partial?.interval == 5 && partial?.fahrenheit == true && partial?.fourth == "auto")
    let bad = try? JSONDecoder().decode(Settings.self, from: Data(#"{"interval":7,"fourth":"cpu"}"#.utf8))
    check("TC-141 settings: invalid values fall back", bad?.validated().interval == 2 && bad?.validated().fourth == "auto")
    check("fmt: pct clamps", Fmt.pct(104) == "100%" && Fmt.pct(-3) == "0%")
    check("fmt: rate", Fmt.rate(1536) == "1.5 KB/s" && Fmt.rate(48e6) == "46 MB/s" && Fmt.rate(12) == "12 B/s")
    check("fmt: gb", Fmt.gb(7.94) == "7.9 GB" && Fmt.gb(31.8) == "32 GB")
    check("color: hex round trip", RGB(hex: "#38BDF8").hex == "#38BDF8")
    check("color: ramp ends", tempColor(20).hex == "#22D3EE" && tempColor(120).hex == "#F43F5E")
    check("color: ramp midpoint 42.5 C", tempColor(42.5).hex == RGB(hex: "22D3EE").mix(RGB(hex: "34D399"), 0.5).hex)
    check("level: thresholds", level(74, Thresholds.cpu) == 0 && level(75, Thresholds.cpu) == 1 && level(90, Thresholds.cpu) == 2 && level(.nan, Thresholds.cpu) == 0)

    check("cpu: 20% busy", abs(cpuPercent([100, 100, 800, 0], [120, 120, 960, 0]) - 20) < 0.01)
    check("cpu: no previous sample -> NaN", cpuPercent(nil, [1, 1, 1, 1]).isNaN)
    check("mem: Activity Monitor formula", memoryUsed(pageSize: 16384, internalPages: 100, purgeable: 10, wired: 20, compressed: 5) == 115 * 16384)
    check("mem: purgeable larger than internal does not underflow", memoryUsed(pageSize: 1, internalPages: 1, purgeable: 5, wired: 0, compressed: 0) == 0)
    check("disk: busiest drive 25% over 2 s", abs(diskBusy(["disk0": 0, "disk1": 0], ["disk0": 500_000_000, "disk1": 100], ms: 2000) - 25) < 0.01)
    check("disk: first sample -> NaN", diskBusy(nil, ["disk0": 1], ms: 2000).isNaN)
    check("net: 32-bit wrap", counterDelta(4_294_967_000, 200) == 496)
    let twoIfaces: NetCounters = ["en0": (rx: 1_000, tx: 500), "en5": (rx: 3_000_000_000, tx: 9_000)]
    check("net: interface unplugged -> no spike", netDelta(twoIfaces, ["en0": (rx: 1_400, tx: 600)]) == (400, 100))
    check("net: interface plugged in -> counted from its next sample", netDelta(["en0": (rx: 1_000, tx: 500)], twoIfaces) == (0, 0))
    check("net: en* only", countsInterface("en0") && !countsInterface("lo0") && !countsInterface("utun3") && !countsInterface("awdl0") && !countsInterface("bridge0"))

    let hid: [(name: String, c: Double)] = [("PMU tdie1", 50), ("PMU tdie2", 54), ("pACC MTR Temp Sensor2", 70), ("GPU MTR Temp Sensor1", 44), ("NAND CH0 temp", 33), ("PMU tcal", 0)]
    check("temp: Apple Silicon die sensors averaged", pickAppleSiliconCpuTemp(hid).map { $0.c == 52 && $0.source == "2 die sensors" } == true)
    check("temp: cluster sensors when no die sensors", pickAppleSiliconCpuTemp([("eACC MTR Temp Sensor0", 40), ("pACC MTR Temp Sensor3", 60)])?.c == 50)
    check("temp: no CPU sensors -> nil", pickAppleSiliconCpuTemp([("NAND CH0 temp", 33), ("Battery", 30)]) == nil)
    check("temp: Apple Silicon GPU sensors", pickAppleSiliconGpuTemp(hid) == 44)
    check("smc: sp78 61.5 C", smcDecode(type: "sp78", bytes: [0x3D, 0x80]) == 61.5)
    check("smc: flt 47.25 C", smcDecode(type: "flt ", bytes: Array(withUnsafeBytes(of: Float(47.25).bitPattern.littleEndian) { Array($0) })) == 47.25)
    check("smc: unknown type -> NaN", smcDecode(type: "ui8 ", bytes: [1]).isNaN)
    check("smc: fourCC", fourCC("TC0P") == 0x5443_3050)
    check("units: Celsius by default", !defaultFahrenheit)

    var hot = Snapshot()
    hot.cpuTempC = 93; hot.cpuPercent = 96; hot.ramPercent = 91; hot.gpuPercent = 97; hot.gpuTempC = 86
    hot.gpus = [GpuInfo(name: "Apple M3 Max", percent: 97, tempC: 86, memUsedGB: 7)]
    var cells = buildCells(hot)
    check("cells: TEMP CPU RAM GPU", cells.map(\.label) == ["TEMP", "CPU", "RAM", "GPU"])
    check("cells: hot levels all critical", cells.allSatisfy { $0.level == 2 })
    var idle = Snapshot(); idle.cpuPercent = 3
    cells = buildCells(idle)
    check("cells: no GPU -> DISK, unknown values are dashes", cells[3].label == "DISK" && cells[0].unknown && cells[1].value == "3%")
    var gonly = Snapshot(); gonly.gpuTempC = 50
    check("cells: GPU temperature fallback is labelled", buildCells(gonly)[0].label == "GPU\u{00B0}")
    var therm = Snapshot(); therm.thermalState = "Serious"
    cells = buildCells(therm)
    check("cells: thermal state when no sensor", cells[0].value == "HOT" && cells[0].level == 1)
    check("cells: forced disk", buildCells(hot, fourth: .disk)[3].label == "DISK")
    check("rows: temp, cpu, memory, 1 GPU, disk, network", cardRows(hot).map(\.title) == ["Temperature", "CPU", "Memory", "GPU", "Disk", "Network"])
    check("rows: critical GPU bar is amber to red", cardRows(hot)[3].a == Colors.warm && cardRows(hot)[3].b == Colors.hot)
    check("rows: thermal state shown as value", cardRows(therm)[0].value == "Serious")
    return (passed, failed)
}
