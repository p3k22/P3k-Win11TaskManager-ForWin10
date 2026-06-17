using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace Win11TaskMan.Interop;

/// <summary>
/// CPU package temperature via LibreHardwareMonitor, which reads the Ryzen
/// SMU / Intel MSR through a small kernel driver it loads on Open(). That
/// driver needs elevation — without it, no temperature sensors appear and
/// <see cref="Available"/> stays false (UI then shows a prompt to run as admin).
/// </summary>
public sealed class CpuThermal : IDisposable
{
    private readonly Computer _computer;
    private readonly IHardware? _cpu;

    public bool Available { get; }

    public CpuThermal()
    {
        try
        {
            _computer = new Computer { IsCpuEnabled = true };
            _computer.Open();
            _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            Available = _cpu != null && ReadInternal() > 0;
        }
        catch
        {
            _computer ??= new Computer();
            Available = false;
        }
    }

    public double Read() => _cpu == null ? 0 : ReadInternal();

    private double ReadInternal()
    {
        try
        {
            _cpu!.Update();
            double pkg = 0, any = 0;
            foreach (var s in _cpu.Sensors)
            {
                if (s.SensorType != SensorType.Temperature || s.Value is not float v) continue;
                if (s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                    pkg = Math.Max(pkg, v);
                any = Math.Max(any, v);
            }
            double t = pkg > 0 ? pkg : any;
            return t is > 0 and < 150 ? t : 0;
        }
        catch { return 0; }
    }

    public void Dispose() { try { _computer.Close(); } catch { } }
}
