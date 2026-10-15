using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>
/// "Start with Windows". Uses a Windows scheduled task, which opens the island when you sign in and also when you
/// reconnect to the PC (e.g. reopening a Shadow or Remote Desktop session) or unlock it, if it isn't already running.
/// If Windows won't let us create the task, it falls back to the classic "Run" entry (sign-in only).
/// No admin rights are needed for either.
/// </summary>
public static class StartupManager
{
    private const string TaskName = "DynamicIsland";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "DynamicIsland";

    /// <summary>Passed to the island when Windows starts it, so it waits for the desktop to finish loading first.</summary>
    public const string AutostartArgument = "--autostart";

    public enum Method { Off, ScheduledTask, RunEntry }

    public static Method Current
    {
        get
        {
            if (TaskExists()) return Method.ScheduledTask;
            return RunEntryExists() ? Method.RunEntry : Method.Off;
        }
    }

    public static bool IsEnabled => Current != Method.Off;

    /// <summary>True when the classic Run entry exists but has been switched off in Windows' Startup apps list.</summary>
    public static bool BlockedByWindows
    {
        get
        {
            if (!RunEntryExists()) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                // The first byte is even when the entry is allowed (02) and odd when it's been turned off (03).
                return key?.GetValue(ValueName) is byte[] data && data.Length > 0 && (data[0] & 1) == 1;
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            DeleteTask();
            DeleteRunEntry();
            return;
        }

        if (CreateTask())
        {
            DeleteRunEntry(); // the task does everything the Run entry did, and more
        }
        else
        {
            App.LogNote("couldn't create the startup task; using the classic Run entry instead (sign-in only)");
            CreateRunEntry();
        }
    }

    /// <summary>Moves people who turned this on in an older version over to the scheduled task.</summary>
    public static void UpgradeIfNeeded()
    {
        try
        {
            var current = Current;
            if (current == Method.RunEntry && !BlockedByWindows && CreateTask()) DeleteRunEntry();
            // The app has moved, or the task is from an older version (which waited 5 seconds after sign-in): update it.
            else if (current == Method.ScheduledTask && (!TaskPointsHere() || TaskHasOldDelay())) CreateTask();
        }
        catch { }
    }

    // ---- The scheduled task ----

    private static string? ExePath => Environment.ProcessPath;

    private static bool TaskExists() => RunSchtasks($"/query /tn \"{TaskName}\"", out _) == 0;

    private static bool TaskPointsHere()
    {
        if (RunSchtasks($"/query /tn \"{TaskName}\" /xml", out string xml) != 0) return false;
        return ExePath is string exe && xml.Contains(SecurityElement.Escape(exe), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TaskHasOldDelay() =>
        RunSchtasks($"/query /tn \"{TaskName}\" /xml", out string xml) == 0 && xml.Contains("<Delay>PT5S</Delay>", StringComparison.OrdinalIgnoreCase);

    private static bool CreateTask()
    {
        string? exe = ExePath;
        if (exe is null) return false;
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string folder = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "");
        string command = SecurityElement.Escape(exe);
        string Trigger(string kind) =>
            $"<SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>{kind}</StateChange><UserId>{user}</UserId></SessionStateChangeTrigger>";

        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <RegistrationInfo><Description>Starts Dynamic Island when you sign in, reconnect or unlock (if it isn't already running).</Description></RegistrationInfo>\n" +
            "  <Triggers>\n" +
            $"    <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>\n" +
            $"    {Trigger("RemoteConnect")}\n    {Trigger("ConsoleConnect")}\n    {Trigger("SessionUnlock")}\n" +
            "  </Triggers>\n" +
            $"  <Principals><Principal id=\"Author\"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n" +
            "  <Settings>\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
            "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\n" +
            "    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
            "    <Enabled>true</Enabled>\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
            "    <Priority>6</Priority>\n" +
            "  </Settings>\n" +
            $"  <Actions Context=\"Author\"><Exec><Command>{command}</Command><Arguments>{AutostartArgument}</Arguments><WorkingDirectory>{folder}</WorkingDirectory></Exec></Actions>\n" +
            "</Task>\n";

        string file = Path.Combine(Path.GetTempPath(), "DynamicIsland-startup-task.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            int code = RunSchtasks($"/create /tn \"{TaskName}\" /xml \"{file}\" /f", out string output);
            if (code != 0) App.LogNote($"schtasks /create failed ({code}): {output.Trim()}");
            return code == 0;
        }
        catch (Exception ex)
        {
            App.LogNote($"creating the startup task failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static void DeleteTask()
    {
        if (TaskExists()) RunSchtasks($"/delete /tn \"{TaskName}\" /f", out _);
    }

    private static int RunSchtasks(string arguments, out string output)
    {
        output = "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return -1;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000)) { try { process.Kill(); } catch { } return -1; }
            output = stdout.Result + stderr.Result;
            return process.ExitCode;
        }
        catch { return -1; }
    }

    // ---- The classic Run entry (fallback) ----

    private static bool RunEntryExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    private static void CreateRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (ExePath is string exe) key.SetValue(ValueName, $"\"{exe}\" {AutostartArgument}");
        }
        catch { }
    }

    private static void DeleteRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
