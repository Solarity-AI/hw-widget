// HW Widget for Windows - opt-in CPU temperature helper.
// Some laptops (for example Intel Lunar Lake) expose no CPU temperature to normal users: the only ACPI zone is a
// placeholder and the real sensors (Intel Dynamic Tuning / ESIF in root\wmi) are admin-only. After 1 UAC prompt
// this installs a tiny copy of the widget as a SYSTEM scheduled task that reads those sensors and writes the value
// to an admin-only folder that the widget reads without admin rights.
// Safety: the elevated binary lives in %ProgramFiles% (admin-only), never in a user-writable folder, and it only
// reads WMI and writes 1 small file in %ProgramData%\HWWidget, whose ACL lets users read but not write.
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace HwWidget
{
    // ------------------------------------------------------------------ pure parts (unit-tested)
    public static class EsifPicker
    {
        // ESIF reports deg C on current drivers; older ones use Kelvin or tenths of Kelvin.
        public static double ToCelsius(double raw)
        {
            if (double.IsNaN(raw) || raw <= 0) return double.NaN;
            double c = raw > 1000 ? raw / 10.0 - 273.15 : raw > 200 ? raw - 273.15 : raw;
            return c >= 10 && c <= 115 ? Math.Round(c, 1) : double.NaN;
        }

        // The hottest live participant is the processor die on every DTT platform we know (TCPU/IA/GT/SoC sit on it).
        public static double Pick(IEnumerable<double> rawTemps)
        {
            double best = double.NaN;
            foreach (double raw in rawTemps)
            {
                double c = ToCelsius(raw);
                if (Math.Abs(c - 27.85) < 0.06) continue;      // ACPI placeholder leaking through
                if (!double.IsNaN(c) && (double.IsNaN(best) || c > best)) best = c;
            }
            return best;
        }
    }

    public static class HelperFile
    {
        public const int MaxAgeSec = 15;

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HWWidget"); } }
        public static string FilePath { get { return Path.Combine(Dir, "cpu-temp.txt"); } }

        public static long UnixMs(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        public static string Format(long unixMs, double tempC, string source)
        {
            return string.Format(CultureInfo.InvariantCulture, "1|{0}|{1:0.0}|{2}", unixMs, tempC, (source ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " "));
        }

        // Returns NaN unless the line is well-formed, fresh and plausible.
        public static double Parse(string text, long nowUnixMs, out string source)
        {
            source = "";
            if (string.IsNullOrEmpty(text)) return double.NaN;
            string[] p = text.Trim().Split(new char[] { '|' }, 4);
            long at; double t;
            if (p.Length < 3 || p[0] != "1") return double.NaN;
            if (!long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out at)) return double.NaN;
            if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out t)) return double.NaN;
            long age = nowUnixMs - at;
            if (age < -5000 || age > MaxAgeSec * 1000L) return double.NaN;
            if (t < 10 || t > 115) return double.NaN;
            source = p.Length > 3 ? p[3] : "";
            return t;
        }

        public static double Read(out string source)
        {
            source = "";
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return double.NaN;
                string text;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8)) text = r.ReadToEnd();
                return Parse(text, UnixMs(DateTime.UtcNow), out source);
            }
            catch { return double.NaN; }
        }
    }

    // ------------------------------------------------------------------ the elevated loop (runs as SYSTEM)
    public static class SensorHelper
    {
        // Single-instance lock: a file in the admin-only data folder that only SYSTEM and Administrators may open, so
        // no other process can take it first (a Global\ named object can be created by any user while we are stopped).
        // Returns null when another copy holds it or the folder is missing.
        public static FileStream TryLock(string dir)
        {
            try
            {
                FileSecurity sec = new FileSecurity();
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                return new FileStream(Path.Combine(dir, "helper.lock"), FileMode.OpenOrCreate, FileSystemRights.ReadData | FileSystemRights.WriteData,
                    FileShare.None, 1, FileOptions.DeleteOnClose, sec);
            }
            catch { return null; }
        }

        public static int Run()
        {
            string dir = HelperFile.Dir, tmp = HelperFile.FilePath + ".tmp";
            using (FileStream single = TryLock(dir))
            {
                if (single == null) return 1;
                while (true)
                {
                    try
                    {
                        string source;
                        double t = ReadEsif(out source);
                        if (double.IsNaN(t)) t = ReadAcpi(out source);
                        if (!double.IsNaN(t) && Directory.Exists(dir))
                        {
                            File.WriteAllText(tmp, HelperFile.Format(HelperFile.UnixMs(DateTime.UtcNow), t, source), new UTF8Encoding(false));
                            if (File.Exists(HelperFile.FilePath)) File.Replace(tmp, HelperFile.FilePath, null);
                            else File.Move(tmp, HelperFile.FilePath);
                        }
                    }
                    catch { }
                    Thread.Sleep(2000);
                }
            }
        }

        static double ReadEsif(out string source)
        {
            source = "Intel Dynamic Tuning (hottest sensor)";
            List<double> temps = new List<double>();
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT Temperature FROM EsifDeviceInformation"))
                using (System.Management.ManagementObjectCollection res = s.Get())
                    foreach (System.Management.ManagementObject o in res)
                        try { temps.Add(Convert.ToDouble(o["Temperature"], CultureInfo.InvariantCulture)); } catch { }
            }
            catch { }
            return EsifPicker.Pick(temps);
        }

        static double ReadAcpi(out string source)
        {
            source = "";
            Dictionary<string, double> zones = new Dictionary<string, double>();
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName,CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                using (System.Management.ManagementObjectCollection res = s.Get())
                    foreach (System.Management.ManagementObject o in res)
                        try { zones[Convert.ToString(o["InstanceName"])] = Convert.ToDouble(o["CurrentTemperature"], CultureInfo.InvariantCulture) / 10.0 - 273.15; } catch { }
            }
            catch { }
            string pick = ZonePicker.Pick(zones, null);
            if (pick == null) return double.NaN;
            source = "ACPI thermal zone " + ZonePicker.Short(pick);
            return Math.Round(zones[pick], 1);
        }
    }

    // ------------------------------------------------------------------ install / remove (needs elevation)
    public static class HelperSetup
    {
        public const string TaskName = "HW Widget CPU temperature helper";
        static string BinDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HWWidget"); } }
        static string BinPath { get { return Path.Combine(BinDir, "HWWidgetSensor.exe"); } }
        // The task definition is written beside the helper exe and deleted after schtasks reads it.
        internal static string TaskXmlPath { get { return Path.Combine(BinDir, "task.xml"); } }

        public static bool Installed { get { try { return File.Exists(BinPath); } catch { return false; } } }

        // Starts this exe elevated with the given verb (1 UAC prompt) and waits; returns false when declined or failed.
        public static bool RunElevated(string verb)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, verb);
                psi.Verb = "runas"; psi.UseShellExecute = true; psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode == 0; }
            }
            catch { return false; }
        }

        static bool IsElevated()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        static int Schtasks(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi)) { p.WaitForExit(30000); return p.HasExited ? p.ExitCode : -1; }
        }

        static void StopHelper()
        {
            Schtasks("/End /TN \"" + TaskName + "\"");
            foreach (Process p in Process.GetProcessesByName("HWWidgetSensor"))
                try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        public static int Install()
        {
            if (!IsElevated()) return 5;
            StopHelper();
            Directory.CreateDirectory(BinDir);
            File.Copy(Process.GetCurrentProcess().MainModule.FileName, BinPath, true);

            // Data folder: SYSTEM and Administrators write, everyone else only reads (no planted links or fake values).
            DirectorySecurity sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
            if (Directory.Exists(HelperFile.Dir)) Directory.Delete(HelperFile.Dir, true);
            Directory.CreateDirectory(HelperFile.Dir, sec);

            string xml = TaskXmlPath;
            File.WriteAllText(xml, TaskXml(BinPath), Encoding.Unicode);
            int rc = Schtasks("/Create /F /TN \"" + TaskName + "\" /XML \"" + xml + "\"");
            try { File.Delete(xml); } catch { }
            if (rc != 0) return 3;
            return Schtasks("/Run /TN \"" + TaskName + "\"") == 0 ? 0 : 4;
        }

        public static int Uninstall()
        {
            if (!IsElevated()) return 5;
            StopHelper();
            Schtasks("/Delete /F /TN \"" + TaskName + "\"");
            try { if (Directory.Exists(BinDir)) Directory.Delete(BinDir, true); } catch { }
            try { if (Directory.Exists(HelperFile.Dir)) Directory.Delete(HelperFile.Dir, true); } catch { }
            return 0;
        }

        static string TaskXml(string exe)
        {
            string e = System.Security.SecurityElement.Escape(exe);
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Reads the CPU temperature for HW Widget (admin-only sensors). Remove with the widget's menu or windows\\uninstall.ps1.</Description></RegistrationInfo>\r\n" +
"  <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"    <Hidden>false</Hidden>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>\"" + e + "\"</Command><Arguments>--sensor-helper</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
        }
    }
}
