// HW Widget for Windows - hardware sampler.
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x), no SDK, no admin rights.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace HwWidget
{
    public sealed class GpuInfo
    {
        public string Name = "";
        public string Luid = "";
        public double Percent = double.NaN;
        public double TempC = double.NaN;
        public double MemUsedGB = double.NaN;
        public bool Integrated;
    }

    public sealed class HwSnapshot
    {
        public DateTime TakenUtc = DateTime.UtcNow;
        public string CpuName = "";
        public double CpuPercent = double.NaN;
        public double CpuTempC = double.NaN;
        public string CpuTempSource = "";
        public string CpuTempHint = "";            // why there is no CPU temperature, shown in the card
        public double RamPercent = double.NaN;
        public double RamUsedGB = double.NaN;
        public double RamTotalGB = double.NaN;
        public double GpuPercent = double.NaN;
        public double GpuTempC = double.NaN;
        public string GpuName = "";
        public GpuInfo[] Gpus = new GpuInfo[0];
        public double DiskPercent = double.NaN;
        public double NetDownBps = double.NaN;
        public double NetUpBps = double.NaN;
    }

    // ------------------------------------------------------------------ PDH (performance counters)
    internal static class Pdh
    {
        public const uint FMT_DOUBLE = 0x200, FMT_NOCAP100 = 0x8000;
        public const int MORE_DATA = unchecked((int)0x800007D2);

        [StructLayout(LayoutKind.Explicit)]
        public struct FmtValue { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Value; }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhOpenQueryW(string source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] public static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] public static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FmtValue value);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")] public static extern int PdhCloseQuery(IntPtr query);

        public static double ReadOne(IntPtr counter, uint format)
        {
            if (counter == IntPtr.Zero) return double.NaN;
            FmtValue v;
            if (PdhGetFormattedCounterValue(counter, format, IntPtr.Zero, out v) != 0) return double.NaN;
            if (v.CStatus != 0 && v.CStatus != 1) return double.NaN;
            return v.Value;
        }

        // Every instance of a wildcard counter. PDH_FMT_COUNTERVALUE_ITEM_W is 24 bytes on x86 and x64:
        // name pointer (padded to 8), CStatus at 8, the double at 16.
        public static Dictionary<string, double> ReadArray(IntPtr counter, uint format)
        {
            Dictionary<string, double> res;
            TryReadArray(counter, format, out res);
            return res;
        }

        // False when PDH gave no data, so a caller can show "unknown" instead of 0. The instance list can grow
        // between the size query and the read (a new GPU engine instance), which returns MORE_DATA again:
        // retry with the new size instead of giving up.
        public static bool TryReadArray(IntPtr counter, uint format, out Dictionary<string, double> res)
        {
            res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (counter == IntPtr.Zero) return false;
            uint size = 0, count;
            int st = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, IntPtr.Zero);
            for (int attempt = 0; attempt < 3 && st == MORE_DATA && size > 0; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    st = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, buf);
                    if (st != 0) continue;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = new IntPtr(buf.ToInt64() + (long)i * 24);
                        uint cs = (uint)Marshal.ReadInt32(item, 8);
                        if (cs != 0 && cs != 1) continue;
                        double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                        string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                        double prev;
                        res[name] = res.TryGetValue(name, out prev) ? prev + v : v;
                    }
                    return true;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return false;
        }
    }

    // ------------------------------------------------------------------ D3DKMT (GPU names and temperature, no admin)
    internal static class Kmt
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct ADAPTERINFO { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }
        [StructLayout(LayoutKind.Sequential)]
        public struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
        [StructLayout(LayoutKind.Sequential)]
        public struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
        [StructLayout(LayoutKind.Sequential)]
        public struct CLOSEADAPTER { public uint hAdapter; }

        public const int TYPE_REGISTRYINFO = 8, TYPE_ADAPTERTYPE = 15, TYPE_PERFDATA = 62;

        [DllImport("gdi32.dll")] public static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 p);
        [DllImport("gdi32.dll")] public static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO p);
        [DllImport("gdi32.dll")] public static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER p);

        public static bool Query(uint h, int type, IntPtr buf, int size)
        {
            QUERYADAPTERINFO q = new QUERYADAPTERINFO();
            q.hAdapter = h; q.Type = type; q.pPrivateDriverData = buf; q.PrivateDriverDataSize = (uint)size;
            return D3DKMTQueryAdapterInfo(ref q) == 0;
        }
    }

    // ------------------------------------------------------------------ NVML (NVIDIA temperature fallback)
    internal sealed class Nvml
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr mod, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HandleFn(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TempFn(IntPtr device, int sensor, out uint temp);
        TempFn temp; IntPtr dev;

        public static Nvml TryOpen()
        {
            try
            {
                string[] paths = {
                    Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\nvml.dll"),
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\NVIDIA Corporation\NVSMI\nvml.dll") };
                foreach (string p in paths)
                {
                    if (!System.IO.File.Exists(p)) continue;
                    IntPtr m = LoadLibraryW(p);
                    if (m == IntPtr.Zero) continue;
                    IntPtr fi = GetProcAddress(m, "nvmlInit_v2"), fh = GetProcAddress(m, "nvmlDeviceGetHandleByIndex_v2"), ft = GetProcAddress(m, "nvmlDeviceGetTemperature");
                    if (fi == IntPtr.Zero || fh == IntPtr.Zero || ft == IntPtr.Zero) continue;
                    InitFn init = (InitFn)Marshal.GetDelegateForFunctionPointer(fi, typeof(InitFn));
                    if (init() != 0) continue;
                    HandleFn h = (HandleFn)Marshal.GetDelegateForFunctionPointer(fh, typeof(HandleFn));
                    IntPtr d;
                    if (h(0, out d) != 0) continue;
                    Nvml n = new Nvml();
                    n.temp = (TempFn)Marshal.GetDelegateForFunctionPointer(ft, typeof(TempFn));
                    n.dev = d;
                    return n;
                }
            }
            catch { }
            return null;
        }

        public double TempC()
        {
            uint t;
            try { if (temp(dev, 0, out t) == 0 && t > 0 && t < 130) return t; } catch { }
            return double.NaN;
        }
    }

    // ------------------------------------------------------------------ thermal-zone choice (pure, unit-tested)
    public static class ZonePicker
    {
        static readonly string[] Bad = { "GFX", "GPU", "PCH", "SK", "BAT", "CHG", "CHR", "LOC", "EXT", "AMB", "WIF", "SSD", "MEM", "DIM", "FAN", "VR", "NVM", "DCK", "CHAR" };
        static readonly string[] Cpuish = { "CPU", "PROC", "PKG", "CORE" };

        public static string Short(string zone)
        {
            string z = (zone ?? "").Trim().ToUpperInvariant();
            int dot = z.LastIndexOf('.');
            return dot >= 0 ? z.Substring(dot + 1) : z;
        }

        static bool HasAny(string s, string[] parts) { foreach (string p in parts) if (s.Contains(p)) return true; return false; }

        // Readings in deg C; dead = zones known to be flat-lined. Returns the chosen zone name or null.
        public static string Pick(IDictionary<string, double> tempsC, ICollection<string> dead)
        {
            string best = null; double bestT = double.MinValue; bool bestCpu = false;
            foreach (KeyValuePair<string, double> kv in tempsC)
            {
                double t = kv.Value;
                if (double.IsNaN(t) || t < 10 || t > 115) continue;
                if (Math.Abs(t - 27.85) < 0.06) continue;      // classic ACPI placeholder (3010 dK / 300.95 K)
                if (dead != null && dead.Contains(kv.Key)) continue;
                string s = Short(kv.Key);
                bool cpu = HasAny(s, Cpuish);
                if (!cpu && HasAny(s, Bad)) continue;
                if (best == null || (cpu && !bestCpu) || (cpu == bestCpu && t > bestT)) { best = kv.Key; bestT = t; bestCpu = cpu; }
            }
            return best;
        }
    }

    // ------------------------------------------------------------------ the sampler
    public sealed class HwSampler : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        sealed class Adapter { public uint Handle; public string Luid; public string Name; public bool Integrated; public bool Software; }

        IntPtr query, cCpu, cGpu, cGpuMem, cZone, cDisk, cRx, cTx;
        readonly string cpuName;
        List<Adapter> adapters = new List<Adapter>();
        DateTime adaptersAt = DateTime.MinValue;
        Nvml nvml; bool nvmlTried;
        readonly Dictionary<string, double[]> zoneHist = new Dictionary<string, double[]>(); // min, max, firstSeenTicks
        readonly HashSet<string> deadZones = new HashSet<string>();
        object lhm; DateTime lhmCheckAt = DateTime.MinValue, lhmReadAt = DateTime.MinValue; double lhmTemp = double.NaN; string lhmName = "";
        bool disposed;

        public HwSampler()
        {
            try
            {
                if (Pdh.PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) query = IntPtr.Zero;
                if (query != IntPtr.Zero)
                {
                    cCpu = Add(@"\Processor Information(_Total)\% Processor Utility");
                    if (cCpu == IntPtr.Zero) cCpu = Add(@"\Processor(_Total)\% Processor Time");
                    cGpu = Add(@"\GPU Engine(*)\Utilization Percentage");
                    cGpuMem = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
                    cZone = Add(@"\Thermal Zone Information(*)\High Precision Temperature");
                    cDisk = Add(@"\PhysicalDisk(*)\% Idle Time");
                    cRx = Add(@"\Network Interface(*)\Bytes Received/sec");
                    cTx = Add(@"\Network Interface(*)\Bytes Sent/sec");
                    Pdh.PdhCollectQueryData(query);
                }
            }
            catch { query = IntPtr.Zero; }
            cpuName = ReadCpuName();
        }

        IntPtr Add(string path)
        {
            IntPtr c;
            try { return Pdh.PdhAddEnglishCounterW(query, path, IntPtr.Zero, out c) == 0 ? c : IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }

        static string ReadCpuName()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string s = k == null ? null : k.GetValue("ProcessorNameString") as string;
                    if (s != null) return System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");
                }
            }
            catch { }
            return "CPU";
        }

        public HwSnapshot Sample()
        {
            HwSnapshot s = new HwSnapshot();
            s.CpuName = cpuName;
            if (disposed) return s;
            try { if (query != IntPtr.Zero) Pdh.PdhCollectQueryData(query); } catch { }

            try
            {
                double c = Pdh.ReadOne(cCpu, Pdh.FMT_DOUBLE | Pdh.FMT_NOCAP100);
                if (!double.IsNaN(c)) s.CpuPercent = Math.Max(0, Math.Min(100, c));
            }
            catch { }

            try
            {
                MEMORYSTATUSEX m = new MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0)
                {
                    double gb = 1024.0 * 1024 * 1024;
                    s.RamTotalGB = m.ullTotalPhys / gb;
                    s.RamUsedGB = (m.ullTotalPhys - m.ullAvailPhys) / gb;
                    s.RamPercent = 100.0 * (m.ullTotalPhys - m.ullAvailPhys) / m.ullTotalPhys;
                }
            }
            catch { }

            try { SampleGpu(s); } catch { }
            try { SampleCpuTemp(s); } catch { }

            try
            {
                double busiest = double.NaN;
                foreach (KeyValuePair<string, double> kv in Pdh.ReadArray(cDisk, Pdh.FMT_DOUBLE | Pdh.FMT_NOCAP100))
                {
                    if (kv.Key == "_Total") continue;
                    double busy = Math.Max(0, Math.Min(100, 100 - kv.Value));
                    if (double.IsNaN(busiest) || busy > busiest) busiest = busy;
                }
                s.DiskPercent = busiest;
            }
            catch { }

            try
            {
                s.NetDownBps = SumNet(Pdh.ReadArray(cRx, Pdh.FMT_DOUBLE));
                s.NetUpBps = SumNet(Pdh.ReadArray(cTx, Pdh.FMT_DOUBLE));
            }
            catch { }
            return s;
        }

        static double SumNet(Dictionary<string, double> d)
        {
            if (d.Count == 0) return double.NaN;
            double sum = 0;
            foreach (KeyValuePair<string, double> kv in d)
            {
                string n = kv.Key.ToLowerInvariant();
                if (n.Contains("vethernet") || n.Contains("hyper-v") || n.Contains("loopback") || n.Contains("isatap") ||
                    n.Contains("teredo") || n.Contains("miniport") || n.Contains("virtual") || n.Contains("vpn") || n.Contains("wsl")) continue;
                sum += kv.Value;
            }
            return sum;
        }

        // ------------------------------------------------ GPU: Task Manager method over GPU Engine counters
        void RefreshAdapters()
        {
            CloseAdapters();
            adaptersAt = DateTime.UtcNow;
            Kmt.ENUMADAPTERS2 e = new Kmt.ENUMADAPTERS2();
            if (Kmt.D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return;
            int itemSize = Marshal.SizeOf(typeof(Kmt.ADAPTERINFO));
            IntPtr arr = Marshal.AllocHGlobal(itemSize * (int)e.NumAdapters);
            IntPtr info = Marshal.AllocHGlobal(4096);
            try
            {
                e.pAdapters = arr;
                if (Kmt.D3DKMTEnumAdapters2(ref e) != 0) return;
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    Kmt.ADAPTERINFO a = (Kmt.ADAPTERINFO)Marshal.PtrToStructure(new IntPtr(arr.ToInt64() + (long)i * itemSize), typeof(Kmt.ADAPTERINFO));
                    Adapter ad = new Adapter();
                    ad.Handle = a.hAdapter;
                    ad.Luid = string.Format(CultureInfo.InvariantCulture, "0x{0:X8}_0x{1:X8}", a.LuidHigh, a.LuidLow);
                    ad.Name = "GPU";
                    for (int b = 0; b < 4096; b += 4) Marshal.WriteInt32(info, b, 0);
                    if (Kmt.Query(a.hAdapter, Kmt.TYPE_REGISTRYINFO, info, 2080))
                    {
                        string n = Marshal.PtrToStringUni(info);
                        if (!string.IsNullOrEmpty(n)) ad.Name = n.Trim();
                    }
                    Marshal.WriteInt32(info, 0, 0);
                    if (Kmt.Query(a.hAdapter, Kmt.TYPE_ADAPTERTYPE, info, 4))
                    {
                        int bits = Marshal.ReadInt32(info);
                        ad.Software = (bits & 0x4) != 0;
                        ad.Integrated = (bits & 0x20) != 0;
                    }
                    string ln = ad.Name.ToLowerInvariant();
                    if (ln.Contains("microsoft basic") || ln.Contains("remote display")) ad.Software = true;
                    if (!ad.Integrated && ((ln.Contains("intel") && !ln.Contains("arc")) || ln == "amd radeon(tm) graphics")) ad.Integrated = true;
                    adapters.Add(ad);
                }
            }
            finally { Marshal.FreeHGlobal(arr); Marshal.FreeHGlobal(info); }
        }

        void CloseAdapters()
        {
            foreach (Adapter a in adapters)
            {
                Kmt.CLOSEADAPTER c = new Kmt.CLOSEADAPTER(); c.hAdapter = a.Handle;
                try { Kmt.D3DKMTCloseAdapter(ref c); } catch { }
            }
            adapters.Clear();
        }

        static string LuidOf(string instance)
        {
            int i = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int j = instance.IndexOf("_phys", i, StringComparison.OrdinalIgnoreCase);
            return j < 0 ? null : instance.Substring(i + 5, j - i - 5).ToUpperInvariant().Replace("0X", "0x");
        }

        void SampleGpu(HwSnapshot s)
        {
            if ((DateTime.UtcNow - adaptersAt).TotalMinutes > 5) RefreshAdapters();
            // Sum each engine over all processes, then take the busiest engine per adapter (what Task Manager shows).
            Dictionary<string, double> engines = new Dictionary<string, double>();
            Dictionary<string, double> gpuRaw;
            bool gpuRead = Pdh.TryReadArray(cGpu, Pdh.FMT_DOUBLE | Pdh.FMT_NOCAP100, out gpuRaw);
            foreach (KeyValuePair<string, double> kv in gpuRaw)
            {
                string luid = LuidOf(kv.Key);
                int e = kv.Key.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
                if (luid == null || e < 0) continue;
                int t = kv.Key.IndexOf("_engtype", e, StringComparison.OrdinalIgnoreCase);
                string key = luid + "|" + (t < 0 ? kv.Key.Substring(e) : kv.Key.Substring(e, t - e));
                double prev;
                engines[key] = engines.TryGetValue(key, out prev) ? prev + kv.Value : kv.Value;
            }
            Dictionary<string, double> perAdapter = new Dictionary<string, double>();
            foreach (KeyValuePair<string, double> kv in engines)
            {
                string luid = kv.Key.Substring(0, kv.Key.IndexOf('|'));
                double prev;
                if (!perAdapter.TryGetValue(luid, out prev) || kv.Value > prev) perAdapter[luid] = kv.Value;
            }
            Dictionary<string, double> mem = new Dictionary<string, double>();
            foreach (KeyValuePair<string, double> kv in Pdh.ReadArray(cGpuMem, Pdh.FMT_DOUBLE))
            {
                string luid = LuidOf(kv.Key + "_phys");
                if (luid != null) { double prev; mem[luid] = (mem.TryGetValue(luid, out prev) ? prev : 0) + kv.Value; }
            }

            List<GpuInfo> list = new List<GpuInfo>();
            IntPtr perf = Marshal.AllocHGlobal(64);
            try
            {
                foreach (Adapter a in adapters)
                {
                    if (a.Software) continue;
                    bool dup = false;
                    foreach (GpuInfo g0 in list) if (g0.Luid == a.Luid) dup = true;
                    if (dup) continue;
                    GpuInfo g = new GpuInfo();
                    g.Name = a.Name; g.Luid = a.Luid; g.Integrated = a.Integrated;
                    double p;
                    if (perAdapter.TryGetValue(a.Luid, out p)) g.Percent = Math.Max(0, Math.Min(100, p));
                    else if (gpuRead) g.Percent = 0;   // read fine and this adapter is idle; a failed read stays unknown
                    double m;
                    if (mem.TryGetValue(a.Luid, out m)) g.MemUsedGB = m / (1024.0 * 1024 * 1024);
                    for (int b = 0; b < 64; b += 4) Marshal.WriteInt32(perf, b, 0);
                    if (Kmt.Query(a.Handle, Kmt.TYPE_PERFDATA, perf, 64))
                    {
                        int deci = Marshal.ReadInt32(perf, 56);       // D3DKMT_ADAPTER_PERFDATA.Temperature, deci-deg C
                        if (deci > 50 && deci < 1300) g.TempC = deci / 10.0;
                    }
                    string un = g.Name.ToUpperInvariant();
                    if (double.IsNaN(g.TempC) && (un.Contains("NVIDIA") || un.Contains("QUADRO") || un.Contains("GEFORCE") || un.Contains("TESLA") || un.Contains("RTX")))
                    {
                        if (!nvmlTried) { nvmlTried = true; nvml = Nvml.TryOpen(); }
                        if (nvml != null) g.TempC = nvml.TempC();
                    }
                    list.Add(g);
                }
            }
            finally { Marshal.FreeHGlobal(perf); }

            s.Gpus = list.ToArray();
            GpuInfo busiest = null;
            foreach (GpuInfo g in list)
            {
                if (double.IsNaN(g.Percent)) continue;
                if (busiest == null || g.Percent > busiest.Percent + 0.5 || (Math.Abs(g.Percent - busiest.Percent) <= 0.5 && busiest.Integrated && !g.Integrated)) busiest = g;
            }
            if (busiest != null) { s.GpuPercent = busiest.Percent; s.GpuName = busiest.Name; }
            foreach (GpuInfo g in list)
                if (!double.IsNaN(g.TempC) && (double.IsNaN(s.GpuTempC) || g.TempC > s.GpuTempC)) s.GpuTempC = g.TempC;
        }

        // ------------------------------------------------ CPU temperature: LibreHardwareMonitor, the opt-in helper, else ACPI zones
        void SampleCpuTemp(HwSnapshot s)
        {
            if (TryLhm(s)) return;
            string hsrc;
            double ht = HelperFile.Read(out hsrc);
            if (!double.IsNaN(ht)) { s.CpuTempC = ht; s.CpuTempSource = hsrc; return; }
            Dictionary<string, double> zones = new Dictionary<string, double>();
            foreach (KeyValuePair<string, double> kv in Pdh.ReadArray(cZone, Pdh.FMT_DOUBLE))
            {
                double c = kv.Value / 10.0 - 273.15;   // tenths of Kelvin
                zones[kv.Key] = c;
                double[] h;
                long now = DateTime.UtcNow.Ticks;
                if (!zoneHist.TryGetValue(kv.Key, out h)) { h = new double[] { c, c, now }; zoneHist[kv.Key] = h; }
                h[0] = Math.Min(h[0], c); h[1] = Math.Max(h[1], c);
                // A zone that has not moved by 0.1 deg in 3 minutes is a dead placeholder, not a CPU sensor.
                if (h[1] - h[0] < 0.1 && TimeSpan.FromTicks(now - (long)h[2]).TotalMinutes >= 3) deadZones.Add(kv.Key);
                else if (h[1] - h[0] >= 0.1) deadZones.Remove(kv.Key);
            }
            string pick = ZonePicker.Pick(zones, deadZones);
            if (pick != null)
            {
                s.CpuTempC = Math.Round(zones[pick], 1);
                s.CpuTempSource = "ACPI thermal zone " + ZonePicker.Short(pick);
            }
            else s.CpuTempHint = HelperSetup.Installed ? "Temperature helper installed, waiting for a reading" : "Right-click and choose Enable CPU temperature";
        }

        bool TryLhm(HwSnapshot s)
        {
            DateTime now = DateTime.UtcNow;
            if (lhm == null)
            {
                if ((now - lhmCheckAt).TotalSeconds < 60) return false;
                lhmCheckAt = now;
                lhm = LhmReader.Open();
                if (lhm == null) return false;
            }
            if ((now - lhmReadAt).TotalSeconds >= 3)
            {
                lhmReadAt = now;
                string name;
                double t = LhmReader.Read(lhm, out name);
                if (double.IsNaN(t)) { lhm = null; lhmTemp = double.NaN; return false; }
                lhmTemp = t; lhmName = name;
            }
            if (double.IsNaN(lhmTemp)) return false;
            s.CpuTempC = Math.Round(lhmTemp, 1);
            s.CpuTempSource = lhmName;
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { if (query != IntPtr.Zero) Pdh.PdhCloseQuery(query); } catch { }
            query = IntPtr.Zero;
            CloseAdapters();
        }
    }

    // LibreHardwareMonitor / OpenHardwareMonitor publish sensors over WMI while they run (best CPU source on desktops).
    internal static class LhmReader
    {
        static readonly string[] Namespaces = { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" };
        static readonly string[] Preferred = { "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "Core Max", "Core Average", "CPU Cores" };

        public static object Open()
        {
            foreach (string ns in Namespaces)
            {
                try
                {
                    System.Management.ManagementScope sc = new System.Management.ManagementScope(ns);
                    sc.Connect();
                    string dummy;
                    if (!double.IsNaN(Read(sc, out dummy))) return sc;
                }
                catch { }
            }
            return null;
        }

        public static double Read(object scope, out string name)
        {
            name = "";
            try
            {
                System.Management.ManagementScope sc = (System.Management.ManagementScope)scope;
                System.Management.ObjectQuery q = new System.Management.ObjectQuery("SELECT Name,Value,Parent FROM Sensor WHERE SensorType='Temperature'");
                Dictionary<string, double> found = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                using (System.Management.ManagementObjectSearcher srch = new System.Management.ManagementObjectSearcher(sc, q))
                using (System.Management.ManagementObjectCollection res = srch.Get())
                {
                    foreach (System.Management.ManagementObject o in res)
                    {
                        string parent = Convert.ToString(o["Parent"]) ?? "";
                        if (parent.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        double v = Convert.ToDouble(o["Value"], CultureInfo.InvariantCulture);
                        if (v > 5 && v < 125) found[Convert.ToString(o["Name"])] = v;
                    }
                }
                foreach (string p in Preferred)
                {
                    double v;
                    if (found.TryGetValue(p, out v)) { name = "Hardware monitor: " + p; return v; }
                }
                double max = double.NaN;
                foreach (KeyValuePair<string, double> kv in found) if (double.IsNaN(max) || kv.Value > max) { max = kv.Value; name = "Hardware monitor: " + kv.Key; }
                return max;
            }
            catch { return double.NaN; }
        }
    }
}
