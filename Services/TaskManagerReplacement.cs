using System;
using System.IO;
using Microsoft.Win32;

namespace Win11TaskMan.Services;

/// <summary>
/// Replaces the built-in Windows Task Manager with this app via the Image File
/// Execution Options "Debugger" hook on taskmgr.exe — the same mechanism
/// Sysinternals Process Explorer uses for "Replace Task Manager". While the hook
/// is set, anything that launches taskmgr.exe (Ctrl+Shift+Esc, the taskbar
/// right-click menu, Ctrl+Alt+Del) starts this exe instead, with the original
/// taskmgr.exe path appended as an argument (which the WPF app simply ignores).
///
/// The registry key under HKLM is the single source of truth for the toggle, so
/// no separate settings file is needed. Reading works for any user; writing
/// needs administrator rights.
/// </summary>
internal static class TaskManagerReplacement
{
    private const string IfeoKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\taskmgr.exe";
    private const string DebuggerValue = "Debugger";

    /// <summary>True when taskmgr.exe is currently hooked to launch this app.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(IfeoKey);
            return key?.GetValue(DebuggerValue) is string dbg
                   && dbg.Length > 0 && PointsToThisApp(dbg);
        }
        catch { return false; }
    }

    /// <summary>Point taskmgr.exe at this app. Requires elevation.</summary>
    public static void Enable()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine this app's executable path.");
        using var key = Registry.LocalMachine.CreateSubKey(IfeoKey, writable: true);
        key.SetValue(DebuggerValue, $"\"{exe}\"", RegistryValueKind.String);
    }

    /// <summary>Restore the default Task Manager. Requires elevation.</summary>
    public static void Disable()
    {
        using var key = Registry.LocalMachine.OpenSubKey(IfeoKey, writable: true);
        if (key?.GetValue(DebuggerValue) != null)
            key.DeleteValue(DebuggerValue, throwOnMissingValue: false);
    }

    /// <summary>
    /// Starts the real Windows Task Manager. While the IFEO hook is set, launching
    /// taskmgr.exe would just start this app again, so the hook is lifted for a few
    /// seconds by an elevated helper (UAC prompt unless already elevated) and restored.
    /// </summary>
    public static void LaunchOriginal()
    {
        string taskmgr = Path.Combine(Environment.SystemDirectory, "taskmgr.exe");
        if (!IsHooked())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(taskmgr) { UseShellExecute = true });
            return;
        }

        string script =
            "$k='HKLM:\\" + IfeoKey + "';" +
            "$v=(Get-ItemProperty $k).Debugger;" +
            "Remove-ItemProperty $k Debugger;" +
            "try{Start-Process '" + taskmgr + "';Start-Sleep 4}finally{Set-ItemProperty $k Debugger $v}";
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}")
        { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
    }

    private static bool IsHooked()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(IfeoKey);
            return key?.GetValue(DebuggerValue) is string d && d.Length > 0;
        }
        catch { return false; }
    }

    private static bool PointsToThisApp(string debugger)
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return false;

        // The Debugger value is a command-line prefix; take its first token,
        // honouring quoting, so a moved-but-same-named exe still reads as "ours".
        string first = debugger.StartsWith('"')
            ? debugger.Substring(1, Math.Max(0, debugger.IndexOf('"', 1) - 1))
            : debugger.Split(' ')[0];

        return string.Equals(first, exe, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(first), Path.GetFileName(exe),
                             StringComparison.OrdinalIgnoreCase);
    }
}
