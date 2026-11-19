// HW Widget for macOS - reads the hardware without root: Mach host statistics for CPU and memory,
// IOKit for GPU load and disk activity, getifaddrs for network, and for temperature the HID thermal
// sensors (Apple Silicon) or the SMC (Intel), with ProcessInfo.thermalState as the last fallback.
import Foundation
import IOKit

final class Sampler {
    private var prevTicks: [UInt64]?
    private var prevDisk: [String: UInt64]?
    private var prevNet: (rx: UInt64, tx: UInt64)?
    private var prevAt: Date?
    private let cpuName: String
    private let hid = HidThermal()
    private let smc = Smc()

    init() {
        var size = 0
        sysctlbyname("machdep.cpu.brand_string", nil, &size, nil, 0)
        var buf = [CChar](repeating: 0, count: max(1, size))
        sysctlbyname("machdep.cpu.brand_string", &buf, &size, nil, 0)
        cpuName = String(cString: buf)
    }

    func sample() -> Snapshot {
        var s = Snapshot()
        let now = Date()
        let ms = prevAt.map { now.timeIntervalSince($0) * 1000 } ?? 0
        s.cpuName = cpuName

        if let t = cpuTicks() { s.cpuPercent = cpuPercent(prevTicks, t); prevTicks = t }

        if let m = memory() {
            s.ramTotalGB = Double(m.total) / 1_073_741_824
            s.ramUsedGB = Double(m.used) / 1_073_741_824
            s.ramPercent = m.total > 0 ? Double(m.used) / Double(m.total) * 100 : .nan
        }

        let disk = diskTimes()
        s.diskPercent = diskBusy(prevDisk, disk, ms: ms)
        prevDisk = disk

        let net = netBytes()
        if let p = prevNet, ms > 0 {
            s.netDownBps = Double(counterDelta(p.rx, net.rx)) / ms * 1000
            s.netUpBps = Double(counterDelta(p.tx, net.tx)) / ms * 1000
        }
        prevNet = net
        prevAt = now

        let sensors = hid.read()
        if let t = pickAppleSiliconCpuTemp(sensors) {
            s.cpuTempC = t.c; s.cpuTempSource = t.source
        } else if let t = smc.cpuTemp() {
            s.cpuTempC = t.c; s.cpuTempSource = "SMC \(t.key)"
        } else {
            s.thermalState = ["Nominal", "Fair", "Serious", "Critical"][min(3, ProcessInfo.processInfo.thermalState.rawValue)]
        }

        s.gpus = gpus()
        let gt = pickAppleSiliconGpuTemp(sensors)
        let gpuTemp = gt.isFinite ? gt : (smc.gpuTemp() ?? .nan)
        if gpuTemp.isFinite, !s.gpus.isEmpty { s.gpus[0].tempC = gpuTemp }
        if let busiest = s.gpus.filter({ $0.percent.isFinite }).max(by: { $0.percent < $1.percent }) {
            s.gpuPercent = busiest.percent
        }
        s.gpuTempC = gpuTemp
        return s
    }

    // MARK: CPU and memory (Mach)

    private func cpuTicks() -> [UInt64]? {
        var info = host_cpu_load_info()
        var count = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info>.size / MemoryLayout<integer_t>.size)
        let kr = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &count) }
        }
        guard kr == KERN_SUCCESS else { return nil }
        let t = info.cpu_ticks   // (user, system, idle, nice)
        return [UInt64(t.0), UInt64(t.1), UInt64(t.2), UInt64(t.3)]
    }

    private func memory() -> (used: UInt64, total: UInt64)? {
        var vm = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.size / MemoryLayout<integer_t>.size)
        let kr = withUnsafeMutablePointer(to: &vm) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count) }
        }
        guard kr == KERN_SUCCESS else { return nil }
        var page: vm_size_t = 0
        host_page_size(mach_host_self(), &page)
        let used = memoryUsed(pageSize: UInt64(page), internalPages: UInt64(vm.internal_page_count), purgeable: UInt64(vm.purgeable_count),
                              wired: UInt64(vm.wire_count), compressed: UInt64(vm.compressor_page_count))
        return (min(used, ProcessInfo.processInfo.physicalMemory), ProcessInfo.processInfo.physicalMemory)
    }

    // MARK: GPU and disk (IOKit registry)

    private func forEachService(_ cls: String, _ body: (io_registry_entry_t) -> Void) {
        var it: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching(cls), &it) == KERN_SUCCESS else { return }
        defer { IOObjectRelease(it) }
        var e = IOIteratorNext(it)
        while e != 0 {
            body(e)
            IOObjectRelease(e)
            e = IOIteratorNext(it)
        }
    }

    private func property(_ e: io_registry_entry_t, _ key: String) -> Any? {
        IORegistryEntryCreateCFProperty(e, key as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue()
    }

    private func gpus() -> [GpuInfo] {
        var out: [GpuInfo] = []
        forEachService("IOAccelerator") { e in
            guard let stats = property(e, "PerformanceStatistics") as? [String: Any] else { return }
            var g = GpuInfo()
            if let u = (stats["Device Utilization %"] ?? stats["GPU Activity(%)"]) as? NSNumber { g.percent = min(100, u.doubleValue) }
            if let m = (stats["In use system memory"] ?? stats["vramUsedBytes"]) as? NSNumber { g.memUsedGB = m.doubleValue / 1_073_741_824 }
            g.name = gpuName(e)
            out.append(g)
        }
        return out
    }

    private func gpuName(_ e: io_registry_entry_t) -> String {
        if let m = property(e, "model") as? String { return m }
        var parent: io_registry_entry_t = 0
        guard IORegistryEntryGetParentEntry(e, kIOServicePlane, &parent) == KERN_SUCCESS else { return "GPU" }
        defer { IOObjectRelease(parent) }
        if let d = property(parent, "model") as? Data {
            return String(decoding: d.prefix { $0 != 0 }, as: UTF8.self)
        }
        if let m = property(parent, "model") as? String { return m }
        return "Apple GPU"
    }

    private func diskTimes() -> [String: UInt64] {
        var out: [String: UInt64] = [:]
        var i = 0
        forEachService("IOBlockStorageDriver") { e in
            guard let st = property(e, "Statistics") as? [String: Any] else { return }
            let r = (st["Total Time (Read)"] as? NSNumber)?.uint64Value ?? 0
            let w = (st["Total Time (Write)"] as? NSNumber)?.uint64Value ?? 0
            out["disk\(i)"] = r + w
            i += 1
        }
        return out
    }

    // MARK: Network

    private func netBytes() -> (rx: UInt64, tx: UInt64) {
        var rx: UInt64 = 0, tx: UInt64 = 0
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0 else { return (0, 0) }
        defer { freeifaddrs(head) }
        var p = head
        while let a = p {
            defer { p = a.pointee.ifa_next }
            guard let sa = a.pointee.ifa_addr, sa.pointee.sa_family == UInt8(AF_LINK), let data = a.pointee.ifa_data,
                  countsInterface(String(cString: a.pointee.ifa_name)) else { continue }
            let d = data.assumingMemoryBound(to: if_data.self).pointee
            rx += UInt64(d.ifi_ibytes); tx += UInt64(d.ifi_obytes)
        }
        return (rx, tx)
    }
}

// MARK: - Apple Silicon thermal sensors (IOHIDEventSystem, no root). Private but stable since macOS 11;
// resolved with dlsym so a missing symbol only means "no reading", never a crash at launch.

final class HidThermal {
    private typealias CreateFn = @convention(c) (CFAllocator?) -> OpaquePointer?
    private typealias MatchFn = @convention(c) (OpaquePointer, CFDictionary) -> Int32
    private typealias ServicesFn = @convention(c) (OpaquePointer) -> Unmanaged<CFArray>?
    private typealias PropFn = @convention(c) (OpaquePointer, CFString) -> Unmanaged<CFTypeRef>?
    private typealias EventFn = @convention(c) (OpaquePointer, Int64, Int32, Int64) -> OpaquePointer?
    private typealias FloatFn = @convention(c) (OpaquePointer, Int32) -> Double

    private var client: OpaquePointer?
    private var services: ServicesFn?, prop: PropFn?, event: EventFn?, float: FloatFn?

    init() {
        #if arch(arm64)
        func sym<T>(_ n: String) -> T? { dlsym(UnsafeMutableRawPointer(bitPattern: -2), n).map { unsafeBitCast($0, to: T.self) } }  // RTLD_DEFAULT
        guard let create: CreateFn = sym("IOHIDEventSystemClientCreate"), let match: MatchFn = sym("IOHIDEventSystemClientSetMatching") else { return }
        services = sym("IOHIDEventSystemClientCopyServices")
        prop = sym("IOHIDServiceClientCopyProperty")
        event = sym("IOHIDServiceClientCopyEvent")
        float = sym("IOHIDEventGetFloatValue")
        client = create(kCFAllocatorDefault)
        if let client { _ = match(client, ["PrimaryUsagePage": 0xFF00, "PrimaryUsage": 5] as CFDictionary) }
        #endif
    }

    func read() -> [(name: String, c: Double)] {
        guard let client, let services, let prop, let event, let float,
              let arr = services(client)?.takeRetainedValue() else { return [] }
        let kTemperature: Int64 = 15
        var out: [(name: String, c: Double)] = []
        for i in 0..<CFArrayGetCount(arr) {
            guard let raw = CFArrayGetValueAtIndex(arr, i) else { continue }
            let svc = OpaquePointer(raw)
            let name = (prop(svc, "Product" as CFString)?.takeRetainedValue() as? String) ?? ""
            guard let ev = event(svc, kTemperature, 0, 0) else { continue }
            let c = float(ev, Int32(kTemperature << 16))
            Unmanaged<AnyObject>.fromOpaque(UnsafeRawPointer(ev)).release()
            out.append((name, c))
        }
        return out
    }
}

// MARK: - SMC (Intel Macs). The 80-byte SMCKeyData_t is laid out by hand: key@0, keyInfo.dataSize@28,
// keyInfo.dataType@32, result@40, data8@42, bytes@48.

final class Smc {
    private var conn: io_connect_t = 0

    init() {
        #if arch(x86_64)
        let svc = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSMC"))
        guard svc != 0 else { return }
        IOServiceOpen(svc, mach_task_self_, 0, &conn)
        IOObjectRelease(svc)
        #endif
    }

    deinit { if conn != 0 { IOServiceClose(conn) } }

    private func call(_ input: [UInt8]) -> [UInt8]? {
        var inp = input, out = [UInt8](repeating: 0, count: 80), size = 80
        let kr = IOConnectCallStructMethod(conn, 2, &inp, 80, &out, &size)
        return kr == KERN_SUCCESS && out[40] == 0 ? out : nil
    }

    private func read(_ key: String) -> Double? {
        guard conn != 0 else { return nil }
        var req = [UInt8](repeating: 0, count: 80)
        withUnsafeBytes(of: fourCC(key)) { b in for i in 0..<4 { req[i] = b[i] } }   // host order, as the kernel expects
        req[42] = 9                                                                    // kSMCGetKeyInfo
        guard let info = call(req) else { return nil }
        let size = info[28..<32].withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }
        let type = info[32..<36].withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }
        let typeStr = String(decoding: withUnsafeBytes(of: type.bigEndian) { Array($0) }, as: UTF8.self)
        for i in 0..<4 { req[28 + i] = info[28 + i] }
        req[42] = 5                                                                    // kSMCReadKey
        guard size > 0, size <= 32, let val = call(req) else { return nil }
        let v = smcDecode(type: typeStr, bytes: Array(val[48..<(48 + Int(size))]))
        return v.isFinite && v > 5 && v < 125 ? v : nil
    }

    func cpuTemp() -> (c: Double, key: String)? {
        for k in ["TC0P", "TC0D", "TC0E", "TC0F", "TCXC"] { if let v = read(k) { return (v, k) } }
        return nil
    }

    func gpuTemp() -> Double? {
        for k in ["TG0P", "TG0D"] { if let v = read(k) { return v } }
        return nil
    }
}
