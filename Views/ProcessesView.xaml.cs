using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

using Win11TaskMan.Interop;
using Win11TaskMan.Models;
using Win11TaskMan.Services;

namespace Win11TaskMan.Views;

public partial class ProcessesView : UserControl
{
    private readonly ObservableCollection<ProcessRow> _rows = new();
    private readonly Dictionary<int, ProcessRow> _byPid = new();
    private readonly ListCollectionView _view;
    private string _filter = "";

    public ProcessesView(SystemMonitor monitor)
    {
        InitializeComponent();

        _view = new ListCollectionView(_rows)
        {
            IsLiveGrouping = true,
            IsLiveSorting = true,
            Filter = o => _filter.Length == 0 ||
                          ((ProcessRow)o).Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        };
        _view.LiveGroupingProperties.Add(nameof(ProcessRow.Group));
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProcessRow.Group)));
        // "Apps" sorts before "Background processes" (ascending), then by name
        _view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Group), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Name), ListSortDirection.Ascending));
        _view.LiveSortingProperties.Add(nameof(ProcessRow.Name));
        Grid.ItemsSource = _view;

        // See PerformanceView: subscribe on Loaded so switching tabs re-attaches.
        Loaded += (_, _) => { monitor.Updated -= OnSnapshot; monitor.Updated += OnSnapshot; };
        Unloaded += (_, _) => monitor.Updated -= OnSnapshot;
    }

    private void OnSnapshot(Snapshot snap)
    {
        var seen = new HashSet<int>(snap.Processes.Count);
        foreach (var s in snap.Processes)
        {
            seen.Add(s.Pid);
            if (_byPid.TryGetValue(s.Pid, out var row))
                row.Apply(s);
            else
            {
                var r = new ProcessRow { Pid = s.Pid, Name = s.Name };
                r.Apply(s);
                _byPid[s.Pid] = r;
                _rows.Add(r);
            }
        }
        // drop dead processes
        if (_byPid.Count != seen.Count)
            foreach (var pid in new List<int>(_byPid.Keys))
                if (!seen.Contains(pid)) { _rows.Remove(_byPid[pid]); _byPid.Remove(pid); }

        // aggregate header numbers (named TextBlocks inside the column headers)
        if (CpuHdr != null)  CpuHdr.Text  = $"{snap.System.CpuPercent:0}%";
        if (MemHdr != null)  MemHdr.Text  = $"{snap.System.MemPercent:0}%";
        if (DiskHdr != null)
        {
            double diskActive = 0;
            foreach (var d in snap.Disks) if (d.ActivePercent > diskActive) diskActive = d.ActivePercent;
            DiskHdr.Text = $"{diskActive:0}%";
        }
        if (NetHdr != null)
        {
            double bps = (snap.Net.SendBps + snap.Net.RecvBps) * 8;
            double pct = snap.Net.LinkSpeedBps > 0 ? bps / snap.Net.LinkSpeedBps * 100.0 : 0;
            NetHdr.Text = $"{Math.Min(pct, 100):0}%";
        }
        if (GpuHdr != null) GpuHdr.Text = $"{snap.Gpu.UtilPercent:0}%";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text ?? "";
        _view.Refresh();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => EndTaskBtn.IsEnabled = Grid.SelectedItem is ProcessRow;

    // A grouped DataGrid doesn't honour a star column, so size the Name column
    // to absorb the leftover width explicitly whenever the grid resizes.
    private void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double others = 0;
        foreach (var c in Grid.Columns)
            if (c != NameCol) others += c.ActualWidth;
        double avail = Grid.ActualWidth - others - 40; // vertical scrollbar + cell padding
        NameCol.Width = new DataGridLength(Math.Max(220, avail));
    }

    // Click-to-sort: keep the Apps/Background grouping (Group always primary),
    // then sort within each group by the clicked column. Resource columns default
    // to descending so the heaviest process surfaces first, like Win11.
    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string? path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path)) return;

        ListSortDirection dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        if (e.Column.SortDirection == null)
            dir = path == nameof(ProcessRow.Name)
                ? ListSortDirection.Ascending : ListSortDirection.Descending;

        foreach (var c in Grid.Columns) c.SortDirection = null;
        e.Column.SortDirection = dir;

        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Group), ListSortDirection.Ascending));
            _view.SortDescriptions.Add(new SortDescription(path, dir));

            _view.LiveSortingProperties.Clear();
            _view.LiveSortingProperties.Add(nameof(ProcessRow.Group));
            _view.LiveSortingProperties.Add(path);
        }
    }

    // Right-click selects the row under the cursor before its context menu opens.
    private void Grid_RightClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is DataGridRow row) { row.IsSelected = true; Grid.SelectedItem = row.Item; break; }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (PathFor(Selected()) is not { } path) return;
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Warn(ex); }
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
        if (PathFor(Selected()) is not { } path) return;
        try { NativeMethods.ShowFileProperties(path); }
        catch (Exception ex) { Warn(ex); }
    }

    private ProcessRow? Selected() => Grid.SelectedItem as ProcessRow;

    private static string? PathFor(ProcessRow? row)
    {
        if (row == null) return null;
        try { using var p = Process.GetProcessById(row.Pid); return p.MainModule?.FileName; }
        catch
        {
            MessageBox.Show("The file location is not available for this process.",
                "Task Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
    }

    private static void Warn(Exception ex)
        => MessageBox.Show(ex.Message, "Task Manager", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void EndTask_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ProcessRow row) return;
        try { using var p = Process.GetProcessById(row.Pid); p.Kill(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't end {row.Name}: {ex.Message}", "Task Manager",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RunNew_Click(object sender, RoutedEventArgs e)
    {
        var input = Prompt("Open:", "Create new task");
        if (string.IsNullOrWhiteSpace(input)) return;
        try { Process.Start(new ProcessStartInfo(input.Trim()) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Task Manager"); }
    }

    private static string? Prompt(string label, string title)
    {
        var box = new TextBox { Margin = new Thickness(12, 8, 12, 12), MinWidth = 300 };
        var win = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, Background = System.Windows.Media.Brushes.DimGray
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(12, 12, 12, 0), Foreground = System.Windows.Media.Brushes.White });
        panel.Children.Add(box);
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(12, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        ok.Click += (_, _) => { win.DialogResult = true; };
        panel.Children.Add(ok);
        win.Content = panel;
        return win.ShowDialog() == true ? box.Text : null;
    }
}
