using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Win11TaskMan.Services;

/// <summary>
/// Startup entries from the Run registry keys and the Startup folders, plus the
/// StartupApproved enabled/disabled flag that Settings and Task Manager toggle.
/// </summary>
public static class StartupProvider
{
    public enum Source { UserRun, MachineRun, MachineRunWow, UserFolder, CommonFolder }

    public sealed record Entry(
        string Name, string Publisher, string Command, bool Enabled, Source Src, string ValueName);

    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunWow = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static List<Entry> Enumerate()
    {
        var list = new List<Entry>();
        ReadRun(Registry.CurrentUser, Run, Source.UserRun, "Run", list);
        ReadRun(Registry.LocalMachine, Run, Source.MachineRun, "Run", list);
        ReadRun(Registry.LocalMachine, RunWow, Source.MachineRunWow, "Run32", list);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Source.UserFolder, list);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Source.CommonFolder, list);
        return list;
    }

    private static void ReadRun(RegistryKey hive, string sub, Source src, string approvedLeaf, List<Entry> list)
    {
        try
        {
            using var key = hive.OpenSubKey(sub);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                string cmd = key.GetValue(name)?.ToString() ?? "";
                bool enabled = IsApproved(hive, approvedLeaf, name);
                list.Add(new Entry(name, Publisher(cmd), cmd, enabled, src, name));
            }
        }
        catch { }
    }

    private static void ReadFolder(string dir, Source src, List<Entry> list)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var hive = src == Source.UserFolder ? Registry.CurrentUser : Registry.LocalMachine;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string valueName = Path.GetFileName(file);
                bool enabled = IsApproved(hive, "StartupFolder", valueName);
                list.Add(new Entry(name, Publisher(file), file, enabled, src, valueName));
            }
        }
        catch { }
    }

    // StartupApproved: missing value => enabled; otherwise enabled when the low
    // bit of the first byte is clear (02/06 = enabled, 03 = disabled).
    private static bool IsApproved(RegistryKey hive, string leaf, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey($@"{Approved}\{leaf}");
            if (key?.GetValue(valueName) is byte[] data && data.Length > 0)
                return (data[0] & 1) == 0;
        }
        catch { }
        return true;
    }

    public static void SetEnabled(Entry e, bool enabled)
    {
        var (hive, leaf) = e.Src switch
        {
            Source.UserRun => (Registry.CurrentUser, "Run"),
            Source.MachineRun => (Registry.LocalMachine, "Run"),
            Source.MachineRunWow => (Registry.LocalMachine, "Run32"),
            Source.UserFolder => (Registry.CurrentUser, "StartupFolder"),
            _ => (Registry.LocalMachine, "StartupFolder")
        };
        using var key = hive.CreateSubKey($@"{Approved}\{leaf}", true);
        var data = new byte[12];
        data[0] = (byte)(enabled ? 0x02 : 0x03);
        key.SetValue(e.ValueName, data, RegistryValueKind.Binary);
    }

    private static string Publisher(string command)
    {
        try
        {
            string path = ExtractPath(command);
            if (path.Length == 0 || !File.Exists(path)) return "";
            return FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
        }
        catch { return ""; }
    }

    private static string ExtractPath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command.Trim('"');
        }
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return command[..(exe + 4)];
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}
