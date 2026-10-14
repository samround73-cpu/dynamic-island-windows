using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>The icons of apps running in the background, shown as named chips on the Controls tab (see TrayIconHost).</summary>
public partial class MainWindow
{
    private TrayIconHost? _trayHost;
    private bool _trayRefreshQueued;

    /// <summary>Starts or stops collecting the background apps' icons, following the setting.</summary>
    private void ApplyTrayIconSetting()
    {
        if (_settings.TrayAppIcons && _trayHost is null)
        {
            var host = new TrayIconHost();
            if (host.Start())
            {
                _trayHost = host;
                TrayIconHost.Current = host;
                host.Changed += QueueTrayRefresh;
            }
        }
        else if (!_settings.TrayAppIcons && _trayHost is not null)
        {
            StopTrayIcons();
        }
        RefreshTrayChips();
    }

    private void StopTrayIcons()
    {
        var host = _trayHost;
        _trayHost = null;
        TrayIconHost.Current = null;
        if (host is null) return;
        host.Changed -= QueueTrayRefresh;
        host.Dispose(); // apps send their icons back to the normal taskbar
    }

    /// <summary>Icons often arrive in a burst (every app re-sends them at once): redraw once, a moment later.</summary>
    private void QueueTrayRefresh()
    {
        if (_trayRefreshQueued) return;
        _trayRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _trayRefreshQueued = false;
            RefreshTrayChips();
        }));
    }

    private void RefreshTrayChips()
    {
        var chips = _trayHost?.Chips();
        bool any = chips is { Count: > 0 };
        TrayAppsList.ItemsSource = any ? chips : null;
        var visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (TrayAppsCard.Visibility != visibility)
        {
            TrayAppsCard.Visibility = visibility;
            if (_expanded && _currentTab == "Controls") ResizeExpandedIfNeeded();
        }
    }

    // ---- Passing the mouse to the app, like the real taskbar does ----

    private void TrayChip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not TrayChip chip) return;
        try
        {
            var corner = element.PointToScreen(new Point(0, 0));
            double scale = DpiScale;
            TrayIconHost.MouseEnter(chip.Source, (int)corner.X, (int)corner.Y,
                (int)(element.ActualWidth * scale), (int)(element.ActualHeight * scale));
        }
        catch { }
    }

    private void TrayChip_MouseLeave(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrayChip chip) TrayIconHost.MouseLeave(chip.Source);
    }

    private void TrayChip_MouseMove(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrayChip chip) TrayIconHost.MouseMove(chip.Source);
    }

    private void TrayChip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrayChip chip) return;
        e.Handled = true;
        TrayIconHost.MouseDown(chip.Source, e.ChangedButton);
    }

    private void TrayChip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrayChip chip) return;
        e.Handled = true;
        // Get the island out of the way first, so the app's window or menu isn't hidden behind it.
        Collapse();
        TrayIconHost.MouseUp(chip.Source, e.ChangedButton);
    }
}
