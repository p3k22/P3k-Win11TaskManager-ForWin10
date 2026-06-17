using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

using Win11TaskMan.Services;

namespace Win11TaskMan.Views;

public partial class StartupView : UserControl
{
    private readonly ObservableCollection<StartupRow> _rows = new();
    private readonly ListCollectionView _view;

    public StartupView()
    {
        InitializeComponent();
        _view = new ListCollectionView(_rows);
        _view.SortDescriptions.Add(new SortDescription(nameof(StartupRow.Name), ListSortDirection.Ascending));
        Grid.ItemsSource = _view;
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _rows.Clear();
        foreach (var e in StartupProvider.Enumerate())
            _rows.Add(new StartupRow(e));
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is StartupRow row)
        {
            ToggleBtn.IsEnabled = true;
            ToggleBtn.Content = row.Entry.Enabled ? "Disable" : "Enable";
        }
        else ToggleBtn.IsEnabled = false;
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not StartupRow row) return;
        bool target = !row.Entry.Enabled;
        try
        {
            StartupProvider.SetEnabled(row.Entry, target);
            row.SetEnabled(target);
            ToggleBtn.Content = target ? "Disable" : "Enable";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't change this entry: {ex.Message}\n\nMachine-wide entries need Task Manager to run as administrator.",
                "Task Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Grid_RightClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is DataGridRow row) { row.IsSelected = true; Grid.SelectedItem = row.Item; break; }
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
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(path, dir));
    }
}

public sealed class StartupRow : INotifyPropertyChanged
{
    public StartupProvider.Entry Entry { get; private set; }
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher;
    public bool Enabled => Entry.Enabled;
    public string StatusText => Entry.Enabled ? "Enabled" : "Disabled";
    public string Impact => "Not measured";

    public StartupRow(StartupProvider.Entry e) => Entry = e;

    public void SetEnabled(bool enabled)
    {
        Entry = Entry with { Enabled = enabled };
        OnChanged(nameof(Enabled)); OnChanged(nameof(StatusText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
