using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BHelper;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string SynapsePath =
        @"C:\Program Files\Razer\RazerAppEngine\RazerAppEngine.exe";
    private const string SynapseArgs = "--url-params=apps=synapse";
    private const string AppName = "B-Helper";
    private const string StartupKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _dgpuMonitoring = true;
    private int _tickCount;
    private string _cachedGpuState = "";
    private string _cachedRefreshLabel = "";
    private IntPtr _dynamicIconHandle = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public TrayApplicationContext()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "B-Helper — loading...",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        _trayIcon.Click += OnTrayClick;

        _timer = new System.Windows.Forms.Timer { Interval = 10000 };
        _timer.Tick += (_, _) => UpdateTooltip();
        _timer.Start();

        UpdateTooltip();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Razer Synapse", null, (_, _) => LaunchSynapse());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show GPU Processes", null, (_, _) => ShowGpuProcesses());
        menu.Items.Add("Show Top Processes", null, (_, _) => ShowTopProcesses());

        var dgpuItem = new ToolStripMenuItem("dGPU Monitoring")
        {
            Checked = _dgpuMonitoring,
            CheckOnClick = true
        };
        dgpuItem.CheckedChanged += (_, _) =>
        {
            _dgpuMonitoring = dgpuItem.Checked;
            UpdateTooltip();
        };
        menu.Items.Add(dgpuItem);

        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) => SetStartupEnabled(startupItem.Checked);
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey, false);
        return key?.GetValue(AppName) is not null;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
        if (key == null) return;

        if (enabled)
        {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    private void UpdateTooltip()
    {
        // Battery: every tick (10s)
        var (watts, percent, charging) = BatteryService.GetBatteryInfo();

        // dGPU: every 3rd tick (30s)
        if (_tickCount % 3 == 0)
        {
            _cachedGpuState = _dgpuMonitoring
                ? BatteryService.GetDGpuStatus()
                : "Monitoring off";
        }

        // Refresh rate: every 6th tick (60s) — uses Win32 API, very cheap
        if (_tickCount % 6 == 0)
        {
            int hz = BatteryService.GetDisplayRefreshRate();
            _cachedRefreshLabel = hz > 0 ? $"Display: {hz}Hz" : "Display: N/A";
        }

        _tickCount++;

        var (remainSec, _) = BatteryService.GetBatteryTimeRemaining();

        string status = charging ? "Charging" : "Discharging";
        string wattLabel = watts > 0 ? $"{status}: {watts:F1}W" : status;

        string timeLabel;
        if (charging && watts > 0)
        {
            // Estimate charge time: remaining % to fill at current charge rate
            // Razer Blade 16 (2025) = 90Wh battery
            double hoursLeft = (100 - percent) / 100.0 * 90.0 / watts;
            var ts = TimeSpan.FromHours(hoursLeft);
            timeLabel = $"~{(int)ts.TotalHours}h {ts.Minutes}m to full";
        }
        else if (!charging && remainSec > 0)
        {
            var ts = TimeSpan.FromSeconds(remainSec);
            timeLabel = $"{(int)ts.TotalHours}h {ts.Minutes}m remaining";
        }
        else
        {
            timeLabel = "";
        }

        string tooltip = $"Battery: {percent:F0}%\n{wattLabel}";
        if (timeLabel.Length > 0) tooltip += $"\n{timeLabel}";
        tooltip += $"\ndGPU: {_cachedGpuState}\n{_cachedRefreshLabel}";
        _trayIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        SetWattIcon(watts, percent, _cachedGpuState);
    }

    private void SetWattIcon(double watts, double percent, string gpuState)
    {
        string text = watts > 0
            ? $"{(int)Math.Round(watts)}"
            : $"{(int)percent}";

        const int size = 64;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        bool gpuAwake = gpuState.StartsWith("Awake", StringComparison.OrdinalIgnoreCase);
        using var brush = new SolidBrush(gpuAwake ? Color.FromArgb(239, 68, 68) : Color.FromArgb(34, 197, 94));

        // Clone GenericTypographic so we can add NoWrap without mutating the shared instance
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        sf.FormatFlags |= StringFormatFlags.NoWrap;

        // Start large and shrink until text fits within the icon with 4px padding each side
        float fontSize = 36f;
        Font font;
        SizeF textSize;
        do
        {
            font = new Font("Arial", fontSize, FontStyle.Bold, GraphicsUnit.Point);
            textSize = g.MeasureString(text, font, PointF.Empty, sf);
            if (textSize.Width <= size - 8) break;
            font.Dispose();
            fontSize -= 1f;
        } while (fontSize > 6f);

        float x = (size - textSize.Width) / 2f;
        float y = (size - textSize.Height) / 2f;
        g.DrawString(text, font, brush, x, y, sf);
        font.Dispose();

        IntPtr newHandle = bmp.GetHicon();
        IntPtr oldHandle = _dynamicIconHandle;
        _dynamicIconHandle = newHandle;
        _trayIcon.Icon = Icon.FromHandle(newHandle);

        if (oldHandle != IntPtr.Zero)
            DestroyIcon(oldHandle);
    }

    private void OnTrayClick(object? sender, EventArgs e)
    {
        if (e is MouseEventArgs me && me.Button != MouseButtons.Left)
            return;

        LaunchSynapse();
    }

    private static void LaunchSynapse()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SynapsePath,
                Arguments = SynapseArgs,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to launch Razer Synapse:\n{ex.Message}",
                "B-Helper",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ShowTopProcesses()
    {
        var procs = BatteryService.FetchTopProcesses();
        string message = procs.Count > 0
            ? string.Join("\n", procs)
            : "No processes found.";

        _trayIcon.BalloonTipTitle = "Top Processes (by CPU time)";
        _trayIcon.BalloonTipText = message;
        _trayIcon.ShowBalloonTip(5000);
    }

    private void ShowGpuProcesses()
    {
        var procs = BatteryService.FetchGpuProcesses();
        string message = procs.Count > 0
            ? string.Join("\n", procs)
            : "No GPU processes found.";

        _trayIcon.BalloonTipTitle = "dGPU Processes";
        _trayIcon.BalloonTipText = message;
        _trayIcon.ShowBalloonTip(5000);
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _trayIcon.Dispose();
        }
        if (_dynamicIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_dynamicIconHandle);
            _dynamicIconHandle = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }
}
