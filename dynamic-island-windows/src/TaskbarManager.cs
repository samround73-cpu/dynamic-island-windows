using System;
using System.Collections.Generic;

namespace DynamicIsland;

/// <summary>
/// Hides the normal Windows taskbar while the island runs, and always puts it back afterwards.
///
/// Three things keep it hidden:
///  1. The taskbar is switched to "auto-hide", so maximised windows can use the space it took up.
///  2. The taskbar windows themselves are hidden.
///  3. We listen for Windows trying to show the taskbar again (it does this after Start opens,
///     after some notifications, etc.) and hide it again instantly, rather than waiting for a timer.
/// Restore() undoes everything, returning auto-hide to whatever setting you had before.
/// </summary>
public sealed class TaskbarManager
{
    private int? _originalState;
    private bool _hidden;
    private IntPtr _showHook;
    private Native.WinEventDelegate? _showCallback; // kept in a field so it isn't garbage-collected

    public bool IsHidden => _hidden;

    private static IntPtr PrimaryTaskbar() => Native.FindWindow("Shell_TrayWnd", null);

    /// <summary>The main taskbar plus the extra ones Windows draws on other monitors.</summary>
    private static List<IntPtr> AllTaskbars()
    {
        var bars = new List<IntPtr>();
        var primary = PrimaryTaskbar();
        if (primary != IntPtr.Zero) bars.Add(primary);

        IntPtr secondary = IntPtr.Zero;
        while ((secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            bars.Add(secondary);
        return bars;
    }

    public void Hide()
    {
        var primary = PrimaryTaskbar();
        if (primary == IntPtr.Zero) return;

        if (_originalState is null)
        {
            var data = Native.NewAppBarData(primary);
            _originalState = (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref data).ToInt64();
        }

        SetState(primary, _originalState.Value | Native.ABS_AUTOHIDE);
        _hidden = true;
        foreach (var bar in AllTaskbars()) Native.ShowWindow(bar, Native.SW_HIDE);
    }

    public void Restore()
    {
        if (!_hidden) return;
        _hidden = false;

        foreach (var bar in AllTaskbars()) Native.ShowWindow(bar, Native.SW_SHOW);
        var primary = PrimaryTaskbar();
        if (primary != IntPtr.Zero && _originalState is int state) SetState(primary, state);
    }

    /// <summary>Backup check, run on a timer, in case a show slipped past the listener.</summary>
    public void EnsureHidden()
    {
        if (!_hidden) return;
        foreach (var bar in AllTaskbars())
            if (Native.IsWindowVisible(bar)) Native.ShowWindow(bar, Native.SW_HIDE);
    }

    /// <summary>
    /// Starts listening for the taskbar being shown. Must be called from the UI thread.
    /// Call again if Explorer restarts (it becomes a new process).
    /// </summary>
    public void StartWatching()
    {
        StopWatching();
        var primary = PrimaryTaskbar();
        if (primary == IntPtr.Zero) return;

        Native.GetWindowThreadProcessId(primary, out uint explorerProcessId);
        _showCallback = OnWindowShown;
        _showHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero,
            _showCallback, explorerProcessId, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    public void StopWatching()
    {
        if (_showHook != IntPtr.Zero) Native.UnhookWinEvent(_showHook);
        _showHook = IntPtr.Zero;
    }

    private void OnWindowShown(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_hidden || hWnd == IntPtr.Zero || idObject != Native.OBJID_WINDOW) return;
        string cls = WindowTracker.GetClassName(hWnd);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") Native.ShowWindow(hWnd, Native.SW_HIDE);
    }

    private static void SetState(IntPtr taskbar, int state)
    {
        var data = Native.NewAppBarData(taskbar);
        data.lParam = new IntPtr(state);
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref data);
    }
}
