using System.Runtime.InteropServices;

namespace BHelper;

internal static class GpuPowerState
{
    private static readonly Guid GUID_DEVCLASS_DISPLAY =
        new("4d36e968-e325-11ce-bfc1-08002be10318");

    private const int DIGCF_PRESENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    private static readonly DEVPROPKEY DEVPKEY_Device_PowerData = new()
    {
        fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd,
                         0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
        pid = 32
    };

    private static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new()
    {
        fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd,
                         0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
        pid = 2
    };

    private const uint DEVPROP_TYPE_STRING = 0x00000012;
    private const uint DEVPROP_TYPE_BINARY = 0x00001003;

    private enum DEVICE_POWER_STATE
    {
        PowerDeviceUnspecified = 0,
        PowerDeviceD0 = 1,
        PowerDeviceD1 = 2,
        PowerDeviceD2 = 3,
        PowerDeviceD3 = 4,
        PowerDeviceMaximum = 5
    }

    private const int POWER_SYSTEM_MAXIMUM = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct CM_POWER_DATA
    {
        public uint PD_Size;
        public DEVICE_POWER_STATE PD_MostRecentPowerState;
        public uint PD_Capabilities;
        public uint PD_D1Latency;
        public uint PD_D2Latency;
        public uint PD_D3Latency;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = POWER_SYSTEM_MAXIMUM)]
        public DEVICE_POWER_STATE[] PD_PowerStateMapping;
        public int PD_DeepestSystemWake;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        ref DEVPROPKEY propertyKey, out uint propertyType,
        byte[]? propertyBuffer, uint propertyBufferSize,
        out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    public static string GetNvidiaGpuPowerState()
    {
        Guid displayClass = GUID_DEVCLASS_DISPLAY;
        IntPtr hDevInfo = SetupDiGetClassDevs(
            ref displayClass, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);

        if (hDevInfo == IntPtr.Zero || hDevInfo == new IntPtr(-1))
            return "SetupDi failed";

        try
        {
            var devInfoData = new SP_DEVINFO_DATA();
            devInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

            for (uint i = 0; SetupDiEnumDeviceInfo(hDevInfo, i, ref devInfoData); i++)
            {
                string? desc = GetStringProperty(hDevInfo, ref devInfoData, DEVPKEY_Device_DeviceDesc);
                if (desc == null || !desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    continue;

                return ReadPowerState(hDevInfo, ref devInfoData);
            }

            return "Not found";
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }
    }

    private static string? GetStringProperty(
        IntPtr hDevInfo, ref SP_DEVINFO_DATA devInfoData, DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(hDevInfo, ref devInfoData, ref key,
            out _, null, 0, out uint reqSize, 0);

        if (reqSize == 0) return null;

        byte[] buffer = new byte[reqSize];
        if (!SetupDiGetDevicePropertyW(hDevInfo, ref devInfoData, ref key,
                out uint propType, buffer, reqSize, out _, 0))
            return null;

        if (propType != DEVPROP_TYPE_STRING) return null;
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string ReadPowerState(
        IntPtr hDevInfo, ref SP_DEVINFO_DATA devInfoData)
    {
        var key = DEVPKEY_Device_PowerData;

        SetupDiGetDevicePropertyW(hDevInfo, ref devInfoData, ref key,
            out _, null, 0, out uint reqSize, 0);

        if (reqSize < Marshal.SizeOf<CM_POWER_DATA>())
            return "No power data";

        byte[] buffer = new byte[reqSize];
        if (!SetupDiGetDevicePropertyW(hDevInfo, ref devInfoData, ref key,
                out uint propType, buffer, reqSize, out _, 0))
            return $"Read failed (err {Marshal.GetLastWin32Error()})";

        if (propType != DEVPROP_TYPE_BINARY)
            return "Unexpected type";

        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var powerData = Marshal.PtrToStructure<CM_POWER_DATA>(handle.AddrOfPinnedObject());
            return powerData.PD_MostRecentPowerState switch
            {
                DEVICE_POWER_STATE.PowerDeviceD0 => "D0",
                DEVICE_POWER_STATE.PowerDeviceD1 => "D1",
                DEVICE_POWER_STATE.PowerDeviceD2 => "D2",
                DEVICE_POWER_STATE.PowerDeviceD3 => "D3",
                _ => $"Unknown ({(int)powerData.PD_MostRecentPowerState})"
            };
        }
        finally
        {
            handle.Free();
        }
    }
}
