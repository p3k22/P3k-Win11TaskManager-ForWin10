using System;
using System.Diagnostics;
using System.Security.Principal;

namespace Win11TaskMan.Services;

/// <summary>
/// Shared helpers for detecting and requesting administrator elevation.
/// </summary>
internal static class Elevation
{
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Relaunches the current executable with the "runas" verb, which raises the
    /// Windows UAC prompt. Returns false if the executable path is unknown (so the
    /// caller should keep running); throws if the prompt is dismissed or elevation
    /// fails, leaving the caller to surface that or fall back to running unelevated.
    /// </summary>
    public static bool RelaunchElevated()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
        return true;
    }
}
