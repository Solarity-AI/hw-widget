// HW Widget for Windows - z-order checks: is the pill covered, is a full-screen app in front.
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
    // Which windows sit above the pill in the z-order, and whether the taskbar is one of those covering it.
    internal static class ZOrder
    {
        // A press turns into a drag once the cursor moves more than 4 DIP sideways. On a vertical taskbar the pill
        // can't move, so a shaky click stays a click (it pins the card) and no offset gets saved.
        public static bool DragStarts(int dx, float scale, bool verticalBar)
        {
            return !verticalBar && Math.Abs(dx) > 4 * scale;
        }

        // A window in front counts as full screen when it covers the whole monitor and isn't merely maximized:
        // a maximized window overhangs the monitor by its border on every side once the taskbar auto-hides.
        public static bool IsFullscreen(Native.RECT win, Native.RECT mon, bool zoomed)
        {
            if (zoomed) return false;
            return win.Left <= mon.Left && win.Top <= mon.Top && win.Right >= mon.Right && win.Bottom >= mon.Bottom;
        }

        public static List<KeyValuePair<string, Native.RECT>> Above(IntPtr hwnd)
        {
            List<KeyValuePair<string, Native.RECT>> list = new List<KeyValuePair<string, Native.RECT>>();
            int seen = 0;
            for (IntPtr h = Native.GetWindow(hwnd, Native.GW_HWNDPREV); h != IntPtr.Zero && ++seen < 512; h = Native.GetWindow(h, Native.GW_HWNDPREV))
            {
                if (!Native.IsWindowVisible(h)) continue;
                Native.RECT r;
                if (Native.GetWindowRect(h, out r)) list.Add(new KeyValuePair<string, Native.RECT>(Native.ClassOf(h), r));
            }
            return list;
        }

        public static bool ShellCovers(List<KeyValuePair<string, Native.RECT>> above, Native.RECT me)
        {
            foreach (KeyValuePair<string, Native.RECT> w in above)
            {
                if (w.Key != "Shell_TrayWnd" && w.Key != "Shell_SecondaryTrayWnd") continue;
                Native.RECT r = w.Value;
                if (r.Left < me.Right && r.Right > me.Left && r.Top < me.Bottom && r.Bottom > me.Top) return true;
            }
            return false;
        }

        // Shell surfaces that cover the whole monitor for a moment (Start, Search, Task View, Alt+Tab, Quick Settings,
        // the notification center, taskbar thumbnails) but are not full-screen apps: the pill stays visible. The same
        // lists as the PowerShell usage widget, plus Quick Settings' ControlCenterWindow.
        static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "MultitaskingViewFrame", "XamlExplorerHostIslandWindow", "TaskListThumbnailWnd", "ForegroundStaging",
            "Windows.UI.Input.InputSite.WindowClass", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
            "ControlCenterWindow", SolarityWidgets.Strip.ClassName };
        static readonly string[] ShellProcesses = { "explorer", "StartMenuExperienceHost", "SearchHost", "SearchApp",
            "ShellExperienceHost", "ShellHost", "TextInputHost" };

        public static bool IsShellClass(string cls) { return Array.IndexOf(ShellClasses, cls) >= 0; }

        public static bool IsShellProcess(string process)
        {
            foreach (string p in ShellProcesses) if (string.Equals(p, process, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Where the pill goes on a horizontal bar (the taskbar or the widget strip): right-aligned before rightLimit
        // (the notification area on the taskbar, the bar's own right edge in the strip), or at the saved offset,
        // centred vertically, then stepped left past other pills so none overlap.
        public static Point PillPoint(Native.RECT bar, int rightLimit, Size sz, float scale, int offsetFromRight, List<Native.RECT> obstacles)
        {
            int w = sz.Width, h = sz.Height, gap = (int)Math.Round(6 * scale);
            int y = bar.Top + (bar.Height - h) / 2;
            int maxX = rightLimit - gap - w, minX = bar.Left + gap;
            int x = offsetFromRight >= 0 ? bar.Right - (int)Math.Round(offsetFromRight * scale) : maxX;
            x = Math.Max(minX, Math.Min(maxX, x));
            for (int iter = 0; iter < 12; iter++)
            {
                bool moved = false;
                foreach (Native.RECT o in obstacles)
                    if (x < o.Right + gap && x + w > o.Left - gap) { x = o.Left - gap - w; moved = true; }
                if (!moved) break;
            }
            return new Point(Math.Max(minX, x), y);
        }

        // The hover card's top on a horizontal bar: above a bar in the lower half of the monitor, below one at the top.
        public static int CardY(Native.RECT bar, Native.RECT mon, int cardHeight, int margin, int gap)
        {
            bool bottomBar = bar.Top > mon.Top + (mon.Bottom - mon.Top) / 2;
            return bottomBar ? bar.Top - cardHeight + margin - gap : bar.Bottom - margin + gap;
        }
    }
}
