using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace DynamicIsland;

public partial class App : Application
{
    /// <summary>Shared so every exit path (normal, crash, sign-out) can put the real taskbar back.</summary>
    public static TaskbarManager Taskbar { get; } = new();

    /// <summary>Where error details are written, to include when reporting a problem.</summary>
    public static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "errors.log");

    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private readonly Queue<DateTime> _recentErrors = new();
    private bool _shuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Only ever run one island. When the island restarts itself, the new copy waits for the old one to finish closing.
        _singleInstance = new Mutex(true, "DynamicIsland_SingleInstance", out _ownsMutex);
        if (!_ownsMutex && Array.Exists(e.Args, a => string.Equals(a, RestartArgument, StringComparison.OrdinalIgnoreCase)))
        {
            try { _ownsMutex = _singleInstance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { _ownsMutex = true; } // the old copy closed without tidying up
        }
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
                + "\n\nThe details are saved in:\n" + CrashLogPath + "\n(include that file if you report the problem)",
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

        // Started by Windows at sign-in / reconnect: let the desktop and taskbar finish loading before touching them.
        bool autostart = Array.Exists(e.Args, a => string.Equals(a, StartupManager.AutostartArgument, StringComparison.OrdinalIgnoreCase));
        if (autostart)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // no window yet; don't let WPF think we're finished
            await WaitForDesktopAsync();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        LineIcons.Load(); // before the window, which uses the icon font

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Escape hatch: if the island hasn't properly appeared a little later, put the taskbar back and quit,
        // so you're never left with no taskbar and no island.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(15));
            // (Not "is it visible": the island legitimately hides itself for full-screen apps.)
            bool showing = window.IsLoaded && window.ActualWidth > 0
                        && new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero;
            if (!showing && !_shuttingDown)
            {
                LogNote("the island didn't appear within 15 seconds of starting, so the taskbar was restored and it closed");
                _shuttingDown = true;
                Taskbar.Restore();
                Shutdown();
            }
        });

        // Move an older "Start with Windows" setting over to the scheduled task (off the UI thread).
        _ = System.Threading.Tasks.Task.Run(StartupManager.UpgradeIfNeeded);

        // Make sure it's in the Start menu, pointing at where it's running from now.
        _ = System.Threading.Tasks.Task.Run(StartMenuShortcut.Ensure);
    }

    /// <summary>
    /// Waits (up to a minute) until Explorer's desktop and taskbar exist and the PC has had a few seconds to settle.
    /// </summary>
    private static async System.Threading.Tasks.Task WaitForDesktopAsync()
    {
        var started = DateTime.Now;
        while (DateTime.Now - started < TimeSpan.FromSeconds(60))
        {
            bool ready = Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero
                      && Native.FindWindow("Progman", null) != IntPtr.Zero;
            if (ready) break;
            await System.Threading.Tasks.Task.Delay(500);
        }
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(4));
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

    public const string RestartArgument = "--restarted";

    /// <summary>
    /// Closes this island (putting the taskbar back as usual) and starts a fresh one, which takes over a second later.
    /// Used after you reconnect to the PC or the screen scaling changes, when Windows can leave see-through windows
    /// drawn wrongly (e.g. as a black box) until the app is restarted.
    /// </summary>
    public static void RestartIsland(string reason)
    {
        if (Current is not App app || app._shuttingDown) return;
        string? exe = Environment.ProcessPath;
        if (exe is null) return;
        // Safety limit: never more than 3 automatic restarts in half an hour, whatever triggers them.
        if (!RecordRestart())
        {
            LogNote($"not restarting ({reason}): already restarted 3 times in the last 30 minutes");
            return;
        }
        LogNote($"restarting the island: {reason}");
        StartReplacement(exe, RestartArgument);
    }

    private static string RestartLogPath => Path.Combine(Path.GetDirectoryName(CrashLogPath)!, "restarts.txt");

    /// <summary>Notes an automatic restart; returns false if there have already been 3 in the last 30 minutes.</summary>
    private static bool RecordRestart()
    {
        try
        {
            var now = DateTime.Now;
            var recent = new List<DateTime>();
            if (File.Exists(RestartLogPath))
                foreach (var line in File.ReadAllLines(RestartLogPath))
                    if (DateTime.TryParse(line, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var when) && now - when < TimeSpan.FromMinutes(30))
                        recent.Add(when);
            if (recent.Count >= 3) return false;
            recent.Add(now);
            Directory.CreateDirectory(Path.GetDirectoryName(RestartLogPath)!);
            File.WriteAllLines(RestartLogPath, recent.Select(d => d.ToString("o")));
            return true;
        }
        catch { return true; }
    }

    /// <summary>
    /// Starts a new copy of the island (which waits for this one to close) and then closes this one, putting the
    /// taskbar back as usual; the new copy hides it again a moment later. Returns false if the new copy couldn't start,
    /// in which case this one carries on.
    /// </summary>
    public static bool StartReplacement(string exe, string arguments)
    {
        if (Current is not App app || app._shuttingDown) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arguments) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            LogNote($"couldn't start a new copy: {ex.Message}");
            return false;
        }
        app._shuttingDown = true;
        app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
        return true;
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
