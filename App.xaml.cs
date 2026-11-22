using System.Windows;
using NotchWin.Services;

namespace NotchWin;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, "NotchWin.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        Log.Write($"Started (pid {Environment.ProcessId})");
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("UI error: " + ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("Fatal: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write("Task error: " + ex.Exception); ex.SetObserved(); };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Write($"Exit (code {e.ApplicationExitCode})");
        base.OnExit(e);
    }
}
