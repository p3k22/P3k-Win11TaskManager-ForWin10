using System;
using System.Windows;
using System.Windows.Controls;

using Win11TaskMan.Services;

namespace Win11TaskMan.Views;

public partial class SettingsView : UserControl
{
    private readonly SystemMonitor _monitor;

    // Suppresses change handlers while we seed control state — both during
    // InitializeComponent (e.g. ComboBox SelectionChanged) and the explicit
    // seeding below — so seeding never re-persists or clobbers saved values.
    private bool _initializing = true;

    public SettingsView(SystemMonitor monitor)
    {
        InitializeComponent();
        _monitor = monitor;

        // Seed every control from persisted settings.
        AlwaysAdmin.IsChecked = AppSettings.AlwaysRunAsAdmin;
        AlwaysOnTop.IsChecked = AppSettings.AlwaysOnTop;
        ReplaceTaskMgr.IsChecked = TaskManagerReplacement.IsEnabled();
        SelectSpeed(AppSettings.UpdateIntervalMs);
        DefaultPage.SelectedIndex = AppSettings.DefaultPageIndex;

        if (Elevation.IsElevated())
        {
            AdminBtn.IsEnabled = false;
            AdminNote.Text = "Running as administrator — CPU temperature and full service/startup control are available.";
        }

        _initializing = false;
    }

    // Checks the speed radio that matches a persisted interval (0 = paused).
    private void SelectSpeed(int ms)
    {
        var radio = ms switch
        {
            0    => SpeedPaused,
            500  => SpeedHigh,
            4000 => SpeedLow,
            _    => SpeedNormal,
        };
        radio.IsChecked = true;
    }

    private void AlwaysAdmin_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        AppSettings.AlwaysRunAsAdmin = AlwaysAdmin.IsChecked == true;
    }

    private void ReplaceTaskMgr_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool enable = ReplaceTaskMgr.IsChecked == true;

        // Writing the HKLM hook needs elevation; offer to relaunch if we lack it.
        if (!Elevation.IsElevated())
        {
            var ans = MessageBox.Show(
                "Changing the default Task Manager requires administrator rights.\n\n" +
                "Restart this app as administrator now?",
                "Task Manager", MessageBoxButton.YesNo, MessageBoxImage.Information);

            SetReplaceChecked(TaskManagerReplacement.IsEnabled()); // snap back to reality
            if (ans == MessageBoxResult.Yes) RunAsAdmin_Click(sender, e);
            return;
        }

        try
        {
            if (enable) TaskManagerReplacement.Enable();
            else TaskManagerReplacement.Disable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Task Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetReplaceChecked(TaskManagerReplacement.IsEnabled());
        }
    }

    private void SetReplaceChecked(bool value)
    {
        _initializing = true;
        ReplaceTaskMgr.IsChecked = value;
        _initializing = false;
    }

    private void RunAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Elevation.RelaunchElevated())
                Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            // user dismissed the UAC prompt, or elevation failed
            MessageBox.Show(ex.Message, "Task Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Speed_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || _monitor == null) return;
        int ms = sender == SpeedPaused ? 0
               : sender == SpeedHigh   ? 500
               : sender == SpeedLow    ? 4000
               : 1000;
        if (ms == 0) _monitor.Pause();
        else _monitor.SetInterval(ms);
        AppSettings.UpdateIntervalMs = ms;
    }

    private void DefaultPage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        AppSettings.DefaultPageIndex = DefaultPage.SelectedIndex;
    }

    private void AlwaysOnTop_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool on = AlwaysOnTop.IsChecked == true;
        if (Window.GetWindow(this) is { } w) w.Topmost = on;
        AppSettings.AlwaysOnTop = on;
    }
}
