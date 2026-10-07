using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

using Win11TaskMan.Controls;
using Win11TaskMan.Interop;
using Win11TaskMan.Services;

namespace Win11TaskMan.Views;

public partial class PerformanceView : UserControl
{
    private enum Metric { Cpu, Memory, Disk, Network, Gpu }

    private static readonly Color CpuAccent = Color.FromRgb(0x17, 0xA9, 0xC4);
    private static readonly Color MemAccent = Color.FromRgb(0x9B, 0x57, 0xD6);
    private static readonly Color DiskAccent = Color.FromRgb(0x4C, 0x9A, 0x2A);
    private static readonly Color NetAccent = Color.FromRgb(0xC4, 0x6A, 0x17);
    private static readonly Color GpuAccent = Color.FromRgb(0xC4, 0x3B, 0x7A);

    // sparklines (left cards)
    private readonly GraphControl _cpuSpark = new(CpuAccent, 60) { ShowGrid = false };
    private readonly GraphControl _memSpark = new(MemAccent, 60) { ShowGrid = false };
    private readonly GraphControl _netSpark = new(NetAccent, 60) { ShowGrid = false, AutoScale = true, ScaleRounder = NiceNetScaleBytes };
    private readonly GraphControl _gpuSpark = new(GpuAccent, 60) { ShowGrid = false };

    // detail graphs (keep their own history so switching cards keeps the trace)
    private readonly GraphControl _cpuBig = new(CpuAccent, 60);
    private readonly GraphControl _memBig = new(MemAccent, 60);
    private readonly GraphControl _netBig = new(NetAccent, 60, dualSeries: true) { AutoScale = true, ScaleRounder = NiceNetScaleBytes };
    private readonly GraphControl _gpuBig = new(GpuAccent, 60);
    private bool _gpuEnabled;

    // one of these per physical disk (cards + detail graphs are built in LoadDisks)
    private sealed class DiskUi
    {
        public int Index;
        public string Title = "", Model = "", Capacity = "—", Formatted = "—", Type = "—";
        public bool IsSystem;
        public RadioButton Card = null!;
        public TextBlock CardSub = null!;
        public GraphControl Spark = null!, ActiveBig = null!, XferBig = null!;
        public Grid Panel = null!;
        public TextBlock? XferMax;
        public double XferScale = DiskXferFloor; // bytes/sec, grows to fit observed peak
    }
    private const double DiskXferFloor = 100 * 1024; // 100 KB/s minimum transfer-rate scale
    private readonly List<DiskUi> _disks = new();
    private DiskUi? _selectedDisk;

    private GraphControl[] _coreGraphs = Array.Empty<GraphControl>();

    // GPU engine + memory graphs (built once when first GPU sample arrives).
    // Keyed by engine-node ordinal (matches PDH "eng_N" and GpuTopology nodes).
    private readonly Dictionary<int, (GraphControl graph, TextBlock pct)> _engineGraphs = new();
    private readonly List<(int ordinal, string label)> _gpuShown = new();
    private readonly GraphControl _gpuDedicatedGraph = new(GpuAccent, 60);
    private readonly GraphControl _gpuSharedGraph = new(GpuAccent, 60);
    private TextBlock? _gpuDedMax, _gpuShareMax;
    private bool _gpuPanelBuilt;
    private const int MaxGpuEngines = 8; // graphs shown in the multi-engine grid

    // single-engine view ("Change graph to" -> a specific engine). null = multi-engine.
    private int? _gpuSingleOrdinal;
    private Border? _gpuEngineGridHost, _gpuSingleHost;
    private readonly GraphControl _gpuEngineBig = new(GpuAccent, 60);
    private TextBlock? _gpuSingleLabel, _gpuSinglePct;

    private Metric _metric = Metric.Cpu;
    private bool _cpuLogical;
    private Snapshot? _last;

    public PerformanceView(SystemMonitor monitor)
    {
        InitializeComponent();
        CpuSparkHost.Child = _cpuSpark;
        MemSparkHost.Child = _memSpark;
        NetSparkHost.Child = _netSpark;
        GpuSparkHost.Child = _gpuSpark;
        BigGraphHost.Child = _cpuBig;

        LoadStaticCpuInfo();
        LoadDisks();
        LoadStaticGpuInfo();
        LoadStaticMemInfo();
        ApplyMetric();

        // Subscribe for the view's whole lifetime (MainWindow caches it and builds it
        // at startup) so the graphs keep collecting history while another tab is shown.
        LoadHistory();
        monitor.Updated += OnSnapshot;
        monitor.NetUpdated += OnNet;
    }

    // CPU graph history survives restarts: saved on close, replayed on launch with the
    // time the app was closed shown as a gap (zeros) so the trace stays honest.
    private static string HistoryPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win11TaskMan", "cpu-history.txt");

    public void SaveHistory()
    {
        try
        {
            string path = HistoryPath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var vals = _cpuBig.Export().Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            System.IO.File.WriteAllText(path, DateTime.UtcNow.Ticks + Environment.NewLine + string.Join(",", vals));
        }
        catch { /* best effort */ }
    }

    private void LoadHistory()
    {
        try
        {
            var lines = System.IO.File.ReadAllLines(HistoryPath);
            if (lines.Length < 2 || !long.TryParse(lines[0], out long ticks)) return;
            var samples = lines[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            int gap = (int)Math.Clamp((DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds, 0, samples.Count);
            samples.AddRange(Enumerable.Repeat(0.0, gap));
            var tail = samples.Skip(Math.Max(0, samples.Count - 60)).ToArray();
            _cpuBig.Import(tail);
            _cpuSpark.Import(tail);
        }
        catch { /* missing or corrupt history -> start empty */ }
    }

    // Network arrives on its own faster, drop-free cadence (SystemMonitor.NetTick), so the
    // throughput graph keeps every spike instead of losing samples to slow/skipped snapshots.
    private void OnNet(NetSample net)
    {
        _netSpark.Push(net.SendBps + net.RecvBps);
        _netBig.Push(net.RecvBps, net.SendBps); // solid filled = Receive, dashed = Send
        NetCardTitle.Text = string.IsNullOrEmpty(net.ConnectionType) ? "Network" : net.ConnectionType;
        NetCardSub.Text = $"S: {Bits(net.SendBps)}  R: {Bits(net.RecvBps)}";
        if (_metric == Metric.Network)
        {
            StatA.Text = Bits(net.SendBps);
            StatB.Text = Bits(net.RecvBps);
            GraphTopRight.Text = Bits(_netBig.NormalizationScale());
        }
    }

    private void OnSnapshot(Snapshot snap)
    {
        _last = snap;
        var s = snap.System;

        // --- always refresh every card's sparkline + caption ---
        _cpuSpark.Push(s.CpuPercent / 100.0);
        _cpuBig.Push(s.CpuPercent / 100.0);
        double memFrac = s.MemTotalBytes == 0 ? 0 : (double)s.MemUsedBytes / s.MemTotalBytes;
        _memSpark.Push(memFrac);
        _memBig.Push(memFrac);

        foreach (var du in _disks)
        {
            var d = FindDisk(snap.Disks, du.Index);
            double active = d?.ActivePercent ?? 0;
            du.Spark.Push(active / 100.0);
            du.ActiveBig.Push(active / 100.0);
            du.CardSub.Text = $"{active:0}%";

            double bps = (d?.ReadBps ?? 0) + (d?.WriteBps ?? 0);
            du.XferScale = Math.Max(bps, Math.Max(du.XferScale * 0.92, DiskXferFloor)); // grow fast, decay slow
            du.XferBig.Push(bps / du.XferScale);
            if (du == _selectedDisk && du.XferMax != null) du.XferMax.Text = Rate(du.XferScale);
        }

        double ghz = s.CurrentMhz / 1000.0;
        CpuCardSub.Text = $"{s.CpuPercent:0}%  {ghz:0.00} GHz";
        MemCardSub.Text = $"{Gb(s.MemUsedBytes):0.0}/{Gb(s.MemTotalBytes):0.0} GB ({s.MemPercent:0}%)";

        if (snap.Gpu.Available)
        {
            if (!_gpuEnabled) { _gpuEnabled = true; CardGpu.IsEnabled = true; CardGpu.Opacity = 1; }
            _gpuSpark.Push(snap.Gpu.UtilPercent / 100.0);
            _gpuBig.Push(snap.Gpu.UtilPercent / 100.0);
            GpuCardSub.Text = $"{snap.Gpu.UtilPercent:0}%";

            if (!_gpuPanelBuilt && snap.Gpu.NodeUtil.Count > 0)
            {
                BuildGpuPanel(snap.Gpu.NodeUtil);
                if (_metric == Metric.Gpu) ApplyMetric();
            }

            foreach (var (ordinal, cell) in _engineGraphs)
            {
                double v = snap.Gpu.NodeUtil.TryGetValue(ordinal, out double ev) ? ev : 0;
                cell.graph.Push(v / 100.0);
                cell.pct.Text = $"{v:0}%";
            }

            if (_gpuSingleOrdinal is int so)
            {
                double v = snap.Gpu.NodeUtil.TryGetValue(so, out double sv) ? sv : 0;
                _gpuEngineBig.Push(v / 100.0);
                if (_gpuSinglePct != null) _gpuSinglePct.Text = $"{v:0}%";
            }

            ulong dedTotal = _gpuTotalBytes > 0 ? _gpuTotalBytes : Math.Max(snap.Gpu.DedicatedUsedBytes, 1);
            ulong shareTotal = GpuSharedTotal(s);
            _gpuDedicatedGraph.Push((double)snap.Gpu.DedicatedUsedBytes / dedTotal);
            _gpuSharedGraph.Push(shareTotal == 0 ? 0 : (double)snap.Gpu.SharedUsedBytes / shareTotal);
            if (_gpuDedMax != null) _gpuDedMax.Text = $"{Gb(dedTotal):0.0} GB";
            if (_gpuShareMax != null) _gpuShareMax.Text = $"{Gb(shareTotal):0.0} GB";
        }

        if (_cpuLogical && _metric == Metric.Cpu && snap.PerCore.Length == _coreGraphs.Length)
            for (int i = 0; i < _coreGraphs.Length; i++)
                _coreGraphs[i].Push(snap.PerCore[i] / 100.0);

        UpdateLive(snap);
    }

    // populate the stat values for whichever metric is selected
    private void UpdateLive(Snapshot snap)
    {
        var s = snap.System;
        switch (_metric)
        {
            case Metric.Cpu:
                StatA.Text = $"{s.CpuPercent:0}%";
                StatB.Text = $"{s.CurrentMhz / 1000.0:0.00} GHz";
                StatC.Text = s.CpuTempC > 0 ? $"{s.CpuTempC:0} °C" : "—";
                StatD.Text = s.ProcessCount.ToString("N0");
                StatE.Text = s.ThreadCount.ToString("N0");
                StatF.Text = s.HandleCount.ToString("N0");
                StatG.Text = FormatUptime(s.UptimeMs);
                DetailSubtitle.Text = _cpuName;
                break;

            case Metric.Memory:
                StatA.Text = $"{Gb(s.MemUsedBytes):0.0} GB";
                StatB.Text = $"{Gb(s.MemTotalBytes - s.MemUsedBytes):0.0} GB";
                StatC.Text = $"{Gb(s.MemCommittedBytes):0.0}/{Gb(s.MemCommitLimitBytes):0.0} GB";
                StatD.Text = $"{Gb(s.MemCachedBytes):0.0} GB";
                StatE.Text = Mb(s.MemPagedPoolBytes);
                StatF.Text = Mb(s.MemNonPagedPoolBytes);
                UpdateMemBar(s);
                break;

            case Metric.Disk:
                var d = _selectedDisk != null ? FindDisk(snap.Disks, _selectedDisk.Index) : null;
                StatA.Text = $"{d?.ActivePercent ?? 0:0}%";
                StatB.Text = $"{d?.ResponseMs ?? 0:0.0} ms";
                StatC.Text = "";
                StatD.Text = Rate(d?.ReadBps ?? 0);
                StatE.Text = Rate(d?.WriteBps ?? 0);
                StatF.Text = "";
                break;

            case Metric.Network:
                StatA.Text = Bits(snap.Net.SendBps);
                StatB.Text = Bits(snap.Net.RecvBps);
                GraphTopRight.Text = Bits(_netBig.NormalizationScale());
                break;

            case Metric.Gpu:
                ulong dedTotal = _gpuTotalBytes > 0 ? _gpuTotalBytes : snap.Gpu.DedicatedUsedBytes;
                ulong shareTotal = GpuSharedTotal(s);
                ulong gpuMemUsed = snap.Gpu.DedicatedUsedBytes + snap.Gpu.SharedUsedBytes;
                StatA.Text = $"{snap.Gpu.UtilPercent:0}%";                                    // Utilization
                StatB.Text = dedTotal > 0
                    ? $"{Gb(snap.Gpu.DedicatedUsedBytes):0.0}/{Gb(dedTotal):0.0} GB"
                    : $"{Gb(snap.Gpu.DedicatedUsedBytes):0.0} GB";                            // Dedicated memory
                StatC.Text = snap.Gpu.TempC > 0 ? $"{snap.Gpu.TempC:0} °C" : "—";             // Temperature
                StatD.Text = $"{Gb(gpuMemUsed):0.0}/{Gb(dedTotal + shareTotal):0.0} GB";      // GPU memory
                StatE.Text = $"{Gb(snap.Gpu.SharedUsedBytes):0.0}/{Gb(shareTotal):0.0} GB";   // Shared memory
                break;
        }
    }

    private void UpdateMemBar(SystemSample s)
    {
        // Simple two-segment bar: in-use vs available (approximation of Win11 composition).
        double used = s.MemUsedBytes, avail = Math.Max(s.MemTotalBytes - s.MemUsedBytes, 0);
        double cached = Math.Min(s.MemCachedBytes, avail);
        MemBarInUse.Width = new GridLength(used, GridUnitType.Star);
        MemBarModified.Width = new GridLength(0, GridUnitType.Star);
        MemBarStandby.Width = new GridLength(cached, GridUnitType.Star);
        MemBarFree.Width = new GridLength(Math.Max(avail - cached, 0), GridUnitType.Star);
        // keep the labels aligned to the bar: "Available" starts at the in-use boundary
        MemLblInUse.Width = new GridLength(used, GridUnitType.Star);
        MemLblAvail.Width = new GridLength(avail, GridUnitType.Star);
        MemBarLeftLbl.Text = $"In use ({Gb(s.MemUsedBytes):0.0} GB)";
        MemBarRightLbl.Text = $"Available ({Gb(avail):0.0} GB)";
    }

    private void Card_Checked(object sender, RoutedEventArgs e)
    {
        if (BigGraphHost == null) return; // during init
        if (sender is RadioButton { Tag: DiskUi du })
        {
            _metric = Metric.Disk;
            _selectedDisk = du;
        }
        else
        {
            _metric = sender == CardMem ? Metric.Memory
                    : sender == CardNet ? Metric.Network
                    : sender == CardGpu ? Metric.Gpu
                    : Metric.Cpu;
        }
        ApplyMetric();
        if (_last is { } snap) UpdateLive(snap);
    }

    // configure the detail pane layout for the selected metric
    private void ApplyMetric()
    {
        bool isCpu = _metric == Metric.Cpu;
        bool isMem = _metric == Metric.Memory;
        bool isDisk = _metric == Metric.Disk;
        bool isNet = _metric == Metric.Network;
        bool isGpu = _metric == Metric.Gpu;

        DetailTitle.Text = _metric switch
        {
            Metric.Memory => "Memory",
            Metric.Disk => _selectedDisk?.Title ?? "Disk",
            Metric.Network => NetCardTitle.Text,
            Metric.Gpu => GpuCardTitle.Text,
            _ => "CPU"
        };
        DetailSubtitle.Text = isCpu ? _cpuName : isDisk ? (_selectedDisk?.Model ?? "") : isGpu ? _gpuName
                            : isNet ? (_last?.Net.AdapterName ?? "") : "";

        // GPU single-engine view swaps the engine grid for one large engine graph.
        bool gpuSingle = isGpu && _gpuPanelBuilt && _gpuSingleOrdinal is int;
        if (_gpuPanelBuilt && _gpuEngineGridHost != null && _gpuSingleHost != null)
        {
            _gpuEngineGridHost.Visibility = gpuSingle ? Visibility.Collapsed : Visibility.Visible;
            _gpuSingleHost.Visibility = gpuSingle ? Visibility.Visible : Visibility.Collapsed;
            if (gpuSingle && _gpuSingleLabel != null) _gpuSingleLabel.Text = LabelForOrdinal(_gpuSingleOrdinal!.Value);
        }

        // graph axis labels
        string gpuTopLeft = gpuSingle ? LabelForOrdinal(_gpuSingleOrdinal!.Value) : "GPU engines";
        GraphTopLeft.Text = isNet ? "Throughput" : isGpu ? gpuTopLeft : isDisk ? "Active time" : "% Utilization";
        GraphTopRight.Text = isNet ? Bits(_netBig.NormalizationScale()) : (isGpu && !gpuSingle) ? "" : "100%";

        // choose graph host + content
        bool showCores = isCpu && _cpuLogical;
        bool showGpu = isGpu && _gpuPanelBuilt;
        bool showDisk = isDisk && _selectedDisk != null;
        CoreGridHost.Visibility = showCores ? Visibility.Visible : Visibility.Collapsed;
        GpuGraphHost.Visibility = showGpu ? Visibility.Visible : Visibility.Collapsed;
        DiskGraphHost.Visibility = showDisk ? Visibility.Visible : Visibility.Collapsed;
        BigGraphHost.Visibility = (showCores || showGpu || showDisk) ? Visibility.Collapsed : Visibility.Visible;
        if (showDisk)
        {
            DiskGraphHost.Child = _selectedDisk!.Panel;
            if (_selectedDisk.XferMax != null) _selectedDisk.XferMax.Text = Rate(_selectedDisk.XferScale);
        }
        BigGraphHost.Child = _metric switch
        {
            Metric.Memory => _memBig,
            Metric.Network => _netBig,
            Metric.Gpu => _gpuBig,
            _ => _cpuBig
        };

        // "Change graph to" is available for CPU (overall/logical) and GPU (engines).
        ChangeGraphMenu.Visibility = (isCpu || (isGpu && _gpuPanelBuilt)) ? Visibility.Visible : Visibility.Collapsed;
        RebuildGraphMenu();

        // composition bar only for memory
        MemCompositionPanel.Visibility = isMem ? Visibility.Visible : Visibility.Collapsed;

        // Memory's info panel holds only short values ("2400 MHz", "DIMM"), so shrink it
        // and widen the stat columns so the long Committed value ("35.8/57.9 GB") fits
        // without colliding with the info panel. Other metrics keep the full info panel
        // (long IP/location text) and narrower stat columns that fit beside it.
        InfoCol.Width = new GridLength(isMem ? 200 : 320);
        double statW = isMem ? 150 : 114;
        StatCol0.Width = new GridLength(statW);
        StatCol1.Width = new GridLength(statW);
        StatCol2.Width = new GridLength(statW);

        // stat slots + labels
        if (isCpu)
        {
            SetStats(("Utilization", true), ("Speed", true), ("Temperature", true),
                     ("Processes", true), ("Threads", true), ("Handles", true),
                     ("Up time", true), ("", false), ("", false));
        }
        else if (isMem)
        {
            SetStats(("In use", true), ("Available", true), ("Committed", true),
                     ("Cached", true), ("Paged pool", true), ("Non-paged pool", true));
        }
        else if (isDisk)
        {
            SetStats(("Active time", true), ("Avg response time", true), ("", false),
                     ("Read speed", true), ("Write speed", true), ("", false));
        }
        else if (isNet)
        {
            SetStats(("Send", true), ("Receive", true), ("", false),
                     ("", false), ("", false), ("", false));
        }
        else // gpu — memory readouts stacked in column 1 (Dedicated over Shared)
        {
            SetStats(("Utilization", true), ("Dedicated memory", true), ("Temperature", true),
                     ("GPU memory", true), ("Shared memory", true), ("", false));
        }

        // right info panel
        if (isCpu) ApplyCpuInfo();
        else if (isMem) ApplyMemInfo();
        else if (isDisk) ApplyDiskInfo();
        else if (isNet) ApplyNetInfo();
        else ApplyGpuInfo();
    }

    private void SetStats(params (string label, bool visible)[] slots)
    {
        var labels = new[] { StatALabel, StatBLabel, StatCLabel, StatDLabel, StatELabel, StatFLabel, StatGLabel, StatHLabel, StatILabel };
        var values = new[] { StatA, StatB, StatC, StatD, StatE, StatF, StatG, StatH, StatI };
        for (int i = 0; i < labels.Length; i++)
        {
            bool on = i < slots.Length && slots[i].visible;
            labels[i].Text = i < slots.Length ? slots[i].label : "";
            var vis = on ? Visibility.Visible : Visibility.Hidden;
            labels[i].Visibility = vis;
            values[i].Visibility = vis;
        }
    }

    // Rebuild the "Change graph to" submenu for the current metric: CPU gets
    // overall/logical processors, GPU gets multi-engine + one item per engine.
    private void RebuildGraphMenu()
    {
        ChangeGraphMenu.Items.Clear();
        if (_metric == Metric.Cpu)
        {
            ChangeGraphMenu.Items.Add(GraphMenuItem("Overall utilization", !_cpuLogical, () => SetCpuLogical(false)));
            ChangeGraphMenu.Items.Add(GraphMenuItem("Logical processors", _cpuLogical, () => SetCpuLogical(true)));
        }
        else if (_metric == Metric.Gpu && _gpuPanelBuilt)
        {
            ChangeGraphMenu.Items.Add(GraphMenuItem("Multi-engine", _gpuSingleOrdinal == null, () => SetGpuEngine(null)));
            ChangeGraphMenu.Items.Add(new Separator());
            foreach (var (ordinal, label) in _gpuShown)
            {
                int o = ordinal; // capture
                ChangeGraphMenu.Items.Add(GraphMenuItem(label, _gpuSingleOrdinal == o, () => SetGpuEngine(o)));
            }
        }
    }

    private static MenuItem GraphMenuItem(string header, bool isChecked, Action onClick)
    {
        var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private void SetCpuLogical(bool logical)
    {
        _cpuLogical = logical;
        if (logical) BuildCoreGrid();
        ApplyMetric();
        if (_last is { } snap) UpdateLive(snap);
    }

    private void SetGpuEngine(int? ordinal)
    {
        _gpuSingleOrdinal = ordinal;
        ApplyMetric();
        if (_last is { } snap) UpdateLive(snap);
    }

    private string LabelForOrdinal(int ordinal)
    {
        foreach (var (o, label) in _gpuShown) if (o == ordinal) return label;
        return $"Engine {ordinal}";
    }

    // Shared GPU memory total: the adapter's reported shared-system segment size,
    // falling back to half of system RAM (Task Manager's convention).
    private static ulong GpuSharedTotal(SystemSample s)
    {
        ulong seg = GpuTopology.PrimarySegments.SharedSystemBytes;
        return seg > 0 ? seg : s.MemTotalBytes / 2;
    }

    private void BuildCoreGrid()
    {
        int n = _last?.PerCore.Length ?? Environment.ProcessorCount;
        if (n <= 0) n = Environment.ProcessorCount;
        if (_coreGraphs.Length == n) return;

        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        var grid = new UniformGrid { Columns = cols, Margin = new Thickness(2) };
        _coreGraphs = new GraphControl[n];
        for (int i = 0; i < n; i++)
        {
            var g = new GraphControl(CpuAccent, 60) { ShowGrid = false };
            _coreGraphs[i] = g;
            grid.Children.Add(new Border
            {
                Margin = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x0E)),
                Child = g
            });
        }
        CoreGridHost.Child = grid;
    }

    // Build the GPU detail: a grid of per-node engine graphs (or one large graph in
    // single-engine view) over the dedicated/shared memory graphs. Engine labels come
    // from the kernel node metadata, matching Task Manager exactly.
    private void BuildGpuPanel(IReadOnlyDictionary<int, double> nodeUtil)
    {
        _gpuPanelBuilt = true;

        // Prefer the kernel node list (Task-Manager names + duplicate numbering);
        // intersect with the engines PDH actually reports. Fall back to raw ordinals
        // if node metadata is unavailable.
        var topo = GpuTopology.GetNodes();
        var shown = topo.Where(n => n.Show && nodeUtil.ContainsKey(n.Ordinal)).ToList();
        if (shown.Count == 0) shown = topo.Where(n => n.Show).ToList();
        _gpuShown.Clear();
        if (shown.Count > 0)
            foreach (var n in shown.Take(MaxGpuEngines)) _gpuShown.Add((n.Ordinal, n.Label));
        else
            foreach (int o in nodeUtil.Keys.OrderBy(x => x).Take(MaxGpuEngines)) _gpuShown.Add((o, $"Engine {o}"));

        int count = _gpuShown.Count;
        int cols = count <= 2 ? count : (count <= 4 ? 2 : 3);
        var engineGrid = new UniformGrid { Columns = Math.Max(cols, 1) };
        foreach (var (ordinal, label) in _gpuShown)
        {
            var lbl = new TextBlock { Text = label, Foreground = Gray, FontSize = 11 };
            var pct = new TextBlock { Text = "0%", Foreground = Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
            var g = new GraphControl(GpuAccent, 60) { ShowGrid = false };
            _engineGraphs[ordinal] = (g, pct);
            engineGrid.Children.Add(new Border { Margin = new Thickness(2), Child = EngineCell(lbl, pct, g) });
        }
        _gpuEngineGridHost = new Border { Child = engineGrid };

        // Single-engine host: one large graph that mirrors the selected engine.
        _gpuSingleLabel = new TextBlock { Text = "", Foreground = Gray, FontSize = 11 };
        _gpuSinglePct = new TextBlock { Text = "0%", Foreground = Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        _gpuSingleHost = new Border { Visibility = Visibility.Collapsed, Child = EngineCell(_gpuSingleLabel, _gpuSinglePct, _gpuEngineBig) };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // engines
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // ded label
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) }); // ded graph
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // shared label
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) }); // shared graph

        Grid.SetRow(_gpuEngineGridHost, 0);
        Grid.SetRow(_gpuSingleHost, 0);
        root.Children.Add(_gpuEngineGridHost);
        root.Children.Add(_gpuSingleHost);

        root.Children.Add(MemHeader("Dedicated GPU memory usage", out _gpuDedMax, 1));
        root.Children.Add(MemGraphBorder(_gpuDedicatedGraph, 2));
        root.Children.Add(MemHeader("Shared GPU memory usage", out _gpuShareMax, 3));
        root.Children.Add(MemGraphBorder(_gpuSharedGraph, 4));

        GpuGraphHost.Child = root;
    }

    // A labelled engine graph cell: header (name + %) above a bordered graph.
    private static Grid EngineCell(TextBlock label, TextBlock pct, GraphControl g)
    {
        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var hdr = new Grid();
        hdr.Children.Add(label);
        hdr.Children.Add(pct);
        Grid.SetRow(hdr, 0);
        var graphBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x0E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 0), Child = g
        };
        Grid.SetRow(graphBorder, 1);
        inner.Children.Add(hdr);
        inner.Children.Add(graphBorder);
        return inner;
    }

    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8));

    private static Grid MemHeader(string text, out TextBlock max, int row)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        g.Children.Add(new TextBlock { Text = text, Foreground = Gray, FontSize = 11 });
        max = new TextBlock { Text = "", Foreground = Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        g.Children.Add(max);
        Grid.SetRow(g, row);
        return g;
    }

    private static Border MemGraphBorder(GraphControl g, int row)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x0E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 0), Child = g
        };
        Grid.SetRow(b, row);
        return b;
    }

    // ===================== static info per metric =====================
    private void ApplyCpuInfo()
    {
        var rows = new[]
        {
            ("Base speed:", _baseSpeed), ("Sockets:", _sockets), ("Cores:", _cores),
            ("Logical processors:", _logical), ("Virtualization:", _virt),
            ("L1 cache:", _l1), ("L2 cache:", _l2), ("L3 cache:", _l3)
        };
        SetInfo(rows);
    }

    private void ApplyMemInfo()
    {
        ulong total = _last?.System.MemTotalBytes ?? 0;
        ulong reserved = _memInstalled > total ? _memInstalled - total : 0;
        SetInfo(new[]
        {
            ("Speed:", _memSpeed > 0 ? $"{_memSpeed} MHz" : "—"),
            ("Slots used:", _memSlotsTotal > 0 ? $"{_memSlotsUsed} of {_memSlotsTotal}" : "—"),
            ("Form factor:", _memFormFactor),
            ("Hardware reserved:", reserved > 0 ? Mb(reserved) : "—"),
            ("", ""), ("", ""), ("", ""), ("", "")
        });
    }

    private void ApplyDiskInfo()
    {
        var du = _selectedDisk;
        SetInfo(new[]
        {
            ("Capacity:", du?.Capacity ?? "—"),
            ("Formatted:", du?.Formatted ?? "—"),
            ("Type:", du?.Type ?? "—"),
            ("System disk:", du?.IsSystem == true ? "Yes" : "No"),
            ("Page file:", du?.IsSystem == true ? "Yes" : "No"),
            ("", ""), ("", ""), ("", "")
        });
    }

    private void ApplyNetInfo()
    {
        var net = _last?.Net;
        var (name, ipv4, ipv6) = LookupIp(net?.AdapterName ?? "");
        SetInfo(new[]
        {
            ("Adapter name:", name),
            ("Connection type:", net?.ConnectionType ?? "—"),
            ("Link speed:", net is { } n && n.LinkSpeedBps > 0 ? Bits(n.LinkSpeedBps / 8.0) : "—"),
            ("IPv4 address:", ipv4),
            ("IPv6 address:", ipv6),
            ("", ""), ("", ""), ("", "")
        });
    }

    private readonly System.Collections.Generic.Dictionary<string, (string name, string v4, string v6)> _ipCache = new();

    // Resolve the NIC's friendly name ("Ethernet") and addresses from its description
    // ("Realtek PCIe GbE Family Controller"), which is what the snapshot carries.
    private (string name, string v4, string v6) LookupIp(string adapter)
    {
        if (adapter.Length == 0) return ("—", "—", "—");
        if (_ipCache.TryGetValue(adapter, out var hit)) return hit;
        string name = "—", v4 = "—", v6 = "—";
        try
        {
            var ni = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Description == adapter);
            if (ni != null)
            {
                name = ni.Name;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && v4 == "—")
                        v4 = ua.Address.ToString();
                    else if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                             && !ua.Address.IsIPv6LinkLocal && v6 == "—")
                        v6 = ua.Address.ToString();
                }
            }
        }
        catch { }
        var result = (name, v4, v6);
        _ipCache[adapter] = result;
        return result;
    }

    private void ApplyGpuInfo()
    {
        SetInfo(new[]
        {
            ("Driver version:", _gpuDriverVersion),
            ("Driver date:", _gpuDriverDate),
            ("DirectX version:", _gpuDirectX),
            ("Physical location:", _gpuLocation),
            ("Hardware reserved:", _gpuReserved),
            ("", ""), ("", ""), ("", "")
        });
    }

    private void SetInfo((string key, string val)[] rows)
    {
        var keys = new[] { InfoKey1, InfoKey2, InfoKey3, InfoKey4, InfoKey5, InfoKey6, InfoKey7, InfoKey8 };
        var vals = new[] { InfoVal1, InfoVal2, InfoVal3, InfoVal4, InfoVal5, InfoVal6, InfoVal7, InfoVal8 };
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i].Text = i < rows.Length ? rows[i].key : "";
            vals[i].Text = i < rows.Length ? rows[i].val : "";
        }
    }

    // ===================== static loads =====================
    private string _cpuName = "", _baseSpeed = "—", _sockets = "—", _cores = "—",
                   _logical = "—", _virt = "—", _l1 = "—", _l2 = "—", _l3 = "—";
    private int _memSpeed, _memSlotsUsed, _memSlotsTotal;
    private string _memFormFactor = "—";
    private ulong _memInstalled;
    private string _gpuName = "GPU 0";
    private ulong _gpuTotalBytes;
    private string _gpuDriverVersion = "—", _gpuDriverDate = "—",
                   _gpuDirectX = "12", _gpuLocation = "—", _gpuReserved = "—";

    private void LoadStaticCpuInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _cpuName = key?.GetValue("ProcessorNameString") as string ?? "Processor";
            double baseMhz = Convert.ToDouble(key?.GetValue("~MHz") ?? 0);
            _baseSpeed = $"{baseMhz / 1000.0:0.00} GHz";
        }
        catch { _cpuName = "Processor"; }

        DetailSubtitle.Text = _cpuName;
        var t = NativeMethods.GetTopology();
        _sockets = t.Sockets.ToString();
        _cores = t.PhysicalCores.ToString();
        _logical = t.LogicalProcessors.ToString();
        _virt = NativeMethods.IsVirtualizationEnabled() ? "Enabled" : "Disabled";
        _l1 = Kb(t.L1Bytes);
        _l2 = Mb((ulong)t.L2Bytes);
        _l3 = Mb((ulong)t.L3Bytes);
    }

    private void LoadStaticMemInfo()
    {
        var mh = NativeMethods.GetMemoryHardware();
        _memSpeed = mh.SpeedMhz;
        _memSlotsUsed = mh.SlotsUsed;
        _memSlotsTotal = mh.SlotsTotal;
        _memFormFactor = mh.FormFactor.Length > 0 ? mh.FormFactor : "—";
        _memInstalled = NativeMethods.GetInstalledMemoryBytes();
    }

    // Enumerate every physical drive, build a sidebar card + detail panel for each,
    // and insert the cards into the metric list right after the Memory card.
    private void LoadDisks()
    {
        var letters = NativeMethods.GetDiskDriveLetters();
        foreach (int idx in NativeMethods.EnumPhysicalDriveIndexes())
        {
            var info = NativeMethods.GetDiskInfo(idx);
            if (info is null) continue;

            var drives = letters.TryGetValue(idx, out var l) ? l : new List<string>();
            string letterText = string.Join(" ", drives);
            var du = new DiskUi
            {
                Index = idx,
                Title = letterText.Length > 0 ? $"Disk {idx} ({letterText})" : $"Disk {idx}",
                Model = info.Model,
                Capacity = $"{Gb((ulong)info.CapacityBytes):0} GB",
                Formatted = ComputeFormatted(drives),
                Type = info.IsSsd ? "SSD" : "HDD",
                IsSystem = drives.Contains("C:"),
                Spark = new GraphControl(DiskAccent, 60) { ShowGrid = false },
                ActiveBig = new GraphControl(DiskAccent, 60),
                XferBig = new GraphControl(DiskAccent, 60),
            };
            BuildDiskPanel(du);
            du.Card = BuildDiskCard(du);
            _disks.Add(du);
        }

        if (_disks.Count == 0) return;
        int insertAt = MetricList.Children.IndexOf(CardMem) + 1;
        for (int i = 0; i < _disks.Count; i++)
            MetricList.Children.Insert(insertAt + i, _disks[i].Card);
        _selectedDisk = _disks[0];
    }

    // Sum the formatted size of every volume the drive hosts (matches the "Formatted"
    // line Task Manager shows). Best-effort; drives that aren't ready are skipped.
    private static string ComputeFormatted(List<string> drives)
    {
        ulong total = 0;
        foreach (var letter in drives)
            try { var di = new System.IO.DriveInfo(letter); if (di.IsReady) total += (ulong)di.TotalSize; }
            catch { }
        return total > 0 ? $"{Gb(total):0} GB" : "—";
    }

    // A sidebar card matching the XAML ones, but built in code so we can have one per disk.
    private RadioButton BuildDiskCard(DiskUi du)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sparkHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            CornerRadius = new CornerRadius(3),
            Child = du.Spark
        };
        Grid.SetColumn(sparkHost, 0);

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = du.Title, FontSize = 14 });
        du.CardSub = new TextBlock { Text = "0%", Foreground = Gray, FontSize = 12 };
        text.Children.Add(du.CardSub);
        Grid.SetColumn(text, 1);

        grid.Children.Add(sparkHost);
        grid.Children.Add(text);

        var card = new RadioButton
        {
            GroupName = "metric",
            Style = (Style)Resources["Card"],
            Content = grid,
            Tag = du
        };
        card.Checked += Card_Checked;
        return card;
    }

    // The disk detail: a large active-time graph over a shorter disk-transfer-rate graph,
    // mirroring the real Performance tab. Each disk keeps its own graphs so switching
    // cards preserves the trace.
    private void BuildDiskPanel(DiskUi du)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) }); // active
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // xfer label
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // xfer graph

        root.Children.Add(DiskGraphBorder(du.ActiveBig, new Thickness(0, 4, 0, 0), 0));
        root.Children.Add(MemHeader("Disk transfer rate", out var xferMax, 1));
        du.XferMax = xferMax;
        root.Children.Add(DiskGraphBorder(du.XferBig, new Thickness(0, 2, 0, 4), 2));

        du.Panel = root;
    }

    private static Border DiskGraphBorder(GraphControl g, Thickness margin, int row)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1), Margin = margin, Child = g
        };
        Grid.SetRow(b, row);
        return b;
    }

    private static DiskSample? FindDisk(IReadOnlyList<DiskSample> disks, int index)
    {
        foreach (var d in disks) if (d.Index == index) return d;
        return null;
    }

    // GPU name + dedicated VRAM come from the display-adapter class key. The PDH
    // counters supply the live numbers; this is just for the static labels.
    private void LoadStaticGpuInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
            if (key?.GetValue("DriverDesc") is string desc && desc.Length > 0)
            {
                _gpuName = desc;
                GpuCardTitle.Text = "GPU 0";
            }
            if (key?.GetValue("HardwareInformation.qwMemorySize") is long qw && qw > 0)
                _gpuTotalBytes = (ulong)qw;

            if (key?.GetValue("DriverVersion") is string dv) _gpuDriverVersion = dv;
            if (key?.GetValue("DriverDate") is string dd) _gpuDriverDate = dd;
            if (key?.GetValue("LocationInformation") is string li && li.Length > 0) _gpuLocation = li;
        }
        catch { /* leave defaults */ }

        // Physical location: the display-adapter class key above usually leaves
        // LocationInformation blank, so read the PnP device property (the formatted
        // "PCI bus X, device Y, function Z" string Task Manager shows).
        if (_gpuLocation == "—")
        {
            try { string loc = NativeMethods.GetGpuLocation(_gpuName); if (loc.Length > 0) _gpuLocation = loc; }
            catch { }
        }

        // Hardware reserved = the card's nominal VRAM (its usable dedicated segment
        // rounded up to a whole GiB — GPUs ship in whole-GB capacities) minus the
        // usable amount. This reproduces Task Manager's value exactly (e.g. a 12 GB
        // card with 12243.2 MB usable shows 44.8 MB reserved). GetNodes() primes
        // PrimarySegments.
        try
        {
            GpuTopology.GetNodes();
            ulong usable = GpuTopology.PrimarySegments.DedicatedVideoBytes;
            if (usable > 0)
            {
                const ulong GiB = 1024UL * 1024 * 1024;
                ulong nominal = (usable + GiB - 1) / GiB * GiB; // round up to whole GB
                ulong reserved = nominal - usable;
                if (reserved > 0) _gpuReserved = Mb(reserved);
            }
        }
        catch { }
    }

    private static double Gb(ulong b) => b / 1024.0 / 1024.0 / 1024.0;
    private static double Gb(long b) => b / 1024.0 / 1024.0 / 1024.0;
    private static double Gb(double b) => b / 1024.0 / 1024.0 / 1024.0;

    private static string FormatUptime(ulong ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalDays}:{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
    private static string Kb(long b) => $"{b / 1024.0:0} KB";
    private static string Mb(ulong b) => $"{b / 1024.0 / 1024.0:0.0} MB";

    // bytes/sec -> bits/sec, humanised (Kbps / Mbps / Gbps)
    private static string Bits(double bytesPerSec)
    {
        double bps = bytesPerSec * 8;
        if (bps >= 1e9) return $"{bps / 1e9:0.0} Gbps";
        if (bps >= 1e6) return $"{bps / 1e6:0.0} Mbps";
        return $"{bps / 1e3:0} Kbps";
    }

    // Round a bytes/sec peak up to the axis max Task Manager shows: a 1/2/5 x 10^n value
    // in bits/sec, floored at 10 Kbps so light traffic still fills the graph instead of
    // hugging the bottom against a fixed 1 Mbps ceiling.
    private static double NiceNetScaleBytes(double peakBytesPerSec)
    {
        double bits = Math.Max(peakBytesPerSec * 8.0, 10_000);
        double pow = Math.Pow(10, Math.Floor(Math.Log10(bits)));
        double m = bits / pow;
        double nice = m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10;
        return nice * pow / 8.0;
    }

    // bytes/sec -> MB/s or KB/s
    private static string Rate(double bytesPerSec)
        => bytesPerSec >= 1024 * 1024
            ? $"{bytesPerSec / 1024.0 / 1024.0:0.0} MB/s"
            : $"{bytesPerSec / 1024.0:0} KB/s";
}
