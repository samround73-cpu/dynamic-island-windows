using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;

namespace DynamicIsland;

public partial class App : Application
{
    /// <summary>Shared so every exit path (normal, crash, sign-out) can put the real taskbar back.</summary>
    public static TaskbarManager Taskbar { get; } = new();

    /// <summary>Where error details are written, for sending to Claude.</summary>
    public static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "errors.log");

    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private readonly Queue<DateTime> _recentErrors = new();
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only ever run one island.
        _singleInstance = new Mutex(true, "DynamicIsland_SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        // Safety net. A one-off glitch (say, in a background refresh) is written to the log and the island
        // carries on. Only if errors keep coming does it close — always putting the normal taskbar back first.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            LogError(args.Exception);
            if (_shuttingDown) return;

            var now = DateTime.Now;
            _recentErrors.Enqueue(now);
            while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromSeconds(20)) _recentErrors.Dequeue();
            if (_recentErrors.Count < 4) return; // keep going

            _shuttingDown = true;
            Taskbar.Restore();
            MessageBox.Show(
                "Dynamic Island keeps running into a problem, so it will close. Your normal taskbar has been restored.\n\n"
                + args.Exception.Message
                + "\n\nThe details are saved in:\n" + CrashLogPath + "\n(send that file to Claude to get it fixed)",
                "Dynamic Island", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogError(ex);
            Taskbar.Restore();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Taskbar.Restore();
        SessionEnding += (_, _) => Taskbar.Restore();

        base.OnStartup(e);
        LineIcons.Load(); // before the window, which uses the icon font

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>Writes the full error (including where in the code it happened) to errors.log.</summary>
    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            var info = new FileInfo(CrashLogPath);
            if (info.Exists && info.Length > 512 * 1024) info.Delete(); // keep it small
            File.AppendAllText(CrashLogPath,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  (version {typeof(App).Assembly.GetName().Version})\n{ex}\n\n");
        }
        catch { /* never let logging cause another problem */ }
    }

    /// <summary>A short note in errors.log for things that went wrong without crashing (helps with bug reports).</summary>
    public static void LogNote(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}  note: {message}\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Taskbar.Restore();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
