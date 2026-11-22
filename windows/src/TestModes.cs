// HW Widget for Windows - command-line test modes: self-test, renders, probe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HwWidget
{
    // ------------------------------------------------------------------ test modes (no console: each writes a file)
    internal static class TestModes
    {
        static HwSnapshot Fake(string kind)
        {
            HwSnapshot s = new HwSnapshot();
            s.CpuName = "Intel(R) Core(TM) i7-10850H CPU @ 2.70GHz";
            if (kind == "unknown") return s;
            bool hot = kind == "hot";
            s.CpuTempC = hot ? 93 : 61; s.CpuTempSource = "ACPI thermal zone CPUZ";
            s.CpuPercent = hot ? 96 : 23; s.RamPercent = hot ? 91 : 58; s.RamUsedGB = hot ? 29.1 : 18.6; s.RamTotalGB = 31.8;
            s.DiskPercent = hot ? 99 : 4; s.NetDownBps = hot ? 48e6 : 182e3; s.NetUpBps = hot ? 2.1e6 : 21e3;
            GpuInfo g = new GpuInfo(); g.Name = "NVIDIA Quadro RTX 4000 with Max-Q Design"; g.Percent = hot ? 97 : 12; g.TempC = hot ? 86 : 51; g.MemUsedGB = hot ? 7.4 : 1.2;
            GpuInfo i = new GpuInfo(); i.Name = "Intel(R) UHD Graphics"; i.Integrated = true; i.Percent = hot ? 40 : 3; i.MemUsedGB = 0.1;
            s.Gpus = new GpuInfo[] { g, i };
            s.GpuPercent = g.Percent; s.GpuName = g.Name; s.GpuTempC = g.TempC;
            if (kind == "warm") { s.CpuTempC = 82; s.CpuPercent = 78; s.RamPercent = 83; s.GpuPercent = 88; g.Percent = 88; }
            return s;
        }

        public static int Render(string dir)
        {
            Directory.CreateDirectory(dir);
            float[] scales = { 1f, 1.25f, 1.5f, 2f };
            string[] kinds = { "normal", "warm", "hot", "unknown", "live" };
            HwSnapshot live;
            using (HwSampler smp = new HwSampler()) { Thread.Sleep(1100); live = smp.Sample(); }
            int n = 0;
            foreach (bool dark in new bool[] { true, false })
            {
                Palette p = Palette.For(dark);
                Color bar = dark ? Palette.Hex("#1C1C1C") : Palette.Hex("#EEEEEE");
                foreach (string k in kinds)
                {
                    HwSnapshot s = k == "live" ? live : Fake(k);
                    foreach (float sc in scales)
                    {
                        using (Bitmap pill = Painter.Pill(Painter.Cells(s, false, Fourth.Auto, true, p), p, sc, false))
                        using (Bitmap canvas = new Bitmap(pill.Width + (int)(48 * sc), (int)(48 * sc), PixelFormat.Format32bppArgb))
                        {
                            using (Graphics g = Graphics.FromImage(canvas))
                            {
                                g.Clear(bar);
                                g.DrawImage(pill, (int)(24 * sc), (canvas.Height - pill.Height) / 2, pill.Width, pill.Height);
                            }
                            canvas.Save(Path.Combine(dir, string.Format(CultureInfo.InvariantCulture, "pill-{0}-{1}-{2:000}.png", dark ? "dark" : "light", k, sc * 100)), ImageFormat.Png);
                            n++;
                        }
                    }
                    using (Bitmap card = Painter.Card(s, false, p, 1.25f, k == "hot"))
                    using (Bitmap canvas = new Bitmap(card.Width + 40, card.Height + 40, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(canvas))
                        {
                            using (LinearGradientBrush wall = new LinearGradientBrush(new Rectangle(0, 0, canvas.Width, canvas.Height),
                                dark ? Palette.Hex("#0B1026") : Palette.Hex("#DCE6F5"), dark ? Palette.Hex("#2B1640") : Palette.Hex("#F5E6F0"), 60f))
                                g.FillRectangle(wall, 0, 0, canvas.Width, canvas.Height);
                            g.DrawImage(card, 20, 20, card.Width, card.Height);
                        }
                        canvas.Save(Path.Combine(dir, string.Format("card-{0}-{1}-125.png", dark ? "dark" : "light", k)), ImageFormat.Png);
                        n++;
                    }
                }
            }
            File.WriteAllText(Path.Combine(dir, "render.txt"), n + " images\r\n");
            return 0;
        }

        static string D(double v) { return double.IsNaN(v) ? "" : v.ToString("0.##", CultureInfo.InvariantCulture); }
        internal static string Unit(double v, string unit) { return double.IsNaN(v) ? Fmt.Dash : D(v) + unit; }
        static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "'") + "\""; }

        public static int Probe(int secs, string csv)
        {
            using (HwSampler smp = new HwSampler())
            using (StreamWriter w = new StreamWriter(csv, false, new UTF8Encoding(false)))
            {
                w.WriteLine("utc,sample_ms,cpu_pct,cpu_temp_c,temp_source,ram_pct,ram_used_gb,gpu_pct,gpu_name,gpu_temp_c,gpus,disk_pct,net_down_bps,net_up_bps");
                Thread.Sleep(1000);
                for (int i = 0; i < secs; i++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    HwSnapshot s = smp.Sample();
                    sw.Stop();
                    List<string> gp = new List<string>();
                    foreach (GpuInfo g in s.Gpus) gp.Add(g.Name + " " + D(g.Percent) + "% " + Unit(g.TempC, "C") + " " + D(g.MemUsedGB) + "GB");
                    w.WriteLine(string.Join(",", new string[] {
                        s.TakenUtc.ToString("o"), sw.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture),
                        D(s.CpuPercent), D(s.CpuTempC), Q(s.CpuTempSource), D(s.RamPercent), D(s.RamUsedGB),
                        D(s.GpuPercent), Q(s.GpuName), D(s.GpuTempC), Q(string.Join(" | ", gp.ToArray())), D(s.DiskPercent), D(s.NetDownBps), D(s.NetUpBps) }));
                    w.Flush();
                    Thread.Sleep(Math.Max(0, 1000 - (int)sw.ElapsedMilliseconds));
                }
            }
            return 0;
        }

        public static int SelfTest(string outFile)
        {
            List<string> lines = new List<string>();
            int pass = 0, fail = 0;
            Action<string, bool> check = delegate(string name, bool ok) { lines.Add((ok ? "PASS " : "FAIL ") + name); if (ok) pass++; else fail++; };

            Dictionary<string, double> z = new Dictionary<string, double>();
            z[@"\_TZ.CPUZ"] = 68; z[@"\_TZ.PCHZ"] = 0.05; z[@"\_TZ.GFXZ"] = 30;
            check("zone: CPUZ beats GFXZ and a 273 K PCH zone", ZonePicker.Pick(z, null) == @"\_TZ.CPUZ");
            z = new Dictionary<string, double>(); z[@"\_TZ.TZ00"] = 27.85; z[@"\_TZ.TZ01"] = 45;
            check("zone: 3010 dK placeholder skipped", ZonePicker.Pick(z, null) == @"\_TZ.TZ01");
            z = new Dictionary<string, double>(); z[@"\_TZ.GFXZ"] = 40; z[@"\_TZ.BATZ"] = 33;
            check("zone: GPU and battery zones never used as CPU", ZonePicker.Pick(z, null) == null);
            z = new Dictionary<string, double>(); z[@"\_TZ.TZ00"] = 50; z[@"\_TZ.TZ01"] = 40;
            check("zone: flat-lined zone excluded", ZonePicker.Pick(z, new string[] { @"\_TZ.TZ00" }) == @"\_TZ.TZ01");
            z = new Dictionary<string, double>(); z[@"\_TZ.CPUZ"] = 125;
            check("zone: implausible 125 C rejected", ZonePicker.Pick(z, null) == null);
            z = new Dictionary<string, double>(); z[@"\_TZ.TZ00"] = 55; z[@"\_TZ.CPUZ"] = 50;
            check("zone: CPU-named zone preferred over a hotter generic one", ZonePicker.Pick(z, null) == @"\_TZ.CPUZ");

            check("fmt: 68 C", Fmt.Temp(68.4, false) == "68°");
            check("fmt: 20 C in F", Fmt.Temp(20, true) == "68°");
            check("fmt: unknown percent is a dash", Fmt.Pct(double.NaN) == "—");
            check("fmt: 1536 B/s", Fmt.Rate(1536) == "1.5 KB/s");
            check("fmt: 48 MB/s", Fmt.Rate(48e6) == "46 MB/s");
            check("fmt: levels", Fmt.Level(79, 80, 90) == 0 && Fmt.Level(80, 80, 90) == 1 && Fmt.Level(95, 80, 90) == 2);
            // TC-135: the colour matches the number shown, and .5 rounds up as on GNOME and macOS.
            check("TC-135 fmt: 74.6 % shows 75% in amber", Fmt.Pct(74.6) == "75%" && Fmt.Level(74.6, 75, 90) == 1);
            check("TC-135 fmt: 89.6 % RAM shows 90% in red", Fmt.Pct(89.6) == "90%" && Fmt.Level(89.6, 80, 90) == 2);
            check("TC-135 fmt: 68.5 C shows 69, 50.5 % shows 51%", Fmt.Temp(68.5, false) == "69°" && Fmt.Pct(50.5) == "51%");
            // TC-122: a maximized window over an auto-hidden taskbar is not full screen; a real full-screen one is.
            Native.RECT monR = new Native.RECT(); monR.Right = 1536; monR.Bottom = 960;
            Native.RECT maxR = new Native.RECT(); maxR.Left = -7; maxR.Top = -7; maxR.Right = 1543; maxR.Bottom = 967;
            check("TC-122 fullscreen: maximized window with auto-hide taskbar is not full screen", !ZOrder.IsFullscreen(maxR, monR, true));
            check("TC-122 fullscreen: borderless window covering the monitor is full screen", ZOrder.IsFullscreen(monR, monR, false));

            HwSnapshot noGpu = new HwSnapshot(); noGpu.CpuPercent = 10; noGpu.DiskPercent = 5;
            Cell[] c = Painter.Cells(noGpu, false, Fourth.Auto, false, Palette.For(true));
            check("cells: auto shows DISK without a GPU", c.Length == 4 && c[3].Label == "DISK");
            HwSnapshot gpuOnlyTemp = Fake("normal"); gpuOnlyTemp.CpuTempC = double.NaN;
            c = Painter.Cells(gpuOnlyTemp, false, Fourth.Auto, false, Palette.For(true));
            check("cells: GPU temperature fallback is labelled", c[0].Label == "GPU°" && c[3].Label == "GPU");
            check("cells: hot CPU is critical", Painter.Cells(Fake("hot"), false, Fourth.Auto, true, Palette.For(true))[1].Level == 2);

            check("esif: deg C, Kelvin and tenths of Kelvin", EsifPicker.ToCelsius(75) == 75 && EsifPicker.ToCelsius(348.15) == 75 && EsifPicker.ToCelsius(3481.5) == 75);
            check("esif: hottest live participant, zeros and placeholders skipped", EsifPicker.Pick(new double[] { 0, 61, 75, 73, 0, 3010 }) == 75);
            check("esif: no live participant is unknown", double.IsNaN(EsifPicker.Pick(new double[] { 0, 0, 200 })));
            string hs; long now = 1800000000000L;
            check("helper file: fresh reading round-trips", HelperFile.Parse(HelperFile.Format(now - 2000, 74.04, "Intel Dynamic Tuning"), now, out hs) == 74 && hs == "Intel Dynamic Tuning");
            check("helper file: stale reading ignored", double.IsNaN(HelperFile.Parse(HelperFile.Format(now - 16000, 74, "x"), now, out hs)));
            check("helper file: garbage and implausible values ignored", double.IsNaN(HelperFile.Parse("hello", now, out hs)) && double.IsNaN(HelperFile.Parse(HelperFile.Format(now, 140, "x"), now, out hs)));
            HwSnapshot hinted = Fake("normal"); hinted.CpuTempC = double.NaN; hinted.Gpus = new GpuInfo[0]; hinted.GpuTempC = double.NaN; hinted.CpuTempHint = "Right-click and choose Enable CPU temperature";
            check("cells: no temperature at all is a dash", Painter.Cells(hinted, false, Fourth.Auto, true, Palette.For(true))[0].Value == Fmt.Temp(double.NaN, false));

            HwSnapshot spoken = new HwSnapshot(); spoken.CpuTempC = 61; spoken.CpuPercent = 23; spoken.RamPercent = 58; spoken.DiskPercent = double.NaN;
            check("a11y: pill reads as text", Painter.AccessibleText(Painter.Cells(spoken, false, Fourth.Disk, false, Palette.For(true))) == "TEMP 61°, CPU 23%, RAM 58%, DISK unknown");
            spoken.NetDownBps = 1258291;
            check("a11y: download cell speaks the full rate", Painter.AccessibleText(Painter.Cells(spoken, false, Fourth.Disk, true, Palette.For(true))).EndsWith(", DOWN 1.2 MB/s"));
            c = Painter.Cells(Fake("normal"), false, Fourth.Auto, true, Palette.For(true));
            check("cells: download cell is 5th, never amber or red", c.Length == 5 && c[4].Label == "DOWN" && c[4].Level == 0 && c[4].Value == "178K");
            check("cells: download cell can be turned off", Painter.Cells(Fake("normal"), false, Fourth.Auto, false, Palette.For(true)).Length == 4);
            check("rate short: K edges", Fmt.RateShort(0) == "0K" && Fmt.RateShort(999 * 1024) == "999K" && Fmt.RateShort(999.4 * 1024) == "999K");
            check("rate short: K to M at 999.5 K", Fmt.RateShort(999.5 * 1024) == "1.0M" && Fmt.RateShort(1023 * 1024) == "1.0M" && Fmt.RateShort(1024 * 1024) == "1.0M");
            check("rate short: one decimal below 9.95 M", Fmt.RateShort(9.94 * 1048576) == "9.9M" && Fmt.RateShort(9.96 * 1048576) == "10M" && Fmt.RateShort(999 * 1048576.0) == "999M");
            check("rate short: G, negative and unknown", Fmt.RateShort(1000 * 1048576.0) == "1.0G" && Fmt.RateShort(-5) == "0K" && Fmt.RateShort(double.NaN) == Fmt.Dash);
            // QA-01: in °F the colour is judged on the °F number shown, so two readings that both show 175° look the same.
            HwSnapshot f1 = new HwSnapshot(); f1.CpuTempC = 79.4; HwSnapshot f2 = new HwSnapshot(); f2.CpuTempC = 79.6;
            Cell cf1 = Painter.Cells(f1, true, Fourth.Disk, false, Palette.For(true))[0], cf2 = Painter.Cells(f2, true, Fourth.Disk, false, Palette.For(true))[0];
            check("QA-01 fmt: 79.4 and 79.6 C both show 175° in °F and share one colour", cf1.Value == "175°" && cf2.Value == "175°" && cf1.Level == cf2.Level && cf1.Level == 0);
            HwSnapshot f3 = new HwSnapshot(); f3.CpuTempC = 79.8;
            check("QA-01 fmt: 176°F (80 C) is amber, 194°F (90 C) is red", Painter.Cells(f3, true, Fourth.Disk, false, Palette.For(true))[0].Level == 1 && Fmt.TempLevel(90, true) == 2 && Fmt.TempLevel(89.6, false) == 2 && Fmt.TempLevel(79.4, false) == 0);
            // QA-02: the card's rates and sizes roll over like the pill's: never "1000 KB/s" or "10.0".
            check("QA-02 fmt: rate rolls over at the shown value", Fmt.Rate(1023999) == "1.0 MB/s" && Fmt.Rate(999.6) == "1.0 KB/s" && Fmt.Rate(10199) == "10 KB/s" && Fmt.Rate(9.94 * 1024) == "9.9 KB/s" && Fmt.Rate(999) == "999 B/s");
            check("QA-02 fmt: 9.96 GB shows 10 GB", Fmt.GB(9.96) == "10 GB" && Fmt.GB(9.94) == "9.9 GB");
            // QA-03: on a vertical taskbar the pill can't be dragged, so a shaky click still pins the card.
            check("QA-03 drag: never starts on a vertical taskbar", !ZOrder.DragStarts(20, 1f, true) && ZOrder.DragStarts(20, 1f, false) && !ZOrder.DragStarts(4, 1f, false));
            // QA-05: error.log stays small when the same error repeats every tick.
            string logF = Path.Combine(Path.GetTempPath(), "hwwidget-selftest-log-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                for (int i = 0; i < 400; i++) Program.AppendCapped(logF, new string('x', 99) + "\n", 8192);
                long len = new FileInfo(logF).Length;
                check("QA-05 log: capped instead of growing without limit", len <= 8192 && len > 0 && File.ReadAllText(logF).EndsWith(new string('x', 99) + "\n"));
            }
            finally { try { File.Delete(logF); } catch { } }
            // QA-10: VPN tunnels carry traffic the physical adapter already counts, so they are left out.
            Dictionary<string, double> net = new Dictionary<string, double>();
            net["Intel(R) Wi-Fi 6E AX211 160MHz"] = 1000; net["Tailscale Tunnel"] = 400; net["WireGuard Tunnel"] = 300;
            net["TAP-Windows Adapter V9"] = 200; net["Wintun Userspace Tunnel"] = 100;
            check("QA-10 net: tunnel adapters are not counted twice", HwSampler.SumNet(net) == 1000);
            // QA-11: an empty adapter list or a new GPU LUID is re-read within seconds, not after 5 minutes.
            check("QA-11 gpu: empty list or unknown LUID re-reads adapters after 10 s",
                HwSampler.AdaptersStale(TimeSpan.FromSeconds(30), true, false) && HwSampler.AdaptersStale(TimeSpan.FromSeconds(30), false, true) &&
                !HwSampler.AdaptersStale(TimeSpan.FromSeconds(30), false, false) && !HwSampler.AdaptersStale(TimeSpan.FromSeconds(5), true, true) &&
                HwSampler.AdaptersStale(TimeSpan.FromMinutes(6), false, false));
            check("probe: unknown temperature is a dash", Unit(double.NaN, "C") == "—" && Unit(51, "C") == "51C");
            string tmp = Path.GetFullPath(Path.GetTempPath()), pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            check("helper: task XML sits beside the helper exe", HelperSetup.TaskXmlPath.StartsWith(pf, StringComparison.OrdinalIgnoreCase) && !HelperSetup.TaskXmlPath.StartsWith(tmp, StringComparison.OrdinalIgnoreCase));
            string lockDir = Path.Combine(tmp, "hwwidget-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(lockDir);
            try
            {
                bool second, again, aclOk;
                using (FileStream first = SensorHelper.TryLock(lockDir))
                {
                    using (FileStream other = SensorHelper.TryLock(lockDir)) second = first != null && other == null;
                    aclOk = first != null;
                    if (first != null)
                    {
                        FileSecurity fsec = File.GetAccessControl(Path.Combine(lockDir, "helper.lock"));
                        aclOk = fsec.AreAccessRulesProtected;
                        foreach (FileSystemAccessRule rule in fsec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                        {
                            SecurityIdentifier sid = (SecurityIdentifier)rule.IdentityReference;
                            if (rule.AccessControlType == AccessControlType.Allow && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid) && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) aclOk = false;
                        }
                    }
                }
                using (FileStream later = SensorHelper.TryLock(lockDir)) again = later != null;
                check("helper lock: a second copy cannot start, the next one can", second && again);
                check("helper lock: only SYSTEM and Administrators may open it", aclOk);
                check("helper lock: no data folder means no start", SensorHelper.TryLock(Path.Combine(lockDir, "missing")) == null);
            }
            finally { try { Directory.Delete(lockDir, true); } catch { } }

            Native.RECT pillR = new Native.RECT(); pillR.Left = 1164; pillR.Top = 1028; pillR.Right = 1404; pillR.Bottom = 1071;
            Native.RECT barR = new Native.RECT(); barR.Left = 0; barR.Top = 1020; barR.Right = 1920; barR.Bottom = 1080;
            Native.RECT menuR = new Native.RECT(); menuR.Left = 1100; menuR.Top = 700; menuR.Right = 1400; menuR.Bottom = 1060;
            Native.RECT otherBar = new Native.RECT(); otherBar.Left = 1920; otherBar.Top = 1020; otherBar.Right = 3840; otherBar.Bottom = 1080;
            List<KeyValuePair<string, Native.RECT>> above = new List<KeyValuePair<string, Native.RECT>>();
            above.Add(new KeyValuePair<string, Native.RECT>("#32768", menuR));
            check("z-order: a menu over the pill stays on top", !ZOrder.ShellCovers(above, pillR));
            above.Add(new KeyValuePair<string, Native.RECT>("Shell_SecondaryTrayWnd", otherBar));
            check("z-order: another monitor's taskbar is ignored", !ZOrder.ShellCovers(above, pillR));
            above.Add(new KeyValuePair<string, Native.RECT>("Shell_TrayWnd", barR));
            check("z-order: a taskbar raised over the pill is detected", ZOrder.ShellCovers(above, pillR));

            check("install: gate passes only a clean report", SelfInstall.GatePasses("48 passed, 0 failed") && !SelfInstall.GatePasses("47 passed, 1 failed") && !SelfInstall.GatePasses(null) && !SelfInstall.GatePasses(""));
            InstallArgs ia = InstallArgs.Parse(new string[] { "--install", "--CPU-TEMP", "--startup", @"C:\x y", "--no-launch" }, 1);
            check("install: arguments parse", ia.CpuTemp && ia.NoLaunch && !ia.KeepHelper && ia.StartupDir == @"C:\x y" && !InstallArgs.Parse(new string[] { "--install" }, 1).CpuTemp);
            check("install: startup shortcut sits in the user's Startup folder", Startup.LinkPath(null) == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "HW Widget.lnk"));

            Size ps = Painter.PillSize(4, 1.25f);
            check("pill: 192x34 DIP -> 240x43 px at 125%", ps.Width == 240 && ps.Height == 43);
            ps = Painter.PillSize(5, 1.25f);
            check("pill: 5 cells 236x34 DIP -> 295x43 px at 125%", ps.Width == 295 && ps.Height == 43);

            lines.Add(string.Format("{0} passed, {1} failed", pass, fail));
            File.WriteAllLines(outFile, lines.ToArray());
            return fail == 0 ? 0 : 1;
        }
    }
}
