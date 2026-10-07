using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Win11TaskMan.Interop;

namespace Win11TaskMan.Services;

public sealed record ProcSample(
    int Pid, string Name, double Cpu, long MemBytes, double DiskBps,
    uint Threads, uint Handles, bool IsApp, double Gpu, uint SessionId,
    ProcExtra Extra = default,
    long GpuDedicated = 0, long GpuShared = 0, long GpuCommitted = 0);

public sealed record SystemSample(
    double CpuPercent, double MemPercent, ulong MemUsedBytes, ulong MemTotalBytes,
    double CurrentMhz, int ProcessCount, long ThreadCount, long HandleCount,
    ulong UptimeMs,
    ulong MemCommittedBytes, ulong MemCommitLimitBytes, ulong MemCachedBytes,
    ulong MemPagedPoolBytes, ulong MemNonPagedPoolBytes,
    double CpuTempC);

public sealed record DiskSample(
    int Index, double ActivePercent, double ReadBps, double WriteBps, double ResponseMs);

public sealed record NetSample(
    string AdapterName, string ConnectionType, ulong LinkSpeedBps,
    double SendBps, double RecvBps);

public sealed record GpuSample(
    bool Available, double UtilPercent, ulong DedicatedUsedBytes,
    ulong SharedUsedBytes, double TempC, IReadOnlyDictionary<int, double> NodeUtil);

public sealed record Snapshot(
    IReadOnlyList<ProcSample> Processes, SystemSample System,
    IReadOnlyList<DiskSample> Disks, NetSample Net, double[] PerCore, GpuSample Gpu);

/// <summary>
/// Polls native data on a background thread and raises <see cref="Updated"/>
/// on the UI thread (via the SynchronizationContext captured at construction).
/// Nothing here touches WPF, so the render thread never blocks.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly Timer _timer;
    private readonly int _logical = Environment.ProcessorCount;

    // per-pid previous CPU time / io bytes for delta calc
    private Dictionary<int, (long cpu, long io)> _prev = new();
    private long _prevIdle, _prevKernel, _prevUser;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private double _lastWallSec;
    private int _running;
    // Network is polled on its own light timer so the heavy snapshot (process
    // enumeration, GPU/thermal PDH) can never block it and drop throughput samples.
    private readonly Timer _netTimer;
    private double _lastNetWallSec;
    private int _netRunning;
    private volatile NetSample _latestNet = new("", "", 0, 0, 0);

    // per-core CPU deltas
    private (long idle, long kernel, long user)[] _prevCore = Array.Empty<(long, long, long)>();
    // physical-disk counter deltas, keyed by drive index
    private readonly Dictionary<int, NativeMethods.DiskCounters> _prevDisks = new();
    private readonly int[] _diskIndexes;
    // per-interface octet deltas keyed by interface index
    private Dictionary<uint, (ulong inO, ulong outO)> _prevNet = new();
    // GPU utilization / memory via PDH (best-effort)
    private readonly GpuCounters _gpu = new();
    // CPU temperature via LibreHardwareMonitor; built lazily on the poll thread
    // so its driver load doesn't stall the UI thread at startup
    private CpuThermal? _cpuTherm;
    // live CPU clock (base × turbo %) via PDH; lazily built (needs a baseline sample)
    private CpuFrequency? _cpuFreq;

    public event Action<Snapshot>? Updated;
    /// <summary>Raised on the UI thread each network poll, independent of the heavy snapshot.</summary>
    public event Action<NetSample>? NetUpdated;

    public SystemMonitor()
    {
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("Construct on the UI thread.");
        NativeMethods.GetSystemCpuTimes(out _prevIdle, out _prevKernel, out _prevUser);
        _prevCore = NativeMethods.GetPerCoreCpuTimes();
        _diskIndexes = NativeMethods.EnumPhysicalDriveIndexes().ToArray();
        foreach (int i in _diskIndexes)
            if (NativeMethods.GetDiskCounters(i) is { } c) _prevDisks[i] = c;
        _lastWallSec = _wall.Elapsed.TotalSeconds;
        // Seed the net baseline now so the first poll is a real 1s delta, not a spike.
        foreach (var f in NativeMethods.GetNetInterfaces()) _prevNet[f.Index] = (f.InOctets, f.OutOctets);
        _lastNetWallSec = _lastWallSec;
        _timer = new Timer(_ => Tick(), null, 0, 1000);
        _netTimer = new Timer(_ => NetTick(), null, 1000, 1000);
    }

    // Light, dedicated network poll — never blocked by the heavy snapshot, so each
    // burst lands in its own 1-second sample instead of being dropped or averaged away.
    private void NetTick()
    {
        if (Interlocked.Exchange(ref _netRunning, 1) == 1) return;
        try
        {
            double now = _wall.Elapsed.TotalSeconds;
            double wall = Math.Max(now - _lastNetWallSec, 0.001);
            _lastNetWallSec = now;
            var net = ComputeNet(wall);
            _latestNet = net;
            _ui.Post(_ => NetUpdated?.Invoke(net), null);
        }
        catch { /* one bad net poll shouldn't kill the timer */ }
        finally { Interlocked.Exchange(ref _netRunning, 0); }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return; // skip if previous still working
        try
        {
            double now = _wall.Elapsed.TotalSeconds;
            double wall = Math.Max(now - _lastWallSec, 0.001);
            _lastWallSec = now;

            // --- per process ---
            var raw = NativeMethods.SnapshotProcesses();
            var appPids = NativeMethods.GetAppPids();
            var gpu = _gpu.Read();
            var next = new Dictionary<int, (long, long)>(raw.Count);
            var procs = new List<ProcSample>(raw.Count);
            long threads = 0, handles = 0;

            foreach (var p in raw)
            {
                next[p.Pid] = (p.CpuTime100ns, p.IoBytes);
                threads += p.Threads;
                handles += p.Handles;

                double cpu = 0, disk = 0;
                if (_prev.TryGetValue(p.Pid, out var old))
                {
                    double cpuSec = (p.CpuTime100ns - old.cpu) / 1e7;
                    cpu = Math.Clamp(cpuSec / (wall * _logical) * 100.0, 0, 100);
                    disk = Math.Max(p.IoBytes - old.io, 0) / wall;
                }
                double pgpu = gpu.PerPid.TryGetValue(p.Pid, out double gv) ? gv : 0;
                procs.Add(new ProcSample(p.Pid, Prettify(p.Name), cpu, p.WorkingSet,
                                         disk, p.Threads, p.Handles, appPids.Contains(p.Pid), pgpu, p.SessionId,
                                         p.Extra,
                                         gpu.ProcDedicated?.GetValueOrDefault(p.Pid) ?? 0,
                                         gpu.ProcShared?.GetValueOrDefault(p.Pid) ?? 0,
                                         gpu.ProcCommitted?.GetValueOrDefault(p.Pid) ?? 0));
            }
            _prev = next;

            // --- system cpu (GetSystemTimes is more accurate than summing procs) ---
            double sysCpu = 0;
            if (NativeMethods.GetSystemCpuTimes(out long idle, out long kern, out long user))
            {
                long dIdle = idle - _prevIdle, dKern = kern - _prevKernel, dUser = user - _prevUser;
                long busyTotal = dKern + dUser; // kernel already includes idle
                if (busyTotal > 0) sysCpu = Math.Clamp((busyTotal - dIdle) / (double)busyTotal * 100.0, 0, 100);
                _prevIdle = idle; _prevKernel = kern; _prevUser = user;
            }

            var (memTotal, memUsed, memLoad) = NativeMethods.GetMemory();
            var memDetail = NativeMethods.GetMemoryDetail();
            _cpuTherm ??= new CpuThermal();
            double cpuTemp = _cpuTherm.Read();
            _cpuFreq ??= new CpuFrequency();
            double currentMhz = _cpuFreq.CurrentMhz();

            var system = new SystemSample(
                CpuPercent: sysCpu,
                MemPercent: memLoad,
                MemUsedBytes: memUsed,
                MemTotalBytes: memTotal,
                CurrentMhz: currentMhz,
                ProcessCount: raw.Count,
                ThreadCount: threads,
                HandleCount: handles,
                UptimeMs: NativeMethods.GetUptimeMs(),
                MemCommittedBytes: memDetail.CommittedBytes,
                MemCommitLimitBytes: memDetail.CommitLimitBytes,
                MemCachedBytes: memDetail.CachedBytes,
                MemPagedPoolBytes: memDetail.PagedPoolBytes,
                MemNonPagedPoolBytes: memDetail.NonPagedPoolBytes,
                CpuTempC: cpuTemp);

            var perCore = ComputePerCore();
            var diskSamples = ComputeDisks(wall);
            var netSample = _latestNet; // computed on the dedicated net timer (NetTick)
            double gpuTemp = _gpu.Available ? GpuThermal.GetTemperatureC() : 0;
            var gpuSample = new GpuSample(_gpu.Available, gpu.UtilPercent, gpu.DedicatedUsedBytes,
                gpu.SharedUsedBytes, gpuTemp, gpu.NodeUtil);

            var snapshot = new Snapshot(procs, system, diskSamples, netSample, perCore, gpuSample);
            _ui.Post(_ => Updated?.Invoke(snapshot), null);
        }
        catch { /* a single bad tick shouldn't kill the timer */ }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private double[] ComputePerCore()
    {
        var cur = NativeMethods.GetPerCoreCpuTimes();
        var result = new double[cur.Length];
        if (_prevCore.Length == cur.Length)
            for (int i = 0; i < cur.Length; i++)
            {
                long dIdle = cur[i].idle - _prevCore[i].idle;
                long total = (cur[i].kernel - _prevCore[i].kernel) + (cur[i].user - _prevCore[i].user);
                result[i] = total > 0 ? Math.Clamp((total - dIdle) / (double)total * 100.0, 0, 100) : 0;
            }
        _prevCore = cur;
        return result;
    }

    private List<DiskSample> ComputeDisks(double wall)
    {
        var result = new List<DiskSample>(_diskIndexes.Length);
        foreach (int idx in _diskIndexes)
        {
            var cur = NativeMethods.GetDiskCounters(idx);
            if (cur is { } c && _prevDisks.TryGetValue(idx, out var p))
            {
                double dQuery = (c.QueryTime - p.QueryTime) / 1e7;          // seconds
                double dIdle = (c.IdleTime - p.IdleTime) / 1e7;
                double active = dQuery > 0 ? Math.Clamp((1 - dIdle / dQuery) * 100.0, 0, 100) : 0;
                double read = Math.Max(c.BytesRead - p.BytesRead, 0) / wall;
                double write = Math.Max(c.BytesWritten - p.BytesWritten, 0) / wall;
                long ops = (long)(c.ReadCount - p.ReadCount) + (c.WriteCount - p.WriteCount);
                double svc = (c.ReadTime - p.ReadTime) + (c.WriteTime - p.WriteTime); // 100ns
                double respMs = ops > 0 ? svc / ops / 1e4 : 0;
                result.Add(new DiskSample(idx, active, read, write, respMs));
            }
            else result.Add(new DiskSample(idx, 0, 0, 0, 0));

            if (cur is { } cc) _prevDisks[idx] = cc;
        }
        return result;
    }

    private NetSample ComputeNet(double wall)
    {
        var ifs = NativeMethods.GetNetInterfaces();
        var next = new Dictionary<uint, (ulong, ulong)>(ifs.Count);
        NativeMethods.NetIf primary = default;
        double primaryActivity = -1;

        foreach (var f in ifs)
        {
            next[f.Index] = (f.InOctets, f.OutOctets);
            if (!IsRealAdapter(f.Description)) continue; // skip WFP/QoS filter pseudo-NICs
            // Prefer the interface with the most recent traffic; tie-break on link speed.
            double activity = 0;
            if (_prevNet.TryGetValue(f.Index, out var old))
                activity = (double)(f.InOctets - old.Item1) + (f.OutOctets - old.Item2);
            double score = activity * 1e6 + f.LinkSpeed; // weight live traffic over idle links
            if (score > primaryActivity) { primaryActivity = score; primary = f; }
        }

        double send = 0, recv = 0;
        if (primary.Index != 0 && _prevNet.TryGetValue(primary.Index, out var prev))
        {
            recv = Math.Max((double)primary.InOctets - prev.Item1, 0) / wall;
            send = Math.Max((double)primary.OutOctets - prev.Item2, 0) / wall;
        }
        _prevNet = next;

        string kind = primary.Type switch { 71 => "Wi-Fi", 6 => "Ethernet", 0 => "Network", _ => "Network" };
        return new NetSample(primary.Description ?? "", kind, primary.LinkSpeed, send, recv);
    }

    // Filter out the WFP / QoS / scheduler pseudo-interfaces that mirror a real
    // NIC's counters, so the Performance tab names the actual adapter.
    private static bool IsRealAdapter(string desc)
    {
        foreach (var noise in new[] { "WFP", "QoS", "Scheduler", "Filter", "Native MAC",
                                      "Miniport", "Kernel Debug", "Virtual Adapter" })
            if (desc.Contains(noise, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // "chrome.exe" -> "chrome", keep readable like Task Manager's name column
    private static string Prettify(string image)
        => image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? image[..^4] : image;

    /// <summary>Change the polling cadence (ms). Use <see cref="Pause"/> to halt.</summary>
    public void SetInterval(int ms) { _timer.Change(0, ms); _netTimer.Change(0, ms); }

    public void Pause() { _timer.Change(Timeout.Infinite, Timeout.Infinite); _netTimer.Change(Timeout.Infinite, Timeout.Infinite); }

    public void Dispose() { _timer.Dispose(); _netTimer.Dispose(); _gpu.Dispose(); _cpuTherm?.Dispose(); _cpuFreq?.Dispose(); }
}
