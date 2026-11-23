// WidgetStrip.cs: a shared strip across the top of the primary monitor that the Solarity taskbar widgets sit on
// instead of the taskbar. Windows reserves it like a second taskbar (an AppBar), so maximized windows stop below it
// and the taskbar, which Quick Settings lifts above every always-on-top window, can no longer cover the pills.
//
// The same file ships in every widget repo (hw-widget, machines-widget, ai-sync-widget, ai-usage-taskbar-widgets).
// Keep the copies identical; bump Version when you change it.
//
// Contract for the widgets:
//   * Strip.Find() returns the strip window (class "SolarityWidgetStrip") or IntPtr.Zero. When it exists, the widget
//     places its pill inside the strip's window rect exactly as it would inside the taskbar rect, stacking right to
//     left past the other pills; when it does not, the widget falls back to the taskbar.
//   * Strip.Ensure() is called from the widget's UI thread on its housekeeping timer. The first widget to call it
//     creates the strip and keeps it for its lifetime (a named mutex makes sure only one does); when that widget
//     exits, the next Ensure() in another widget creates it again.
//   * Strip.Close() on exit gives the reserved space back.
//   * HKCU\Software\Solarity\WidgetStrip  Enabled (DWORD) = 0 turns the strip off: no widget creates it, the
//     current host removes it, and every pill goes back to the taskbar.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace SolarityWidgets
{
    public static class Strip
    {
        public const int Version = 1;
        public const string ClassName = "SolarityWidgetStrip";
        public const int HeightDip = 40;
        const string MutexName = "Local\\SolarityWidgetStrip.Host";
        const string SettingsKey = "Software\\Solarity\\WidgetStrip";
        const int WM_APP_BAR = 0x8000 + 0x2F1;   // WM_APP + n: our AppBar callback

        static IntPtr hwnd = IntPtr.Zero;
        static Mutex mutex;
        static WndProcDelegate proc;             // kept alive: the window procedure must never be collected
        static uint taskbarCreated;
        static bool registered, fullscreen;
        static RECT lastSet;                     // what ABM_SETPOS last got: never re-send the same (it broadcasts a work-area change)

        public static bool Hosting { get { return hwnd != IntPtr.Zero; } }

        public static IntPtr Find()
        {
            if (!Enabled()) return IntPtr.Zero;
            IntPtr h = FindWindowW(ClassName, null);
            return h != IntPtr.Zero && IsWindowVisible(h) ? h : IntPtr.Zero;
        }

        public static bool Enabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    object v = k == null ? null : k.GetValue("Enabled");
                    return !(v is int) || (int)v != 0;
                }
            }
            catch { return true; }
        }

        // Creates the strip when no widget has one yet; returns the strip window or IntPtr.Zero.
        public static IntPtr Ensure()
        {
            try
            {
                if (!Enabled()) { Close(); return IntPtr.Zero; }
                if (hwnd != IntPtr.Zero) return fullscreen ? IntPtr.Zero : hwnd;
                IntPtr other = FindWindowW(ClassName, null);
                if (other != IntPtr.Zero) return IsWindowVisible(other) ? other : IntPtr.Zero;
                if (mutex == null) mutex = new Mutex(false, MutexName);
                bool owned;
                try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) return IntPtr.Zero;   // another widget is creating it right now
                Create();
                return hwnd;
            }
            catch { return IntPtr.Zero; }
        }

        public static void Close()
        {
            if (hwnd != IntPtr.Zero)
            {
                Unregister();
                DestroyWindow(hwnd);
                hwnd = IntPtr.Zero;
            }
            if (mutex != null)
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose(); mutex = null;
            }
        }

        // ------------------------------------------------------------------ window
        static void Create()
        {
            IntPtr inst = GetModuleHandleW(null);
            proc = WndProc;
            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc);
            wc.hInstance = inst;
            wc.hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512));
            wc.lpszClassName = ClassName;
            RegisterClassExW(ref wc);   // fails harmlessly when this process registered it before
            taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            // A tool window that never takes focus and is not always-on-top: the pills (always-on-top) sit above it.
            hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "Widget strip", WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Close(); return; }
            Register();
            Place();
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        }

        static void Register()
        {
            APPBARDATA ab = Bar();
            ab.uCallbackMessage = WM_APP_BAR;
            registered = SHAppBarMessage(ABM_NEW, ref ab) != IntPtr.Zero;
            lastSet = new RECT();
        }

        static void Unregister()
        {
            if (!registered) return;
            APPBARDATA ab = Bar();
            SHAppBarMessage(ABM_REMOVE, ref ab);
            registered = false;
        }

        static APPBARDATA Bar()
        {
            APPBARDATA ab = new APPBARDATA();
            ab.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            ab.hWnd = hwnd;
            ab.uEdge = ABE_TOP;
            return ab;
        }

        // The rect the strip wants: the top edge of the primary monitor, HeightDip tall at that monitor's scale.
        public static RECT Wanted(RECT monitor, uint dpi)
        {
            int h = (int)Math.Round(HeightDip * (dpi == 0 ? 96 : dpi) / 96.0);
            RECT r = monitor;
            r.Bottom = r.Top + h;
            return r;
        }

        static void Place()
        {
            if (hwnd == IntPtr.Zero || !registered) return;
            IntPtr mon = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
            MONITORINFO mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfoW(mon, ref mi)) return;
            uint dx = 96, dy = 96;
            try { GetDpiForMonitor(mon, 0, out dx, out dy); } catch { }
            RECT want = Wanted(mi.rcMonitor, dy);
            int h = want.Bottom - want.Top;
            if (Same(want, lastSet)) return;     // nothing changed: a settings broadcast we caused ourselves
            lastSet = want;
            APPBARDATA ab = Bar();
            ab.rc = want;
            SHAppBarMessage(ABM_QUERYPOS, ref ab);   // the shell may move the top down (another top bar)
            ab.rc.Bottom = ab.rc.Top + h;
            SHAppBarMessage(ABM_SETPOS, ref ab);
            RECT cur;
            if (GetWindowRect(hwnd, out cur) && Same(cur, ab.rc)) return;
            SetWindowPos(hwnd, HWND_BOTTOM, ab.rc.Left, ab.rc.Top, ab.rc.Right - ab.rc.Left, h, SWP_NOACTIVATE);
        }

        static bool Same(RECT a, RECT b) { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }

        static IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            try
            {
                if (msg == WM_APP_BAR)
                {
                    int code = w.ToInt32();
                    if (code == ABN_POSCHANGED) { lastSet = new RECT(); Place(); }
                    else if (code == ABN_FULLSCREENAPP)
                    {
                        fullscreen = l != IntPtr.Zero;   // a full-screen app: get out of its way
                        ShowWindow(h, fullscreen ? SW_HIDE : SW_SHOWNOACTIVATE);
                    }
                    return IntPtr.Zero;
                }
                if (taskbarCreated != 0 && msg == taskbarCreated) { registered = false; Register(); Place(); return IntPtr.Zero; }   // Explorer restarted
                switch (msg)
                {
                    case WM_DISPLAYCHANGE: case WM_DPICHANGED:
                        lastSet = new RECT(); Place(); InvalidateRect(h, IntPtr.Zero, true); break;
                    case WM_SETTINGCHANGE:   // the light/dark switch; work-area broadcasts land here too, so no re-placing
                        InvalidateRect(h, IntPtr.Zero, true); break;
                    case WM_MOUSEACTIVATE: return new IntPtr(MA_NOACTIVATE);
                    case WM_ERASEBKGND:
                        {
                            RECT rc; GetClientRect(h, out rc);
                            IntPtr br = CreateSolidBrush(Dark() ? 0x00202020u : 0x00F3F3F3u);
                            FillRect(w, ref rc, br); DeleteObject(br);
                            return new IntPtr(1);
                        }
                    case WM_CLOSE: return IntPtr.Zero;   // only Close() removes the strip
                }
            }
            catch { }
            return DefWindowProcW(h, msg, w, l);
        }

        static bool Dark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return !(v is int) || (int)v == 0;
                }
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------ Win32
        delegate IntPtr WndProcDelegate(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
        }
        const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABE_TOP = 1;
        const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
        const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, MA_NOACTIVATE = 3;
        const uint SWP_NOACTIVATE = 0x10, MONITOR_DEFAULTTOPRIMARY = 1;
        const uint WM_CLOSE = 0x10, WM_ERASEBKGND = 0x14, WM_SETTINGCHANGE = 0x1A, WM_MOUSEACTIVATE = 0x21, WM_DISPLAYCHANGE = 0x7E, WM_DPICHANGED = 0x2E0;
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string t);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string s);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
        [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref RECT r, IntPtr br);
        [DllImport("user32.dll")] static extern IntPtr LoadCursorW(IntPtr inst, IntPtr id);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint f);
        [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr m, ref MONITORINFO mi);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr m, int type, out uint dx, out uint dy);
        [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    }
}
