using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace BHelper;

internal static class BatteryService
{
    private static readonly ManagementObjectSearcher BatterySearcher = new(
        "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");

    private static readonly ManagementObjectSearcher ChargeSearcher = new(
        @"root\WMI",
        "SELECT ChargeRate, DischargeRate, Charging FROM BatteryStatus");

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;      // seconds remaining, -1 if unknown
        public int BatteryFullLifeTime;  // seconds of full charge, -1 if unknown
    }

    public static (int remainingSeconds, bool charging) GetBatteryTimeRemaining()
    {
        if (GetSystemPowerStatus(out var status))
        {
            bool charging = status.ACLineStatus == 1;
            return (status.BatteryLifeTime, charging);
        }
        return (-1, false);
    }

    public static (double watts, double percent, bool charging) GetBatteryInfo()
    {
        double watts = 0;
        double percent = 0;
        bool charging = false;

        try
        {
            foreach (var obj in BatterySearcher.Get())
            {
                percent = Convert.ToDouble(obj["EstimatedChargeRemaining"]);
                var status = Convert.ToInt32(obj["BatteryStatus"]);
                charging = status >= 2;
            }

            foreach (var obj in ChargeSearcher.Get())
            {
                charging = Convert.ToBoolean(obj["Charging"]);
                int chargeRate = Convert.ToInt32(obj["ChargeRate"]);
                int dischargeRate = Convert.ToInt32(obj["DischargeRate"]);

                watts = charging
                    ? chargeRate / 1000.0
                    : dischargeRate / 1000.0;
            }
        }
        catch
        {
        }

        return (watts, percent, charging);
    }

    // Win32 API for refresh rate — no WMI overhead
    [DllImport("user32.dll")]
    private static extern bool EnumDisplaySettingsW(
        string? deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

    public static int GetDisplayRefreshRate()
    {
        var dm = new DEVMODE();
        dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
        if (EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref dm))
            return (int)dm.dmDisplayFrequency;
        return 0;
    }

    public static string GetDGpuStatus()
    {
        try
        {
            string powerState = GpuPowerState.GetNvidiaGpuPowerState();
            return powerState == "D0" ? "Awake (D0)" : $"Sleeping ({powerState})";
        }
        catch
        {
            return "Unknown";
        }
    }

    public static List<string> FetchTopProcesses()
    {
        var results = new List<string>();
        try
        {
            var procs = Process.GetProcesses()
                .Where(p => p.Id != 0)
                .Select(p =>
                {
                    try { return (name: p.ProcessName, cpu: p.TotalProcessorTime.TotalMilliseconds); }
                    catch { return (name: p.ProcessName, cpu: -1.0); }
                })
                .Where(p => p.cpu > 0)
                .GroupBy(p => p.name)
                .Select(g => (name: g.Key, cpu: g.Sum(p => p.cpu)))
                .OrderByDescending(p => p.cpu)
                .Take(10);

            foreach (var p in procs)
            {
                var time = TimeSpan.FromMilliseconds(p.cpu);
                results.Add($"{p.name} — {time:h\\:mm\\:ss}");
            }
        }
        catch { }

        return results;
    }

    public static List<string> FetchGpuProcesses()
    {
        var names = new List<string>();
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            bool inProcessSection = false;
            var seen = new HashSet<string>();
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("Processes:"))
                {
                    inProcessSection = true;
                    continue;
                }

                if (!inProcessSection || !line.TrimStart().StartsWith('|'))
                    continue;

                var trimmed = line.Trim('|', ' ');
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 5 && int.TryParse(parts[0], out _)
                    && int.TryParse(parts[3], out int pid))
                {
                    if (seen.Add(parts[3]))
                    {
                        try
                        {
                            var p = Process.GetProcessById(pid);
                            names.Add(p.ProcessName);
                        }
                        catch
                        {
                            // Process may have exited — use raw text as fallback
                            string raw = parts.Length >= 6 ? parts[5] : parts[3];
                            names.Add(Path.GetFileNameWithoutExtension(raw));
                        }
                    }
                }
            }
        }
        catch { }

        return names;
    }
}
