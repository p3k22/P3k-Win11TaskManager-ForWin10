using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

using Win11TaskMan.Interop;
using Win11TaskMan.Services;

namespace Win11TaskMan.Views;

public partial class DetailsView : UserControl
{
    private readonly ObservableCollection<DetailRow> _rows = new();
    private readonly Dictionary<int, DetailRow> _byPid = new();
    private readonly ListCollectionView _view;
    private string _filter = "";

    // static per-pid info (user / arch / description) resolved off the UI thread, once
    private static readonly ConcurrentDictionary<int, StaticInfo> _cache = new();
    private readonly HashSet<int> _resolving = new();

    private sealed record StaticInfo(string User, string Arch, string Description, string Path, string Cmd);

    public DetailsView(SystemMonitor monitor)
    {
        InitializeComponent();
        _view = new ListCollectionView(_rows)
        {
            Filter = o => _filter.Length == 0 ||
                          ((DetailRow)o).Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        };
        _view.SortDescriptions.Add(new SortDescription(nameof(DetailRow.Name), ListSortDirection.Ascending));
        Grid.ItemsSource = _view;
        BuildColumns();

        var menu = new ContextMenu();
        var item = new MenuItem { Header = "Select columns..." };
        item.Click += SelectColumns_Click;
        menu.Items.Add(item);
        var hdr = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader),
                            (Style)FindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)));
        hdr.Setters.Add(new Setter(ContextMenuProperty, menu));
        Grid.ColumnHeaderStyle = hdr;

        Loaded += (_, _) => { monitor.Updated -= OnSnapshot; monitor.Updated += OnSnapshot; };
        Unloaded += (_, _) => monitor.Updated -= OnSnapshot;
    }

    private void OnSnapshot(Snapshot snap)
    {
        var seen = new HashSet<int>(snap.Processes.Count);
        foreach (var s in snap.Processes)
        {
            seen.Add(s.Pid);
            if (_byPid.TryGetValue(s.Pid, out var row)) row.Apply(s);
            else
            {
                var r = new DetailRow { Pid = s.Pid, Name = s.Name };
                r.Apply(s);
                _byPid[s.Pid] = r;
                _rows.Add(r);
                ResolveStatic(r);
            }
        }
        if (_byPid.Count != seen.Count)
            foreach (var pid in new List<int>(_byPid.Keys))
                if (!seen.Contains(pid)) { _rows.Remove(_byPid[pid]); _byPid.Remove(pid); }

        // Re-sort once per tick (not per property change) and keep the user's selection.
        var sel = Grid.SelectedItem as DetailRow;
        _view.Refresh();
        if (sel != null && _byPid.ContainsKey(sel.Pid) && !ReferenceEquals(Grid.SelectedItem, sel))
            Grid.SelectedItem = sel;
    }

    // Resolve user/arch/description on a worker thread, then push back to the row.
    private void ResolveStatic(DetailRow row)
    {
        int pid = row.Pid;
        if (_cache.TryGetValue(pid, out var hit)) { row.ApplyStatic(hit.User, hit.Arch, hit.Description, hit.Path, hit.Cmd); return; }
        if (!_resolving.Add(pid)) return;

        Task.Run(() =>
        {
            string user = NativeMethods.GetProcessUserName(pid);
            string arch = NativeMethods.GetProcessArchitecture(pid);
            string desc = "";
            string path = NativeMethods.GetProcessImagePath(pid);
            if (path.Length > 0)
            {
                try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; } catch { }
                if (desc.Length == 0) desc = Path.GetFileName(path);
            }
            var info = new StaticInfo(user, arch, desc, path, NativeMethods.GetProcessCommandLine(pid));
            _cache[pid] = info;
            Dispatcher.BeginInvoke(() =>
            {
                _resolving.Remove(pid);
                if (_byPid.TryGetValue(pid, out var r)) r.ApplyStatic(info.User, info.Arch, info.Description, info.Path, info.Cmd);
            });
        });
    }

    // ---- column picker ----
    private sealed record ColDef(string Id, string Header, string Text, string Sort, double Width, bool Right, bool Default);

    private static ColDef C(string id, string header, double w, bool right = true)
        => new(id, header, id + "Text", id + "Val", w, right, false);

    // Optional columns, created on demand. Built-in ones (XAML) are registered in BuildColumns.
    private static readonly ColDef[] Extra =
    {
        C("CpuTime", "CPU time", 90), C("Cycles", "Cycles delta total", 130),
        C("WsPrivate", "Memory (active private working set)", 130), C("PeakWs", "Peak working set (memory)", 130),
        C("Commit", "Commit size", 100), C("VirtualSize", "Virtual size", 110),
        C("PagedPool", "Paged pool", 90), C("NonPagedPool", "NP pool", 90),
        C("PageFaults", "Page faults", 100), C("HardFaults", "Hard faults", 90),
        C("BasePriority", "Base priority", 100, false), C("Threads", "Threads", 70), C("Handles", "Handles", 80),
        C("SessionId", "Session ID", 80),
        C("IoReads", "I/O reads", 90), C("IoWrites", "I/O writes", 90), C("IoOther", "I/O other", 90),
        C("IoReadBytes", "I/O read bytes", 110), C("IoWriteBytes", "I/O write bytes", 110), C("IoOtherBytes", "I/O other bytes", 110),
        C("GpuUtil", "GPU", 60), C("GpuDedicated", "Dedicated GPU memory", 140),
        C("GpuShared", "Shared GPU memory", 130), C("GpuCommitted", "Committed GPU memory", 150),
        C("StartTime", "Start time", 150, false), C("CommandLine", "Command line", 400, false), C("ImagePath", "Image path name", 300, false),
    };

    private readonly List<(ColDef def, DataGridColumn col)> _cols = new();

    private void BuildColumns()
    {
        // built-in XAML columns, matched by header text
        var builtIn = new (string header, string id)[]
        {
            ("Name", "Name"), ("PID", "Pid"), ("Status", "Status"), ("User name", "UserName"),
            ("CPU", "Cpu"), ("Memory", "Memory"), ("Architecture", "Architecture"), ("Description", "Description"),
        };
        foreach (var (header, id) in builtIn)
        {
            var col = Grid.Columns.First(c => c.Header as string == header);
            _cols.Add((new ColDef(id, header, "", "", 0, false, true), col));
        }
        int insertAt = Grid.Columns.IndexOf(_cols[^1].col); // keep the star-width Description last
        foreach (var d in Extra)
        {
            var col = new DataGridTextColumn
            {
                Header = d.Header, Binding = new Binding(d.Text), SortMemberPath = d.Sort,
                Width = d.Width,
            };
            if (d.Right) col.ElementStyle = (Style)FindResource("NumCell");
            Grid.Columns.Insert(insertAt++, col);
            _cols.Add((d, col));
        }
        ApplyVisible(LoadVisible());
    }

    private HashSet<string> LoadVisible()
    {
        var saved = AppSettings.DetailColumns;
        if (saved == null) return _cols.Where(c => c.def.Default).Select(c => c.def.Id).ToHashSet();
        return saved.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }

    private void ApplyVisible(HashSet<string> visible)
    {
        visible.Add("Name");
        var active = new HashSet<string>();
        foreach (var (def, col) in _cols)
        {
            bool on = visible.Contains(def.Id);
            col.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on && def.Text.Length > 0) { active.Add(def.Text); active.Add(def.Sort); }
        }
        DetailRow.ActiveProps = active;
    }

    private void SelectColumns_Click(object sender, RoutedEventArgs e)
    {
        var boxes = new List<(string id, CheckBox box)>();
        var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        panel.Children.Add(new TextBlock
        {
            Text = "Select the columns that will appear on the Details page.",
            Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
        });
        var visible = LoadVisible();
        foreach (var (def, _) in _cols)
        {
            var cb = new CheckBox
            {
                Content = def.Header, Margin = new Thickness(0, 3, 0, 3), Foreground = Brushes.White,
                IsChecked = def.Id == "Name" || visible.Contains(def.Id), IsEnabled = def.Id != "Name"
            };
            panel.Children.Add(cb);
            boxes.Add((def.Id, cb));
        }

        var win = new Window
        {
            Title = "Select Columns", Width = 380, Height = 560, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this), Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
            Foreground = Brushes.White,
        };
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => win.DialogResult = true;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 16, 12)
        };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        win.Content = dock;

        if (win.ShowDialog() != true) return;
        var chosen = boxes.Where(b => b.box.IsChecked == true).Select(b => b.id).ToHashSet();
        AppSettings.DetailColumns = string.Join(',', chosen);
        ApplyVisible(chosen);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text ?? "";
        _view.Refresh();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => EndTaskBtn.IsEnabled = Grid.SelectedItem is DetailRow;

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string? path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path)) return;
        ListSortDirection dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var c in Grid.Columns) c.SortDirection = null;
        e.Column.SortDirection = dir;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(path, dir));
        }
    }

    private void Grid_RightClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is DataGridRow row) { row.IsSelected = true; Grid.SelectedItem = row.Item; break; }
    }

    private DetailRow? Selected() => Grid.SelectedItem as DetailRow;

    private void EndTask_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row) return;
        try { using var p = Process.GetProcessById(row.Pid); p.Kill(); }
        catch (Exception ex) { Warn(ex); }
    }

    private void Priority_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row || sender is not MenuItem mi) return;
        if (!uint.TryParse(mi.Tag?.ToString(), out uint cls)) return;
        if (!NativeMethods.SetProcessPriority(row.Pid, cls))
            MessageBox.Show("Couldn't change priority (access denied or process exited).",
                "Task Manager", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row) return;
        string path = NativeMethods.GetProcessImagePath(row.Pid);
        if (path.Length == 0) { Warn(new Exception("File location not available.")); return; }
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch (Exception ex) { Warn(ex); }
    }

    private void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row) return;
        string q = Uri.EscapeDataString($"{row.Name} process");
        try { Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={q}") { UseShellExecute = true }); }
        catch (Exception ex) { Warn(ex); }
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row) return;
        string path = NativeMethods.GetProcessImagePath(row.Pid);
        if (path.Length == 0) { Warn(new Exception("File not available.")); return; }
        try { NativeMethods.ShowFileProperties(path); } catch (Exception ex) { Warn(ex); }
    }

    private static void Warn(Exception ex)
        => MessageBox.Show(ex.Message, "Task Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
}

public sealed class DetailRow : INotifyPropertyChanged
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";

    private double _cpu;
    public double Cpu { get => _cpu; set => Set(ref _cpu, value, nameof(Cpu), nameof(CpuText)); }
    private long _mem;
    public long MemBytes { get => _mem; set => Set(ref _mem, value, nameof(MemBytes), nameof(MemText)); }

    private string _status = "Running";
    public string Status { get => _status; private set => SetStr(ref _status, value, nameof(Status)); }
    private string _user = "";
    public string UserName { get => _user; private set => SetStr(ref _user, value, nameof(UserName)); }
    private string _arch = "";
    public string Architecture { get => _arch; private set => SetStr(ref _arch, value, nameof(Architecture)); }
    private string _desc = "";
    public string Description { get => _desc; private set => SetStr(ref _desc, value, nameof(Description)); }

    public string CpuText => Cpu < 0.05 ? "00" : $"{Cpu:00}";
    public string MemText => $"{MemBytes / 1024.0:N0} K";

    /// <summary>Property names of the optional columns currently shown; only these are re-notified each tick.</summary>
    public static HashSet<string> ActiveProps { get; set; } = new();

    private ProcSample? _s;
    public void Apply(ProcSample s)
    {
        Cpu = s.Cpu; MemBytes = s.MemBytes;
        _s = s;
        foreach (var p in ActiveProps) OnChanged(p);
    }

    public void ApplyStatic(string user, string arch, string desc, string path, string cmd)
    {
        UserName = user; Architecture = arch; Description = desc;
        if (_cmd != cmd) { _cmd = cmd; OnChanged(nameof(CommandLineText)); OnChanged(nameof(CommandLineVal)); }
        if (_path != path) { _path = path; OnChanged(nameof(ImagePathText)); OnChanged(nameof(ImagePathVal)); }
    }

    private string _path = "", _cmd = "";
    private ProcExtra X => _s?.Extra ?? default;
    private static string K(long b) => $"{b / 1024.0:N0} K";
    private static string N(long v) => v.ToString("N0");

    public string CpuTimeText { get { var t = TimeSpan.FromTicks(X.CpuTime100ns); return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"; } }
    public double CpuTimeVal => X.CpuTime100ns;
    public string CyclesText => X.Cycles.ToString("N0");
    public double CyclesVal => X.Cycles;
    public string WsPrivateText => K(X.WorkingSetPrivate);
    public double WsPrivateVal => X.WorkingSetPrivate;
    public string PeakWsText => K(X.PeakWorkingSet);
    public double PeakWsVal => X.PeakWorkingSet;
    public string CommitText => K(X.PagefileUsage);
    public double CommitVal => X.PagefileUsage;
    public string VirtualSizeText => K(X.VirtualSize);
    public double VirtualSizeVal => X.VirtualSize;
    public string PagedPoolText => K(X.PagedPool);
    public double PagedPoolVal => X.PagedPool;
    public string NonPagedPoolText => K(X.NonPagedPool);
    public double NonPagedPoolVal => X.NonPagedPool;
    public string PageFaultsText => N(X.PageFaults);
    public double PageFaultsVal => X.PageFaults;
    public string HardFaultsText => N(X.HardFaults);
    public double HardFaultsVal => X.HardFaults;
    public string BasePriorityText => X.BasePriority switch
    {
        <= 4 => "Low", <= 6 => "Below normal", <= 8 => "Normal", <= 10 => "Above normal",
        <= 13 => "High", _ => "Realtime"
    };
    public double BasePriorityVal => X.BasePriority;
    public string ThreadsText => N(_s?.Threads ?? 0);
    public double ThreadsVal => _s?.Threads ?? 0;
    public string HandlesText => N(_s?.Handles ?? 0);
    public double HandlesVal => _s?.Handles ?? 0;
    public string SessionIdText => (_s?.SessionId ?? 0).ToString();
    public double SessionIdVal => _s?.SessionId ?? 0;
    public string IoReadsText => N(X.ReadOps);
    public double IoReadsVal => X.ReadOps;
    public string IoWritesText => N(X.WriteOps);
    public double IoWritesVal => X.WriteOps;
    public string IoOtherText => N(X.OtherOps);
    public double IoOtherVal => X.OtherOps;
    public string IoReadBytesText => K(X.ReadBytes);
    public double IoReadBytesVal => X.ReadBytes;
    public string IoWriteBytesText => K(X.WriteBytes);
    public double IoWriteBytesVal => X.WriteBytes;
    public string IoOtherBytesText => K(X.OtherBytes);
    public double IoOtherBytesVal => X.OtherBytes;
    public string GpuUtilText => (_s?.Gpu ?? 0) < 0.5 ? "0%" : $"{_s!.Gpu:0}%";
    public double GpuUtilVal => _s?.Gpu ?? 0;
    public string GpuDedicatedText => K(_s?.GpuDedicated ?? 0);
    public double GpuDedicatedVal => _s?.GpuDedicated ?? 0;
    public string GpuSharedText => K(_s?.GpuShared ?? 0);
    public double GpuSharedVal => _s?.GpuShared ?? 0;
    public string GpuCommittedText => K(_s?.GpuCommitted ?? 0);
    public double GpuCommittedVal => _s?.GpuCommitted ?? 0;
    public string StartTimeText
    {
        get
        {
            try { return X.CreateFileTime > 0 ? DateTime.FromFileTime(X.CreateFileTime).ToString("g") : ""; }
            catch { return ""; }
        }
    }
    public double StartTimeVal => X.CreateFileTime;
    public string CommandLineText => _cmd;
    public string CommandLineVal => _cmd;
    public string ImagePathText => _path;
    public string ImagePathVal => _path;

    private void Set(ref double f, double v, string a, string b)
    { if (Math.Abs(f - v) > 0.0001) { f = v; OnChanged(a); OnChanged(b); } }
    private void Set(ref long f, long v, string a, string b)
    { if (f != v) { f = v; OnChanged(a); OnChanged(b); } }
    private void SetStr(ref string f, string v, string a)
    { if (f != v) { f = v; OnChanged(a); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
