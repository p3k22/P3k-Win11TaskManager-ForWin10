using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Win11TaskMan.Interop;

/// <summary>
/// SetupAPI lookup for the GPU's "Physical location" string (e.g.
/// "PCI bus 45, device 0, function 0"). This is the SPDRP_LOCATION_INFORMATION
/// device property — already formatted by the PnP manager, the same text Task
/// Manager shows. The display-adapter class key the registry path uses elsewhere
/// often leaves LocationInformation blank, so we read it from the device here.
/// </summary>
internal static partial class NativeMethods
{
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA did);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr set, ref SP_DEVINFO_DATA did, uint property,
        out uint regDataType, byte[] buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint SPDRP_LOCATION_INFORMATION = 0x0000000D;

    /// <summary>
    /// "Physical location" for a display adapter. Prefers the adapter whose
    /// description matches <paramref name="preferDescription"/>; otherwise the
    /// first adapter that reports a location. Empty string if unavailable.
    /// </summary>
    public static string GetGpuLocation(string preferDescription)
    {
        var displayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318"); // GUID_DEVCLASS_DISPLAY
        IntPtr set = SetupDiGetClassDevsW(ref displayClass, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == new IntPtr(-1)) return "";
        try
        {
            string firstLocation = "";
            var did = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref did); i++)
            {
                string loc = RegProp(set, ref did, SPDRP_LOCATION_INFORMATION);
                if (loc.Length == 0) continue;
                if (preferDescription.Length > 0 &&
                    RegProp(set, ref did, SPDRP_DEVICEDESC) == preferDescription) return loc;
                if (firstLocation.Length == 0) firstLocation = loc;
            }
            return firstLocation;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static string RegProp(IntPtr set, ref SP_DEVINFO_DATA did, uint property)
    {
        var b = new byte[512];
        if (SetupDiGetDeviceRegistryPropertyW(set, ref did, property, out _, b, (uint)b.Length, out uint req)
            && req >= 2)
            return Encoding.Unicode.GetString(b, 0, (int)req - 2); // drop the terminating NUL
        return "";
    }
}
