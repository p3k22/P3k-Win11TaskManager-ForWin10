using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

using Win11TaskMan.Services;

namespace Win11TaskMan.Models;

public sealed class ProcessRow : INotifyPropertyChanged
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Group { get; private set; } = "Background processes";

    private double _cpu;
    public double Cpu { get => _cpu; set => Set(ref _cpu, value, nameof(Cpu), nameof(CpuText)); }

    private long _mem;
    public long MemBytes { get => _mem; set => Set(ref _mem, value, nameof(MemBytes), nameof(MemText)); }

    private double _disk;
    public double DiskBps { get => _disk; set => Set(ref _disk, value, nameof(DiskBps), nameof(DiskText)); }

    private double _gpu;
    public double Gpu { get => _gpu; set => Set(ref _gpu, value, nameof(Gpu), nameof(GpuText)); }

    public string CpuText => Cpu < 0.05 ? "0%" : $"{Cpu:0.0}%".Replace(".0%", "%");
    public string MemText => $"{MemBytes / 1024.0 / 1024.0:0.0} MB";
    public string DiskText => DiskBps < 51200 ? "0 MB/s" : $"{DiskBps / 1024.0 / 1024.0:0.1} MB/s";
    public string NetText => "0 Mbps";   // per-process network needs ETW (see notes)
    public string GpuText => Gpu < 0.5 ? "0%" : $"{Gpu:0}%";

    public string PowerText => Cpu switch
    {
        >= 10 => "Very high",
        >= 4  => "High",
        >= 1  => "Moderate",
        >= 0.2 => "Low",
        _ => "Very low"
    };

    public void Apply(ProcSample s)
    {
        Cpu = s.Cpu; MemBytes = s.MemBytes; DiskBps = s.DiskBps; Gpu = s.Gpu;
        var g = s.IsApp ? "Apps" : "Background processes";
        if (g != Group) { Group = g; OnChanged(nameof(Group)); }
    }

    private void Set(ref double f, double v, string a, string b)
    { if (Math.Abs(f - v) > 0.0001) { f = v; OnChanged(a); OnChanged(b); } }
    private void Set(ref long f, long v, string a, string b)
    { if (f != v) { f = v; OnChanged(a); OnChanged(b); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>0..max value -> translucent blue cell fill (the Win11 heatmap look).</summary>
public sealed class HeatConverter : IValueConverter
{
    public double Max { get; set; } = 100;
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double v = System.Convert.ToDouble(value, c);
        double cap = p != null ? double.Parse(p.ToString()!, CultureInfo.InvariantCulture) : Max;
        double k = Math.Clamp(v / cap, 0, 1);
        // base accent #2E9BD6, alpha scales with intensity
        byte a = (byte)(k * 150);
        return new SolidColorBrush(Color.FromArgb(a, 0x2E, 0x9B, 0xD6));
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>"Very high" power rating -> bright fill, others -> graded.</summary>
public sealed class PowerHeatConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        byte a = (value as string) switch
        {
            "Very high" => 150, "High" => 90, "Moderate" => 50, "Low" => 20, _ => 0
        };
        return new SolidColorBrush(Color.FromArgb(a, 0x2E, 0x9B, 0xD6));
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
