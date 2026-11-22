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

        // WCAG 2 contrast: relative luminance of opaque colours, and the lowest ratio of any text colour on any background.
        static double Luminance(Color c)
        {
            double[] ch = { c.R / 255.0, c.G / 255.0, c.B / 255.0 };
            for (int i = 0; i < 3; i++) ch[i] = ch[i] <= 0.03928 ? ch[i] / 12.92 : Math.Pow((ch[i] + 0.055) / 1.055, 2.4);
            return 0.2126 * ch[0] + 0.7152 * ch[1] + 0.0722 * ch[2];
        }

        static double MinContrast(IEnumerable<Color> texts, Color[] backgrounds)
        {
            double min = double.MaxValue;
            foreach (Color t in texts)
                foreach (Color b in backgrounds)
                {
                    double x = Luminance(t), y = Luminance(b);
                    min = Math.Min(min, (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05));
                }
            return Math.Floor(min * 100) / 100;
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

            // TC-223 (QA-19): the card tells a screen reader its rows, not only "HW Widget details".
            string cardSaid = Painter.CardAccessibleText(Fake("normal"), false);
            check("TC-223 a11y: the card reads its rows", cardSaid.StartsWith("Temperature 61°C (CPU, ACPI thermal zone CPUZ), CPU 23%") && cardSaid.Contains("Memory 58% (19 of 32 GB in use)") &&
                cardSaid.Contains("GPU 12% (51°C, 1.2 GB VRAM, Quadro RTX 4000 Max-Q)") && cardSaid.Contains("Disk 4%") && cardSaid.EndsWith("Network 178 KB/s download (21 KB/s upload)"));
            cardSaid = Painter.CardAccessibleText(Fake("unknown"), false);
            check("TC-223 a11y: unknown card values are read as unknown", cardSaid.Contains("CPU unknown") && cardSaid.Contains("Memory unknown") && !cardSaid.Contains(Fmt.Dash));
            // TC-232 (QA-13): a VM's own network adapters count; the host's Hyper-V switch adapter and other virtual adapters don't.
            Dictionary<string, double> vmNet = new Dictionary<string, double>();
            vmNet["Microsoft Hyper-V Network Adapter"] = 1000; vmNet["Microsoft Hyper-V Network Adapter _2"] = 200;
            check("TC-232 net: a VM's Hyper-V network adapters count", HwSampler.SumNet(vmNet) == 1200);
            Dictionary<string, double> hostNet = new Dictionary<string, double>();
            hostNet["Intel[R] Ethernet Connection [16] I219-LM"] = 1000; hostNet["Hyper-V Virtual Ethernet Adapter"] = 1000; hostNet["Hyper-V Virtual Ethernet Adapter _2"] = 50;
            hostNet["Hyper-V Virtual Switch Extension Adapter"] = 5; hostNet["VMware Virtual Ethernet Adapter for VMnet8"] = 30; hostNet["VirtualBox Host-Only Ethernet Adapter"] = 20;
            hostNet["Microsoft Wi-Fi Direct Virtual Adapter _2"] = 10; hostNet["PANGP Virtual Ethernet Adapter"] = 400;
            check("TC-232 net: the host's Hyper-V switch adapter and other virtual adapters stay out", HwSampler.SumNet(hostNet) == 1000);
            // TC-235 (QA-20): fonts and string formats are made once, and a pill that would look the same is not drawn again.
            Palette dp = Palette.For(true);
            Cell[] frame = Painter.Cells(Fake("normal"), false, Fourth.Auto, true, dp);
            using (Bitmap first1 = Painter.Pill(frame, dp, 1.25f, false)) { }
            using (Bitmap first2 = Painter.Card(Fake("normal"), false, dp, 1.25f, false)) { }
            int fontsBefore = Painter.FontsMade, formatsBefore = Painter.FormatsMade;
            using (Bitmap next1 = Painter.Pill(frame, dp, 1.5f, true)) { }
            using (Bitmap next2 = Painter.Card(Fake("hot"), true, Palette.For(false), 2f, true)) { }
            check("TC-235 render: no new fonts or string formats after the first frame", Painter.FontsMade == fontsBefore && Painter.FormatsMade == formatsBefore);
            string key = Painter.PillKey(frame, dp, 1.25f, false);
            check("TC-235 render: an identical frame is not drawn again", key == Painter.PillKey(Painter.Cells(Fake("normal"), false, Fourth.Auto, true, dp), dp, 1.25f, false));
            Cell[] recoloured = Painter.Cells(Fake("normal"), false, Fourth.Auto, true, dp); recoloured[1].ValueColor = Color.Red;
            Cell[] longer = Painter.Cells(Fake("normal"), false, Fourth.Auto, true, dp); longer[4].Frac += 0.01;
            check("TC-235 render: a theme, DPI, hover, colour, bar or value change is drawn",
                key != Painter.PillKey(Painter.Cells(Fake("normal"), false, Fourth.Auto, true, Palette.For(false)), Palette.For(false), 1.25f, false) &&
                key != Painter.PillKey(frame, dp, 1.5f, false) && key != Painter.PillKey(frame, dp, 1.25f, true) &&
                key != Painter.PillKey(recoloured, dp, 1.25f, false) && key != Painter.PillKey(longer, dp, 1.25f, false) &&
                key != Painter.PillKey(Painter.Cells(Fake("warm"), false, Fourth.Auto, true, dp), dp, 1.25f, false));
            // TC-243 (QA-25): settings values are read in any case, like the keys.
            check("TC-243 settings: fourth=GPU and Fourth = Disk are read in any case",
                Settings.Parse(new string[] { "fourth=GPU" }).FourthMetric == Fourth.Gpu && Settings.Parse(new string[] { "FOURTH = Disk" }).FourthMetric == Fourth.Disk &&
                Settings.Parse(new string[] { "fourth=gpu" }).FourthMetric == Fourth.Gpu && Settings.Parse(new string[] { "fourth=other" }).FourthMetric == Fourth.Auto);
            Settings parsed = Settings.Parse(new string[] { "interval=5", "unit=f", "offset=12", "download=OFF", "no equals sign" });
            check("TC-243 settings: the other keys still parse", parsed.IntervalSec == 5 && parsed.Fahrenheit && parsed.OffsetFromRight == 12 && !parsed.Download && parsed.FourthMetric == Fourth.Auto);
            // TC-246 (QA-02): a self-test that stops with an exception fails the install gate (exit 3) instead of crashing.
            string gateReport = Path.Combine(tmp, "hwwidget-selftest-gate-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                bool gateThrew = false, gateOk = true;
                try { gateOk = SelfInstall.RunGate(delegate(string f) { throw new InvalidOperationException("self-test stopped"); }, gateReport); }
                catch { gateThrew = true; }
                check("TC-246 install: a self-test that throws fails the gate instead of crashing", !gateThrew && !gateOk);
                File.WriteAllText(gateReport, "9 passed, 0 failed");
                bool fresh = SelfInstall.RunGate(delegate(string f) { File.WriteAllLines(f, new string[] { "PASS x", "1 passed, 0 failed" }); return 0; }, gateReport);
                File.WriteAllText(gateReport, "9 passed, 0 failed");
                bool stale = SelfInstall.RunGate(delegate(string f) { return 0; }, gateReport);
                check("TC-246 install: the gate passes only a fresh clean report", fresh && !stale);
            }
            finally { try { File.Delete(gateReport); } catch { } }
            // TC-250 (QA-12): the install finds its own exe and report also in a folder named like p%41q.
            check("TC-250 install: the exe path is the real file, also in a folder with %41 in its name", File.Exists(SelfInstall.ExePath));
            // TC-259 (QA-18): every light-mode text colour reaches 4.5:1 on the pill and card backgrounds; dark mode is unchanged.
            Palette lp = Palette.For(false);
            Color[] lightBgs = { Palette.Hex("#FFFFFF"), Palette.Hex("#EEF1F8"), Palette.Hex("#F3F5FB") };
            List<Color> tints = new List<Color>();
            foreach (Color accent in new Color[] { Painter.CpuA, Painter.CpuB, Painter.RamA, Painter.RamB, Painter.GpuA, Painter.GpuB, Painter.DskA, Painter.DskB, Painter.NetA, Painter.NetB })
                tints.Add(Painter.LabelTint(accent, lp));
            foreach (Cell lc in Painter.Cells(Fake("normal"), false, Fourth.Auto, true, lp)) { tints.Add(lc.LabelColor); tints.Add(lc.ValueColor); }
            double lowest = MinContrast(tints, lightBgs);
            check("TC-259 contrast: light label tints of every accent reach 4.5:1 (lowest " + lowest.ToString("0.00", CultureInfo.InvariantCulture) + ")", lowest >= 4.5);
            List<Color> tempColours = new List<Color>();
            foreach (double tv in new double[] { double.NaN, 20, 35, 42, 50, 57, 65, 70, 75, 79, 82, 85, 90, 95, 105 })
            {
                HwSnapshot ts = new HwSnapshot(); ts.CpuTempC = tv;
                Cell tcell = Painter.Cells(ts, false, Fourth.Disk, false, lp)[0];
                tempColours.Add(tcell.LabelColor);
                if (!double.IsNaN(tv)) { tempColours.Add(tcell.ValueColor); tempColours.Add(Painter.CardRows(ts, false, lp)[0].ValueColor); }
            }
            lowest = MinContrast(tempColours, lightBgs);
            check("TC-259 contrast: light temperature colours and the unknown grey reach 4.5:1 (lowest " + lowest.ToString("0.00", CultureInfo.InvariantCulture) + ")", lowest >= 4.5);
            lowest = MinContrast(new Color[] { lp.Warn, lp.Crit, Painter.PinnedColor(lp) }, lightBgs);
            check("TC-259 contrast: light warn, crit and Pinned reach 4.5:1 (lowest " + lowest.ToString("0.00", CultureInfo.InvariantCulture) + ")", lowest >= 4.5);
            check("TC-259 contrast: dark warn, crit and Pinned are unchanged", Painter.PinnedColor(dp).ToArgb() == Palette.Hex("#A78BFA").ToArgb() &&
                dp.Warn.ToArgb() == Palette.Hex("#FCD34D").ToArgb() && dp.Crit.ToArgb() == Palette.Hex("#FF8FA3").ToArgb());
            // TC-264 (QA-22): ages come from a monotonic clock, and a clock that steps back never freezes a re-read.
            check("TC-264 clock: a re-read stays due when the clock steps back", HwSampler.Due(1000, 600000, 3000) && HwSampler.Due(6000, 3000, 3000) && !HwSampler.Due(5000, 3000, 3000));
            check("TC-264 gpu: a negative adapter-list age re-reads the adapters", HwSampler.AdaptersStale(TimeSpan.FromMinutes(-30), false, false) && HwSampler.AdaptersStale(TimeSpan.FromSeconds(-1), true, false));
            // TC-265 (QA-24): NVML's device 0 stands for a GPU only when there is exactly 1 NVIDIA GPU, and a failed open is tried again.
            check("TC-265 gpu: the NVML temperature is used only with exactly 1 NVIDIA GPU",
                HwSampler.NvmlFits(new string[] { "NVIDIA Quadro RTX 4000 with Max-Q Design", "Intel(R) UHD Graphics" }) &&
                !HwSampler.NvmlFits(new string[] { "NVIDIA GeForce RTX 4090", "NVIDIA GeForce RTX 3060" }) && !HwSampler.NvmlFits(new string[] { "Intel(R) Arc(TM) Graphics" }));
            check("TC-265 gpu: a failed NVML open is tried again after 10 s, doubling up to 10 minutes",
                HwSampler.NvmlRetryMs(1) == 10000 && HwSampler.NvmlRetryMs(2) == 20000 && HwSampler.NvmlRetryMs(3) == 40000 && HwSampler.NvmlRetryMs(7) == 600000 && HwSampler.NvmlRetryMs(1000) == 600000);
            // TC-309 (QA-05): a helper install that fails after the copy leaves no helper exe, so the menu offers Enable again.
            string helperBox = Path.Combine(tmp, "hwwidget-selftest-helper-" + Guid.NewGuid().ToString("N"));
            try
            {
                string hsrc = Path.Combine(helperBox, "source.bin"), hbin = Path.Combine(helperBox, "bin"), hexe = Path.Combine(hbin, "helper.bin"), hkeep = Path.Combine(hbin, "keep.txt");
                Directory.CreateDirectory(helperBox);
                File.WriteAllText(hsrc, "x");
                bool removed = HelperSetup.CopyThen(hsrc, hbin, hexe, delegate { return 3; }) == 3 && !File.Exists(hexe) && !Directory.Exists(hbin);
                bool threw = false;
                try { HelperSetup.CopyThen(hsrc, hbin, hexe, delegate { throw new IOException("stopped"); }); }
                catch (IOException) { threw = true; }
                check("TC-309 helper: a failed install removes the copied exe and its empty folder", removed && threw && !File.Exists(hexe) && !Directory.Exists(hbin));
                Directory.CreateDirectory(hbin);
                File.WriteAllText(hkeep, "x");
                bool kept = HelperSetup.CopyThen(hsrc, hbin, hexe, delegate { return 4; }) == 4 && !File.Exists(hexe) && File.Exists(hkeep);
                check("TC-309 helper: a folder with other files stays, and a good install keeps the exe", kept && HelperSetup.CopyThen(hsrc, hbin, hexe, delegate { return 0; }) == 0 && File.Exists(hexe));
                // A reinstall that fails also removes the old scheduled task: Uninstall no longer sees a helper without its exe.
                int undone = 0;
                bool undoOnFail = HelperSetup.CopyThen(hsrc, hbin, hexe, delegate { return 6; }, delegate { undone++; }) == 6 && undone == 1;
                bool undoOnThrow = false;
                try { HelperSetup.CopyThen(hsrc, hbin, hexe, delegate { throw new IOException("stopped"); }, delegate { undone++; }); }
                catch (IOException) { undoOnThrow = undone == 2; }
                bool keptOnOk = HelperSetup.CopyThen(hsrc, hbin, hexe, delegate { return 0; }, delegate { undone++; }) == 0 && undone == 2;
                check("TC-309 helper: a failed install also removes its scheduled task, a good one keeps it", undoOnFail && undoOnThrow && keptOnOk);
            }
            finally { try { Directory.Delete(helperBox, true); } catch { } }
            // TC-310 (QA-06): a failed shortcut still asks for the helper; both failing is exit 8.
            bool asked = false, askedAnyway = false;
            int both = SelfInstall.Outcome(false, true, delegate { asked = true; return false; });
            check("TC-310 install: a failed shortcut still asks for the helper, and both failing exit 8", asked && both == 8);
            check("TC-310 install: exit codes 0, 4 and 7 as before, and no helper without --cpu-temp",
                SelfInstall.Outcome(true, false, delegate { askedAnyway = true; return false; }) == 0 && SelfInstall.Outcome(false, false, delegate { askedAnyway = true; return false; }) == 4 && !askedAnyway &&
                SelfInstall.Outcome(true, true, delegate { return true; }) == 0 && SelfInstall.Outcome(false, true, delegate { return true; }) == 4 && SelfInstall.Outcome(true, true, delegate { return false; }) == 7);
            // The CPU temperature helper's reading counts only from the installed helper's folder owned by Administrators or SYSTEM.
            SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
            bool meSystem = me.IsWellKnown(WellKnownSidType.LocalSystemSid);
            string readingBox = Path.Combine(tmp, "hwwidget-selftest-reading-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(readingBox);
            try
            {
                // The folder owned by this user; a temp folder whose owner can't be set skips only those assertions.
                bool userOwned = false;
                try
                {
                    DirectorySecurity owner = Directory.GetAccessControl(readingBox, AccessControlSections.Owner);
                    owner.SetOwner(me);
                    Directory.SetAccessControl(readingBox, owner);
                    userOwned = !meSystem;
                }
                catch { }
                long nowRead = HelperFile.UnixMs(DateTime.UtcNow);
                File.WriteAllText(Path.Combine(readingBox, "cpu-temp.txt"), HelperFile.Format(nowRead, 74, "x"));
                check("helper reading: only a folder owned by Administrators or SYSTEM counts" + (userOwned ? "" : " (user-owned folder part skipped)"),
                    HelperFile.OwnerTrusted(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) && HelperFile.OwnerTrusted(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) &&
                    !HelperFile.OwnerTrusted(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)) && !HelperFile.OwnerTrusted(null) &&
                    (!userOwned || !HelperFile.AdminOwned(readingBox)) && !HelperFile.AdminOwned(Path.Combine(readingBox, "missing")));
                string hsrc2;
                check("helper reading: only from the installed helper's admin-owned folder",
                    (!userOwned || double.IsNaN(HelperFile.ReadFrom(readingBox, true, nowRead, out hsrc2))) && double.IsNaN(HelperFile.ReadFrom(readingBox, false, nowRead, out hsrc2)));
            }
            finally { try { Directory.Delete(readingBox, true); } catch { } }

            lines.Add(string.Format("{0} passed, {1} failed", pass, fail));
            File.WriteAllLines(outFile, lines.ToArray());
            return fail == 0 ? 0 : 1;
        }
    }
}
