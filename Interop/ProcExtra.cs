namespace Win11TaskMan.Interop;

/// <summary>Extra per-process counters from the same snapshot, for optional Details columns.</summary>
public readonly record struct ProcExtra(
    long PrivateBytes, long WorkingSetPrivate, long PeakWorkingSet, long VirtualSize,
    long PagefileUsage, uint PageFaults, uint HardFaults, int BasePriority,
    long ReadBytes, long WriteBytes, long OtherBytes,
    long ReadOps, long WriteOps, long OtherOps,
    long PagedPool, long NonPagedPool, long CpuTime100ns, long CreateFileTime, ulong Cycles);

