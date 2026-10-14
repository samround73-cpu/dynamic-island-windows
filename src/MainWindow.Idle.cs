using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// Settings › Behaviour › "When you're not using it: Fade": after a while without the mouse anywhere near it, the island
/// fades to a faint ghost. It comes straight back when the mouse heads up there, or when something pops out.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _idleTimerWired;
    private bool _idleFaded;
    private bool _loggedIdleFade;
    private DateTime _lastIslandUse = DateTime.Now;
    private const double IdleOpacity = 0.22;

    private void ApplyIdleSetting()
    {
        if (!_idleTimerWired)
        {
            _idleTimer.Tick += (_, _) => OnIdleTick();
            _idleTimerWired = true;
        }
        _lastIslandUse = DateTime.Now;
        if (_settings.IdleMode == "Fade")
        {
            _idleTimer.Start();
            return;
        }
        _idleTimer.Stop();
        Unfade();
    }

    private void OnIdleTick()
    {
        bool busy = _expanded || _peeking || _menuOpen || _activityShowing || _dropZoneShowing || _movingScreens
                    || _presence != Presence.Shown || _preview.IsVisible || IslandHost.IsMouseOver;
        if (busy || MouseHeadingForIsland())
        {
            _lastIslandUse = DateTime.Now;
            Unfade();
            return;
        }
        if (!_idleFaded && DateTime.Now - _lastIslandUse > TimeSpan.FromSeconds(Math.Max(5, _settings.IdleSeconds)))
        {
            _idleFaded = true;
            if (!_loggedIdleFade) App.LogNote($"idle: faded after {_settings.IdleSeconds} s without the mouse nearby");
            _loggedIdleFade = true;
            IslandHost.BeginAnimation(OpacityProperty, new DoubleAnimation(IdleOpacity, TimeSpan.FromMilliseconds(900))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            });
            SyncBackdrops();
        }
    }

    /// <summary>The mouse is in the band along the top of the island's screen, around the island.</summary>
    private bool MouseHeadingForIsland()
    {
        if (!Native.GetCursorPos(out var cursor)) return false;
        var area = IslandArea();
        double scale = DpiScale;
        int sideways = (int)(160 * scale), down = (int)(130 * scale);
        return cursor.X >= area.Left - sideways && cursor.X <= area.Right + sideways
               && cursor.Y >= area.Top - 2 && cursor.Y <= area.Bottom + down;
    }

    /// <summary>Back to full strength (quickly), e.g. for a pop-out.</summary>
    private void Unfade()
    {
        if (!_idleFaded) return;
        _idleFaded = false;
        _lastIslandUse = DateTime.Now;
        IslandHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        SyncBackdrops();
    }
}
