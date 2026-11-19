using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Ultra Contrast")]
[assembly: AssemblyDescription("Hardware Gamma LUT & DWM 5x5 Color Matrix Display Tuner for Windows")]
[assembly: AssemblyProduct("Ultra Contrast")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace UltraContrast
{
    internal static class DisplayColorEngine
    {
        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        private const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

        [DllImport("gdi32.dll")]
        private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

        [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("Magnification.dll")]
        private static extern bool MagInitialize();

        [DllImport("Magnification.dll")]
        private static extern bool MagUninitialize();

        [DllImport("Magnification.dll")]
        private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT pEffect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAMP
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public UInt16[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public UInt16[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public UInt16[] Blue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MAGCOLOREFFECT
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
            public float[] transform;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        private static bool _magInitialized = false;
        private static bool _magUnavailable = false;

        public static void EnsureMagInit()
        {
            if (_magUnavailable || _magInitialized)
                return;

            try
            {
                _magInitialized = MagInitialize();
            }
            catch
            {
                _magUnavailable = true;
                _magInitialized = false;
            }
        }

        public static void ShutdownMag()
        {
            if (!_magInitialized)
                return;

            try
            {
                MagUninitialize();
            }
            catch
            {
            }
            _magInitialized = false;
        }

        private static bool ApplyRampToAllMonitors(ref RAMP ramp)
        {
            bool anySuccess = false;
            int foundCount = 0;

            try
            {
                DISPLAY_DEVICE dev = new DISPLAY_DEVICE();
                dev.cb = Marshal.SizeOf(dev);
                uint idx = 0;

                while (EnumDisplayDevices(null, idx, ref dev, 0))
                {
                    bool attached = (dev.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
                    bool mirroring = (dev.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0;

                    if (attached && !mirroring && !string.IsNullOrEmpty(dev.DeviceName))
                    {
                        foundCount++;
                        IntPtr hdc = CreateDC("DISPLAY", dev.DeviceName, null, IntPtr.Zero);
                        if (hdc != IntPtr.Zero)
                        {
                            try
                            {
                                if (SetDeviceGammaRamp(hdc, ref ramp))
                                    anySuccess = true;
                            }
                            finally
                            {
                                DeleteDC(hdc);
                            }
                        }
                    }

                    idx++;
                    dev = new DISPLAY_DEVICE();
                    dev.cb = Marshal.SizeOf(dev);
                }
            }
            catch
            {
            }

            if (foundCount == 0 || !anySuccess)
            {
                IntPtr primaryDc = GetDC(IntPtr.Zero);
                if (primaryDc != IntPtr.Zero)
                {
                    try
                    {
                        if (SetDeviceGammaRamp(primaryDc, ref ramp))
                            anySuccess = true;
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, primaryDc);
                    }
                }
            }

            return anySuccess;
        }

        public static bool ApplyColorProfile(DisplayProfile profile)
        {
            if (profile == null)
                return false;

            return ApplyColorProfile(
                profile.Contrast,
                profile.SCurve,
                profile.Gamma,
                profile.Brightness,
                profile.Saturation,
                profile.Temperature
            );
        }

        public static bool ApplyColorProfile(
            double contrast,
            double sCurve,
            double gamma,
            double brightness,
            double saturation,
            double temperature)
        {
            // 1. Hardware Gamma LUT (SetDeviceGammaRamp) for non-linear Gamma, S-Curve deep black, and core Contrast
            RAMP ramp = new RAMP();
            ramp.Red = new UInt16[256];
            ramp.Green = new UInt16[256];
            ramp.Blue = new UInt16[256];

            double sPower = Math.Max(0.70, 1.0 + (contrast - 1.0) * 1.15 + sCurve * 0.55);
            double linearToe = 1.0 + (contrast - 1.0) * 0.18;
            double matrixContrast = 1.0 + (contrast - 1.0) * 0.12;

            double lutBrightness = Math.Min(0.12, Math.Max(-0.12, brightness));
            double matrixBrightness = brightness - lutBrightness;

            double rGain = 1.0 + (temperature > 0 ? temperature * 0.18 : temperature * 0.12);
            double gGain = 1.0 - Math.Abs(temperature) * 0.04;
            double bGain = 1.0 - (temperature > 0 ? temperature * 0.22 : temperature * 0.18);

            for (int i = 0; i < 256; i++)
            {
                double x = i / 255.0;

                // Monotonic Power S-Curve (deepens shadows & boosts highlights smoothly without clipping)
                if (Math.Abs(sPower - 1.0) > 0.001)
                {
                    if (x < 0.5)
                        x = 0.5 * Math.Pow(2.0 * x, sPower);
                    else
                        x = 1.0 - 0.5 * Math.Pow(2.0 * (1.0 - x), sPower);
                }

                // Gamma correction
                double gSafe = Math.Max(0.55, Math.Min(2.20, gamma));
                x = Math.Pow(Math.Max(0.0, x), 1.0 / gSafe);

                // Linear contrast toe & brightness offset
                x = (x - 0.5) * linearToe + 0.5 + lutBrightness;

                double xr = Math.Max(0.0, Math.Min(1.0, x * rGain));
                double xg = Math.Max(0.0, Math.Min(1.0, x * gGain));
                double xb = Math.Max(0.0, Math.Min(1.0, x * bGain));

                // Win32k kernel guard rails so SetDeviceGammaRamp succeeds on strict display drivers
                int minAllowed = Math.Max(0, (i - 120) * 256);
                int maxAllowed = Math.Min(65535, (i + 120) * 256 + 255);
                if (i == 0) minAllowed = 0;
                if (i == 255) maxAllowed = 65535;

                int vr = Math.Max(minAllowed, Math.Min(maxAllowed, (int)Math.Round(xr * 65535.0)));
                int vg = Math.Max(minAllowed, Math.Min(maxAllowed, (int)Math.Round(xg * 65535.0)));
                int vb = Math.Max(minAllowed, Math.Min(maxAllowed, (int)Math.Round(xb * 65535.0)));

                ramp.Red[i] = (UInt16)vr;
                ramp.Green[i] = (UInt16)vg;
                ramp.Blue[i] = (UInt16)vb;
            }

            bool gammaOk = ApplyRampToAllMonitors(ref ramp);

            // 2. DWM Fullscreen 5x5 Color Matrix (Magnification API) for Digital Vibrance & Saturation
            bool magOk = false;
            EnsureMagInit();
            if (_magInitialized)
            {
                try
                {
                    MAGCOLOREFFECT effect = new MAGCOLOREFFECT();
                    effect.transform = new float[25];

                    float sat = (float)Math.Max(0.0, Math.Min(3.0, saturation));
                    float c = (float)matrixContrast;
                    float bOffset = (float)(0.5 * (1.0 - matrixContrast) + matrixBrightness);

                    // Rec.709 luminance weights
                    float lumR = 0.2126f;
                    float lumG = 0.7152f;
                    float lumB = 0.0722f;

                    float sr = (1.0f - sat) * lumR;
                    float sg = (1.0f - sat) * lumG;
                    float sb = (1.0f - sat) * lumB;

                    effect.transform[0] = c * (sr + sat);
                    effect.transform[1] = c * sr;
                    effect.transform[2] = c * sr;
                    effect.transform[3] = 0.0f;
                    effect.transform[4] = 0.0f;

                    effect.transform[5] = c * sg;
                    effect.transform[6] = c * (sg + sat);
                    effect.transform[7] = c * sg;
                    effect.transform[8] = 0.0f;
                    effect.transform[9] = 0.0f;

                    effect.transform[10] = c * sb;
                    effect.transform[11] = c * sb;
                    effect.transform[12] = c * (sb + sat);
                    effect.transform[13] = 0.0f;
                    effect.transform[14] = 0.0f;

                    effect.transform[15] = 0.0f;
                    effect.transform[16] = 0.0f;
                    effect.transform[17] = 0.0f;
                    effect.transform[18] = 1.0f;
                    effect.transform[19] = 0.0f;

                    effect.transform[20] = bOffset;
                    effect.transform[21] = bOffset;
                    effect.transform[22] = bOffset;
                    effect.transform[23] = 0.0f;
                    effect.transform[24] = 1.0f;

                    magOk = MagSetFullscreenColorEffect(ref effect);
                }
                catch
                {
                    magOk = false;
                }
            }

            return gammaOk || magOk;
        }

        public static void ResetToDefault()
        {
            RAMP ramp = new RAMP();
            ramp.Red = new UInt16[256];
            ramp.Green = new UInt16[256];
            ramp.Blue = new UInt16[256];
            for (int i = 0; i < 256; i++)
            {
                UInt16 v = (UInt16)(i * 257);
                ramp.Red[i] = v;
                ramp.Green[i] = v;
                ramp.Blue[i] = v;
            }
            ApplyRampToAllMonitors(ref ramp);

            EnsureMagInit();
            if (_magInitialized)
            {
                try
                {
                    MAGCOLOREFFECT id = new MAGCOLOREFFECT();
                    id.transform = new float[25];
                    id.transform[0] = 1.0f;
                    id.transform[6] = 1.0f;
                    id.transform[12] = 1.0f;
                    id.transform[18] = 1.0f;
                    id.transform[24] = 1.0f;
                    MagSetFullscreenColorEffect(ref id);
                }
                catch
                {
                }
                ShutdownMag();
            }
        }
    }

    internal sealed class DisplayProfile
    {
        public string Name;
        public double Contrast;
        public double SCurve;
        public double Gamma;
        public double Brightness;
        public double Saturation;
        public double Temperature;
        public int Backlight;
        public bool AutoStart;

        public DisplayProfile()
        {
            Name = "Custom";
            Contrast = 1.00;
            SCurve = 0.00;
            Gamma = 1.00;
            Brightness = 0.00;
            Saturation = 1.00;
            Temperature = 0.00;
            Backlight = 100;
            AutoStart = false;
        }

        public DisplayProfile Clone()
        {
            DisplayProfile p = new DisplayProfile();
            p.Name = Name;
            p.Contrast = Contrast;
            p.SCurve = SCurve;
            p.Gamma = Gamma;
            p.Brightness = Brightness;
            p.Saturation = Saturation;
            p.Temperature = Temperature;
            p.Backlight = Backlight;
            p.AutoStart = AutoStart;
            return p;
        }

        public bool IsDefaultColor()
        {
            return Math.Abs(Contrast - 1.0) < 0.005
                && Math.Abs(SCurve) < 0.005
                && Math.Abs(Gamma - 1.0) < 0.005
                && Math.Abs(Brightness) < 0.005
                && Math.Abs(Saturation - 1.0) < 0.005
                && Math.Abs(Temperature) < 0.005;
        }
    }

    internal static class Presets
    {
        public static DisplayProfile UltraContrast()
        {
            DisplayProfile p = new DisplayProfile();
            p.Name = "Ultra Contrast & Vibrance";
            p.Contrast = 1.24;
            p.SCurve = 0.45;
            p.Gamma = 0.94;
            p.Brightness = 0.00;
            p.Saturation = 1.48;
            p.Temperature = -0.08;
            p.Backlight = 100;
            p.AutoStart = true;
            return p;
        }

        public static DisplayProfile CS2Esports()
        {
            DisplayProfile p = new DisplayProfile();
            p.Name = "CS2 / Esports Pro";
            p.Contrast = 1.22;
            p.SCurve = 0.25;
            p.Gamma = 1.14;
            p.Brightness = 0.02;
            p.Saturation = 1.70;
            p.Temperature = -0.05;
            p.Backlight = 100;
            p.AutoStart = true;
            return p;
        }

        public static DisplayProfile OledCinema()
        {
            DisplayProfile p = new DisplayProfile();
            p.Name = "OLED Cinema";
            p.Contrast = 1.30;
            p.SCurve = 0.65;
            p.Gamma = 0.89;
            p.Brightness = -0.01;
            p.Saturation = 1.38;
            p.Temperature = -0.04;
            p.Backlight = 100;
            p.AutoStart = true;
            return p;
        }

        public static DisplayProfile FactoryDefault()
        {
            DisplayProfile p = new DisplayProfile();
            p.Name = "Default";
            p.Contrast = 1.00;
            p.SCurve = 0.00;
            p.Gamma = 1.00;
            p.Brightness = 0.00;
            p.Saturation = 1.00;
            p.Temperature = 0.00;
            p.Backlight = 100;
            p.AutoStart = false;
            return p;
        }

        public static DisplayProfile ByName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string key = name.Trim().ToLowerInvariant();
            if (key == "ultracontrast" || key == "ultra") return UltraContrast();
            if (key == "cs2esports" || key == "cs2" || key == "esports") return CS2Esports();
            if (key == "oledcinema" || key == "oled" || key == "cinema") return OledCinema();
            if (key == "default" || key == "reset") return FactoryDefault();
            return null;
        }
    }

    internal static class ProfileStorage
    {
        private const string AutoStartRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoStartValueName = "UltraContrast";
        public const string StopDaemonEventName = @"Local\UltraContrast_StopDaemon_Event";

        public static string AppDataDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "UltraContrast"
                );
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ProfilePath
        {
            get { return Path.Combine(AppDataDir, "profile.json"); }
        }

        public static string PidPath
        {
            get { return Path.Combine(AppDataDir, "daemon.pid"); }
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "");
        }

        public static void Save(DisplayProfile prof)
        {
            if (prof == null) return;

            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendFormat(inv, "  \"Name\": \"{0}\",\r\n", EscapeJson(prof.Name));
            sb.AppendFormat(inv, "  \"Contrast\": {0:0.00},\r\n", prof.Contrast);
            sb.AppendFormat(inv, "  \"SCurve\": {0:0.00},\r\n", prof.SCurve);
            sb.AppendFormat(inv, "  \"Gamma\": {0:0.00},\r\n", prof.Gamma);
            sb.AppendFormat(inv, "  \"Brightness\": {0:0.00},\r\n", prof.Brightness);
            sb.AppendFormat(inv, "  \"Saturation\": {0:0.00},\r\n", prof.Saturation);
            sb.AppendFormat(inv, "  \"Temperature\": {0:0.00},\r\n", prof.Temperature);
            sb.AppendFormat(inv, "  \"Backlight\": {0},\r\n", prof.Backlight);
            sb.AppendFormat(inv, "  \"AutoStart\": {0}\r\n", prof.AutoStart ? "true" : "false");
            sb.AppendLine("}");

            try
            {
                File.WriteAllText(ProfilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }

            UpdateAutoStart(prof.AutoStart);
        }

        public static DisplayProfile Load()
        {
            try
            {
                if (!File.Exists(ProfilePath))
                    return Presets.UltraContrast();

                string content = File.ReadAllText(ProfilePath, Encoding.UTF8);
                DisplayProfile p = Presets.UltraContrast();

                string[] lines = content.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                CultureInfo inv = CultureInfo.InvariantCulture;

                foreach (string rawLine in lines)
                {
                    int colon = rawLine.IndexOf(':');
                    if (colon <= 0) continue;

                    string k = rawLine.Substring(0, colon).Trim().Trim('"');
                    string v = rawLine.Substring(colon + 1).Trim().TrimEnd(',').Trim();

                    double dVal;
                    int iVal;

                    if (k == "Name")
                        p.Name = v.Trim('"');
                    else if (k == "Contrast" && double.TryParse(v, NumberStyles.Float, inv, out dVal))
                        p.Contrast = dVal;
                    else if (k == "SCurve" && double.TryParse(v, NumberStyles.Float, inv, out dVal))
                        p.SCurve = dVal;
                    else if (k == "Gamma" && double.TryParse(v, NumberStyles.Float, inv, out dVal))
                        p.Gamma = dVal;
                    else if (k == "Brightness" && double.TryParse(v, NumberStyles.Float, inv, out dVal))
                        p.Brightness = dVal;
                    else if (k == "Saturation" && double.TryParse(v, NumberStyles.Float, inv, out dVal))
                        p.Saturation = dVal;
                    else if (k == "Temperature" && double.TryParse(v, NumberStyles.Float, inv, out dVal))
                        p.Temperature = dVal;
                    else if (k == "Backlight" && int.TryParse(v, NumberStyles.Integer, inv, out iVal))
                        p.Backlight = iVal;
                    else if (k == "AutoStart")
                        p.AutoStart = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
                }

                return p;
            }
            catch
            {
                return Presets.UltraContrast();
            }
        }

        public static void UpdateAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AutoStartRegPath, true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        string exe = Application.ExecutablePath;
                        key.SetValue(AutoStartValueName, string.Format("\"{0}\" --daemon", exe), RegistryValueKind.String);
                    }
                    else
                    {
                        if (key.GetValue(AutoStartValueName) != null)
                            key.DeleteValue(AutoStartValueName, false);
                    }
                }
            }
            catch
            {
            }
        }

        public static void StopExistingDaemon()
        {
            // First try graceful shutdown via named event so tray icon cleans up cleanly
            try
            {
                EventWaitHandle stopEvt;
                if (EventWaitHandle.TryOpenExisting(StopDaemonEventName, out stopEvt))
                {
                    using (stopEvt)
                    {
                        stopEvt.Set();
                    }
                    Thread.Sleep(220);
                }
            }
            catch
            {
            }

            // Fallback: verify PID & process name before terminating
            try
            {
                if (!File.Exists(PidPath)) return;
                string pidText = File.ReadAllText(PidPath).Trim();
                int oldPid;
                int currentPid = Process.GetCurrentProcess().Id;
                string currentName = Process.GetCurrentProcess().ProcessName;

                if (int.TryParse(pidText, out oldPid) && oldPid > 0 && oldPid != currentPid)
                {
                    try
                    {
                        Process proc = Process.GetProcessById(oldPid);
                        if (proc != null && !proc.HasExited &&
                            string.Equals(proc.ProcessName, currentName, StringComparison.OrdinalIgnoreCase))
                        {
                            proc.Kill();
                            proc.WaitForExit(1000);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        public static void StartBackgroundDaemon()
        {
            StopExistingDaemon();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--daemon";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process proc = Process.Start(psi);
                if (proc != null)
                {
                    File.WriteAllText(PidPath, proc.Id.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
            }
        }
    }

    internal static class BacklightController
    {
        private static bool? _isSupported = null;

        public static bool IsSupported()
        {
            if (_isSupported.HasValue)
                return _isSupported.Value;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    _isSupported = (results != null && results.Count > 0);
                    return _isSupported.Value;
                }
            }
            catch
            {
                _isSupported = false;
                return false;
            }
        }

        public static int GetCurrentLevel(int fallback)
        {
            if (!IsSupported())
                return fallback;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        object val = obj["CurrentBrightness"];
                        if (val != null)
                            return Convert.ToInt32(val, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch
            {
            }
            return fallback;
        }

        public static void SetLevel(int level)
        {
            if (!IsSupported())
                return;

            int clamped = Math.Max(0, Math.Min(100, level));
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject mObj in results)
                    {
                        mObj.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)clamped });
                    }
                }
            }
            catch
            {
            }
        }
    }

    internal static class IntelDpstHelper
    {
        private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        public static int DisableIntelDpst()
        {
            int patched = 0;
            try
            {
                using (RegistryKey classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey, false))
                {
                    if (classKey == null)
                        return 0;

                    foreach (string subName in classKey.GetSubKeyNames())
                    {
                        if (subName.Length != 4) continue;
                        int dummy;
                        if (!int.TryParse(subName, out dummy)) continue;

                        using (RegistryKey adapterKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey + "\\" + subName, true))
                        {
                            if (adapterKey == null) continue;

                            string provider = adapterKey.GetValue("ProviderName") as string ?? "";
                            string desc = adapterKey.GetValue("DriverDesc") as string ?? "";

                            bool isIntel = provider.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0
                                || desc.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0;

                            if (!isIntel) continue;

                            object ftcObj = adapterKey.GetValue("FeatureTestControl");
                            if (ftcObj != null)
                            {
                                int ftc = Convert.ToInt32(ftcObj, CultureInfo.InvariantCulture);
                                int newFtc = ftc | 0x10;
                                adapterKey.SetValue("FeatureTestControl", newFtc, RegistryValueKind.DWord);
                            }

                            try
                            {
                                adapterKey.SetValue("PowerDpstAggressivenessLevel", 0, RegistryValueKind.DWord);
                            }
                            catch
                            {
                            }

                            patched++;
                        }
                    }
                }
            }
            catch
            {
            }
            return patched;
        }
    }

    internal sealed class DaemonAppContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private readonly EventWaitHandle _stopEvent;
        private DateTime _lastWriteTime = DateTime.MinValue;
        private DisplayProfile _currentProfile;
        private bool _exiting = false;

        public DaemonAppContext()
        {
            try
            {
                File.WriteAllText(ProfileStorage.PidPath, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
            }

            bool createdNew;
            _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ProfileStorage.StopDaemonEventName, out createdNew);
            if (!createdNew)
                _stopEvent.Reset();

            _currentProfile = ProfileStorage.Load();
            BacklightController.SetLevel(_currentProfile.Backlight);
            DisplayColorEngine.ApplyColorProfile(_currentProfile);

            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem openItem = new ToolStripMenuItem("Открыть Ultra Contrast");
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            openItem.Click += OnOpenGuiClick;
            menu.Items.Add(openItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem pUltra = new ToolStripMenuItem("Пресет: Ultra Contrast");
            pUltra.Click += delegate { ApplyPresetFromTray(Presets.UltraContrast()); };
            menu.Items.Add(pUltra);

            ToolStripMenuItem pCs2 = new ToolStripMenuItem("Пресет: CS2 / Esports Pro");
            pCs2.Click += delegate { ApplyPresetFromTray(Presets.CS2Esports()); };
            menu.Items.Add(pCs2);

            ToolStripMenuItem pOled = new ToolStripMenuItem("Пресет: OLED Cinema");
            pOled.Click += delegate { ApplyPresetFromTray(Presets.OledCinema()); };
            menu.Items.Add(pOled);

            ToolStripMenuItem pReset = new ToolStripMenuItem("Сбросить к заводским (1:1)");
            pReset.Click += OnResetAndExitClick;
            menu.Items.Add(pReset);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Выход (сбросить цвета)");
            exitItem.Click += OnExitOnlyClick;
            menu.Items.Add(exitItem);

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = Program.LoadAppIcon();
            _trayIcon.Text = "Ultra Contrast — Активен";
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += OnOpenGuiClick;

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Interval = 500;
            int tickCounter = 0;
            _refreshTimer.Tick += delegate
            {
                if (_exiting) return;

                // Check stop signal every 500ms for instant clean handoff to GUI
                if (_stopEvent.WaitOne(0))
                {
                    ShutdownDaemon(false);
                    return;
                }

                tickCounter++;
                if (tickCounter >= 5)
                {
                    tickCounter = 0;
                    ReapplyProfile();
                }
            };
            _refreshTimer.Start();
        }

        private void ApplyPresetFromTray(DisplayProfile preset)
        {
            _currentProfile = preset;
            ProfileStorage.Save(_currentProfile);
            BacklightController.SetLevel(_currentProfile.Backlight);
            DisplayColorEngine.ApplyColorProfile(_currentProfile);
        }

        private void OnOpenGuiClick(object sender, EventArgs e)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch
            {
            }
        }

        private void OnResetAndExitClick(object sender, EventArgs e)
        {
            ProfileStorage.Save(Presets.FactoryDefault());
            ShutdownDaemon(true);
        }

        private void OnExitOnlyClick(object sender, EventArgs e)
        {
            ShutdownDaemon(true);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume && !_exiting)
            {
                ReapplyProfile();
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!_exiting)
            {
                ReapplyProfile();
            }
        }

        private void ReapplyProfile()
        {
            try
            {
                if (File.Exists(ProfileStorage.ProfilePath))
                {
                    DateTime wt = File.GetLastWriteTimeUtc(ProfileStorage.ProfilePath);
                    if (wt > _lastWriteTime)
                    {
                        _currentProfile = ProfileStorage.Load();
                        _lastWriteTime = wt;
                    }
                }
                DisplayColorEngine.ApplyColorProfile(_currentProfile);
            }
            catch
            {
            }
        }

        private void ShutdownDaemon(bool resetDisplay)
        {
            if (_exiting) return;
            _exiting = true;

            try { _refreshTimer.Stop(); _refreshTimer.Dispose(); } catch { }
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
            try { _trayIcon.Visible = false; _trayIcon.Dispose(); } catch { }
            try { _stopEvent.Close(); } catch { }

            if (resetDisplay)
                DisplayColorEngine.ResetToDefault();
            else
                DisplayColorEngine.ShutdownMag();

            ExitThread();
        }
    }

    internal sealed class SliderRow
    {
        public TrackBar TrackBar;
        public Label ValueLabel;
        public Label HintLabel;
    }

    internal sealed class MainForm : Form
    {
        private readonly DisplayProfile _initialProfile;
        private readonly bool _backlightAvailable;

        private readonly SliderRow _sContrast;
        private readonly SliderRow _sSCurve;
        private readonly SliderRow _sGamma;
        private readonly SliderRow _sSat;
        private readonly SliderRow _sBright;
        private readonly SliderRow _sTemp;
        private readonly SliderRow _sBacklight;

        private readonly CheckBox _chkAutoStart;
        private readonly Button _btnSave;
        private bool _updatingUi = false;

        public MainForm(DisplayProfile initialProfile)
        {
            _initialProfile = initialProfile ?? Presets.UltraContrast();
            _backlightAvailable = BacklightController.IsSupported();

            AutoScaleMode = AutoScaleMode.None;
            Text = "Ultra Contrast — Настройка контраста, гаммы и цвета";
            ClientSize = new Size(636, 682);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(18, 18, 24);
            ForeColor = Color.FromArgb(240, 240, 245);
            Font = new Font("Segoe UI", 9.5f);
            Icon = Program.LoadAppIcon();

            // Header Banner
            Panel headerPanel = new Panel();
            headerPanel.Size = new Size(636, 68);
            headerPanel.Location = new Point(0, 0);
            headerPanel.BackColor = Color.FromArgb(26, 27, 38);
            Controls.Add(headerPanel);

            Label titleLabel = new Label();
            titleLabel.Text = "ULTRA CONTRAST — DISPLAY STUDIO";
            titleLabel.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            titleLabel.ForeColor = Color.FromArgb(90, 200, 250);
            titleLabel.Location = new Point(20, 10);
            titleLabel.AutoSize = true;
            headerPanel.Controls.Add(titleLabel);

            Label subTitleLabel = new Label();
            subTitleLabel.Text = "Аппаратная гамма-кривая (LUT) + DWM матрица насыщенности в реальном времени";
            subTitleLabel.Font = new Font("Segoe UI", 8.5f);
            subTitleLabel.ForeColor = Color.FromArgb(160, 165, 185);
            subTitleLabel.Location = new Point(22, 39);
            subTitleLabel.AutoSize = true;
            headerPanel.Controls.Add(subTitleLabel);

            // Preset Buttons Section
            GroupBox presetGroup = new GroupBox();
            presetGroup.Text = " Готовые пресеты (применяются мгновенно в 1 клик) ";
            presetGroup.ForeColor = Color.FromArgb(130, 180, 255);
            presetGroup.Location = new Point(20, 80);
            presetGroup.Size = new Size(596, 98);
            Controls.Add(presetGroup);

            Button btnUltra = CreatePresetButton("◆ ULTRA CONTRAST (Сочная картинка)", 15, 24, 276, Color.FromArgb(185, 45, 70));
            Button btnCs2 = CreatePresetButton("◎ CS2 / ESPORTS (Контраст + Тени)", 305, 24, 276, Color.FromArgb(35, 105, 175));
            Button btnOled = CreatePresetButton("◐ OLED КИНО (Глубокий чёрный)", 15, 59, 276, Color.FromArgb(75, 50, 150));
            Button btnReset = CreatePresetButton("↺ Заводской стандарт (Сброс 1:1)", 305, 59, 276, Color.FromArgb(55, 58, 72));

            presetGroup.Controls.Add(btnUltra);
            presetGroup.Controls.Add(btnCs2);
            presetGroup.Controls.Add(btnOled);
            presetGroup.Controls.Add(btnReset);

            // Sliders Section
            GroupBox slidersPanel = new GroupBox();
            slidersPanel.Text = " Тонкая настройка матрицы (двигай ползунки — экран меняется сразу) ";
            slidersPanel.ForeColor = Color.FromArgb(130, 180, 255);
            slidersPanel.Location = new Point(20, 188);
            slidersPanel.Size = new Size(596, 396);
            Controls.Add(slidersPanel);

            _sContrast = AddTunerSlider(slidersPanel, 24, "Контрастность (Contrast)", "Усиливает разницу между светом и тенью (124% = ультра-контраст)", 75, 160, (int)Math.Round(_initialProfile.Contrast * 100));
            _sSCurve = AddTunerSlider(slidersPanel, 78, "Глубина чёрного (S-Curve)", "Убирает серую пелену IPS-матрицы, делает чёрный глубже", 0, 90, (int)Math.Round(_initialProfile.SCurve * 100));
            _sGamma = AddTunerSlider(slidersPanel, 132, "Гамма-коррекция (Gamma)", "< 1.00 плотнее и глубже картинка | > 1.05 высветляет тени в играх", 65, 155, (int)Math.Round(_initialProfile.Gamma * 100));
            _sSat = AddTunerSlider(slidersPanel, 186, "Насыщенность (Digital Vibrance)", "Сочность цветов (100% = стандарт, 145–170% = макс. сочность)", 0, 220, (int)Math.Round(_initialProfile.Saturation * 100));
            _sBright = AddTunerSlider(slidersPanel, 240, "Яркость / Точка чёрного", "Подстройка уровня чёрного (- глубже тени, + светлее тени)", -20, 20, (int)Math.Round(_initialProfile.Brightness * 100));
            _sTemp = AddTunerSlider(slidersPanel, 294, "Цветовая температура", "Отрицательные значения (-) убирают желтизну и дают чистый белый", -30, 30, (int)Math.Round(_initialProfile.Temperature * 100));

            string blHint = _backlightAvailable
                ? "Аппаратная яркость матрицы ноутбука (100% для макс. контраста)"
                : "Недоступно на внешних мониторах (только встроенные экраны ноутбуков)";
            int initBl = _backlightAvailable ? BacklightController.GetCurrentLevel(_initialProfile.Backlight) : 100;
            _sBacklight = AddTunerSlider(slidersPanel, 346, "Подсветка матрицы ноутбука", blHint, 10, 100, initBl);

            if (!_backlightAvailable)
            {
                _sBacklight.TrackBar.Enabled = false;
                _sBacklight.ValueLabel.Text = "N/A";
                _sBacklight.ValueLabel.ForeColor = Color.FromArgb(120, 125, 145);
            }

            // Checkbox AutoStart
            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "Закрепить настройки (Автозагрузка с Windows + защита от сброса в играх)";
            _chkAutoStart.Checked = _initialProfile.AutoStart;
            _chkAutoStart.ForeColor = Color.FromArgb(210, 220, 235);
            _chkAutoStart.Location = new Point(22, 594);
            _chkAutoStart.AutoSize = true;
            Controls.Add(_chkAutoStart);

            // Bottom Action Buttons
            _btnSave = new Button();
            _btnSave.Text = "✔ СОХРАНИТЬ И ЗАКРЕПИТЬ ПРОФИЛЬ";
            _btnSave.Location = new Point(20, 626);
            _btnSave.Size = new Size(374, 42);
            _btnSave.FlatStyle = FlatStyle.Flat;
            _btnSave.FlatAppearance.BorderSize = 0;
            _btnSave.BackColor = Color.FromArgb(30, 150, 95);
            _btnSave.ForeColor = Color.White;
            _btnSave.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            _btnSave.Cursor = Cursors.Hand;
            Controls.Add(_btnSave);

            Button btnDpst = new Button();
            btnDpst.Text = "⚡ Отключить Intel DPST (Admin)";
            btnDpst.Location = new Point(404, 626);
            btnDpst.Size = new Size(212, 42);
            btnDpst.FlatStyle = FlatStyle.Flat;
            btnDpst.FlatAppearance.BorderSize = 1;
            btnDpst.FlatAppearance.BorderColor = Color.FromArgb(80, 92, 130);
            btnDpst.BackColor = Color.FromArgb(55, 65, 95);
            btnDpst.ForeColor = Color.White;
            btnDpst.Font = new Font("Segoe UI", 8.75f, FontStyle.Bold);
            btnDpst.Cursor = Cursors.Hand;
            Controls.Add(btnDpst);

            // Events
            _sContrast.TrackBar.ValueChanged += OnColorSliderChanged;
            _sSCurve.TrackBar.ValueChanged += OnColorSliderChanged;
            _sGamma.TrackBar.ValueChanged += OnColorSliderChanged;
            _sSat.TrackBar.ValueChanged += OnColorSliderChanged;
            _sBright.TrackBar.ValueChanged += OnColorSliderChanged;
            _sTemp.TrackBar.ValueChanged += OnColorSliderChanged;

            _sBacklight.TrackBar.Scroll += delegate
            {
                if (_backlightAvailable)
                {
                    BacklightController.SetLevel(_sBacklight.TrackBar.Value);
                    _sBacklight.ValueLabel.Text = string.Format("{0}%", _sBacklight.TrackBar.Value);
                    ResetSaveButtonState();
                }
            };

            _chkAutoStart.CheckedChanged += delegate { ResetSaveButtonState(); };

            btnUltra.Click += delegate { LoadProfileIntoUi(Presets.UltraContrast()); };
            btnCs2.Click += delegate { LoadProfileIntoUi(Presets.CS2Esports()); };
            btnOled.Click += delegate { LoadProfileIntoUi(Presets.OledCinema()); };
            btnReset.Click += delegate { LoadProfileIntoUi(Presets.FactoryDefault()); };

            _btnSave.Click += delegate
            {
                DisplayProfile prof = ReadCurrentProfile();
                ProfileStorage.Save(prof);
                _btnSave.Text = "✔ СОХРАНЕНО И АКТИВНО!";
                _btnSave.BackColor = Color.FromArgb(40, 180, 110);
            };

            btnDpst.Click += delegate
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.Arguments = "--disable-dpst";
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;

                    Process proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit();
                        if (proc.ExitCode == 0)
                        {
                            MessageBox.Show(
                                "Энергосбережение матрицы Intel DPST успешно отключено в реестре!\nРекомендуется перезагрузить ПК, чтобы драйвер применил изменения.",
                                "Intel DPST отключён",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information
                            );
                        }
                        else
                        {
                            MessageBox.Show(
                                "Интегрированная графика Intel на этом устройстве не обнаружена — отключение DPST не требуется.",
                                "Intel DPST",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information
                            );
                        }
                    }
                }
                catch
                {
                    MessageBox.Show("Запрос прав администратора отменён пользователем.", "Ultra Contrast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            Shown += delegate
            {
                LoadProfileIntoUi(_initialProfile);
            };

            FormClosing += delegate
            {
                DisplayProfile finalProf = ReadCurrentProfile();
                ProfileStorage.Save(finalProf);

                if (!finalProf.IsDefaultColor())
                {
                    DisplayColorEngine.ShutdownMag();
                    ProfileStorage.StartBackgroundDaemon();
                }
                else
                {
                    DisplayColorEngine.ResetToDefault();
                }
            };
        }

        private static Button CreatePresetButton(string text, int x, int y, int w, Color bgColor)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = new Point(x, y);
            btn.Size = new Size(w, 30);
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 110);
            btn.BackColor = bgColor;
            btn.ForeColor = Color.White;
            btn.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private static SliderRow AddTunerSlider(Control parent, int y, string title, string hint, int min, int max, int val)
        {
            Label lblTitle = new Label();
            lblTitle.Text = title;
            lblTitle.Font = new Font("Segoe UI", 9.25f, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(235, 238, 245);
            lblTitle.Location = new Point(16, y);
            lblTitle.AutoSize = true;
            parent.Controls.Add(lblTitle);

            Label lblVal = new Label();
            lblVal.Text = val.ToString(CultureInfo.InvariantCulture);
            lblVal.Font = new Font("Consolas", 10.5f, FontStyle.Bold);
            lblVal.ForeColor = Color.FromArgb(80, 220, 160);
            lblVal.Location = new Point(505, y);
            lblVal.Size = new Size(76, 20);
            lblVal.TextAlign = ContentAlignment.TopRight;
            parent.Controls.Add(lblVal);

            Label lblHint = new Label();
            lblHint.Text = hint;
            lblHint.Font = new Font("Segoe UI", 7.5f);
            lblHint.ForeColor = Color.FromArgb(145, 150, 170);
            lblHint.Location = new Point(16, y + 20);
            lblHint.Size = new Size(276, 28);
            parent.Controls.Add(lblHint);

            TrackBar tb = new TrackBar();
            tb.Minimum = min;
            tb.Maximum = max;
            tb.Value = Math.Max(min, Math.Min(max, val));
            tb.TickStyle = TickStyle.None;
            tb.Location = new Point(296, y + 14);
            tb.Size = new Size(286, 28);
            tb.BackColor = Color.FromArgb(18, 18, 24);
            parent.Controls.Add(tb);

            SliderRow row = new SliderRow();
            row.TrackBar = tb;
            row.ValueLabel = lblVal;
            row.HintLabel = lblHint;
            return row;
        }

        private void ResetSaveButtonState()
        {
            if (_updatingUi) return;
            _btnSave.Text = "✔ СОХРАНИТЬ И ЗАКРЕПИТЬ ПРОФИЛЬ";
            _btnSave.BackColor = Color.FromArgb(30, 150, 95);
        }

        private void OnColorSliderChanged(object sender, EventArgs e)
        {
            if (_updatingUi) return;
            ResetSaveButtonState();
            UpdateLiveScreen();
        }

        private DisplayProfile ReadCurrentProfile()
        {
            DisplayProfile p = new DisplayProfile();
            p.Name = "Custom";
            p.Contrast = Math.Round(_sContrast.TrackBar.Value / 100.0, 2);
            p.SCurve = Math.Round(_sSCurve.TrackBar.Value / 100.0, 2);
            p.Gamma = Math.Round(_sGamma.TrackBar.Value / 100.0, 2);
            p.Brightness = Math.Round(_sBright.TrackBar.Value / 100.0, 2);
            p.Saturation = Math.Round(_sSat.TrackBar.Value / 100.0, 2);
            p.Temperature = Math.Round(_sTemp.TrackBar.Value / 100.0, 2);
            p.Backlight = _sBacklight.TrackBar.Value;
            p.AutoStart = _chkAutoStart.Checked;
            return p;
        }

        private void UpdateLiveScreen()
        {
            if (_updatingUi) return;

            DisplayProfile p = ReadCurrentProfile();

            _sContrast.ValueLabel.Text = string.Format("{0}%", (int)Math.Round(p.Contrast * 100));
            _sSCurve.ValueLabel.Text = string.Format("{0}%", (int)Math.Round(p.SCurve * 100));
            _sGamma.ValueLabel.Text = p.Gamma.ToString("0.00", CultureInfo.InvariantCulture);
            _sSat.ValueLabel.Text = string.Format("{0}%", (int)Math.Round(p.Saturation * 100));
            _sBright.ValueLabel.Text = string.Format("{0}{1}%", p.Brightness >= 0 ? "+" : "", (int)Math.Round(p.Brightness * 100));
            _sTemp.ValueLabel.Text = string.Format("{0}{1}", p.Temperature >= 0 ? "+" : "", (int)Math.Round(p.Temperature * 100));

            if (_backlightAvailable)
                _sBacklight.ValueLabel.Text = string.Format("{0}%", p.Backlight);

            DisplayColorEngine.ApplyColorProfile(p);
        }

        private void LoadProfileIntoUi(DisplayProfile prof)
        {
            if (prof == null) return;

            _updatingUi = true;
            _sContrast.TrackBar.Value = ClampInt((int)Math.Round(prof.Contrast * 100), _sContrast.TrackBar.Minimum, _sContrast.TrackBar.Maximum);
            _sSCurve.TrackBar.Value = ClampInt((int)Math.Round(prof.SCurve * 100), _sSCurve.TrackBar.Minimum, _sSCurve.TrackBar.Maximum);
            _sGamma.TrackBar.Value = ClampInt((int)Math.Round(prof.Gamma * 100), _sGamma.TrackBar.Minimum, _sGamma.TrackBar.Maximum);
            _sSat.TrackBar.Value = ClampInt((int)Math.Round(prof.Saturation * 100), _sSat.TrackBar.Minimum, _sSat.TrackBar.Maximum);
            _sBright.TrackBar.Value = ClampInt((int)Math.Round(prof.Brightness * 100), _sBright.TrackBar.Minimum, _sBright.TrackBar.Maximum);
            _sTemp.TrackBar.Value = ClampInt((int)Math.Round(prof.Temperature * 100), _sTemp.TrackBar.Minimum, _sTemp.TrackBar.Maximum);

            if (_backlightAvailable)
            {
                _sBacklight.TrackBar.Value = ClampInt(prof.Backlight, _sBacklight.TrackBar.Minimum, _sBacklight.TrackBar.Maximum);
                BacklightController.SetLevel(_sBacklight.TrackBar.Value);
            }

            _chkAutoStart.Checked = prof.AutoStart;
            _updatingUi = false;

            ResetSaveButtonState();
            UpdateLiveScreen();
        }

        private static int ClampInt(int val, int min, int max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }
    }

    internal static class Program
    {
        public static Icon LoadAppIcon()
        {
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico;
            }
            catch
            {
            }
            return SystemIcons.Application;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            bool daemonMode = false;
            bool applyOnly = false;
            bool resetMode = false;
            bool disableDpst = false;
            bool showHelp = false;
            string presetName = "";

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].Trim();
                string al = a.ToLowerInvariant();
                if (al == "--daemon" || al == "-daemon" || al == "/daemon")
                    daemonMode = true;
                else if (al == "--apply" || al == "-apply" || al == "-applyonly")
                    applyOnly = true;
                else if (al == "--reset" || al == "-reset")
                    resetMode = true;
                else if (al == "--disable-dpst" || al == "-disabledpst")
                    disableDpst = true;
                else if (al == "--help" || al == "-h" || al == "/?" || al == "--version")
                    showHelp = true;
                else if ((al == "--preset" || al == "-preset") && i + 1 < args.Length)
                    presetName = args[++i];
            }

            if (showHelp)
            {
                MessageBox.Show(
                    "Ultra Contrast v1.0.0\n\n" +
                    "Параметры командной строки:\n" +
                    "  (без параметров)         — Открыть графический интерфейс\n" +
                    "  --daemon                 — Фоновый режим в трее (удержание профиля)\n" +
                    "  --apply                  — Применить сохранённый профиль и запустить фон\n" +
                    "  --preset <имя>           — UltraContrast | CS2Esports | OledCinema | Default\n" +
                    "  --reset                  — Сбросить экран к заводским настройкам (1:1)\n" +
                    "  --disable-dpst           — Отключить Intel DPST в реестре (нужен Admin)",
                    "Ultra Contrast — Справка",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return 0;
            }

            if (disableDpst)
            {
                int patched = IntelDpstHelper.DisableIntelDpst();
                return patched > 0 ? 0 : 2;
            }

            if (resetMode)
            {
                ProfileStorage.StopExistingDaemon();
                ProfileStorage.Save(Presets.FactoryDefault());
                DisplayColorEngine.ResetToDefault();
                return 0;
            }

            if (!string.IsNullOrEmpty(presetName))
            {
                DisplayProfile preset = Presets.ByName(presetName);
                if (preset != null)
                {
                    ProfileStorage.Save(preset);
                }
            }

            if (applyOnly)
            {
                DisplayProfile prof = ProfileStorage.Load();
                BacklightController.SetLevel(prof.Backlight);
                if (prof.IsDefaultColor())
                {
                    ProfileStorage.StopExistingDaemon();
                    DisplayColorEngine.ResetToDefault();
                }
                else
                {
                    ProfileStorage.StartBackgroundDaemon();
                }
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (daemonMode)
            {
                Application.Run(new DaemonAppContext());
                return 0;
            }

            // Interactive GUI Mode (Single Instance)
            bool createdGuiMutex;
            using (Mutex guiMutex = new Mutex(true, @"Local\UltraContrast_GUI_SingleInstance", out createdGuiMutex))
            {
                if (!createdGuiMutex)
                {
                    return 0;
                }

                ProfileStorage.StopExistingDaemon();
                DisplayProfile current = ProfileStorage.Load();
                Application.Run(new MainForm(current));
            }

            return 0;
        }
    }
}
