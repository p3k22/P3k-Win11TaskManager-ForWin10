using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Win11TaskMan.Services;

namespace Win11TaskMan.Views;

public partial class ServicesView : UserControl
{
    private readonly ObservableCollection<ServiceRow> _rows = new();
    private readonly Dictionary<string, ServiceRow> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _timer;
    private string _filter = "";

    public ServicesView()
    {
        InitializeComponent();
        _view = new ListCollectionView(_rows)
        {
            IsLiveSorting = true,
            Filter = o => _filter.Length == 0 ||
                          ((ServiceRow)o).Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                          ((ServiceRow)o).DisplayName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        };
        _view.SortDescriptions.Add(new SortDescription(nameof(ServiceRow.Name), ListSortDirection.Ascending));
        _view.LiveSortingProperties.Add(nameof(ServiceRow.Name));
        Grid.ItemsSource = _view;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in ServicesProvider.Enumerate())
        {
            seen.Add(s.Name);
            if (_byName.TryGetValue(s.Name, out var row)) row.Apply(s);
            else
            {
                var r = new ServiceRow(s);
                _byName[s.Name] = r;
                _rows.Add(r);
            }
        }
        if (_byName.Count != seen.Count)
            foreach (var name in _byName.Keys.ToList())
                if (!seen.Contains(name)) { _rows.Remove(_byName[name]); _byName.Remove(name); }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text ?? "";
        _view.Refresh();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool has = Grid.SelectedItem is ServiceRow;
        bool running = (Grid.SelectedItem as ServiceRow)?.Status == "Running";
        StartBtn.IsEnabled = has && !running;
        StopBtn.IsEnabled = has && running;
        RestartBtn.IsEnabled = has && running;
    }

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

    private void Start_Click(object sender, RoutedEventArgs e) => Control(true);
    private void Stop_Click(object sender, RoutedEventArgs e) => Control(false);

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ServiceRow row) return;
        try { ServicesProvider.Control(row.Name, false); System.Threading.Thread.Sleep(400); ServicesProvider.Control(row.Name, true); Refresh(); }
        catch (Exception ex) { Warn(ex); }
    }

    private void Control(bool start)
    {
        if (Grid.SelectedItem is not ServiceRow row) return;
        try { ServicesProvider.Control(row.Name, start); Refresh(); }
        catch (Exception ex) { Warn(ex); }
    }

    private void OpenServices_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true }); }
        catch (Exception ex) { Warn(ex); }
    }

    private static void Warn(Exception ex)
        => MessageBox.Show(ex.Message, "Task Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
}

public sealed class ServiceRow : INotifyPropertyChanged
{
    public string Name { get; }
    public string DisplayName { get; private set; }

    private int _pid;
    public int Pid { get => _pid; private set => Set(ref _pid, value, nameof(Pid), nameof(PidText)); }
    private string _status = "";
    public string Status { get => _status; private set { if (_status != value) { _status = value; OnChanged(nameof(Status)); } } }

    public string PidText => Pid == 0 ? "" : Pid.ToString();

    public ServiceRow(ServicesProvider.ServiceInfo s)
    {
        Name = s.Name; DisplayName = s.DisplayName; _pid = s.Pid; _status = s.Status;
    }

    public void Apply(ServicesProvider.ServiceInfo s)
    {
        if (DisplayName != s.DisplayName) { DisplayName = s.DisplayName; OnChanged(nameof(DisplayName)); }
        Pid = s.Pid; Status = s.Status;
    }

    private void Set(ref int f, int v, string a, string b)
    { if (f != v) { f = v; OnChanged(a); OnChanged(b); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
