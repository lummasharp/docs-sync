using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace DocsSync;

public partial class App : Application
{
    Mutex? _single;
    EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--check")
        {
            Shutdown(SelfCheck.Run(e.Args[1]));
            return;
        }

        // One copy only: a second launch asks the running one to show its window.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DocsSync.Show");
        _single = new Mutex(true, @"Local\DocsSync.Single", out var first);
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing the window keeps it in the tray
        var window = new MainWindow();
        if (!e.Args.Contains("--tray")) window.Show();

        var signal = _showSignal;
        new Thread(() => { while (signal.WaitOne()) Dispatcher.BeginInvoke(window.ShowFromTray); }) { IsBackground = true }.Start();
    }
}
