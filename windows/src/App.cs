// HW Widget for Windows - the taskbar window, hover card, menu, settings and test modes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HwWidget
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width { get { return Right - Left; } } public int Height { get { return Bottom - Top; } } }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int CX, CY; public SIZE(int x, int y) { CX = x; CY = y; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        public const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, GWL_EXSTYLE = -20;

        public static string ClassOf(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }

        public static MONITORINFO Monitor(IntPtr hwnd)
        {
            MONITORINFO mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref mi);
            return mi;
        }
    }

    // Window drawn from a 32-bit bitmap with per-pixel alpha: rounded, anti-aliased, never takes focus.
    internal class LayeredForm : Form
    {
        public Rectangle Placed;
        public LayeredForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Text = "HW Widget";
        }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = new IntPtr(Native.MA_NOACTIVATE); return; }
            base.WndProc(ref m);
        }

        public void Present(Bitmap bmp, int x, int y)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen);
            IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0)), old = Native.SelectObject(mem, hb);
            try
            {
                Native.SIZE sz = new Native.SIZE(bmp.Width, bmp.Height);
                Native.POINT src = new Native.POINT(0, 0), dst = new Native.POINT(x, y);
                Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION(); bf.SourceConstantAlpha = 255; bf.AlphaFormat = 1;
                Native.UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref bf, 2);
                Placed = new Rectangle(x, y, bmp.Width, bmp.Height);
            }
            finally
            {
                Native.SelectObject(mem, old); Native.DeleteObject(hb); Native.DeleteDC(mem); Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public bool Shown2;
        public void ShowQuiet()
        {
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Shown2 = true;
        }
        public void HideQuiet() { Native.ShowWindow(Handle, Native.SW_HIDE); Shown2 = false; }
    }

    public sealed class Settings
    {
        public int IntervalSec = 2;
        public bool Fahrenheit;
        public Fourth FourthMetric = Fourth.Auto;
        public int OffsetFromRight = -1;          // DIP from the taskbar's right edge; -1 = automatic

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HWWidget"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            Settings s = new Settings();
            // Celsius by default everywhere; Fahrenheit only when chosen in the menu.
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                    int n;
                    if (k == "interval" && int.TryParse(v, out n) && (n == 1 || n == 2 || n == 5)) s.IntervalSec = n;
                    else if (k == "unit") s.Fahrenheit = v.ToUpperInvariant() == "F";
                    else if (k == "fourth") s.FourthMetric = v == "gpu" ? Fourth.Gpu : v == "disk" ? Fourth.Disk : Fourth.Auto;
                    else if (k == "offset" && int.TryParse(v, out n)) s.OffsetFromRight = n;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, string.Format(CultureInfo.InvariantCulture,
                    "interval={0}\r\nunit={1}\r\nfourth={2}\r\noffset={3}\r\n",
                    IntervalSec, Fahrenheit ? "F" : "C", FourthMetric.ToString().ToLowerInvariant(), OffsetFromRight));
            }
            catch { }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool StartsWithWindows
        {
            get { try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("HWWidget") != null; } catch { return false; } }
            set
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue("HWWidget", "\"" + Application.ExecutablePath + "\"");
                        else k.DeleteValue("HWWidget", false);
                    }
                }
                catch { }
            }
        }
    }

    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        static readonly Color Bg = Palette.Hex("#1E1E2E"), Hi = Palette.Hex("#34345A"), Line = Palette.Hex("#3A3A55");
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color MenuBorder { get { return Line; } }
        public override Color MenuItemBorder { get { return Hi; } }
        public override Color MenuItemSelected { get { return Hi; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
        public override Color MenuItemPressedGradientBegin { get { return Hi; } }
        public override Color MenuItemPressedGradientEnd { get { return Hi; } }
        public override Color SeparatorDark { get { return Line; } }
        public override Color SeparatorLight { get { return Line; } }
        public override Color CheckBackground { get { return Palette.Hex("#4C4C80"); } }
        public override Color CheckSelectedBackground { get { return Palette.Hex("#5B5B99"); } }
        public override Color CheckPressedBackground { get { return Palette.Hex("#5B5B99"); } }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Color.White; base.OnRenderArrow(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Palette.Hex("#4C4C80"))) e.Graphics.FillRectangle(b, r);
            using (Pen p = new Pen(Color.White, Math.Max(1.5f, r.Height / 9f)))
                e.Graphics.DrawLines(p, new PointF[] { new PointF(r.Left + r.Width * 0.25f, r.Top + r.Height * 0.52f), new PointF(r.Left + r.Width * 0.43f, r.Top + r.Height * 0.70f), new PointF(r.Left + r.Width * 0.76f, r.Top + r.Height * 0.32f) });
        }
    }

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

        Cell[] CurrentCells() { return Painter.Cells(last, cfg.Fahrenheit, cfg.FourthMetric, Palette.For(dark)); }

        void Redraw()
        {
            if (stopping) return;
            Cell[] cells = CurrentCells();
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
            if (ticks % 4 == 0) Housekeep();
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
            return r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom;
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
            if (!dragging && Math.Abs(dx) > 4 * scale) { dragging = true; card.HideQuiet(); pinned = false; }
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
            menu.Items.Add(new ToolStripSeparator());
            bool auto = Settings.StartsWithWindows;
            menu.Items.Add(Item("Start with Windows", auto, delegate { Settings.StartsWithWindows = !auto; }));
            menu.Items.Add(Item("Reset position", false, delegate { cfg.OffsetFromRight = -1; cfg.Save(); Housekeep(); Redraw(); }));
            menu.Items.Add(Item("Open Task Manager", false, delegate { try { Process.Start("taskmgr.exe"); } catch { } }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Exit", false, delegate { Quit(); }));
            if (dark) foreach (ToolStripItem it in menu.Items) if (it is ToolStripSeparator) it.ForeColor = Palette.Hex("#3A3A55");
            menu.Show(Cursor.Position);
        }

        void Quit()
        {
            stopping = true; wake.Set(); tick.Stop();
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
                        using (Bitmap pill = Painter.Pill(Painter.Cells(s, false, Fourth.Auto, p), p, sc, false))
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
                    foreach (GpuInfo g in s.Gpus) gp.Add(g.Name + " " + D(g.Percent) + "% " + D(g.TempC) + "C " + D(g.MemUsedGB) + "GB");
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

            HwSnapshot noGpu = new HwSnapshot(); noGpu.CpuPercent = 10; noGpu.DiskPercent = 5;
            Cell[] c = Painter.Cells(noGpu, false, Fourth.Auto, Palette.For(true));
            check("cells: auto shows DISK without a GPU", c.Length == 4 && c[3].Label == "DISK");
            HwSnapshot gpuOnlyTemp = Fake("normal"); gpuOnlyTemp.CpuTempC = double.NaN;
            c = Painter.Cells(gpuOnlyTemp, false, Fourth.Auto, Palette.For(true));
            check("cells: GPU temperature fallback is labelled", c[0].Label == "GPU°" && c[3].Label == "GPU");
            check("cells: hot CPU is critical", Painter.Cells(Fake("hot"), false, Fourth.Auto, Palette.For(true))[1].Level == 2);

            Size ps = Painter.PillSize(4, 1.25f);
            check("pill: 192x34 DIP -> 240x43 px at 125%", ps.Width == 240 && ps.Height == 43);

            lines.Add(string.Format("{0} passed, {1} failed", pass, fail));
            File.WriteAllLines(outFile, lines.ToArray());
            return fail == 0 ? 0 : 1;
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
                File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + ex + "\r\n");
            }
            catch { }
        }
    }
}
