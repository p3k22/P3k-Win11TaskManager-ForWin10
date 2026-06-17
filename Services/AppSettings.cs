using Microsoft.Win32;

namespace Win11TaskMan.Services;

/// <summary>
/// Minimal persisted user settings, stored under HKCU\Software\Win11TaskMan.
/// Failures (e.g. a locked-down registry) are swallowed so the app still runs;
/// the setting simply won't persist.
/// </summary>
internal static class AppSettings
{
    private const string KeyPath = @"Software\Win11TaskMan";

    /// <summary>
    /// When set, the app relaunches itself elevated on every startup if it is not
    /// already running as administrator (raising a UAC prompt each launch).
    /// </summary>
    public static bool AlwaysRunAsAdmin
    {
        get => GetBool(nameof(AlwaysRunAsAdmin));
        set => SetBool(nameof(AlwaysRunAsAdmin), value);
    }

    /// <summary>
    /// Polling interval for the live monitor, in milliseconds. A value of 0 means
    /// the monitor starts paused. Defaults to 1000 ms ("Normal").
    /// </summary>
    public static int UpdateIntervalMs
    {
        get => GetInt(nameof(UpdateIntervalMs), 1000);
        set => SetInt(nameof(UpdateIntervalMs), value);
    }

    /// <summary>
    /// Zero-based index of the page shown on launch, matching the sidebar order
    /// (0 = Processes). Defaults to Processes.
    /// </summary>
    public static int DefaultPageIndex
    {
        get => GetInt(nameof(DefaultPageIndex), 0);
        set => SetInt(nameof(DefaultPageIndex), value);
    }

    /// <summary>Keeps the main window above other windows when set.</summary>
    public static bool AlwaysOnTop
    {
        get => GetBool(nameof(AlwaysOnTop));
        set => SetBool(nameof(AlwaysOnTop), value);
    }

    private static bool GetBool(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int v && v != 0;
        }
        catch { return false; }
    }

    private static void SetBool(string name, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { /* ignore — setting just won't persist */ }
    }

    private static int GetInt(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }

    private static void SetInt(string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key?.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch { /* ignore — setting just won't persist */ }
    }
}
