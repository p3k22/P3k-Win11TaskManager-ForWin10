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

    /// <summary>
    /// Sentinel for a window coordinate that has never been saved. Left/Top use it so
    /// a first run (no stored position) can fall back to centring on screen.
    /// </summary>
    public const int UnsetCoordinate = int.MinValue;

    /// <summary>Main window width in device-independent pixels. Defaults to the XAML size.</summary>
    public static int WindowWidth
    {
        get => GetInt(nameof(WindowWidth), 1180);
        set => SetInt(nameof(WindowWidth), value);
    }

    /// <summary>Main window height in device-independent pixels. Defaults to the XAML size.</summary>
    public static int WindowHeight
    {
        get => GetInt(nameof(WindowHeight), 760);
        set => SetInt(nameof(WindowHeight), value);
    }

    /// <summary>Main window left edge. <see cref="UnsetCoordinate"/> when never saved.</summary>
    public static int WindowLeft
    {
        get => GetInt(nameof(WindowLeft), UnsetCoordinate);
        set => SetInt(nameof(WindowLeft), value);
    }

    /// <summary>Main window top edge. <see cref="UnsetCoordinate"/> when never saved.</summary>
    public static int WindowTop
    {
        get => GetInt(nameof(WindowTop), UnsetCoordinate);
        set => SetInt(nameof(WindowTop), value);
    }

    /// <summary>Whether the main window was maximized when it last closed.</summary>
    public static bool WindowMaximized
    {
        get => GetBool(nameof(WindowMaximized));
        set => SetBool(nameof(WindowMaximized), value);
    }

    /// <summary>Comma-separated ids of the visible Details columns; null until the user customises them.</summary>
    public static string? DetailColumns
    {
        get
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue(nameof(DetailColumns)) as string; }
            catch { return null; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                if (value == null) key?.DeleteValue(nameof(DetailColumns), false);
                else key?.SetValue(nameof(DetailColumns), value, RegistryValueKind.String);
            }
            catch { /* ignore — setting just won't persist */ }
        }
    }

    /// <summary>Selected Performance page view: "Metric|diskIndex|cpuLogical|gpuEngine".</summary>
    public static string? PerformanceView
    {
        get
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue(nameof(PerformanceView)) as string; }
            catch { return null; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                if (value == null) key?.DeleteValue(nameof(PerformanceView), false);
                else key?.SetValue(nameof(PerformanceView), value, RegistryValueKind.String);
            }
            catch { /* ignore — setting just won't persist */ }
        }
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
