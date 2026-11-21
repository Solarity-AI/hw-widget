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
    }
}
