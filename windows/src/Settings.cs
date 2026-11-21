// HW Widget for Windows - the saved settings and the Start with Windows shortcut.
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
    public sealed class Settings
    {
        public int IntervalSec = 2;
        public bool Fahrenheit;
        public Fourth FourthMetric = Fourth.Auto;
        public int OffsetFromRight = -1;          // DIP from the taskbar's right edge; -1 = automatic
        public bool Download = true;              // the live download-rate cell

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
                    else if (k == "download") s.Download = v.ToLowerInvariant() != "off";
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
                    "interval={0}\r\nunit={1}\r\nfourth={2}\r\noffset={3}\r\ndownload={4}\r\n",
                    IntervalSec, Fahrenheit ? "F" : "C", FourthMetric.ToString().ToLowerInvariant(), OffsetFromRight, Download ? "on" : "off"));
            }
            catch { }
        }

        // A shortcut in the user's Startup folder (Install.cs); installs before v1.2 used the HKCU Run key.
        public static bool StartsWithWindows
        {
            get { return Startup.Enabled; }
            set { Startup.Set(value, null); }
        }
    }
}
