using System;
using System.Runtime.InteropServices;

namespace Win11TaskMan.Interop;

/// <summary>
/// GPU temperature (and fan/power) via the D3DKMT kernel-thunk interface, the
/// same source Task Manager uses. Enumerate adapters, then query
/// ADAPTERPERFDATA which exposes Temperature in tenths of a degree Celsius.
/// All best-effort: returns 0 when unsupported.
/// </summary>
public static class GpuThermal
{
    // KMTQAITYPE_ADAPTERPERFDATA. The numeric value drifts between Windows
    // builds; 62 matches current Win10/11 where the perf-data struct is 64
    // bytes with Temperature (deci-°C) at offset 56. Verified against the live
    // adapter (reads the same value Task Manager shows).
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;
    private const int PERFDATA_SIZE = 64;
    private const int TEMPERATURE_OFFSET = 56;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    // D3DKMT_ADAPTERINFO: hAdapter(0), AdapterLuid(4, 8 bytes, 4-aligned),
    // NumOfSources(12), bPrecisePresentRegionsPreferred(16) -> 20 bytes
    private const int ADAPTERINFO_STRIDE = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER p);

    /// <summary>Highest GPU temperature in °C across adapters, or 0 if unavailable.</summary>
    public static double GetTemperatureC()
    {
        const int max = 16;
        IntPtr buf = Marshal.AllocHGlobal(ADAPTERINFO_STRIDE * max);
        try
        {
            var en = new D3DKMT_ENUMADAPTERS2 { NumAdapters = max, pAdapters = buf };
            if (D3DKMTEnumAdapters2(ref en) != 0) return 0;

            double best = 0;
            for (int i = 0; i < en.NumAdapters; i++)
            {
                uint hAdapter = (uint)Marshal.ReadInt32(buf + i * ADAPTERINFO_STRIDE);
                double t = QueryTemp(hAdapter);
                if (t > best) best = t;

                var close = new D3DKMT_CLOSEADAPTER { hAdapter = hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
            return best;
        }
        catch { return 0; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static double QueryTemp(uint hAdapter)
    {
        IntPtr data = Marshal.AllocHGlobal(PERFDATA_SIZE);
        try
        {
            for (int b = 0; b < PERFDATA_SIZE; b++) Marshal.WriteByte(data, b, 0); // PhysicalAdapterIndex = 0
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = hAdapter,
                Type = KMTQAITYPE_ADAPTERPERFDATA,
                pPrivateDriverData = data,
                PrivateDriverDataSize = PERFDATA_SIZE
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return 0;
            double c = (uint)Marshal.ReadInt32(data, TEMPERATURE_OFFSET) / 10.0;
            return c is > 0 and < 150 ? c : 0; // sanity bound
        }
        finally { Marshal.FreeHGlobal(data); }
    }
}
