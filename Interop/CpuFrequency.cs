using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Win11TaskMan.Interop;

/// <summary>
/// Live CPU clock speed the way Task Manager computes it: base frequency ×
/// "% Processor Performance" (which exceeds 100% under turbo). The PDH counter
/// reflects the real boosting clock, unlike CallNtPowerInformation's CurrentMhz
/// which reports the nominal speed on Ryzen. Falls back to the registry base.
/// </summary>
public sealed class CpuFrequency : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x200;
    private const uint PDH_FMT_NOCAP100 = 0x8000; // don't clamp turbo % to 100

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? s, IntPtr u, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr q, string p, IntPtr u, out IntPtr c);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr c, uint fmt, out uint type, out PDH_FMT_COUNTERVALUE v);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr q);

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    private IntPtr _query, _perf, _freq;
    private readonly bool _ok;
    private readonly double _baseMhz;

    public CpuFrequency()
    {
        _baseMhz = ReadBaseMhz();
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
            bool perf = PdhAddEnglishCounterW(_query,
                @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _perf) == 0;
            PdhAddEnglishCounterW(_query,
                @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _freq);
            if (!perf) return;
            PdhCollectQueryData(_query); // prime baseline
            _ok = true;
        }
        catch { /* no PDH -> fall back to base speed */ }
    }

    /// <summary>Live clock in MHz (base × turbo %); base speed if unavailable.</summary>
    public double CurrentMhz()
    {
        if (!_ok || PdhCollectQueryData(_query) != 0) return _baseMhz;
        double perf = Read(_perf);
        double baseFreq = _freq != IntPtr.Zero ? Read(_freq) : 0;
        if (baseFreq <= 0) baseFreq = _baseMhz;
        return perf > 0 && baseFreq > 0 ? baseFreq * perf / 100.0 : _baseMhz;
    }

    private static double Read(IntPtr c)
        => PdhGetFormattedCounterValue(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) == 0
            ? v.doubleValue : 0;

    private static double ReadBaseMhz()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return Convert.ToDouble(k?.GetValue("~MHz") ?? 0);
        }
        catch { return 0; }
    }

    public void Dispose() { if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; } }
}
