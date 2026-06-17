using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Win11TaskMan.Interop;

/// <summary>
/// Thin P/Invoke layer. All struct offsets here assume a 64-bit process
/// (csproj pins PlatformTarget=x64). Do not build x86 against this.
/// </summary>
internal static partial class NativeMethods
{
    // ---- NtQuerySystemInformation: one call, every process ----
    private const int SystemProcessInformation = 5;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation,
        int systemInformationLength, out int returnLength);

    // Only the fields we actually read are mapped (explicit x64 offsets).
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        [FieldOffset(0)]   public uint NextEntryOffset;
        [FieldOffset(4)]   public uint NumberOfThreads;
        [FieldOffset(40)]  public long UserTime;          // 100ns units
        [FieldOffset(48)]  public long KernelTime;        // 100ns units
        [FieldOffset(56)]  public ushort ImageNameLength; // bytes
        [FieldOffset(64)]  public IntPtr ImageNameBuffer; // PWSTR
        [FieldOffset(80)]  public IntPtr UniqueProcessId;
        [FieldOffset(96)]  public uint HandleCount;
        [FieldOffset(100)] public uint SessionId;
        [FieldOffset(144)] public long WorkingSetSize;    // bytes
        [FieldOffset(232)] public long ReadTransferCount; // bytes
        [FieldOffset(240)] public long WriteTransferCount;// bytes
        [FieldOffset(248)] public long OtherTransferCount;// bytes
    }

    public readonly record struct RawProcess(
        int Pid, string Name, long CpuTime100ns,
        long WorkingSet, long IoBytes, uint Threads, uint Handles, uint SessionId);

    /// <summary>Snapshot every process in a single syscall. Cheap; safe to call ~1Hz.</summary>
    public static List<RawProcess> SnapshotProcesses()
    {
        var list = new List<RawProcess>(400);
        int size = 512 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint status;
            int returnLength;
            while ((status = NtQuerySystemInformation(
                       SystemProcessInformation, buffer, size, out returnLength))
                   == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(buffer);
                size *= 2;
                buffer = Marshal.AllocHGlobal(size);
            }
            if (status != 0) return list;

            // Bound the walk to the bytes the kernel actually wrote (never past the
            // allocation), so a bad NextEntryOffset can't march us off the buffer
            // into an AccessViolationException a normal catch couldn't recover from.
            long start = buffer.ToInt64();
            long end = start + (returnLength > 0 ? Math.Min(returnLength, size) : size);
            int header = Marshal.SizeOf<SYSTEM_PROCESS_INFORMATION>();
            IntPtr cur = buffer;
            while (cur.ToInt64() + header <= end)
            {
                var spi = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(cur);

                string name = spi.ImageNameBuffer != IntPtr.Zero && spi.ImageNameLength > 0
                    ? Marshal.PtrToStringUni(spi.ImageNameBuffer, spi.ImageNameLength / 2) ?? ""
                    : "System Idle Process";

                list.Add(new RawProcess(
                    Pid: spi.UniqueProcessId.ToInt32(),
                    Name: name,
                    CpuTime100ns: spi.KernelTime + spi.UserTime,
                    WorkingSet: spi.WorkingSetSize,
                    IoBytes: spi.ReadTransferCount + spi.WriteTransferCount + spi.OtherTransferCount,
                    Threads: spi.NumberOfThreads,
                    Handles: spi.HandleCount,
                    SessionId: spi.SessionId));

                // Stop on the terminator, and bail on any offset that doesn't move
                // strictly forward (0, or a value large enough to wrap) rather than
                // risk a backward jump or an infinite loop.
                if (spi.NextEntryOffset == 0 || spi.NextEntryOffset > int.MaxValue) break;
                cur += (int)spi.NextEntryOffset;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    // ---- System-wide CPU via GetSystemTimes ----
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public static bool GetSystemCpuTimes(out long idle, out long kernel, out long user)
        => GetSystemTimes(out idle, out kernel, out user);

    // ---- Physical memory ----
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    public static (ulong totalBytes, ulong usedBytes, uint loadPct) GetMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        return (m.ullTotalPhys, m.ullTotalPhys - m.ullAvailPhys, m.dwMemoryLoad);
    }

    // ---- Live clock speed ----
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int level, IntPtr inBuf, uint inLen, IntPtr outBuf, uint outLen);

    public static double GetCurrentMhz()
    {
        int n = Environment.ProcessorCount;
        int sz = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(sz * n);
        try
        {
            if (CallNtPowerInformation(11, IntPtr.Zero, 0, buf, (uint)(sz * n)) != 0) return 0;
            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(buf + i * sz).CurrentMhz;
            return sum / n;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---- Static CPU topology + cache ----
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct LOGICAL_PROCESSOR_INFO
    {
        [FieldOffset(0)]  public UIntPtr ProcessorMask;
        [FieldOffset(8)]  public int Relationship;   // 0=Core 1=NUMA 2=Cache 3=Package
        [FieldOffset(16)] public byte CacheLevel;    // overlay: CACHE_DESCRIPTOR.Level
        [FieldOffset(20)] public uint CacheSize;      // overlay: CACHE_DESCRIPTOR.Size
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnLength);

    public sealed record CpuTopology(
        int PhysicalCores, int LogicalProcessors, int Sockets,
        long L1Bytes, long L2Bytes, long L3Bytes);

    public static CpuTopology GetTopology()
    {
        int logical = Environment.ProcessorCount;
        uint len = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref len);
        if (len == 0) return new CpuTopology(logical, logical, 1, 0, 0, 0);

        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformation(buf, ref len))
                return new CpuTopology(logical, logical, 1, 0, 0, 0);

            int stride = Marshal.SizeOf<LOGICAL_PROCESSOR_INFO>();
            int count = (int)len / stride;
            int cores = 0, sockets = 0;
            long l1 = 0, l2 = 0, l3 = 0;

            for (int i = 0; i < count; i++)
            {
                var e = Marshal.PtrToStructure<LOGICAL_PROCESSOR_INFO>(buf + i * stride);
                switch (e.Relationship)
                {
                    case 0: cores++; break;        // RelationProcessorCore
                    case 3: sockets++; break;      // RelationProcessorPackage
                    case 2:                          // RelationCache
                        switch (e.CacheLevel)
                        {
                            case 1: l1 += e.CacheSize; break;
                            case 2: l2 += e.CacheSize; break;
                            case 3: l3 += e.CacheSize; break;
                        }
                        break;
                }
            }
            return new CpuTopology(cores == 0 ? logical : cores, logical,
                                   sockets == 0 ? 1 : sockets, l1, l2, l3);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static bool IsVirtualizationEnabled()
    {
        try { return IsProcessorFeaturePresent(21); } // PF_VIRT_FIRMWARE_ENABLED
        catch { return false; }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessorFeaturePresent(uint feature);

    public static ulong GetUptimeMs() => GetTickCount64();

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    // ---- Which PIDs own a visible top-level window (Apps vs Background) ----
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public static HashSet<int> GetAppPids()
    {
        var pids = new HashSet<int>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindowTextLength(h) > 0)
            {
                GetWindowThreadProcessId(h, out uint pid);
                pids.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }
}
