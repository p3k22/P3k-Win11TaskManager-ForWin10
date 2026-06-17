using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Win11TaskMan.Interop;

/// <summary>
/// GPU utilization + dedicated memory via the PDH performance library, reading
/// the same "GPU Engine" / "GPU Adapter Memory" counters Task Manager uses.
/// The query is opened once and re-collected each tick (utilization is a rate
/// counter, so it needs a previous sample). Everything is best-effort: if PDH
/// or the GPU counters are unavailable, <see cref="Available"/> stays false and
/// the rest of the app treats the GPU as absent.
/// </summary>
public sealed class GpuCounters : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const int ITEM_STRIDE = 24; // x64: LPWSTR(8) + PDH_FMT_COUNTERVALUE(16)

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private IntPtr _query, _util, _dedicated, _shared;

    public bool Available { get; }
    public string Luid { get; private set; } = ""; // "0xHIGH_0xLOW" from instance names

    public GpuCounters()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
            bool util = PdhAddEnglishCounterW(_query,
                @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _util) == 0;
            // Memory counters are optional; ignore failure.
            PdhAddEnglishCounterW(_query,
                @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _dedicated);
            PdhAddEnglishCounterW(_query,
                @"\GPU Adapter Memory(*)\Shared Usage", IntPtr.Zero, out _shared);
            if (!util) return;
            PdhCollectQueryData(_query); // prime the baseline sample
            Available = true;
        }
        catch { /* no PDH / no GPU counters -> Available stays false */ }
    }

    public readonly record struct Reading(
        double UtilPercent, ulong DedicatedUsedBytes, ulong SharedUsedBytes,
        Dictionary<int, double> PerPid, Dictionary<int, double> NodeUtil);

    public Reading Read()
    {
        var empty = new Reading(0, 0, 0, new(), new());
        if (!Available || PdhCollectQueryData(_query) != 0) return empty;

        double overall = 0;
        var perPid = new Dictionary<int, double>();
        // node ordinal -> (engtype string -> busiest value). The driver exposes
        // each node under a generic "3D" instance plus its specific type, so keep
        // both and resolve below.
        var nodeEngtypes = new Dictionary<int, Dictionary<string, double>>();
        foreach (var (name, val) in ReadArray(_util))
        {
            if (val > overall) overall = val;
            if (Luid.Length == 0) Luid = ParseLuid(name);

            int pid = ParsePid(name);
            if (pid > 0 && (!perPid.TryGetValue(pid, out double cur) || val > cur))
                perPid[pid] = val; // a process's GPU% is its busiest engine

            int eng = ParseEng(name);
            if (eng >= 0)
            {
                string t = ParseEngtype(name);
                var m = nodeEngtypes.TryGetValue(eng, out var mm) ? mm : (nodeEngtypes[eng] = new());
                if (!m.TryGetValue(t, out double e) || val > e) m[t] = val;
            }
        }

        // A node's load is its specific engine (the non-generic-"3D" engtype the
        // driver reports for it); the catch-all "3D" instance present on every node
        // would otherwise bleed graphics load onto every engine. Pure-3D nodes
        // (only a "3D" engtype) use that.
        var nodeUtil = new Dictionary<int, double>();
        foreach (var (eng, m) in nodeEngtypes)
        {
            double v = 0; bool specific = false;
            foreach (var (t, val) in m)
                if (!t.Equals("3D", StringComparison.Ordinal)) { v = Math.Max(v, val); specific = true; }
            if (!specific) m.TryGetValue("3D", out v);
            nodeUtil[eng] = Math.Min(v, 100);
        }

        return new Reading(Math.Min(overall, 100), SumMem(_dedicated), SumMem(_shared), perPid, nodeUtil);
    }

    private static ulong SumMem(IntPtr counter)
    {
        ulong sum = 0;
        foreach (var (_, val) in ReadArray(counter))
            sum += val > 0 ? (ulong)val : 0;
        return sum;
    }

    private static List<(string name, double val)> ReadArray(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;

        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero) != PDH_MORE_DATA)
            return list;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, buf) != 0)
                return list;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buf + i * ITEM_STRIDE;
                IntPtr namePtr = Marshal.ReadIntPtr(item, 0);
                double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                string name = namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr) ?? "" : "";
                list.Add((name, val));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    // instance names look like "pid_1234_luid_0x0_0xABCD_phys_0_eng_0_engtype_3D"
    private static int ParsePid(string name)
    {
        const string tag = "pid_";
        int i = name.IndexOf(tag, StringComparison.Ordinal);
        if (i < 0) return 0;
        i += tag.Length;
        int j = i;
        while (j < name.Length && char.IsDigit(name[j])) j++;
        return j > i && int.TryParse(name.AsSpan(i, j - i), out int pid) ? pid : 0;
    }

    // "..._phys_0_eng_12_engtype_..." -> 12 (the engine NODE ordinal, which lines
    // up with D3DKMT NODEMETADATA node ordinals — see GpuTopology).
    private static int ParseEng(string name)
    {
        const string tag = "_eng_";
        int i = name.IndexOf(tag, StringComparison.Ordinal);
        if (i < 0) return -1;
        i += tag.Length;
        int j = i;
        while (j < name.Length && char.IsDigit(name[j])) j++;
        return j > i && int.TryParse(name.AsSpan(i, j - i), out int e) ? e : -1;
    }

    // "..._engtype_Compute 0" -> "Compute 0"
    private static string ParseEngtype(string name)
    {
        const string tag = "engtype_";
        int i = name.IndexOf(tag, StringComparison.Ordinal);
        return i < 0 ? "" : name[(i + tag.Length)..];
    }

    // pull "luid_0xHIGH_0xLOW" out of the instance name
    private static string ParseLuid(string name)
    {
        const string tag = "luid_";
        int i = name.IndexOf(tag, StringComparison.Ordinal);
        if (i < 0) return "";
        int start = i + tag.Length;
        int end = name.IndexOf("_phys", start, StringComparison.Ordinal);
        return end > start ? name[start..end] : "";
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}
