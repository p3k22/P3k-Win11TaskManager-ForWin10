using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

using Win11TaskMan.Services;
using Win11TaskMan.Views;

namespace Win11TaskMan;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch-all so a stray exception can't take the whole app down. This
        // matters more than usual here: when the app is registered as the system
        // Task Manager, a crash would leave the user with no task manager at all.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // "Always run as administrator": if the user enabled it and we are not
        // already elevated, relaunch elevated and bow out. If the UAC prompt is
        // dismissed (or elevation fails), fall through and start unelevated.
        if (AppSettings.AlwaysRunAsAdmin && !Elevation.IsElevated())
        {
            try
            {
                if (Elevation.RelaunchElevated())
                {
                    Shutdown();
                    return;
                }
            }
            catch { /* UAC dismissed or elevation failed — continue unelevated */ }
        }

        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            // The shell itself failed to come up — nothing left to keep running for.
            Log(ex, "startup");
            MessageBox.Show("Task Manager couldn't start.\n\n" + ex.Message,
                "Task Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    // UI-thread exception: log it, tell the user, and keep the app alive.
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception, "dispatcher");
        e.Handled = true;
        MessageBox.Show("Something went wrong, but Task Manager will keep running.\n\n" + e.Exception.Message,
            "Task Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // Background-thread exception. The runtime tears the process down after this;
    // we can't stop it, but we can leave a record of why.
    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Log(ex, "domain");
    }

    // A faulted Task whose exception was never observed — mark it observed so it
    // doesn't escalate, and log it.
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log(e.Exception, "task");
        e.SetObserved();
    }

    // Best-effort crash log under %LOCALAPPDATA%\Win11TaskMan\crash.log. Never throws.
    private static void Log(Exception ex, string source)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Win11TaskMan");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:u}] ({source}) {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never itself crash the handler */ }
    }
}
