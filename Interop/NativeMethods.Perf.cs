using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Win11TaskMan.Interop;

/// <summary>
/// Extra P/Invoke for the Performance tab: per-core CPU, physical disk
/// counters, network interface throughput and detailed memory composition.
/// 64-bit layout assumed throughout (csproj pins x64).
/// </summary>
internal static partial class NativeMethods
{
    // ===================== Per-core CPU =====================
    // NtQuerySystemInformation(SystemProcessorPerformanceInformation) returns
    // one SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION per logical processor.
    private const int SystemProcessorPerformanceInformation = 8;

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct PROCESSOR_PERFORMANCE
    {
        [FieldOffset(0)]  public long IdleTime;   // 100ns
        [FieldOffset(8)]  public long KernelTime; // 100ns (includes idle)
        [FieldOffset(16)] public long UserTime;   // 100ns
    }

    /// <summary>Raw idle/kernel/user 100ns counters for each logical CPU.</summary>
    public static (long idle, long kernel, long user)[] GetPerCoreCpuTimes()
    {
        int n = Environment.ProcessorCount;
        int stride = Marshal.SizeOf<PROCESSOR_PERFORMANCE>();
        IntPtr buf = Marshal.AllocHGlobal(stride * n);
        try
        {
            if (NtQuerySystemInformation(SystemProcessorPerformanceInformation,
                    buf, stride * n, out _) != 0)
                return Array.Empty<(long, long, long)>();

            var result = new (long, long, long)[n];
            for (int i = 0; i < n; i++)
            {
                var p = Marshal.PtrToStructure<PROCESSOR_PERFORMANCE>(buf + i * stride);
                result[i] = (p.IdleTime, p.KernelTime, p.UserTime);
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ===================== Physical disk =====================
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_DISK_PERFORMANCE = 0x70020;
    // GEOMETRY_EX is FILE_ANY_ACCESS, so it works on a 0-access handle (no admin);
    // GET_LENGTH_INFO needs FILE_READ_ACCESS and would fail here.
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x700A0;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x560000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device, uint code, IntPtr inBuf, uint inSize,
        IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    // Full struct is 88 bytes (QueryTime at 56, StorageDeviceNumber at 64,
    // StorageManagerName[8] at 68). IOCTL_DISK_PERFORMANCE needs the whole
    // buffer or it fails with insufficient-buffer, so Size must be >= 88.
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    private struct DISK_PERFORMANCE
    {
        [FieldOffset(0)]  public long BytesRead;
        [FieldOffset(8)]  public long BytesWritten;
        [FieldOffset(16)] public long ReadTime;    // 100ns
        [FieldOffset(24)] public long WriteTime;   // 100ns
        [FieldOffset(32)] public long IdleTime;    // 100ns
        [FieldOffset(40)] public uint ReadCount;
        [FieldOffset(44)] public uint WriteCount;
        [FieldOffset(56)] public long QueryTime;   // 100ns timestamp
    }

    public readonly record struct DiskCounters(
        long BytesRead, long BytesWritten, long ReadTime, long WriteTime,
        long IdleTime, long QueryTime, uint ReadCount, uint WriteCount);

    /// <summary>Raw IO counters for \\.\PhysicalDrive{index}. null if unavailable.</summary>
    public static DiskCounters? GetDiskCounters(int index)
    {
        IntPtr h = CreateFileW($@"\\.\PhysicalDrive{index}", 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return null;
        int sz = Marshal.SizeOf<DISK_PERFORMANCE>();
        IntPtr buf = Marshal.AllocHGlobal(sz);
        try
        {
            if (!DeviceIoControl(h, IOCTL_DISK_PERFORMANCE, IntPtr.Zero, 0, buf, (uint)sz, out _, IntPtr.Zero))
                return null;
            var d = Marshal.PtrToStructure<DISK_PERFORMANCE>(buf);
            return new DiskCounters(d.BytesRead, d.BytesWritten, d.ReadTime, d.WriteTime,
                d.IdleTime, d.QueryTime, d.ReadCount, d.WriteCount);
        }
        finally { Marshal.FreeHGlobal(buf); CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters; // anysize byte[1]
    }

    public sealed record DiskInfo(string Model, long CapacityBytes, bool IsSsd);

    /// <summary>Static info for \\.\PhysicalDrive{index} (capacity, SSD vs HDD).</summary>
    public static DiskInfo? GetDiskInfo(int index)
    {
        IntPtr h = CreateFileW($@"\\.\PhysicalDrive{index}", 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return null;
        try
        {
            // DISK_GEOMETRY_EX: DiskSize (LARGE_INTEGER) sits at offset 24, after
            // the 24-byte DISK_GEOMETRY. 32-byte buffer covers what we need.
            long capacity = 0;
            IntPtr geoBuf = Marshal.AllocHGlobal(32);
            try
            {
                if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero, 0, geoBuf, 32, out _, IntPtr.Zero))
                    capacity = Marshal.ReadInt64(geoBuf, 24);
            }
            finally { Marshal.FreeHGlobal(geoBuf); }

            // Seek-penalty descriptor: IncursSeekPenalty==false => SSD.
            bool isSsd = false;
            var q = new STORAGE_PROPERTY_QUERY { PropertyId = 7 /*SeekPenalty*/, QueryType = 0 };
            int qsz = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
            IntPtr qbuf = Marshal.AllocHGlobal(qsz);
            IntPtr outBuf = Marshal.AllocHGlobal(16); // DEVICE_SEEK_PENALTY_DESCRIPTOR
            try
            {
                Marshal.StructureToPtr(q, qbuf, false);
                if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, qbuf, (uint)qsz, outBuf, 16, out uint ret, IntPtr.Zero)
                    && ret >= 9)
                    isSsd = Marshal.ReadByte(outBuf, 8) == 0; // offset 8 = IncursSeekPenalty (BOOLEAN)
            }
            finally { Marshal.FreeHGlobal(qbuf); Marshal.FreeHGlobal(outBuf); }

            return new DiskInfo($"Disk {index}", capacity, isSsd);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Physical-drive indices that can be opened, e.g. {0,1,2}. Probes 0..31.</summary>
    public static List<int> EnumPhysicalDriveIndexes()
    {
        var list = new List<int>();
        for (int i = 0; i < 32; i++)
        {
            IntPtr h = CreateFileW($@"\\.\PhysicalDrive{i}", 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) continue;
            list.Add(i);
            CloseHandle(h);
        }
        return list;
    }

    /// <summary>
    /// Maps each physical-drive index to the drive letters it hosts (e.g. 0 -> ["C:"]).
    /// Walks every logical volume and asks which physical disk(s) back it via
    /// IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS. Volumes that reject the IOCTL (network
    /// drives, empty optical drives) are skipped.
    /// </summary>
    public static Dictionary<int, List<string>> GetDiskDriveLetters()
    {
        var map = new Dictionary<int, List<string>>();
        foreach (var drive in Environment.GetLogicalDrives())
        {
            string letter = drive.TrimEnd('\\'); // "C:\" -> "C:"
            if (letter.Length < 2) continue;

            IntPtr h = CreateFileW($@"\\.\{letter}", 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) continue;

            // VOLUME_DISK_EXTENTS: DWORD NumberOfDiskExtents @0, then DISK_EXTENT[]
            // @8 (8-byte aligned). DISK_EXTENT is 24 bytes with DiskNumber @0.
            const int extentSize = 24, maxExtents = 16;
            int sz = 8 + extentSize * maxExtents;
            IntPtr buf = Marshal.AllocHGlobal(sz);
            try
            {
                if (DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0,
                        buf, (uint)sz, out _, IntPtr.Zero))
                {
                    int num = Marshal.ReadInt32(buf, 0);
                    for (int i = 0; i < num && i < maxExtents; i++)
                    {
                        int diskNo = Marshal.ReadInt32(buf, 8 + i * extentSize);
                        if (!map.TryGetValue(diskNo, out var lst)) map[diskNo] = lst = new List<string>();
                        if (!lst.Contains(letter)) lst.Add(letter);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); CloseHandle(h); }
        }
        return map;
    }

    // ===================== Network interfaces =====================
    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr table);

    // Field offsets inside MIB_IF_ROW2 (x64). Only the parts we read are mapped.
    private const int IFROW_DESCRIPTION = 542;   // WCHAR[257]
    private const int IFROW_TYPE = 1128;         // ULONG  (71=Wi-Fi, 6=Ethernet, 24=loopback)
    private const int IFROW_OPER_STATUS = 1156;  // ULONG  (1 = up)
    private const int IFROW_XMIT_SPEED = 1192;   // ULONG64 bits/sec
    private const int IFROW_IN_OCTETS = 1208;    // ULONG64
    private const int IFROW_OUT_OCTETS = 1280;   // ULONG64
    private const int IFROW_INDEX = 8;           // ULONG
    private const int IFROW_STRIDE = 1352;

    public readonly record struct NetIf(
        uint Index, string Description, uint Type, ulong LinkSpeed,
        ulong InOctets, ulong OutOctets);

    /// <summary>Every operational, non-loopback network interface and its octet counters.</summary>
    public static List<NetIf> GetNetInterfaces()
    {
        var list = new List<NetIf>();
        if (GetIfTable2(out IntPtr table) != 0) return list;
        try
        {
            uint count = (uint)Marshal.ReadInt32(table);
            IntPtr rows = table + 8; // ULONG NumEntries + 4 pad, rows start at 8
            for (int i = 0; i < count; i++)
            {
                IntPtr row = rows + i * IFROW_STRIDE;
                uint type = (uint)Marshal.ReadInt32(row, IFROW_TYPE);
                uint oper = (uint)Marshal.ReadInt32(row, IFROW_OPER_STATUS);
                if (oper != 1 || type == 24 /*loopback*/) continue;

                string desc = Marshal.PtrToStringUni(row + IFROW_DESCRIPTION) ?? "";
                list.Add(new NetIf(
                    (uint)Marshal.ReadInt32(row, IFROW_INDEX),
                    desc, type,
                    (ulong)Marshal.ReadInt64(row, IFROW_XMIT_SPEED),
                    (ulong)Marshal.ReadInt64(row, IFROW_IN_OCTETS),
                    (ulong)Marshal.ReadInt64(row, IFROW_OUT_OCTETS)));
            }
        }
        finally { FreeMibTable(table); }
        return list;
    }

    // ===================== Detailed memory =====================
    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

    public readonly record struct MemoryDetail(
        ulong CommittedBytes, ulong CommitLimitBytes, ulong CachedBytes,
        ulong PagedPoolBytes, ulong NonPagedPoolBytes);

    public static MemoryDetail GetMemoryDetail()
    {
        var pi = new PERFORMANCE_INFORMATION();
        if (!GetPerformanceInfo(out pi, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
            return default;
        ulong page = (ulong)pi.PageSize;
        return new MemoryDetail(
            (ulong)pi.CommitTotal * page,
            (ulong)pi.CommitLimit * page,
            (ulong)pi.SystemCache * page,
            (ulong)pi.KernelPaged * page,
            (ulong)pi.KernelNonpaged * page);
    }

    // ===================== Physical memory (SMBIOS) =====================
    [DllImport("kernel32.dll")]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, IntPtr buffer, uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    public sealed record MemoryHardware(int SpeedMhz, int SlotsUsed, int SlotsTotal, string FormFactor);

    /// <summary>Installed RAM in bytes (includes any hardware-reserved memory).</summary>
    public static ulong GetInstalledMemoryBytes()
        => GetPhysicallyInstalledSystemMemory(out ulong kb) ? kb * 1024UL : 0;

    /// <summary>Parse SMBIOS type-16/17 records for memory speed, slots and form factor.</summary>
    public static MemoryHardware GetMemoryHardware()
    {
        const uint RSMB = 0x52534D42;
        uint size = GetSystemFirmwareTable(RSMB, 0, IntPtr.Zero, 0);
        if (size == 0) return new MemoryHardware(0, 0, 0, "");
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetSystemFirmwareTable(RSMB, 0, buf, size) == 0) return new MemoryHardware(0, 0, 0, "");
            byte[] data = new byte[size];
            Marshal.Copy(buf, data, 0, (int)size);

            int slotsUsed = 0, slotsTotal = 0, speed = 0;
            string form = "";
            int p = 8; // skip RawSMBIOSData header
            while (p + 4 < data.Length)
            {
                byte type = data[p];
                byte len = data[p + 1];
                if (len < 4) break;

                if (type == 17 && p + 0x16 < data.Length) // Memory Device = one slot
                {
                    slotsTotal++;
                    int devSize = data[p + 0x0C] | (data[p + 0x0D] << 8);
                    if (devSize != 0 && devSize != 0xFFFF)
                    {
                        slotsUsed++;
                        int s = data[p + 0x15] | (data[p + 0x16] << 8); // Speed (MT/s)
                        if (len > 0x21) { int cfg = data[p + 0x20] | (data[p + 0x21] << 8); if (cfg > 0 && cfg != 0xFFFF) s = cfg; }
                        if (s > 0 && s != 0xFFFF && speed == 0) speed = s;
                        if (form.Length == 0) form = FormFactor(data[p + 0x0E]);
                    }
                }

                // advance past formatted area, then the double-null-terminated string set
                int q = p + len;
                while (q + 1 < data.Length && !(data[q] == 0 && data[q + 1] == 0)) q++;
                p = q + 2;
            }
            return new MemoryHardware(speed, slotsUsed, slotsTotal, form);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string FormFactor(byte f) => f switch
    {
        0x09 => "DIMM",
        0x0D => "SODIMM",
        0x0B => "RIMM",
        0x0C => "Micro DIMM",
        _ => ""
    };

    // ===================== Shell file properties dialog =====================
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);

    /// <summary>Open the Explorer "Properties" dialog for a file path.</summary>
    public static void ShowFileProperties(string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            lpVerb = "properties",
            lpFile = path,
            nShow = 1,
            fMask = 0x0000000C // SEE_MASK_INVOKEIDLIST
        };
        ShellExecuteExW(ref info);
    }
}
