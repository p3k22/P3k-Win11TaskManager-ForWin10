using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Controls;

using Win11TaskMan.Interop;
using Win11TaskMan.Services;

namespace Win11TaskMan.Views;

public partial class UsersView : UserControl
{
    private readonly ObservableCollection<UserRow> _rows = new();
    private readonly Dictionary<uint, UserRow> _bySession = new();

    public UsersView(SystemMonitor monitor)
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;
        Loaded += (_, _) => { monitor.Updated -= OnSnapshot; monitor.Updated += OnSnapshot; };
        Unloaded += (_, _) => monitor.Updated -= OnSnapshot;
    }

    private void OnSnapshot(Snapshot snap)
    {
        // aggregate CPU / memory / count per session
        var agg = new Dictionary<uint, (double cpu, long mem, int count)>();
        foreach (var p in snap.Processes)
        {
            agg.TryGetValue(p.SessionId, out var a);
            agg[p.SessionId] = (a.cpu + p.Cpu, a.mem + p.MemBytes, a.count + 1);
        }

        var sessions = NativeMethods.GetSessions();
        var live = new HashSet<uint>();
        foreach (var s in sessions)
        {
            live.Add(s.SessionId);
            agg.TryGetValue(s.SessionId, out var a);
            if (_bySession.TryGetValue(s.SessionId, out var row))
                row.Update(s.User, s.Status, a.cpu, a.mem, a.count);
            else
            {
                var r = new UserRow(s.SessionId);
                r.Update(s.User, s.Status, a.cpu, a.mem, a.count);
                _bySession[s.SessionId] = r;
                _rows.Add(r);
            }
        }
        foreach (var id in _bySession.Keys.ToList())
            if (!live.Contains(id)) { _rows.Remove(_bySession[id]); _bySession.Remove(id); }
    }
}

public sealed class UserRow : INotifyPropertyChanged
{
    public uint SessionId { get; }
    public UserRow(uint id) => SessionId = id;

    private string _user = "";
    public string User { get => _user; private set => Set(ref _user, value, nameof(User)); }
    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value, nameof(Status)); }
    private int _count;
    public int ProcCount { get => _count; private set => Set(ref _count, value, nameof(ProcCount)); }

    private double _cpu;
    private long _mem;
    public string CpuText => $"{System.Math.Min(_cpu, 100):0}%";
    public string MemText => $"{_mem / 1024.0 / 1024.0:N0} MB";

    public void Update(string user, string status, double cpu, long mem, int count)
    {
        User = user; Status = status; ProcCount = count;
        _cpu = cpu; _mem = mem;
        OnChanged(nameof(CpuText)); OnChanged(nameof(MemText));
    }

    private void Set<T>(ref T f, T v, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(f, v)) { f = v; OnChanged(name); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
