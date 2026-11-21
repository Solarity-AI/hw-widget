// HW Widget for Windows - install and uninstall without PowerShell.
// "Install HW Widget (Windows).cmd" compiles the widget straight into %LOCALAPPDATA%\HWWidget and runs it with
// --install: the exe self-tests, adds a shortcut to the user's Startup folder and starts. Nothing is copied,
// no registry Run value is written and nothing elevated runs unless --cpu-temp asks for the helper.
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x).
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HwWidget
{
    internal sealed class InstallArgs
    {
        public bool CpuTemp, NoLaunch, KeepHelper;
        public string StartupDir;   // test seam: a scratch folder instead of the user's Startup folder

        public static InstallArgs Parse(string[] args, int from)
        {
            InstallArgs a = new InstallArgs();
            for (int i = from; i < args.Length; i++)
            {
                string s = args[i].ToLowerInvariant();
                if (s == "--cpu-temp") a.CpuTemp = true;
                else if (s == "--no-launch") a.NoLaunch = true;
                else if (s == "--keep-helper") a.KeepHelper = true;
                else if (s == "--startup" && i + 1 < args.Length) a.StartupDir = args[++i];
            }
            return a;
        }
    }

    internal static class Startup
    {
        public const string LinkName = "HW Widget.lnk";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string LinkPath(string dir)
        {
            return Path.Combine(dir ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup), LinkName);
        }

        public static bool Enabled { get { return File.Exists(LinkPath(null)) || RunValueExists(); } }

        // On: a Startup-folder shortcut to this exe. Both ways the old HKCU Run value (installs before v1.2) goes.
        public static bool Set(bool on, string dir)
        {
            bool ok = true;
            try
            {
                if (on) ShellLink.Create(LinkPath(dir), Application.ExecutablePath, Path.GetDirectoryName(Application.ExecutablePath));
                else if (File.Exists(LinkPath(dir))) File.Delete(LinkPath(dir));
            }
            catch { ok = false; }
            if (dir == null) RemoveRunValue();
            return ok;
        }

        static bool RunValueExists()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("HWWidget") != null; }
            catch { return false; }
        }

        static void RemoveRunValue()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue("HWWidget", false); }
            catch { }
        }
    }

    internal static class SelfInstall
    {
        // Exit codes, read by the .cmd scripts.
        public const int Ok = 0, SelfTestFailed = 3, StartupFailed = 4, HelperLeft = 5, HelperDeclined = 7;

        // The gate passes only a report whose last line is "N passed, 0 failed".
        public static bool GatePasses(string lastLine)
        {
            return lastLine != null && Regex.IsMatch(lastLine, @"^\d+ passed, 0 failed$");
        }

        public static int Install(InstallArgs a)
        {
            string exe = Application.ExecutablePath;
            string report = Path.Combine(Path.GetDirectoryName(exe), "selftest.txt");
            try { File.Delete(report); } catch { }   // an old passing report must never pass a new build
            TestModes.SelfTest(report);
            string last = null;
            try { string[] lines = File.ReadAllLines(report); if (lines.Length > 0) last = lines[lines.Length - 1]; } catch { }
            if (!GatePasses(last)) return SelfTestFailed;

            if (!Startup.Set(true, a.StartupDir)) return StartupFailed;
            if (!a.NoLaunch) try { Process.Start(exe); } catch { }
            if (a.CpuTemp && !HelperSetup.RunElevated("--install-helper")) return HelperDeclined;
            return Ok;
        }

        public static int Uninstall(InstallArgs a)
        {
            Startup.Set(false, a.StartupDir);
            if (a.KeepHelper) return Ok;
            if (HelperSetup.Installed || Directory.Exists(HelperFile.Dir)) HelperSetup.RunElevated("--uninstall-helper");
            return HelperSetup.Installed ? HelperLeft : Ok;
        }
    }

    // Writes and reads .lnk files through the shell's own ShellLink object (no scripting host).
    internal static class ShellLink
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        public static void Create(string lnk, string target, string workDir)
        {
            IShellLinkW link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(workDir);
                link.SetDescription("HW Widget: CPU, memory, disk and GPU on the taskbar");
                Directory.CreateDirectory(Path.GetDirectoryName(lnk));
                ((IPersistFile)link).Save(lnk, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
    }
}
