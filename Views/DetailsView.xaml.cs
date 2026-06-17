using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    private sealed record StaticInfo(string User, string Arch, string Description);

    public DetailsView(SystemMonitor monitor)
    {
        InitializeComponent();
        _view = new ListCollectionView(_rows)
        {
            IsLiveSorting = true,
            Filter = o => _filter.Length == 0 ||
                          ((DetailRow)o).Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        };
        _view.SortDescriptions.Add(new SortDescription(nameof(DetailRow.Name), ListSortDirection.Ascending));
        _view.LiveSortingProperties.Add(nameof(DetailRow.Name));
        Grid.ItemsSource = _view;

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
    }

    // Resolve user/arch/description on a worker thread, then push back to the row.
    private void ResolveStatic(DetailRow row)
    {
        int pid = row.Pid;
        if (_cache.TryGetValue(pid, out var hit)) { row.ApplyStatic(hit.User, hit.Arch, hit.Description); return; }
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
            var info = new StaticInfo(user, arch, desc);
            _cache[pid] = info;
            Dispatcher.BeginInvoke(() =>
            {
                _resolving.Remove(pid);
                if (_byPid.TryGetValue(pid, out var r)) r.ApplyStatic(info.User, info.Arch, info.Description);
            });
        });
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
            _view.LiveSortingProperties.Clear();
            _view.LiveSortingProperties.Add(path);
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

    public void Apply(ProcSample s) { Cpu = s.Cpu; MemBytes = s.MemBytes; }

    public void ApplyStatic(string user, string arch, string desc)
    {
        UserName = user; Architecture = arch; Description = desc;
    }

    private void Set(ref double f, double v, string a, string b)
    { if (Math.Abs(f - v) > 0.0001) { f = v; OnChanged(a); OnChanged(b); } }
    private void Set(ref long f, long v, string a, string b)
    { if (f != v) { f = v; OnChanged(a); OnChanged(b); } }
    private void SetStr(ref string f, string v, string a)
    { if (f != v) { f = v; OnChanged(a); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
