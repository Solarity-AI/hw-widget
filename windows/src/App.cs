// HW Widget for Windows - the taskbar window, hover card, menu and the program entry point.
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
    // ------------------------------------------------------------------ the widget
    internal sealed class WidgetForm : LayeredForm
    {
        readonly Settings cfg = Settings.Load();
        readonly LayeredForm card = new LayeredForm();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread worker;
        volatile bool stopping;
        volatile HwSnapshot last = new HwSnapshot();
        bool dark = true, hover, pinned, hiddenForShell = true, mouseDown, dragging;
        float scale = 1f;
        int ticks, hoverTicks, leaveTicks, dragStartX, dragOrigX, trayLeft;
        Native.RECT tb, mon, work;
        Point pos;
        ContextMenuStrip menu;
        uint myPid = (uint)Process.GetCurrentProcess().Id;
        Native.WinEventProc onForeground;   // kept in a field so the GC never frees the callback
        IntPtr fgHook;

        public WidgetForm()
        {
            dark = ReadDark();
            card.Text = "HW Widget details";
            card.MouseUp += CardMouseUp;
            MouseDown += PillMouseDown; MouseMove += PillMouseMove; MouseUp += PillMouseUp;
            tick.Interval = 250; tick.Tick += OnTick;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            card.CreateControl();
            IntPtr force = card.Handle;
            worker = new Thread(SampleLoop); worker.IsBackground = true; worker.Name = "HW sampler"; worker.Start();
            // Clicking the taskbar activates it, and Windows then raises it above every topmost window, the pill
            // included. React to the activation at once instead of waiting for the next poll.
            onForeground = delegate { try { if (!stopping) BeginInvoke(new Action(KeepAboveTaskbar)); } catch { } };
            try { fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, onForeground, 0, 0, Native.WINEVENT_OUTOFCONTEXT); } catch { }
            tick.Start();
        }

        protected override void SetVisibleCore(bool value)
        {
            // The pill shows itself once it knows where the taskbar is.
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
        }

        void SampleLoop()
        {
            using (HwSampler smp = new HwSampler())
            {
                wake.WaitOne(1000);
                while (!stopping)
                {
                    try { last = smp.Sample(); } catch { }
                    try { if (IsHandleCreated && !stopping) BeginInvoke(new Action(Redraw)); } catch { }
                    wake.WaitOne(cfg.IntervalSec * 1000);
                }
            }
        }

        static bool ReadDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return !(v is int && (int)v == 1);
                }
            }
            catch { return true; }
        }

        Cell[] CurrentCells() { return Painter.Cells(last, cfg.Fahrenheit, cfg.FourthMetric, cfg.Download, Palette.For(dark)); }

        void Redraw()
        {
            if (stopping) return;
            Cell[] cells = CurrentCells();
            string spoken = Painter.AccessibleText(cells);
            if (spoken != AccessibleName) { AccessibleName = spoken; Text = "HW Widget: " + spoken; }
            if (!hiddenForShell)
            {
                using (Bitmap b = Painter.Pill(cells, Palette.For(dark), scale, hover || dragging)) Present(b, pos.X, pos.Y);
                if (!Native.IsWindowVisible(Handle)) ShowQuiet();
            }
            if (card.Shown2) ShowCard();
        }

        // ------------------------------------------------ placement
        void OnTick(object sender, EventArgs e)
        {
            ticks++;
            if (ticks % 4 == 0) Housekeep(); else KeepAboveTaskbar();
            HoverLogic();
        }

        void Housekeep()
        {
            if (ticks % 8 == 0) { bool d = ReadDark(); if (d != dark) { dark = d; Redraw(); } }
            IntPtr tray = Native.FindWindowW("Shell_TrayWnd", null);
            bool hide = tray == IntPtr.Zero || !Native.GetWindowRect(tray, out tb);
            if (!hide)
            {
                Native.MONITORINFO mi = Native.Monitor(tray); mon = mi.rcMonitor; work = mi.rcWork;
                uint dpi = 0; try { dpi = Native.GetDpiForWindow(tray); } catch { }
                float s = dpi > 0 ? dpi / 96f : 1f;
                if (Math.Abs(s - scale) > 0.01f) { scale = s; }
                hide = TaskbarAutoHidden() || ForegroundIsFullscreen(tray);
                if (!hide && !mouseDown)
                {
                    Size sz = Painter.PillSize(CurrentCells().Length, scale);
                    pos = Target(tray, sz);
                }
            }
            if (hide != hiddenForShell)
            {
                hiddenForShell = hide;
                if (hide) { HideQuiet(); card.HideQuiet(); pinned = false; }
            }
            if (!hide)
            {
                Size sz = Painter.PillSize(CurrentCells().Length, scale);
                if (!Native.IsWindowVisible(Handle) || Placed.X != pos.X || Placed.Y != pos.Y || Placed.Width != sz.Width || Placed.Height != sz.Height) Redraw();
                else Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        // Puts the pill back above the taskbar when the taskbar has been raised over it (a taskbar click does that).
        // Only the taskbar counts: menus, flyouts and tooltips that open over the pill must stay on top.
        void KeepAboveTaskbar()
        {
            if (stopping || hiddenForShell || !Shown2) return;
            Native.RECT me;
            if (!Native.GetWindowRect(Handle, out me)) return;
            if (ZOrder.ShellCovers(ZOrder.Above(Handle), me))
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        bool TaskbarAutoHidden()
        {
            Native.APPBARDATA ab = new Native.APPBARDATA(); ab.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
            long state = Native.SHAppBarMessage(4, ref ab).ToInt64();
            if ((state & 1) == 0) return false;
            return tb.Top < mon.Top - 1 || tb.Bottom > mon.Bottom + 1 || tb.Left < mon.Left - 1 || tb.Right > mon.Right + 1;
        }

        bool ForegroundIsFullscreen(IntPtr tray)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == tray || fg == Handle || fg == card.Handle) return false;
            string cls = Native.ClassOf(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            Native.MONITORINFO fm = Native.Monitor(fg);
            if (fm.rcMonitor.Left != mon.Left || fm.rcMonitor.Top != mon.Top) return false;
            return ZOrder.IsFullscreen(r, mon, Native.IsZoomed(fg));
        }

        Point Target(IntPtr tray, Size sz)
        {
            int w = sz.Width, h = sz.Height, gap = (int)Math.Round(6 * scale);
            if (tb.Height > tb.Width)   // vertical taskbar: float in the work area's bottom-right corner
                return new Point(work.Right - w - gap * 2, work.Bottom - h - gap * 2);

            IntPtr notify = Native.FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            Native.RECT nr;
            trayLeft = notify != IntPtr.Zero && Native.GetWindowRect(notify, out nr) && nr.Left > tb.Left ? nr.Left : tb.Right - (int)(300 * scale);
            int y = tb.Top + (tb.Height - h) / 2;
            int maxX = trayLeft - gap - w, minX = tb.Left + gap;
            int x = cfg.OffsetFromRight >= 0 ? tb.Right - (int)Math.Round(cfg.OffsetFromRight * scale) : maxX;
            x = Math.Max(minX, Math.Min(maxX, x));

            // Step left past other taskbar pills (the Claude/Codex usage widgets and the like).
            List<Native.RECT> obs = Obstacles(tray);
            for (int iter = 0; iter < 12; iter++)
            {
                bool moved = false;
                foreach (Native.RECT o in obs)
                    if (x < o.Right + gap && x + w > o.Left - gap) { x = o.Left - gap - w; moved = true; }
                if (!moved) break;
            }
            return new Point(Math.Max(minX, x), y);
        }

        List<Native.RECT> Obstacles(IntPtr tray)
        {
            List<Native.RECT> list = new List<Native.RECT>();
            uint explorerPid; Native.GetWindowThreadProcessId(tray, out explorerPid);
            Native.EnumWindowsProc cb = delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid == myPid || pid == explorerPid) return true;
                if ((Native.GetWindowLongW(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) == 0) return true;
                int cloaked; if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r) || r.Width <= 0 || r.Height <= 0) return true;
                if (r.Width > 700 * scale || r.Height > tb.Height + 2) return true;
                if (r.Top < tb.Top - 2 || r.Bottom > tb.Bottom + 2 || r.Right <= tb.Left || r.Left >= tb.Right) return true;
                list.Add(r);
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return list;
        }

        // ------------------------------------------------ hover card, pinning, dragging
        void HoverLogic()
        {
            if (hiddenForShell || mouseDown) return;
            Point c = Cursor.Position;
            bool inPill = Shown2 && Placed.Contains(c);
            bool inCard = card.Shown2 && card.Placed.Contains(c);
            if (inPill != hover) { hover = inPill; Redraw(); }
            if (inPill || inCard)
            {
                leaveTicks = 0;
                if (inPill && ++hoverTicks >= 2 && !card.Shown2) ShowCard();
            }
            else
            {
                hoverTicks = 0;
                if (card.Shown2 && !pinned && ++leaveTicks >= 2) card.HideQuiet();
            }
        }

        void ShowCard()
        {
            if (hiddenForShell) return;
            using (Bitmap b = Painter.Card(last, cfg.Fahrenheit, Palette.For(dark), scale, pinned))
            {
                int m = (int)Math.Round(Painter.CardMargin * scale), gap = (int)Math.Round(6 * scale);
                int x = Placed.X + Placed.Width / 2 - b.Width / 2;
                x = Math.Max(work.Left - m + gap, Math.Min(work.Right - b.Width + m - gap, x));
                bool bottomBar = tb.Top > mon.Top + (mon.Bottom - mon.Top) / 2;
                int y;
                if (tb.Height > tb.Width) y = Placed.Y - b.Height + m - gap;
                else y = bottomBar ? tb.Top - b.Height + m - gap : tb.Bottom - m + gap;
                card.Present(b, x, y);
            }
            if (!card.Shown2) card.ShowQuiet();
        }

        void PillMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true; dragging = false;
            dragStartX = Cursor.Position.X; dragOrigX = Placed.X;
        }

        void PillMouseMove(object sender, MouseEventArgs e)
        {
            if (!mouseDown) return;
            int dx = Cursor.Position.X - dragStartX;
            if (!dragging && ZOrder.DragStarts(dx, scale, tb.Height > tb.Width)) { dragging = true; card.HideQuiet(); pinned = false; }
            if (!dragging || tb.Height > tb.Width) return;
            int gap = (int)Math.Round(6 * scale);
            int x = Math.Max(tb.Left + gap, Math.Min(trayLeft - gap - Placed.Width, dragOrigX + dx));
            pos = new Point(x, Placed.Y);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, Placed.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Placed = new Rectangle(x, Placed.Y, Placed.Width, Placed.Height);
        }

        void PillMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { ShowMenu(); return; }
            if (e.Button != MouseButtons.Left || !mouseDown) return;
            mouseDown = false;
            if (dragging)
            {
                dragging = false;
                cfg.OffsetFromRight = (int)Math.Round((tb.Right - Placed.X) / scale);
                cfg.Save();
                Redraw();
                return;
            }
            pinned = !pinned;
            if (pinned) ShowCard(); else card.HideQuiet();
        }

        void CardMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { ShowMenu(); return; }
            if (e.Button == MouseButtons.Left) { pinned = !pinned; ShowCard(); }
        }

        // ------------------------------------------------ menu
        ToolStripMenuItem Item(string text, bool check, EventHandler click)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text, null, click);
            it.Checked = check;
            if (dark) it.ForeColor = Color.White;
            return it;
        }

        ToolStripMenuItem Sub(string text, params ToolStripItem[] items)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            if (dark) { it.ForeColor = Color.White; it.DropDown.BackColor = Palette.Hex("#1E1E2E"); }
            it.DropDownItems.AddRange(items);
            return it;
        }

        void SetInterval(int s) { cfg.IntervalSec = s; cfg.Save(); wake.Set(); }
        void SetUnit(bool f) { cfg.Fahrenheit = f; cfg.Save(); Redraw(); }
        void SetFourth(Fourth f) { cfg.FourthMetric = f; cfg.Save(); Housekeep(); Redraw(); }
        void SetDownload(bool on) { cfg.Download = on; cfg.Save(); Housekeep(); Redraw(); }

        void ShowMenu()
        {
            if (menu != null) menu.Dispose();
            menu = new ContextMenuStrip();
            menu.ShowImageMargin = true;
            if (dark) { menu.Renderer = new DarkMenuRenderer(); menu.BackColor = Palette.Hex("#1E1E2E"); menu.ForeColor = Color.White; }
            ToolStripMenuItem head = Item("HW Widget", false, null); head.Enabled = false;
            menu.Items.Add(head);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Sub("Refresh every",
                Item("1 second", cfg.IntervalSec == 1, delegate { SetInterval(1); }),
                Item("2 seconds", cfg.IntervalSec == 2, delegate { SetInterval(2); }),
                Item("5 seconds", cfg.IntervalSec == 5, delegate { SetInterval(5); })));
            menu.Items.Add(Sub("Temperature unit",
                Item("Celsius (°C)", !cfg.Fahrenheit, delegate { SetUnit(false); }),
                Item("Fahrenheit (°F)", cfg.Fahrenheit, delegate { SetUnit(true); })));
            menu.Items.Add(Sub("Fourth metric",
                Item("Automatic (GPU, else disk)", cfg.FourthMetric == Fourth.Auto, delegate { SetFourth(Fourth.Auto); }),
                Item("GPU", cfg.FourthMetric == Fourth.Gpu, delegate { SetFourth(Fourth.Gpu); }),
                Item("Disk", cfg.FourthMetric == Fourth.Disk, delegate { SetFourth(Fourth.Disk); })));
            bool down = cfg.Download;
            menu.Items.Add(Item("Show download speed", down, delegate { SetDownload(!down); }));
            menu.Items.Add(new ToolStripSeparator());
            bool helper = HelperSetup.Installed;
            menu.Items.Add(Item(helper ? "Remove CPU temperature helper (admin)" : "Enable CPU temperature (admin)", false, delegate { ToggleHelper(!helper); }));
            bool auto = Settings.StartsWithWindows;
            menu.Items.Add(Item("Start with Windows", auto, delegate { Settings.StartsWithWindows = !auto; }));
            menu.Items.Add(Item("Reset position", false, delegate { cfg.OffsetFromRight = -1; cfg.Save(); Housekeep(); Redraw(); }));
            menu.Items.Add(Item("Open Task Manager", false, delegate { try { Process.Start("taskmgr.exe"); } catch { } }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Exit", false, delegate { Quit(); }));
            if (dark) foreach (ToolStripItem it in menu.Items) if (it is ToolStripSeparator) it.ForeColor = Palette.Hex("#3A3A55");
            menu.Show(Cursor.Position);
        }

        // 1 UAC prompt; runs off the UI thread so the pill keeps drawing while Windows asks.
        void ToggleHelper(bool install)
        {
            Thread t = new Thread(delegate()
            {
                bool ok = HelperSetup.RunElevated(install ? "--install-helper" : "--uninstall-helper");
                if (!ok && install)
                    try { BeginInvoke((MethodInvoker)delegate { MessageBox.Show("The CPU temperature helper was not installed: the admin prompt was declined or failed.\n\nTo try again, right-click the pill and choose Enable CPU temperature (admin), then click Yes in the Windows prompt.", "HW Widget", MessageBoxButtons.OK, MessageBoxIcon.Information); }); } catch { }
                wake.Set();
            });
            t.IsBackground = true; t.Start();
        }

        void Quit()
        {
            stopping = true; wake.Set(); tick.Stop();
            if (fgHook != IntPtr.Zero) { Native.UnhookWinEvent(fgHook); fgHook = IntPtr.Zero; }
            try { if (worker != null) worker.Join(2000); } catch { }
            card.HideQuiet(); HideQuiet();
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            stopping = true; wake.Set();
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            try
            {
                if (args.Length >= 2 && args[0] == "--render") return TestModes.Render(args[1]);
                if (args.Length >= 3 && args[0] == "--probe") return TestModes.Probe(int.Parse(args[1], CultureInfo.InvariantCulture), args[2]);
                if (args.Length >= 2 && args[0] == "--selftest") return TestModes.SelfTest(args[1]);
                if (args.Length >= 1 && args[0] == "--sensor-helper") return SensorHelper.Run();
                if (args.Length >= 1 && args[0] == "--install-helper") return HelperSetup.Install();
                if (args.Length >= 1 && args[0] == "--uninstall-helper") return HelperSetup.Uninstall();
                if (args.Length >= 1 && args[0] == "--install") return SelfInstall.Install(InstallArgs.Parse(args, 1));
                if (args.Length >= 1 && args[0] == "--uninstall") return SelfInstall.Uninstall(InstallArgs.Parse(args, 1));
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "hwwidget-error.txt"), ex.ToString()); } catch { }
                return 2;
            }

            bool created;
            using (Mutex single = new Mutex(true, @"Local\HWWidget.Single", out created))
            {
                if (!created) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log(e.Exception); };
                Application.Run(new WidgetForm());
            }
            return 0;
        }

        static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                AppendCapped(Path.Combine(Settings.Dir, "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + ex + "\r\n", LogCap);
            }
            catch { }
        }

        public const long LogCap = 256 * 1024;

        // Appends a line to the log; once the log passes the cap, it keeps only its newer half, so an error that
        // repeats every tick can't fill the disk.
        public static void AppendCapped(string path, string text, long cap)
        {
            FileInfo fi = new FileInfo(path);
            if (fi.Exists && fi.Length + Encoding.UTF8.GetByteCount(text) > cap)
            {
                string old = File.ReadAllText(path);
                int keep = (int)Math.Min(old.Length, cap / 2);
                string tail = old.Substring(old.Length - keep);
                int nl = tail.IndexOf('\n');
                File.WriteAllText(path, nl >= 0 ? tail.Substring(nl + 1) : "");
            }
            File.AppendAllText(path, text);
        }
    }
}
