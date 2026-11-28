// HW Widget for Windows - the busiest processes for the hover card (CPU, memory, GPU, data read).
// C# 5 only. Read only while the card is open, so the closed pill costs nothing extra.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HwWidget
{
    public sealed class TopProc
    {
        public string Name = "";
        public double Value;
        public TopProc() { }
        public TopProc(string name, double value) { Name = name; Value = value; }
    }

    // Pure helpers, unit-tested by the self-test.
    public static class TopList
    {
        public const int Count = 3;

        // PDH names a second copy of a program "chrome#1": every copy counts as the program.
        public static string BaseName(string instance)
        {
            string n = (instance ?? "").Trim();
            int h = n.LastIndexOf('#');
            if (h > 0)
            {
                bool digits = h < n.Length - 1;
                for (int i = h + 1; i < n.Length; i++) if (!char.IsDigit(n[i])) digits = false;
                if (digits) n = n.Substring(0, h);
            }
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n;
        }

        static bool Skip(string name)
        {
            return name.Length == 0 || name.Equals("_Total", StringComparison.OrdinalIgnoreCase) || name.Equals("Idle", StringComparison.OrdinalIgnoreCase);
        }

        // The n biggest programs: copies merged by name, each value divided by `div` (CPU time is per core),
        // values under `min` dropped, then sorted biggest first (ties by name, so the order never flickers).
        public static TopProc[] Top(IDictionary<string, double> perInstance, int n, double min, double div)
        {
            Dictionary<string, double> byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> shown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> kv in perInstance)
            {
                if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value)) continue;
                string b = BaseName(kv.Key);
                if (Skip(b)) continue;
                double prev;
                byName[b] = (byName.TryGetValue(b, out prev) ? prev : 0) + kv.Value / (div > 0 ? div : 1);
                if (!shown.ContainsKey(b)) shown[b] = b;
            }
            List<TopProc> list = new List<TopProc>();
            foreach (KeyValuePair<string, double> kv in byName) if (kv.Value >= min) list.Add(new TopProc(shown[kv.Key], kv.Value));
            list.Sort(delegate(TopProc a, TopProc b)
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            if (list.Count > n) list.RemoveRange(n, list.Count - n);
            return list.ToArray();
        }

        // GPU Engine instances look like "pid_1234_luid_0x00000000_0x0000D1B7_phys_0_eng_3_engtype_3D".
        // Per adapter and process: each engine summed, then the busiest engine (Task Manager's GPU column).
        public static Dictionary<string, Dictionary<int, double>> GpuByPid(IDictionary<string, double> engines)
        {
            Dictionary<string, Dictionary<string, double>> perEngine = new Dictionary<string, Dictionary<string, double>>();
            foreach (KeyValuePair<string, double> kv in engines)
            {
                string k = kv.Key;
                if (!k.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) continue;
                int l = k.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
                int ph = k.IndexOf("_phys", StringComparison.OrdinalIgnoreCase);
                int e = k.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
                int pid;
                if (l < 0 || ph < l || e < 0 || !int.TryParse(k.Substring(4, l - 4), out pid) || pid <= 0) continue;
                string luid = k.Substring(l + 6, ph - l - 6).ToUpperInvariant().Replace("0X", "0x");
                int t = k.IndexOf("_engtype", e, StringComparison.OrdinalIgnoreCase);
                string eng = t < 0 ? k.Substring(e) : k.Substring(e, t - e);
                string key = luid + "|" + pid;
                Dictionary<string, double> d;
                if (!perEngine.TryGetValue(key, out d)) { d = new Dictionary<string, double>(); perEngine[key] = d; }
                double prev;
                d[eng] = (d.TryGetValue(eng, out prev) ? prev : 0) + kv.Value;
            }
            Dictionary<string, Dictionary<int, double>> res = new Dictionary<string, Dictionary<int, double>>();
            foreach (KeyValuePair<string, Dictionary<string, double>> kv in perEngine)
            {
                int bar = kv.Key.IndexOf('|');
                string luid = kv.Key.Substring(0, bar);
                int pid = int.Parse(kv.Key.Substring(bar + 1));
                double max = 0;
                foreach (double v in kv.Value.Values) if (v > max) max = v;
                Dictionary<int, double> byPid;
                if (!res.TryGetValue(luid, out byPid)) { byPid = new Dictionary<int, double>(); res[luid] = byPid; }
                byPid[pid] = Math.Min(100, max);
            }
            return res;
        }
    }

    // Per-process counters on a PDH query of their own, collected only while the card is open.
    internal sealed class ProcSampler : IDisposable
    {
        IntPtr query, cCpu, cMem, cRead;
        bool primed;
        readonly int cores = Math.Max(1, Environment.ProcessorCount);
        readonly Dictionary<int, string> names = new Dictionary<int, string>();
        int gpuCalls;

        public ProcSampler()
        {
            try
            {
                if (Pdh.PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
                cCpu = Add(@"\Process(*)\% Processor Time");
                cMem = Add(@"\Process(*)\Working Set - Private");
                cRead = Add(@"\Process(*)\IO Read Bytes/sec");
            }
            catch { query = IntPtr.Zero; }
        }

        IntPtr Add(string path)
        {
            IntPtr c;
            try { return Pdh.PdhAddEnglishCounterW(query, path, IntPtr.Zero, out c) == 0 ? c : IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }

        // The card was closed: the next reading only primes the rates, so they never average over the closed time.
        public void Pause() { primed = false; }

        // True when this call only primed the rate counters, so the caller should read again soon.
        public bool Sample(HwSnapshot s)
        {
            if (query == IntPtr.Zero) return false;
            try { Pdh.PdhCollectQueryData(query); } catch { return false; }
            s.TopRam = TopList.Top(Pdh.ReadArray(cMem, Pdh.FMT_DOUBLE), TopList.Count, 1, 1);
            if (!primed) { primed = true; return true; }
            s.TopCpu = TopList.Top(Pdh.ReadArray(cCpu, Pdh.FMT_DOUBLE | Pdh.FMT_NOCAP100), TopList.Count, 0.5, cores);
            s.TopRead = TopList.Top(Pdh.ReadArray(cRead, Pdh.FMT_DOUBLE), TopList.Count, 1024, 1);
            return false;
        }

        // Fills each GPU's busiest processes from the GPU Engine counters the main sampler already read.
        public void SampleGpu(HwSnapshot s, IDictionary<string, double> engines)
        {
            Dictionary<string, Dictionary<int, double>> byLuid = TopList.GpuByPid(engines);
            if (++gpuCalls % 60 == 0 || names.Count > 1000) names.Clear();   // a reused process id gets its new name
            foreach (GpuInfo g in s.Gpus)
            {
                Dictionary<int, double> byPid;
                if (!byLuid.TryGetValue(g.Luid, out byPid)) { g.Top = new TopProc[0]; continue; }
                Dictionary<string, double> byName = new Dictionary<string, double>();
                foreach (KeyValuePair<int, double> kv in byPid)
                {
                    if (kv.Value < 0.5) continue;
                    string n = NameOf(kv.Key);
                    double prev;
                    byName[n] = Math.Min(100, (byName.TryGetValue(n, out prev) ? prev : 0) + kv.Value);
                }
                g.Top = TopList.Top(byName, TopList.Count, 0.5, 1);
            }
        }

        string NameOf(int pid)
        {
            string n;
            if (names.TryGetValue(pid, out n)) return n;
            try { using (Process p = Process.GetProcessById(pid)) n = p.ProcessName; } catch { n = "pid " + pid; }
            names[pid] = n;
            return n;
        }

        public void Dispose()
        {
            try { if (query != IntPtr.Zero) Pdh.PdhCloseQuery(query); } catch { }
            query = IntPtr.Zero;
        }
    }
}
