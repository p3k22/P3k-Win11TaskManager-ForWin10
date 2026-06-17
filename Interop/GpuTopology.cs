using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Win11TaskMan.Interop;

/// <summary>
/// Static GPU topology from the D3DKMT kernel-thunk interface — the same source
/// Task Manager uses for its engine list. Enumerates the primary adapter's engine
/// NODES (NODEMETADATA) to get Task-Manager-exact labels, and reads the adapter's
/// segment sizes (GETSEGMENTSIZE) for the dedicated/shared totals and the small
/// "hardware reserved" delta. All best-effort: returns empty when unsupported.
/// </summary>
public static class GpuTopology
{
    private const int KMTQAITYPE_GETSEGMENTSIZE = 3;
    private const int KMTQAITYPE_NODEMETADATA = 25;

    // sizeof(D3DKMT_NODEMETADATA) is exactly 78: UINT NodeOrdinalAndAdapterIndex(4)
    // + DXGK_NODEMETADATA{ DXGK_ENGINE_TYPE EngineType(4) + WCHAR FriendlyName[32](64)
    // + Flags(4) + BOOLEAN GpuMmu(1) + BOOLEAN IoMmu(1) }. The kernel validates
    // PrivateDriverDataSize against this exact value (any other size -> INVALID_PARAMETER).
    private const int NODEMETADATA_SIZE = 78;
    private const int ENGINETYPE_OFFSET = 4;
    private const int FRIENDLYNAME_OFFSET = 8;
    private const int SEGMENTSIZE_SIZE = 24; // D3DKMT_SEGMENTSIZEINFO = 3 x ULONG64

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pData; public uint Size; }
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }
    private const int ADAPTERINFO_STRIDE = 20; // D3DKMT_ADAPTERINFO, see GpuThermal

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER p);

    /// <summary>One scheduling engine of the GPU. Ordinal matches the PDH "eng_N" instance.</summary>
    public readonly record struct Node(int Ordinal, string Label, bool Show);
    public readonly record struct Segments(ulong DedicatedVideoBytes, ulong SharedSystemBytes);

    private static IReadOnlyList<Node>? _cached;

    /// <summary>Engine nodes of the primary (highest-VRAM) adapter, Task-Manager labelled and ordered. Cached.</summary>
    public static IReadOnlyList<Node> GetNodes() => _cached ??= Enumerate();

    /// <summary>Dedicated/shared segment sizes of the primary adapter. Populated by the first <see cref="GetNodes"/>.</summary>
    public static Segments PrimarySegments { get; private set; }

    private static IReadOnlyList<Node> Enumerate()
    {
        var result = new List<Node>();
        IntPtr buf = Marshal.AllocHGlobal(ADAPTERINFO_STRIDE * 16);
        try
        {
            var en = new D3DKMT_ENUMADAPTERS2 { NumAdapters = 16, pAdapters = buf };
            if (D3DKMTEnumAdapters2(ref en) != 0) return result;

            // Several adapters enumerate (the real GPU plus the Basic-Render / WARP
            // adapter). Pick the one with the most dedicated video memory.
            var handles = new List<uint>();
            uint best = 0; ulong bestVram = 0; Segments bestSeg = default;
            for (int i = 0; i < en.NumAdapters; i++)
            {
                uint h = (uint)Marshal.ReadInt32(buf + i * ADAPTERINFO_STRIDE);
                handles.Add(h);
                var seg = QuerySegments(h);
                if (seg.DedicatedVideoBytes > bestVram) { bestVram = seg.DedicatedVideoBytes; best = h; bestSeg = seg; }
            }
            PrimarySegments = bestSeg;
            if (best != 0) result.AddRange(EnumerateNodes(best));

            foreach (uint h in handles)
            {
                var c = new D3DKMT_CLOSEADAPTER { hAdapter = h };
                D3DKMTCloseAdapter(ref c);
            }
        }
        catch { /* leave empty -> callers fall back */ }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    private static Segments QuerySegments(uint hAdapter)
    {
        IntPtr data = Marshal.AllocHGlobal(SEGMENTSIZE_SIZE);
        try
        {
            for (int b = 0; b < SEGMENTSIZE_SIZE; b++) Marshal.WriteByte(data, b, 0);
            var q = new D3DKMT_QUERYADAPTERINFO
            { hAdapter = hAdapter, Type = KMTQAITYPE_GETSEGMENTSIZE, pData = data, Size = SEGMENTSIZE_SIZE };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return default;
            ulong dedicated = (ulong)Marshal.ReadInt64(data, 0);   // DedicatedVideoMemorySize
            ulong shared = (ulong)Marshal.ReadInt64(data, 16);     // SharedSystemMemorySize
            return new Segments(dedicated, shared);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private static List<Node> EnumerateNodes(uint hAdapter)
    {
        var raw = new List<(int ord, int type, string friendly)>();
        IntPtr data = Marshal.AllocHGlobal(NODEMETADATA_SIZE);
        try
        {
            for (int node = 0; node < 64; node++)
            {
                for (int b = 0; b < NODEMETADATA_SIZE; b++) Marshal.WriteByte(data, b, 0);
                Marshal.WriteInt32(data, 0, node); // NodeOrdinalAndAdapterIndex (phys index 0)
                var q = new D3DKMT_QUERYADAPTERINFO
                { hAdapter = hAdapter, Type = KMTQAITYPE_NODEMETADATA, pData = data, Size = NODEMETADATA_SIZE };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) break; // INVALID_PARAMETER once past the last node
                int type = Marshal.ReadInt32(data, ENGINETYPE_OFFSET);
                string friendly = Marshal.PtrToStringUni(data + FRIENDLYNAME_OFFSET) ?? "";
                raw.Add((node, type, friendly));
            }
        }
        finally { Marshal.FreeHGlobal(data); }

        // Label like Task Manager: use the driver's friendly name when present
        // ("Compute 0"); otherwise the engine-type name, numbering duplicates
        // ("Copy", "Copy 1").
        var typeCount = new Dictionary<string, int>();
        var nodes = new List<Node>();
        foreach (var (ord, type, friendly) in raw)
        {
            string label;
            if (friendly.Length > 0)
                label = friendly;
            else
            {
                string baseName = TypeName(type);
                int c = typeCount.GetValueOrDefault(baseName);
                typeCount[baseName] = c + 1;
                label = c == 0 ? baseName : $"{baseName} {c}";
            }
            nodes.Add(new Node(ord, label, IsShown(label)));
        }

        // Order the way Task Manager presents them: 3D, Copy*, Compute*, Video*, rest.
        return nodes.OrderBy(n => Rank(n.Label)).ThenBy(n => n.Ordinal).ToList();
    }

    // DXGK_ENGINE_TYPE -> Task Manager's name. OTHER(0) nodes always carry a
    // friendly name, so they never reach here.
    private static string TypeName(int t) => t switch
    {
        1 => "3D",
        2 => "Video Decode",
        3 => "Video Encode",
        4 => "Video Processing",
        5 => "Scene Assembly",
        6 => "Copy",
        7 => "Overlay",
        8 => "Crypto",
        _ => "Other"
    };

    // The driver's internal scheduling / auxiliary nodes that Task Manager doesn't
    // surface as work engines.
    private static readonly string[] Hidden =
        { "Timer", "Security", "Audio", "High Priority", "JPEG", "Overlay", "Crypto", "Scene Assembly", "Other" };

    private static bool IsShown(string label)
    {
        foreach (var h in Hidden)
            if (label.Contains(h, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static int Rank(string label) =>
          label.StartsWith("3D", StringComparison.Ordinal) ? 0
        : label.StartsWith("Copy", StringComparison.Ordinal) ? 1
        : label.StartsWith("Compute", StringComparison.Ordinal) ? 2
        : label.StartsWith("Video", StringComparison.Ordinal) ? 3
        : 4;
}
