using System.Runtime.InteropServices;
using TouchPortalHardwareMonitor.Models;

namespace TouchPortalHardwareMonitor.Services;

/// <summary>
/// Enumerates connected displays and their current mode (resolution +
/// refresh rate) using Win32 display APIs. Deliberately independent of
/// LibreHardwareMonitor - needs no kernel driver and no elevation.
/// </summary>
public static class DisplayInfoService
{
    public static List<DisplayInfo> GetDisplays()
    {
        var results = new List<DisplayInfo>();

        var adapter = new DISPLAY_DEVICE();
        adapter.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

        uint adapterIndex = 0;
        int displayIndex = 0;

        while (EnumDisplayDevices(null, adapterIndex, ref adapter, 0))
        {
            adapterIndex++;

            // Only adapters actually driving the desktop have a live mode.
            if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                var mode = new DEVMODE();
                mode.dmSize = (short)Marshal.SizeOf<DEVMODE>();

                if (EnumDisplaySettings(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref mode))
                {
                    displayIndex++;

                    // Prefer the attached monitor's friendly name over the adapter.
                    var name = adapter.DeviceString;
                    var monitor = new DISPLAY_DEVICE();
                    monitor.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
                    if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0)
                        && !string.IsNullOrWhiteSpace(monitor.DeviceString))
                    {
                        name = monitor.DeviceString;
                    }

                    results.Add(new DisplayInfo
                    {
                        Index = displayIndex,
                        Name = name,
                        IsPrimary = (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                        Width = (int)mode.dmPelsWidth,
                        Height = (int)mode.dmPelsHeight,
                        RefreshRateHz = (int)mode.dmDisplayFrequency
                    });
                }
            }

            // cb is overwritten by the call; reset before reusing the struct.
            adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }

        return results;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public int dmDisplayFlags;
        public uint dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
