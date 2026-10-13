using System;
using Microsoft.Win32;

namespace DynamicIsland;

public enum UpdateState { Unknown, UpToDate, Available, RestartNeeded }

/// <summary>
/// What Windows Update is waiting for, shown on the Power tab. Reads Windows Update's last results
/// (it never starts a new online check, so it's quick and uses no data).
/// </summary>
public static class UpdateStatus
{
    public static (UpdateState State, int Count) Read()
    {
        bool restartNeeded = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                          || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");

        int count = -1;
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type is not null)
            {
                dynamic session = Activator.CreateInstance(type)!;
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.Online = false; // use the results of Windows' own last check
                dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
                count = (int)result.Updates.Count;
            }
        }
        catch
        {
            // Windows Update service off or not reachable: we just won't show a count.
        }

        if (restartNeeded) return (UpdateState.RestartNeeded, Math.Max(count, 0));
        if (count > 0) return (UpdateState.Available, count);
        if (count == 0) return (UpdateState.UpToDate, 0);
        return (UpdateState.Unknown, 0);
    }

    private static bool KeyExists(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key is not null;
        }
        catch { return false; }
    }
}
