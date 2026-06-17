using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

using Win11TaskMan.Services;
using Win11TaskMan.Views;

namespace Win11TaskMan;

public partial class MainWindow : Window
{
    private readonly SystemMonitor _monitor;

    // views are created on first navigation and cached
    private ProcessesView? _processes;
    private PerformanceView? _performance;
    private StartupView? _startup;
    private UsersView? _users;
    private DetailsView? _details;
    private ServicesView? _services;
    private SettingsView? _settings;

    public MainWindow()
    {
        InitializeComponent();
        LoadAppIcon();
        RestoreWindowPlacement();
        _monitor = new SystemMonitor();           // captures this UI thread

        // Apply the saved polling speed (0 = start paused).
        int interval = AppSettings.UpdateIntervalMs;
        if (interval == 0) _monitor.Pause();
        else _monitor.SetInterval(interval);

        // Defer first content until the window has a real size — a grouped
        // DataGrid created during construction latches onto the zero-size
        // measure pass and renders its columns collapsed.
        Loaded += (_, _) =>
        {
            if (Host.Content != null) return;
            Topmost = AppSettings.AlwaysOnTop;

            // Selecting the nav button fires Nav_Checked, which builds the view.
            // NavProcesses is already checked in XAML, so re-selecting it won't
            // raise Checked — fall through and build it directly in that case.
            NavForPage(AppSettings.DefaultPageIndex).IsChecked = true;
            if (Host.Content == null) Host.Content = _processes ??= new ProcessesView(_monitor);
        };
        Closing += (_, _) => SaveWindowPlacement();
        Closed += (_, _) => _monitor.Dispose();
    }

    // Restore the size, position, and maximized state the window had when it last
    // closed. Runs before the window is shown so there's no resize flicker. A first
    // run (no saved position) falls back to centring on the primary monitor.
    private void RestoreWindowPlacement()
    {
        Width = AppSettings.WindowWidth;
        Height = AppSettings.WindowHeight;

        int left = AppSettings.WindowLeft;
        int top = AppSettings.WindowTop;
        if (left == AppSettings.UnsetCoordinate || top == AppSettings.UnsetCoordinate)
        {
            // No stored position yet — centre on the primary monitor's work area.
            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }
        else
        {
            // Clamp into the current virtual desktop so a window saved on a monitor
            // that's since been unplugged can't open completely off-screen.
            double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width;
            double maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height;
            Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, maxLeft));
            Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, maxTop));
        }

        if (AppSettings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    // Persist the window's placement on close. When maximized (or minimized) we save
    // RestoreBounds — the size/position the window would return to — so that "restore"
    // brings back a sensible normal-state window rather than the maximized bounds.
    private void SaveWindowPlacement()
    {
        bool maximized = WindowState == WindowState.Maximized;
        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.IsEmpty) return;   // never shown — nothing meaningful to save

        AppSettings.WindowMaximized = maximized;
        AppSettings.WindowWidth = (int)Math.Round(bounds.Width);
        AppSettings.WindowHeight = (int)Math.Round(bounds.Height);
        AppSettings.WindowLeft = (int)Math.Round(bounds.Left);
        AppSettings.WindowTop = (int)Math.Round(bounds.Top);
    }

    private RadioButton NavForPage(int index) => index switch
    {
        1 => NavPerformance,
        2 => NavStartup,
        3 => NavUsers,
        4 => NavDetails,
        5 => NavServices,
        _ => NavProcesses,
    };

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Host == null) return;
        Host.Content =
            sender == NavPerformance ? _performance ??= new PerformanceView(_monitor) :
            sender == NavStartup     ? _startup     ??= new StartupView() :
            sender == NavUsers       ? _users       ??= new UsersView(_monitor) :
            sender == NavDetails     ? _details     ??= new DetailsView(_monitor) :
            sender == NavServices    ? _services    ??= new ServicesView() :
            sender == NavSettings    ? _settings    ??= new SettingsView(_monitor) :
            (object)(_processes ??= new ProcessesView(_monitor));
    }

    // Window + taskbar icon: the app's own embedded AppIcon.ico (the same icon the
    // .exe carries). Loaded at runtime, best-effort — if no icon is embedded the
    // window just keeps the default, so a missing AppIcon.ico never stops startup.
    private void LoadAppIcon()
    {
        try
        {
            Icon = BitmapFrame.Create(
                new Uri("pack://application:,,,/AppIcon.ico", UriKind.Absolute));
        }
        catch { /* no AppIcon.ico embedded yet — keep the default window icon */ }
    }
}
