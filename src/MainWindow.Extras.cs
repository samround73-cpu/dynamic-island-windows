using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>The Clipboard and Shelf tabs, weather, device pop-ups, Settings and accent colours.</summary>
public partial class MainWindow
{
    // =====================================================================================
    //  Clipboard tab
    // =====================================================================================

    private string _clipFilter = "All";
    private readonly List<ClipItem> _clipRows = new();
    private ClipItem? _clipSelected;
    private int _clipboardRetries;

    private void CaptureClipboard()
    {
        if (_clipboard.Capture())
        {
            CheckForSnip();
            return;
        }

        // Another app still had the clipboard open; try again in a moment (a few times at most).
        if (++_clipboardRetries > 4) return;
        var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            CaptureClipboard();
        };
        retry.Start();
    }

    private bool ClipMatchesFilter(ClipItem item) => _clipFilter switch
    {
        "Text" => item.Kind is ClipKind.Text or ClipKind.Code,
        "Images" => item.Kind == ClipKind.Image,
        "Links" => item.Kind == ClipKind.Link,
        _ => true,
    };

    /// <summary>Rebuilds the list under its headings (Pinned, Today, Yesterday, Earlier), keeping your selection.</summary>
    private void RefreshClipboardList()
    {
        if (ClipList is null) return; // still starting up
        string query = ClipSearchBox.Text.Trim();
        ClipSearchPlaceholder.Visibility = ClipSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipSearchPlaceholder.Text = $"Search {_clipboard.Items.Count} {(_clipboard.Items.Count == 1 ? "item" : "items")}…";

        var matches = _clipboard.Items
            .Where(ClipMatchesFilter)
            .Where(i => query.Length == 0 || (i.Text ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        var items = new List<object>();
        _clipRows.Clear();
        foreach (var group in new[] { "PINNED", "TODAY", "YESTERDAY", "EARLIER" })
        {
            var inGroup = matches.Where(i => i.Group == group).ToList();
            if (inGroup.Count == 0) continue;
            items.Add(new AppSectionHeader { Title = group });
            items.AddRange(inGroup);
            _clipRows.AddRange(inGroup);
        }

        ClipList.ItemsSource = items;
        ClipEmpty.Visibility = _clipRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipEmptyText.Text = _clipboard.Items.Count == 0 ? "Nothing copied yet" : "Nothing matches";

        SelectClip(_clipSelected is not null && _clipRows.Contains(_clipSelected) ? _clipSelected : _clipRows.FirstOrDefault());
    }

    private void SelectClip(ClipItem? item)
    {
        if (_clipSelected is not null) _clipSelected.IsSelected = false;
        _clipSelected = item;
        if (item is not null)
        {
            item.IsSelected = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                (ClipList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement)?.BringIntoView()));
        }
        UpdateClipDetail();
    }

    private void UpdateClipDetail()
    {
        var item = _clipSelected;
        ClipDetailMeta.Visibility = item is null ? Visibility.Hidden : Visibility.Visible;
        if (item is null)
        {
            ClipDetailImage.Source = null;
            ClipDetailImage.Visibility = Visibility.Collapsed;
            ClipDetailText.Visibility = Visibility.Visible;
            ClipDetailText.Text = _clipboard.Items.Count == 0 ? "Copy something and it'll show up here." : "";
            ClipDetailText.FontWeight = FontWeights.Normal;
            ClipDetailText.TextAlignment = TextAlignment.Center;
            return;
        }

        bool isImage = item.Kind == ClipKind.Image;
        ClipDetailImage.Source = item.Image;
        ClipDetailImage.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        ClipDetailText.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        ClipDetailText.Text = item.DetailText;
        ClipDetailText.FontWeight = item.Kind == ClipKind.Code ? FontWeights.Normal : FontWeights.SemiBold;
        ClipDetailText.FontFamily = item.Kind == ClipKind.Code ? new FontFamily("Cascadia Mono, Consolas") : FontFamily;
        ClipDetailText.TextAlignment = item.DetailText.Length < 220 && item.Kind != ClipKind.Code ? TextAlignment.Center : TextAlignment.Left;

        ClipDetailType.Text = item.TypeLabel;
        ClipDetailSize.Text = item.SizeText;
        ClipDetailCopied.Text = item.IsPinned ? "Pinned" : item.CopiedText;
        ClipPinText.Text = item.IsPinned ? "Unpin" : "Pin";
    }

    private void ClipRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipItem item) return;
        SelectClip(item);
        if (e.ClickCount == 2) PasteClip(item); // double-click pastes
        e.Handled = true;
    }

    private void ClipSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshClipboardList();

    private void ClipFilter_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string filter) _clipFilter = filter;
        RefreshClipboardList();
    }

    /// <summary>↑↓ move, Enter paste, Ctrl+C copy, Ctrl+P pin, Del remove, Tab next filter.</summary>
    private void ClipSearch_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        int index = _clipSelected is null ? -1 : _clipRows.IndexOf(_clipSelected);

        switch (e.Key)
        {
            case Key.Down when _clipRows.Count > 0:
                SelectClip(_clipRows[Math.Min(_clipRows.Count - 1, index + 1)]);
                e.Handled = true;
                break;
            case Key.Up when _clipRows.Count > 0:
                SelectClip(_clipRows[Math.Max(0, index - 1)]);
                e.Handled = true;
                break;
            case Key.Enter when _clipSelected is not null:
                PasteClip(_clipSelected);
                e.Handled = true;
                break;
            case Key.Tab:
                CycleClipFilter();
                e.Handled = true;
                break;
            case Key.Delete when _clipSelected is not null && ClipSearchBox.Text.Length == 0:
                RemoveSelectedClip();
                e.Handled = true;
                break;
            case Key.C when ctrl && _clipSelected is not null && ClipSearchBox.SelectionLength == 0:
                CopyClip(_clipSelected);
                e.Handled = true;
                break;
            case Key.P when ctrl && _clipSelected is not null:
                _clipboard.TogglePin(_clipSelected);
                e.Handled = true;
                break;
        }
    }

    private void CycleClipFilter()
    {
        var chips = new[] { "All", "Text", "Images", "Links" };
        string next = chips[(Array.IndexOf(chips, _clipFilter) + 1) % chips.Length];
        foreach (var chip in FindChips(ClipboardTab))
            if (chip.Tag as string == next) chip.IsChecked = true;
    }

    private static IEnumerable<RadioButton> FindChips(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton radio && radio.GroupName == "ClipFilter") yield return radio;
            foreach (var nested in FindChips(child)) yield return nested;
        }
    }

    /// <summary>Copies the item, goes back to the app you were in, and pastes it there.</summary>
    private void PasteClip(ClipItem item)
    {
        if (!_clipboard.CopyBack(item)) return;
        IntPtr previous = _lastForeground;
        ClipSearchBox.Clear();
        Collapse();
        if (previous == IntPtr.Zero || !Native.IsWindow(previous)) return;
        WindowTracker.Activate(previous);
        var paste = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        paste.Tick += (_, _) =>
        {
            paste.Stop();
            Native.PressCtrlKey('V');
        };
        paste.Start();
    }

    /// <summary>Copies the item and goes back to your app, ready for you to paste where you like.</summary>
    private void CopyClip(ClipItem item)
    {
        if (!_clipboard.CopyBack(item)) return;
        IntPtr previous = _lastForeground;
        Collapse();
        if (previous != IntPtr.Zero && Native.IsWindow(previous)) WindowTracker.Activate(previous);
        ShowActivity("", item.Image, "Copied", "Press Ctrl+V to paste", null);
    }

    private void RemoveSelectedClip()
    {
        if (_clipSelected is null) return;
        int index = _clipRows.IndexOf(_clipSelected);
        var removed = _clipSelected;
        _clipSelected = null;
        _clipboard.Remove(removed); // refreshes the list
        if (_clipRows.Count > 0) SelectClip(_clipRows[Math.Clamp(index, 0, _clipRows.Count - 1)]);
    }

    private void ClipPaste_Click(object sender, RoutedEventArgs e) { if (_clipSelected is not null) PasteClip(_clipSelected); }
    private void ClipCopy_Click(object sender, RoutedEventArgs e) { if (_clipSelected is not null) CopyClip(_clipSelected); }
    private void ClipPin_Click(object sender, RoutedEventArgs e) { if (_clipSelected is not null) _clipboard.TogglePin(_clipSelected); }
    private void ClipRemove_Click(object sender, RoutedEventArgs e) => RemoveSelectedClip();

    private void WindowsClipboard_Click(object sender, RoutedEventArgs e)
    {
        IntPtr previous = _lastForeground;
        Collapse();
        if (previous != IntPtr.Zero && Native.IsWindow(previous)) WindowTracker.Activate(previous);
        Native.PressWinKey('V');
    }

    // =====================================================================================
    //  Shelf: drop files on the island, drag them back out later
    // =====================================================================================

    private Point _dragStart;
    private bool _draggingOut;
    private bool _dropZoneShowing;
    private readonly DispatcherTimer _dropLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private void RefreshShelf()
    {
        _shelf.Prune();
        ShelfList.ItemsSource = _shelf.Items.ToList();
        bool empty = _shelf.Items.Count == 0;
        ShelfEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ShelfDragAllButton.Visibility = _shelf.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShelfTitle.Text = empty ? "SHELF" : $"SHELF  ·  {_shelf.Items.Count} {(_shelf.Items.Count == 1 ? "ITEM" : "ITEMS")}";
        UpdateShelfSelection();
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        string? link = files ? null : DraggedLink(e.Data);
        if (_draggingOut || (!files && link is null))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.Link; // files are never moved; they're only remembered
        _dropLeaveTimer.Stop();
        if (_expanded) return;

        if (!_dropZoneShowing) ShowDropZones(canShelve: files);
        HighlightDropZone(DropTargetIsPin(e));
    }

    private void Island_DragLeave(object sender, DragEventArgs e) => RestartTimer(_dropLeaveTimer);

    private void OnDropLeave()
    {
        _dropLeaveTimer.Stop();
        HideDropZones();
    }

    /// <summary>Two choices while dragging onto the pill: pin it to the tray, or put it on the Shelf (files only).</summary>
    private void ShowDropZones(bool canShelve)
    {
        _dropZoneShowing = true;
        ExitPeek();
        _activityTimer.Stop();
        DropShelfZone.Visibility = canShelve ? Visibility.Visible : Visibility.Collapsed;
        FadeOut(CompactContent, 90);
        FadeOut(ActivityContent, 60);
        _activityShowing = true;
        FadeIn(DropZones, 160, delay: 80);
        AnimateSize(canShelve ? 400 : 250, 92, 420, bouncy: true);
    }

    private void HideDropZones()
    {
        if (!_dropZoneShowing) return;
        _dropZoneShowing = false;
        FadeOut(DropZones, 100);
        _activityShowing = false;
        if (_expanded || _peeking) return;
        FadeIn(CompactContent, 180, delay: 140);
        AnimateSize(CompactWidth, CompactHeight, 420, bouncy: false);
    }

    /// <summary>Over the left ("Pin to tray") zone? (Links can only be pinned.)</summary>
    private bool DropTargetIsPin(DragEventArgs e)
    {
        if (DropShelfZone.Visibility != Visibility.Visible) return true;
        return e.GetPosition(DropZones).X < DropPinZone.ActualWidth + 5;
    }

    private void HighlightDropZone(bool pin)
    {
        var accent = (Brush)FindResource("Accent");
        var faint = (Brush)FindResource("AccentFaint");
        var plain = (Brush)FindResource("Ink38");
        DropPinZone.BorderBrush = pin ? accent : plain;
        DropPinZone.Background = pin ? faint : Brushes.Transparent;
        DropShelfZone.BorderBrush = pin ? plain : accent;
        DropShelfZone.Background = pin ? Brushes.Transparent : faint;
        DropPinText.Foreground = DropPinGlyph.Foreground = pin ? accent : Theme.Ink();
        DropShelfText.Foreground = DropShelfGlyph.Foreground = pin ? Theme.Ink() : accent;
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        bool pin = _dropZoneShowing && DropTargetIsPin(e);
        HideDropZones();
        if (_draggingOut) return;

        string[]? paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        string? link = paths is null ? DraggedLink(e.Data) : null;
        if ((paths is null || paths.Length == 0) && link is null) return;

        if (pin)
        {
            var targets = link is not null ? new[] { link } : paths!;
            foreach (var target in targets) AddPin(target);
            string what = targets.Length == 1 ? DefaultPinName(targets[0]) : $"{targets.Length} items";
            ShowActivity("\uE718", null, $"{what} pinned", "Rest the mouse on the island to see it", null);
            return;
        }
        if (paths is null || paths.Length == 0) return;

        int added = _shelf.Add(paths);
        if (_expanded)
        {
            if (ShelfTabButton.IsChecked == true) RefreshShelf();
            else ShelfTabButton.IsChecked = true;
            return;
        }

        var first = _shelf.Items.FirstOrDefault();
        string title = added switch
        {
            0 => "Already on your shelf",
            1 => $"{first?.Name ?? "File"} added to your shelf",
            _ => $"{added} files added to your shelf",
        };
        ShowActivity("\uE8B7", added == 1 ? first?.Thumbnail : null, title, "Open the island › Shelf to use them", null);
        if (_activityShowing && added > 0) _activityOpensTab = "Shelf";
    }

    private bool _shelfDoubleClicked;

    private void ShelfTile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _shelfDoubleClicked = e.ClickCount == 2;
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is ShelfItem item)
        {
            Collapse();
            SystemActions.Open(item.Path);
        }
    }

    /// <summary>A single click ticks a file (or unticks it); double-click opens it.</summary>
    private void ShelfTile_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShelfItem item) return;
        e.Handled = true;
        if (_shelfDoubleClicked) { _shelfDoubleClicked = false; return; }
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) > 6 || Math.Abs(delta.Y) > 6) return; // that was a drag
        item.IsSelected = !item.IsSelected;
        UpdateShelfSelection();
    }

    private List<ShelfItem> SelectedShelfItems => _shelf.Items.Where(i => i.IsSelected).ToList();

    private void UpdateShelfSelection()
    {
        int count = _shelf.Items.Count(i => i.IsSelected);
        ShelfSelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfSelectionText.Text = count == 1 ? "1 selected" : $"{count} selected";
        if (_currentTab == "Shelf") ResizeExpandedIfNeeded();
    }

    private void ShelfSelectionNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _shelf.Items) item.IsSelected = false;
        UpdateShelfSelection();
    }

    private void ShelfSelectionCopy_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedShelfItems.Select(i => i.Path).ToList();
        try { FileActions.CopyFiles(paths); } catch { }
        ShowShelfNote(paths.Count == 1 ? "Copied, ready to paste" : $"{paths.Count} files copied, ready to paste");
    }

    private void ShelfSelectionZip_Click(object sender, RoutedEventArgs e) => ZipShelfItems(SelectedShelfItems);

    private void ShelfSelectionShow_Click(object sender, RoutedEventArgs e)
    {
        var first = SelectedShelfItems.FirstOrDefault();
        if (first is null) return;
        Collapse();
        FileActions.ShowInFolder(first.Path);
    }

    private void ShelfSelectionRemove_Click(object sender, RoutedEventArgs e)
    {
        _shelf.Remove(SelectedShelfItems);
        UpdateShelfSelection();
    }

    /// <summary>A short note in the Shelf's title line (e.g. "Copied"), which goes back after a few seconds.</summary>
    private async void ShowShelfNote(string note)
    {
        ShelfTitle.Text = note.ToUpperInvariant();
        await System.Threading.Tasks.Task.Delay(2500);
        RefreshShelf();
    }

    private async void ZipShelfItems(List<ShelfItem> items)
    {
        if (items.Count == 0) return;
        ShowShelfNote("Zipping…");
        try
        {
            string zip = await FileActions.ZipAsync(items.Select(i => i.Path).ToList());
            _shelf.Add(new[] { zip });
            foreach (var item in _shelf.Items) item.IsSelected = false;
            UpdateShelfSelection();
            ShowShelfNote($"Made {System.IO.Path.GetFileName(zip)}");
        }
        catch (Exception ex)
        {
            App.LogNote($"zip failed: {ex.Message}");
            ShowShelfNote("Couldn't zip those");
        }
    }

    /// <summary>Right-click a file on the Shelf: everything you might want to do with it.</summary>
    private void ShelfTile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ShelfItem item) return;
        e.Handled = true;
        string path = item.Path;
        bool isFile = File.Exists(path);
        bool image = isFile && FileActions.IsImage(path);

        var menu = new ContextMenu { DataContext = item };
        menu.Opened += Menu_Opened;
        menu.Closed += Menu_Closed;
        menu.Items.Add(MenuEntry("Open", (_, _) => { Collapse(); SystemActions.Open(path); }));
        if (isFile) menu.Items.Add(MenuEntry("Open with…", (_, _) => { Collapse(); FileActions.OpenWith(path); }));
        if (image) menu.Items.Add(MenuEntry("Edit", (_, _) => { Collapse(); FileActions.Edit(path); }));
        if (image) menu.Items.Add(MenuEntry("Copy image", (_, _) => ShowShelfNote(FileActions.CopyImage(path) ? "Image copied" : "Couldn't copy that image")));
        menu.Items.Add(MenuEntry(item.IsFolder ? "Copy folder" : "Copy file", (_, _) => { try { FileActions.CopyFiles(new[] { path }); ShowShelfNote("Copied, ready to paste"); } catch { } }));
        menu.Items.Add(MenuEntry("Copy path", (_, _) => { try { Clipboard.SetText(path); ShowShelfNote("Path copied"); } catch { } }));
        menu.Items.Add(MenuEntry("Compress to zip", (_, _) => ZipShelfItems(new List<ShelfItem> { item })));
        if (isFile) menu.Items.Add(MenuEntry("Save a copy as…", (_, _) => SaveShelfCopy(path)));
        menu.Items.Add(MenuEntry("Rename…", (_, _) => RenameShelfItem(item)));
        menu.Items.Add(MenuEntry(_settings.IsTrayPinned(path) ? "Unpin from tray" : "Pin to tray", (_, _) => ToggleTrayPin(path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Show in folder", (_, _) => { Collapse(); FileActions.ShowInFolder(path); }));
        menu.Items.Add(MenuEntry("Remove from Shelf", (_, _) => { _shelf.Remove(item); UpdateShelfSelection(); }));
        var delete = MenuEntry("Delete (to Recycle Bin)", (_, _) => DeleteShelfItem(item));
        delete.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x4B, 0x4B));
        menu.Items.Add(delete);

        element.ContextMenu = menu;
        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    private void SaveShelfCopy(string path)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save a copy",
            FileName = System.IO.Path.GetFileName(path),
            Filter = $"{System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} file|*{System.IO.Path.GetExtension(path)}|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.Copy(path, dialog.FileName, overwrite: true);
            ShowShelfNote("Copy saved");
        }
        catch (Exception ex)
        {
            App.LogNote($"save copy failed: {ex.Message}");
            ShowShelfNote("Couldn't save the copy");
        }
    }

    private void RenameShelfItem(ShelfItem item)
    {
        string? name = PromptWindow.Ask("Rename", "The new name for this file or folder (it's renamed where it is).", item.Name);
        if (name is null || name == item.Name) return;
        if (FileActions.Rename(item.Path, name) is { } renamed)
        {
            _shelf.Replace(item, renamed);
            ShowShelfNote("Renamed");
        }
        else ShowShelfNote("Couldn't rename it (is that name taken, or the file open?)");
    }

    private void DeleteShelfItem(ShelfItem item)
    {
        if (FileActions.Recycle(item.Path))
        {
            _shelf.Remove(item);
            UpdateShelfSelection();
            ShowShelfNote("Moved to the Recycle Bin");
        }
        else ShowShelfNote("Couldn't delete it (is it open?)");
    }

    private bool MovedEnoughToDrag(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return false;
        var delta = e.GetPosition(this) - _dragStart;
        return Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private void ShelfTile_MouseMove(object sender, MouseEventArgs e)
    {
        if (!MovedEnoughToDrag(e) || sender is not FrameworkElement element || element.DataContext is not ShelfItem item) return;
        DragOut(element, new[] { item.Path });
    }

    private void ShelfDragAll_MouseMove(object sender, MouseEventArgs e)
    {
        if (!MovedEnoughToDrag(e) || _shelf.Items.Count == 0) return;
        DragOut((FrameworkElement)sender, _shelf.Items.Select(i => i.Path).ToArray());
        e.Handled = true;
    }

    /// <summary>Starts a normal Windows drag, so files can be dropped into Explorer, an email, a chat…</summary>
    private void DragOut(FrameworkElement source, string[] paths)
    {
        _draggingOut = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths);
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch { }
        finally
        {
            _draggingOut = false;
        }
        RefreshShelf(); // anything that was moved away disappears from the shelf
    }

    private void ShelfOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShelfItem item) return;
        Collapse();
        SystemActions.Open(item.Path);
    }

    private void ShelfShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShelfItem item) return;
        Collapse();
        try { Process.Start("explorer.exe", $"/select,\"{item.Path}\""); } catch { }
    }

    private void ShelfCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShelfItem item) return;
        try { Clipboard.SetText(item.Path); } catch { }
    }

    private void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShelfItem item) _shelf.Remove(item);
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e) => _shelf.Clear();

    // =====================================================================================
    //  Weather (Today tab)
    // =====================================================================================

    private DateTime _weatherFetchedAt = DateTime.MinValue;
    private bool _weatherLoading;

    private async void RefreshWeather(bool force = false)
    {
        if (_weatherLoading) return;
        if (!force && DateTime.Now - _weatherFetchedAt < TimeSpan.FromMinutes(30)) return;
        _weatherLoading = true;
        try
        {
            double latitude, longitude;
            string place;
            if (_settings.WeatherLatitude is double savedLat && _settings.WeatherLongitude is double savedLon)
            {
                latitude = savedLat;
                longitude = savedLon;
                place = _settings.WeatherPlace;
            }
            else
            {
                var here = await WeatherService.CurrentLocationAsync();
                if (here is null)
                {
                    ShowWeatherMessage("Set your town in Settings to see the weather (or turn on location for desktop apps in Windows).", showButton: true);
                    _weatherFetchedAt = DateTime.Now;
                    return;
                }
                latitude = here.Value.Latitude;
                longitude = here.Value.Longitude;
                place = await WeatherService.NameForAsync(latitude, longitude) ?? "Your location";
            }

            var report = await WeatherService.GetAsync(latitude, longitude, place, _settings.Fahrenheit);
            if (report is null)
            {
                ShowWeatherMessage("Couldn't get the weather just now.", showButton: false);
                _weatherFetchedAt = DateTime.Now - TimeSpan.FromMinutes(25); // try again in about 5 minutes
                return;
            }
            _weatherFetchedAt = DateTime.Now;
            ShowWeather(report);
        }
        finally
        {
            _weatherLoading = false;
        }
    }

    private void ShowWeatherMessage(string message, bool showButton)
    {
        WeatherPanel.Visibility = Visibility.Collapsed;
        WeatherMessagePanel.Visibility = Visibility.Visible;
        WeatherMessage.Text = message;
        WeatherSetupButton.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowWeather(WeatherReport report)
    {
        WeatherMessagePanel.Visibility = Visibility.Collapsed;
        WeatherPanel.Visibility = Visibility.Visible;

        WeatherSymbol.Text = WeatherService.Symbol(report.Code, report.IsDay);
        WeatherTemp.Text = $"{report.Temperature:0}°";
        WeatherCondition.Text = WeatherService.Describe(report.Code);
        WeatherHighLow.Text = $"H {report.High:0}°  L {report.Low:0}°";
        WeatherPlace.Text = report.Place;
        WeatherPlace.ToolTip = $"{report.Place}  ·  feels like {report.FeelsLike:0}°";

        WeatherHours.Children.Clear();
        var subtle = Theme.Ink(0x99);
        foreach (var hour in report.Hours)
        {
            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            column.Children.Add(new TextBlock { Text = hour.Time.ToString("HH:mm"), FontSize = 10, Foreground = subtle, HorizontalAlignment = HorizontalAlignment.Center });
            var symbol = new TextBlock
            {
                Text = WeatherService.Symbol(hour.Code, hour.IsDay),
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 14,
                Margin = new Thickness(0, 2, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            symbol.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            column.Children.Add(symbol);
            column.Children.Add(new TextBlock { Text = $"{hour.Temperature:0}°", FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            WeatherHours.Children.Add(column);
        }
        if (_currentTab == "Today") ResizeExpandedIfNeeded();
    }

    // =====================================================================================
    //  Device pop-ups: Bluetooth, USB drives, sound output
    // =====================================================================================

    private void OnBluetoothChanged(string name, bool connected)
    {
        if (!_settings.DevicePopOuts) return;
        ShowActivity("", null, name, connected ? "Connected" : "Disconnected", null);
    }

    private void OnOutputDeviceChanged()
    {
        _volume.Attach();
        RefreshVolume();
        RefreshControls();
        if (_deviceList == DeviceListKind.Sound)
        {
            LoadSoundOutputs();
            _mixerLayout = "";
            RefreshAppVolumes();
        }
        if (_settings.DevicePopOuts && _volume.OutputName.Length > 0)
            ShowActivity("", null, _volume.OutputName, "Sound now plays here", null);
    }

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 2;
    private const short DBTF_NET = 2;

    /// <summary>Windows tells every window when a drive appears or disappears; pick out USB/removable drives.</summary>
    private void HandleDeviceChange(IntPtr wParam, IntPtr lParam)
    {
        int change = wParam.ToInt32();
        if ((change != DBT_DEVICEARRIVAL && change != DBT_DEVICEREMOVECOMPLETE) || lParam == IntPtr.Zero) return;
        if (Marshal.ReadInt32(lParam, 4) != DBT_DEVTYP_VOLUME) return;
        int unitMask = Marshal.ReadInt32(lParam, 12);
        short flags = Marshal.ReadInt16(lParam, 16);
        if ((flags & DBTF_NET) != 0) return; // network drives aren't interesting here

        for (int i = 0; i < 26; i++)
        {
            if ((unitMask & (1 << i)) == 0) continue;
            char letter = (char)('A' + i);
            bool arrived = change == DBT_DEVICEARRIVAL;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnDriveChanged(letter, arrived)));
            break;
        }
    }

    private void OnDriveChanged(char letter, bool arrived)
    {
        if (!_settings.DevicePopOuts) return;
        if (!arrived)
        {
            ShowActivity("", null, "Drive removed", $"{letter}:", null);
            return;
        }
        string details = $"{letter}:";
        try
        {
            var drive = new DriveInfo($"{letter}:\\");
            if (drive.IsReady)
            {
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "" : $"  {drive.VolumeLabel}";
                details = $"{letter}:{label}  ·  {SystemStats.FormatBytes(drive.AvailableFreeSpace)} free";
            }
        }
        catch { }
        ShowActivity("", null, "Drive connected", details, null);
    }

    // =====================================================================================
    //  Settings
    // =====================================================================================

    private bool _loadingSettings;

    private static readonly (string Name, string Hex)[] AccentChoices =
    {
        ("Gold", "#D6B46A"), ("Sky", "#5AA9F0"), ("Mint", "#6CCB8C"),
        ("Rose", "#F08DB4"), ("Lilac", "#A98BF0"), ("Silver", "#C8CCD2"),
    };

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!_expanded) Expand();
        // No tab is highlighted while Settings is open; clicking any tab goes back to it.
        foreach (var tab in new[] { TodayTabButton, ControlsTabButton, AppsTabButton, ClipboardTabButton, ShelfTabButton,
                                    MediaTabButton, StatsTabButton, PowerTabButton })
            tab.IsChecked = false;
        ShowTab("Settings", animate: true);
    }

    private void BuildAccentSwatches()
    {
        AccentSwatches.Children.Clear();
        foreach (var (name, hex) in AccentChoices)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            bool selected = string.Equals(hex, _settings.AccentColor, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(color),
                BorderBrush = Theme.Ink(),
                BorderThickness = new Thickness(selected ? 2.5 : 0),
                Cursor = Cursors.Hand,
                ToolTip = name,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _settings.AccentColor = hex;
                _settings.Save();
                ApplyAccent();
                BuildAccentSwatches();
            };
            AccentSwatches.Children.Add(swatch);
        }
    }

    /// <summary>
    /// Switches the whole island between light and dark while it's running: the colour resources update by
    /// themselves, and the few things drawn in code are redrawn.
    /// </summary>
    private void SwitchTheme(bool light)
    {
        if (light == Theme.Light) return;
        Theme.Set(light);
        LineIcons.ClearCache();
        ApplyThemeResources();
        ApplyAccent(); // the deepened accent, island style, calendar, battery, media colours, power sliders
        BuildAccentSwatches();
        ShowHotkeyStatus();
        RefreshStartupStatus();
        OnMediaChanged();
        RefreshOpenApps();
        _rows.Clear(); // the Apps list rebuilds with the adjusted icon colours
        if (_currentTab == "Apps") RebuildAppList();
        App.LogNote($"switched to the {(light ? "light" : "dark")} island");
    }

    /// <summary>
    /// Text and fine-line colours for the light or dark island (see Theme). The XAML refers to these as
    /// Ink, Ink80 (half-strength) and so on, plus a few solid surfaces.
    /// </summary>
    private void ApplyThemeResources()
    {
        Resources["Ink"] = Theme.Ink();
        foreach (byte alpha in new byte[] { 0x12, 0x14, 0x16, 0x1A, 0x1F, 0x22, 0x26, 0x2A, 0x2E, 0x33, 0x38, 0x4D, 0x55,
                                            0x66, 0x73, 0x80, 0x8C, 0x99, 0xA0, 0xB3, 0xC0, 0xCC, 0xDD })
            Resources[$"Ink{alpha:X2}"] = Theme.Ink(alpha);
        Resources["SubtleText"] = Theme.Ink(0x99);

        bool light = Theme.Light;
        Resources["TrackOffBrush"] = Frozen(light ? Color.FromRgb(0xD1, 0xD1, 0xD6) : Color.FromRgb(0x33, 0x33, 0x33));
        Resources["TileOffBrush"] = Frozen(light ? Color.FromArgb(0x14, 0x00, 0x00, 0x00) : Color.FromRgb(0x26, 0x26, 0x26));
        Resources["BadgeBrush"] = Frozen(light ? Color.FromRgb(0xD8, 0xD8, 0xDD) : Color.FromRgb(0x3A, 0x3A, 0x3C));
        Resources["BadgeRing"] = Frozen(light ? Color.FromRgb(0xEE, 0xEE, 0xF1) : Color.FromRgb(0x15, 0x15, 0x15));
        Resources["ClosedPinOpacity"] = light ? 0.55 : 0.38; // faded pinned apps need a little more strength on light
    }

    /// <summary>Recolours everything gold-coloured to the chosen accent.</summary>
    private void ApplyAccent()
    {
        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(_settings.AccentColor); }
        catch { accent = Color.FromRgb(0xD6, 0xB4, 0x6A); }
        // On the light island the accent is deepened a little so it stays readable on white.
        if (Theme.Light) accent = Color.FromRgb((byte)(accent.R * 0.78), (byte)(accent.G * 0.78), (byte)(accent.B * 0.78));

        static Color Blend(Color a, Color b, double amountOfA) => Color.FromRgb(
            (byte)(a.R * amountOfA + b.R * (1 - amountOfA)),
            (byte)(a.G * amountOfA + b.G * (1 - amountOfA)),
            (byte)(a.B * amountOfA + b.B * (1 - amountOfA)));

        static SolidColorBrush Solid(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        Resources["Accent"] = Solid(accent);
        Resources["AccentSoft"] = Solid(Blend(accent, Color.FromRgb(0x12, 0x12, 0x12), 0.22));
        Resources["AccentBright"] = Solid(Blend(accent, Colors.White, 0.85));
        Resources["AccentFaint"] = Solid(Color.FromArgb(0x33, accent.R, accent.G, accent.B));

        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x46, accent.R, accent.G, accent.B), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 0.85));
        gradient.Freeze();
        Resources["AccentHoverGradient"] = gradient;

        ToggleTile.SetAccent(accent);
        ApplyIslandStyle(); // the glow and outline use the accent colour
        RenderCalendar();
        RefreshBattery(allowPopOut: false);
        RefreshMediaTint();
        ApplyPowerSliderAccent();
    }

    /// <summary>Black, Light (frosted), Glow (soft accent halo), Outline (thin accent edge), or Automatic (Black or Light, following Windows).</summary>
    private void ApplyIslandStyle()
    {
        IslandShape.Effect = null;
        IslandShape.Stroke = null;
        IslandShape.StrokeThickness = 0;
        IslandShape.Fill = Brushes.Black;

        // Default (solid) surfaces
        Color card = Color.FromRgb(0x17, 0x17, 0x17), field = Color.FromRgb(0x1E, 0x1E, 0x1E), tileOff = Color.FromRgb(0x26, 0x26, 0x26);
        Color cardBorder = Colors.Transparent;
        Color tile = Color.FromRgb(0x1C, 0x1C, 0x1E), tileHover = Color.FromRgb(0x2A, 0x2A, 0x2D);

        Color accent = (Resources["Accent"] as SolidColorBrush)?.Color ?? Color.FromRgb(0xD6, 0xB4, 0x6A);
        switch (Theme.Light ? "Light" : _settings.IslandStyle)
        {
            case "Light":
                // Light frosted: Windows blurs whatever is behind (see AcrylicBackdrop, set to its light frost);
                // the island adds a soft white wash on top so it reads as light over any wallpaper, a fine edge,
                // and white see-through cards. Without the blur it's simply a near-solid soft white.
                // A soft light grey island (same shape as the others) with crisp white cards on it, so there's
                // clear contrast. With "Frosted blur" on, it's a see-through grey wash over Windows' blur instead.
                var wash = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                wash.GradientStops.Add(new GradientStop(IsGlass ? Color.FromArgb(0xC8, 0xEC, 0xEC, 0xEF) : Color.FromRgb(0xEE, 0xEE, 0xF1), 0));
                wash.GradientStops.Add(new GradientStop(IsGlass ? Color.FromArgb(0xC0, 0xE6, 0xE6, 0xEA) : Color.FromRgb(0xE8, 0xE8, 0xEC), 1));
                wash.Freeze();
                IslandShape.Fill = wash;
                IslandShape.Stroke = new SolidColorBrush(Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
                IslandShape.StrokeThickness = 1;
                card = IsGlass ? Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF) : Colors.White;
                field = Color.FromArgb(0x10, 0x00, 0x00, 0x00);
                tileOff = Color.FromArgb(0x16, 0x00, 0x00, 0x00);
                cardBorder = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
                tile = Color.FromArgb(0x10, 0x00, 0x00, 0x00);
                tileHover = Color.FromArgb(0x22, 0x00, 0x00, 0x00);
                break;

            case "Glow":
                // The halo itself is drawn on a click-through layer behind the island (see GlowBackdrop).
                // Inside, the island glows faintly from the bottom edge and the cards pick up the accent.
                var inner = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                inner.GradientStops.Add(new GradientStop(Colors.Black, 0));
                inner.GradientStops.Add(new GradientStop(Colors.Black, 0.55));
                inner.GradientStops.Add(new GradientStop(Blend(accent, Colors.Black, 0.16), 1));
                inner.Freeze();
                IslandShape.Fill = inner;
                cardBorder = Color.FromArgb(0x55, accent.R, accent.G, accent.B);
                break;

            case "Outline":
                IslandShape.Stroke = new SolidColorBrush(accent);
                IslandShape.StrokeThickness = 1.4;
                break;
        }

        Resources["CardBrush"] = Frozen(card);
        Resources["FieldBrush"] = Frozen(field);
        Resources["CardBorder"] = Frozen(cardBorder);
        Resources["TileBrush"] = Frozen(tile);
        _glass.SetLight(Theme.Light);
        Resources["ListFadeTop"] = IsGlass ? Brushes.Transparent : FadeBrush(toBottom: false);
        // In Glow, the island is tinted near the bottom, so the bottom fade blends into that tint instead of black.
        Resources["ListFadeBottom"] = IsGlass ? Brushes.Transparent
            : FadeBrush(toBottom: true, _settings.IslandStyle == "Glow" && !Theme.Light ? Blend(accent, Colors.Black, 0.12) : IslandEdge);
        Resources["TileHoverBrush"] = Frozen(tileHover);
        ToggleTile.SetOffBackground(tileOff);
        UpdateIslandShape();
        UpdateContentClip();
        SyncBackdrops();
    }

    private static Color Blend(Color a, Color b, double amountOfA) => Color.FromRgb(
        (byte)(a.R * amountOfA + b.R * (1 - amountOfA)),
        (byte)(a.G * amountOfA + b.G * (1 - amountOfA)),
        (byte)(a.B * amountOfA + b.B * (1 - amountOfA)));

    /// <summary>The island's own colour, which list edges fade into.</summary>
    private static Color IslandEdge => Theme.Light ? Color.FromRgb(0xEB, 0xEB, 0xEF) : Colors.Black;

    private static Brush FadeBrush(bool toBottom) => FadeBrush(toBottom, IslandEdge);

    private static Brush FadeBrush(bool toBottom, Color edge)
    {
        // Fade to see-through *black*: WPF's "Transparent" is see-through white, which turns grey halfway through a fade.
        var clear = Color.FromArgb(0, edge.R, edge.G, edge.B);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(toBottom ? clear : edge, 0));
        brush.GradientStops.Add(new GradientStop(toBottom ? edge : clear, 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    // ---- Keeping the glass and glow layers lined up with the island ----

    /// <summary>Puts the glass and glow layers where the island window is (the glow layer a bit bigger, for the halo).</summary>
    private void PositionBackdrops()
    {
        _glass.Left = Left;   // just a starting point; SyncBackdrops sizes it to the island
        _glass.Top = Top;
        _glass.Width = 1;
        _glass.Height = 1;
        _glow.Left = Left - GlowBackdrop.Spread;
        _glow.Top = Top;
        _glow.Width = Width + GlowBackdrop.Spread * 2;
        _glow.Height = Height + GlowBackdrop.Spread;
    }

    /// <summary>Called whenever the island changes size, slides, shows or hides, or the style changes.</summary>
    private void SyncBackdrops()
    {
        if (!IsLoaded || _glass.Handle == IntPtr.Zero || _glow.Handle == IntPtr.Zero) return;
        bool visible = IslandHost.IsVisible && IslandHost.ActualWidth > 0 && IslandShape.Data is not null;
        Color accent = (Resources["Accent"] as SolidColorBrush)?.Color ?? Color.FromRgb(0xD6, 0xB4, 0x6A);
        Point offset = visible ? IslandHost.TranslatePoint(new Point(0, 0), this) : default; // includes the slide

        // Glass: the frost sits exactly behind the island's glass panel. Its top edge is tucked just above the
        // screen so Windows' rounded top corners are out of sight, leaving a flat top and rounded bottom corners.
        if (visible && IsGlass)
        {
            _glass.SetAcrylic(true);
            double scale = DpiScale;
            var corner = IslandHost.PointToScreen(new Point(Shoulder, 0)); // screen pixels, includes the slide
            int tuck = (int)Math.Ceiling(GlassCornerRadius * scale);
            int width = (int)Math.Round((IslandHost.ActualWidth - Shoulder * 2) * scale);
            int height = (int)Math.Round(IslandHost.ActualHeight * scale) + tuck;
            _glass.Place((int)Math.Round(corner.X), (int)Math.Round(corner.Y) - tuck, width, height);
        }
        else
        {
            _glass.Conceal();
        }

        // Glow: draw the halo around the same outline.
        if (visible && _settings.IslandStyle == "Glow")
            _glow.Update(IslandShape.Data, new Point(offset.X + GlowBackdrop.Spread, offset.Y), accent);
        else
            _glow.Update(null, default, accent);

        KeepBackdropsBelow();
    }

    /// <summary>The layers must always sit directly underneath the island, never on top of it.</summary>
    private void KeepBackdropsBelow()
    {
        foreach (var layer in new[] { _glass.Handle, _glow.Handle })
            if (layer != IntPtr.Zero && _hwnd != IntPtr.Zero)
                Native.SetWindowPos(layer, _hwnd, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }


    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;
        BuildAccentSwatches();
        MusicBarsSwitch.IsChecked = _settings.ShowMusicBars;
        BatterySwitch.IsChecked = _settings.ShowBatteryInPill;
        LineIconsSwitch.IsChecked = _settings.UseLineIcons && LineIcons.Available;
        LineIconsSwitch.IsEnabled = LineIcons.Available;
        LineIconsStatusText.Text = LineIcons.Available
            ? "Tidy, matching icons for your apps instead of their own icons"
            : "Icon set not downloaded: run Build.cmd while online to add it";
        StyleBlack.IsChecked = _settings.IslandStyle is not ("Light" or "Glow" or "Outline" or "Auto");
        StyleLight.IsChecked = _settings.IslandStyle == "Light";
        StyleAuto.IsChecked = _settings.IslandStyle == "Auto";
        LightFrostedSwitch.IsChecked = _settings.LightFrosted;
        StyleGlow.IsChecked = _settings.IslandStyle == "Glow";
        StyleOutline.IsChecked = _settings.IslandStyle == "Outline";
        PeekSwitch.IsChecked = _settings.PeekOnHover;
        DelayQuick.IsChecked = _settings.PeekDelayMs <= 300;
        DelayNormal.IsChecked = _settings.PeekDelayMs is > 300 and <= 600;
        DelayRelaxed.IsChecked = _settings.PeekDelayMs > 600;
        HideMaximizedSwitch.IsChecked = _settings.HideOverMaximized;
        DevicePopOutsSwitch.IsChecked = _settings.DevicePopOuts;
        SearchHotkeySwitch.IsChecked = _settings.SearchHotkey;
        AutoUpdateSwitch.IsChecked = _settings.AutoUpdate;
        ScreenshotPopupsSwitch.IsChecked = _settings.ScreenshotPopups;
        DownloadPopupsSwitch.IsChecked = _settings.DownloadPopups;
        CapturesToShelfSwitch.IsChecked = _settings.CapturesToShelf;
        DownloadProgressSwitch.IsChecked = _settings.DownloadProgress;
        CapsLockPopupsSwitch.IsChecked = _settings.CapsLockPopups;
        TrayAppIconsSwitch.IsChecked = _settings.TrayAppIcons;
        SongPopupsSwitch.IsChecked = _settings.SongPopups;
        PopupShort.IsChecked = _settings.PopupLength == "Short";
        PopupLong.IsChecked = _settings.PopupLength == "Long";
        PopupNormal.IsChecked = _settings.PopupLength is not ("Short" or "Long");
        Clock12.IsChecked = _settings.ClockFormat == "12";
        Clock24.IsChecked = _settings.ClockFormat == "24";
        ClockWindows.IsChecked = _settings.ClockFormat is not ("12" or "24");
        ClockSecondsSwitch.IsChecked = _settings.ClockSeconds;
        DateNumbers.IsChecked = _settings.PillDate == "Numbers";
        DateDay.IsChecked = _settings.PillDate == "Day";
        DateOff.IsChecked = _settings.PillDate == "Off";
        DateShort.IsChecked = _settings.PillDate is not ("Numbers" or "Day" or "Off");
        DateNumbers.Content = DateTime.Now.ToString(DayFirst ? "dd/MM" : "MM/dd");
        DateShort.Content = DateTime.Now.ToString("ddd d MMM");
        DateDay.Content = DateTime.Now.ToString("dddd");
        ScreensMain.IsChecked = _settings.ScreenMode == "Main";
        ScreensFollow.IsChecked = _settings.ScreenMode != "Main";
        Clean1.IsChecked = _settings.ShelfCleanDays == 1;
        Clean7.IsChecked = _settings.ShelfCleanDays == 7;
        Clean30.IsChecked = _settings.ShelfCleanDays == 30;
        CleanNever.IsChecked = _settings.ShelfCleanDays is not (1 or 7 or 30);
        ClearClipboardSwitch.IsChecked = _settings.ClearClipboardOnLock;
        ShowUpdateState();
        ShowHotkeyStatus();
        StartWithWindowsSwitch.IsChecked = _startupEnabled;
        AboutVersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(2)}";
        CelsiusChip.IsChecked = !_settings.Fahrenheit;
        FahrenheitChip.IsChecked = _settings.Fahrenheit;
        PlaceBox.Text = _settings.WeatherPlace;
        PlaceStatus.Text = _settings.WeatherLatitude is null ? "Using Windows' location." : $"Using {_settings.WeatherPlace}.";
        _loadingSettings = false;
        RefreshStartupStatus();
    }

    /// <summary>Any switch or choice in Settings: save it and apply it straight away.</summary>
    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool unitsChanged = _settings.Fahrenheit != (FahrenheitChip.IsChecked == true);

        _settings.ShowMusicBars = MusicBarsSwitch.IsChecked == true;
        bool batteryChanged = _settings.ShowBatteryInPill != (BatterySwitch.IsChecked == true);
        bool iconsChanged = LineIcons.Available && _settings.UseLineIcons != (LineIconsSwitch.IsChecked == true);
        if (LineIcons.Available) _settings.UseLineIcons = LineIconsSwitch.IsChecked == true;
        _settings.ShowBatteryInPill = BatterySwitch.IsChecked == true;
        _settings.LightFrosted = LightFrostedSwitch.IsChecked == true;
        _settings.IslandStyle = StyleLight.IsChecked == true ? "Light"
            : StyleAuto.IsChecked == true ? "Auto"
            : StyleGlow.IsChecked == true ? "Glow"
            : StyleOutline.IsChecked == true ? "Outline"
            : "Black";
        _settings.PeekOnHover = PeekSwitch.IsChecked == true;
        _settings.PeekDelayMs = DelayQuick.IsChecked == true ? 250 : DelayRelaxed.IsChecked == true ? 750 : 450;
        _settings.HideOverMaximized = HideMaximizedSwitch.IsChecked == true;
        _settings.DevicePopOuts = DevicePopOutsSwitch.IsChecked == true;
        _settings.SearchHotkey = SearchHotkeySwitch.IsChecked == true;
        bool autoUpdateTurnedOn = !_settings.AutoUpdate && AutoUpdateSwitch.IsChecked == true;
        _settings.AutoUpdate = AutoUpdateSwitch.IsChecked == true;
        _settings.ScreenshotPopups = ScreenshotPopupsSwitch.IsChecked == true;
        _settings.DownloadPopups = DownloadPopupsSwitch.IsChecked == true;
        _settings.CapturesToShelf = CapturesToShelfSwitch.IsChecked == true;
        _settings.DownloadProgress = DownloadProgressSwitch.IsChecked == true;
        _settings.CapsLockPopups = CapsLockPopupsSwitch.IsChecked == true;
        _settings.TrayAppIcons = TrayAppIconsSwitch.IsChecked == true;
        _settings.Fahrenheit = FahrenheitChip.IsChecked == true;
        _settings.SongPopups = SongPopupsSwitch.IsChecked == true;
        _settings.PopupLength = PopupShort.IsChecked == true ? "Short" : PopupLong.IsChecked == true ? "Long" : "Normal";
        _settings.ClockFormat = Clock12.IsChecked == true ? "12" : Clock24.IsChecked == true ? "24" : "Windows";
        _settings.ClockSeconds = ClockSecondsSwitch.IsChecked == true;
        _settings.PillDate = DateNumbers.IsChecked == true ? "Numbers" : DateDay.IsChecked == true ? "Day" : DateOff.IsChecked == true ? "Off" : "Short";
        bool screensChanged = _settings.ScreenMode != (ScreensMain.IsChecked == true ? "Main" : "Follow");
        _settings.ScreenMode = ScreensMain.IsChecked == true ? "Main" : "Follow";
        int cleanDays = Clean1.IsChecked == true ? 1 : Clean7.IsChecked == true ? 7 : Clean30.IsChecked == true ? 30 : 0;
        bool cleanChanged = cleanDays != _settings.ShelfCleanDays;
        _settings.ShelfCleanDays = cleanDays;
        _settings.ClearClipboardOnLock = ClearClipboardSwitch.IsChecked == true;
        _settings.Save();
        UpdateClock();
        if (cleanChanged) _lastShelfClean = DateTime.MinValue; // clean up at the next check, a moment from now
        if (screensChanged) ApplyScreenMode();

        if (sender == StartWithWindowsSwitch) SetStartup(StartWithWindowsSwitch.IsChecked == true);

        SwitchTheme(Theme.Decide(_settings.IslandStyle));

        if (autoUpdateTurnedOn && _availableUpdate is not null) InstallUpdate(_availableUpdate, automatic: true);
        ApplyBehaviourSettings();
        UpdateSearchHotkey();
        ShowHotkeyStatus();
        ApplyIslandStyle();
        if (batteryChanged) UpdatePillBatteryVisibility();
        ApplyDownloadState();
        ApplyTrayIconSetting();
        if (iconsChanged)
        {
            RefreshOpenApps();
            _rows.Clear(); // the Apps list rebuilds with the new icons next time it opens
        }
        if (unitsChanged) RefreshWeather(force: true);
    }

    private void ShowHotkeyStatus()
    {
        bool taken = _settings.SearchHotkey && _hotkeyTaken;
        SearchHotkeyStatusText.Text = taken
            ? "Another app is already using Ctrl+Space, so this can't be used right now"
            : "From anywhere: type an app's name and press Enter. Press it again to close";
        SearchHotkeyStatusText.Foreground = taken ? new SolidColorBrush(Theme.Light ? Color.FromRgb(0xC0, 0x39, 0x2B) : Color.FromRgb(0xFF, 0x9A, 0x94)) : (Brush)FindResource("SubtleText");
    }

    // ---- Screenshots and downloads ----

    private readonly CaptureWatcher _captures = new();
    private string? _activityOpensTab; // clicking the current pop-out opens this tab

    private void StartCaptureWatcher()
    {
        _captures.FileReady += (path, kind) => Dispatcher.BeginInvoke(new Action(() => OnCaptureReady(path, kind)));
        _captures.DownloadsChanged += progress => Dispatcher.BeginInvoke(new Action(() => OnDownloadsChanged(progress)));
        _captures.Start();
    }

    // ---- Downloads in progress: a glow on the pill, and a row in the hover tray ----

    private CaptureWatcher.DownloadProgress? _download;
    private bool _downloadShown;

    /// <summary>Something is downloading and the glow is turned on in Settings.</summary>
    private bool DownloadShowing => _download is not null && _settings.DownloadProgress;

    private void OnDownloadsChanged(CaptureWatcher.DownloadProgress? progress)
    {
        _download = progress;
        if (progress is not null)
        {
            CompactDownloadText.Text = FormatSpeed(progress.BytesPerSecond);
            PeekDownloadName.Text = progress.Count > 1 ? $"{progress.Count} downloads" : progress.Name;
            PeekDownloadSub.Text = $"{FormatBytes(progress.Bytes)} so far";
            PeekDownloadSpeed.Text = FormatSpeed(progress.BytesPerSecond);
        }
        ApplyDownloadState();
        UpdatePillWidth(); // the speed's width changes a little
    }

    /// <summary>Shows or hides the download glow, speed and tray row, and resizes the pill to fit.</summary>
    private void ApplyDownloadState()
    {
        bool show = DownloadShowing;
        if (show == _downloadShown) return;
        _downloadShown = show;

        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CompactDownload.Visibility = visibility;
        CompactDownloadGlow.Visibility = visibility;
        PeekDownloadRow.Visibility = visibility;
        UpdateLiveCard();

        foreach (var (shift, width, distance) in new[] { (CompactDownloadGlowShift, 80.0, 360.0), (PeekDownloadGlowShift, 110.0, 440.0) })
        {
            shift.BeginAnimation(TranslateTransform.XProperty, show
                ? new DoubleAnimation(-width, distance, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever }
                : null);
        }

        UpdatePillItems(); // the speed takes the music bars' place, and the pill resizes to fit
        RemeasurePeek();
    }

    /// <summary>The hover tray's card for the song and any download: shown if either is, with a hairline between both.</summary>
    private void UpdateLiveCard()
    {
        bool media = PeekMediaRow.Visibility == Visibility.Visible;
        bool download = PeekDownloadRow.Visibility == Visibility.Visible;
        PeekLiveCard.Visibility = media || download ? Visibility.Visible : Visibility.Collapsed;
        PeekLiveDivider.Visibility = media && download ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- The pill: one centred line that grows to fit ----

    private double _pillWidth = 236;

    /// <summary>Music bars show on the pill while something plays, unless a download's speed is showing instead.</summary>
    private void UpdatePillItems()
    {
        EqBars.Visibility = _equalizerOn && !DownloadShowing ? Visibility.Visible : Visibility.Collapsed;
        UpdatePillWidth();
    }

    /// <summary>
    /// Works out how wide the pill needs to be for what it's showing (time and date, plus battery, music bars or
    /// download speed), and resizes it if that's changed by more than a few pixels.
    /// </summary>
    private void UpdatePillWidth()
    {
        CompactRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Max(236, Math.Ceiling(CompactRow.DesiredSize.Width + 90));
        if (Math.Abs(width - _pillWidth) < 6) return;
        _pillWidth = width;
        if (!_expanded && !_peeking && !_activityShowing)
        {
            IslandHost.BeginAnimation(WidthProperty, null);
            AnimateSize(CompactWidth, CompactHeight, 300, bouncy: false);
        }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        double mb = bytesPerSecond / (1024 * 1024);
        if (mb >= 100) return $"{mb:0} MB/s";
        if (mb >= 1) return $"{mb:0.0} MB/s";
        return $"{Math.Max(1, bytesPerSecond / 1024):0} KB/s";
    }

    private static string FormatBytes(long bytes)
    {
        double mb = bytes / (1024.0 * 1024);
        if (mb >= 1024) return $"{mb / 1024:0.0} GB";
        if (mb >= 10) return $"{mb:0} MB";
        if (mb >= 1) return $"{mb:0.0} MB";
        return $"{Math.Max(1, bytes / 1024)} KB";
    }

    private void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        if (_captures.DownloadsFolder is not { } folder) return;
        ExitPeek();
        SystemActions.Open(folder);
    }

    // ---- Caps Lock pop-out ----

    private bool? _capsLockOn;
    private readonly DispatcherTimer _capsTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private void StartCapsLockWatch()
    {
        _capsTimer.Tick += (_, _) => CheckCapsLock();
        _capsTimer.Start();
    }

    private bool _capsKeyDown;
    private bool _capsLastReading;
    private DateTime? _capsPressedAt;

    private void CheckCapsLock()
    {
        // Notice the key itself being pressed, in case Windows is slow to tell a background app the new state.
        short key = Native.GetAsyncKeyState(Native.VK_CAPITAL);
        bool down = (key & 0x8000) != 0;
        if ((down || (key & 1) != 0) && !_capsKeyDown) _capsPressedAt = DateTime.Now;
        _capsKeyDown = down;

        bool reading = (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0;
        if (_capsLockOn is not bool believed) // just started: no pop-out
        {
            _capsLockOn = _capsLastReading = reading;
            _capsPressedAt = null;
            return;
        }

        bool on;
        if (reading != _capsLastReading)
        {
            _capsLastReading = reading;
            _capsPressedAt = null;
            if (reading == believed) return; // already shown (from the key press)
            on = reading;
        }
        else
        {
            // Pressed, but Windows hasn't told us the new state after a moment: go by the key press.
            if (_capsPressedAt is not { } pressed || DateTime.Now - pressed < TimeSpan.FromMilliseconds(400)) return;
            _capsPressedAt = null;
            on = !believed;
        }
        _capsLockOn = on;
        if (!_settings.CapsLockPopups) return;

        ShowActivity("\uE74A", null, on ? "Caps Lock on" : "Caps Lock off", on ? "Typing in CAPITALS" : "Back to normal", null, seconds: 1.5);
        if (!_activityShowing) return; // the island is open or hidden for a full-screen app

        // The line-style arrow, in the accent colour when on and grey when off.
        var line = LineIcons.Named("arrow-big-up-line", on ? ((SolidColorBrush)FindResource("Accent")).Color.ToString() : Theme.InkColor(0x8C).ToString());
        if (line is not null)
        {
            ActivityGlyph.FontFamily = LineIcons.FontOrDefault;
            ActivityGlyph.Text = line.Glyph;
            ActivityGlyph.FontSize = 24;
            ActivityGlyph.Foreground = line.Brush;
        }
        else
        {
            ActivityGlyph.Foreground = on ? (Brush)FindResource("Accent") : Theme.Ink(0x8C);
        }
    }

    /// <summary>The file the current pop-out is about (a new screenshot), for its quick buttons.</summary>
    private string? _activityFile;

    private void ActivityCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_activityFile is not { } path) return;
        bool ok = FileActions.CopyImage(path);
        ShowActivity("\uE8C8", null, ok ? "Copied" : "Couldn't copy it", ok ? "Paste it into a chat, email or document" : null, null, seconds: 1.6);
    }

    private void ActivityEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_activityFile is not { } path) return;
        EndActivity();
        FileActions.Edit(path);
    }

    private void ActivityDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_activityFile is not { } path) return;
        bool gone = FileActions.Recycle(path);
        var onShelf = _shelf.Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (gone && onShelf is not null) _shelf.Remove(onShelf);
        ShowActivity("\uE74D", null, gone ? "Screenshot deleted" : "Couldn't delete it", gone ? "It's in the Recycle Bin if you need it" : null, null, seconds: 1.8, width: ActivityWidth);
    }

    // ---- Snips that only go to the clipboard ----
    // Win+Shift+S always copies the snip, but Snipping Tool only saves a file when "Automatically save screenshots"
    // is on. So when a screenshot tool puts a picture on the clipboard and no file turns up, we save it ourselves.

    private DateTime _lastScreenshotNoticed = DateTime.MinValue;
    private DateTime _lastSnipSaved = DateTime.MinValue;
    private string? _snipSavedPath;
    private bool _snipPending;

    private void CheckForSnip()
    {
        if (_snipPending || (!_settings.ScreenshotPopups && !_settings.CapturesToShelf)) return;
        try
        {
            if (!LooksLikeSnip(out string owner)) return;
            App.LogNote($"picture copied by '{owner}' - treating it as a snip");

            // Snipping Tool often updates the clipboard several times in a row, so wait for it to settle,
            // then take whatever picture is there at that point.
            _snipPending = true;
            DateTime copiedAt = DateTime.Now;
            var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            wait.Tick += (_, _) =>
            {
                wait.Stop();
                _snipPending = false;
                // Snipping Tool saved a file after all; the watcher has already shown it.
                if (_lastScreenshotNoticed > copiedAt.AddSeconds(-2)) return;
                try
                {
                    if (!LooksLikeSnip(out _)) return;
                    var image = ClipboardPicture();
                    if (image is not null) HandleSnip(image);
                }
                catch (Exception ex)
                {
                    App.LogNote($"couldn't read the snip from the clipboard: {ex.Message}");
                }
            };
            wait.Start();
        }
        catch (Exception ex)
        {
            _snipPending = false;
            App.LogNote($"couldn't check the clipboard for a snip: {ex.Message}");
        }
    }

    /// <summary>
    /// Is what's on the clipboard just a picture, as a screenshot tool copies it? Pictures copied from a web page
    /// come with HTML, copied files come as a file list, and pictures copied in an app (Paint, Photoshop, us)
    /// are owned by that app. Snipping Tool's copies can show up as owned by a Windows service (svchost),
    /// especially on remote and cloud PCs, so those count too.
    /// </summary>
    private static bool LooksLikeSnip(out string owner)
    {
        owner = "unknown";
        if (!Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()) return false;
        if (Clipboard.ContainsData(DataFormats.Html) || Clipboard.ContainsText()) return false;

        IntPtr ownerWindow = Native.GetClipboardOwner();
        if (ownerWindow == IntPtr.Zero) return true;
        Native.GetWindowThreadProcessId(ownerWindow, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false; // our own "Copy image"
        string exe = Path.GetFileNameWithoutExtension(WindowTracker.GetExePath(ownerWindow));
        if (exe.Length == 0) return true;
        owner = exe;
        return WindowTracker.ScreenCaptureTools.Contains(exe)
            || exe.Equals("svchost", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("explorer", StringComparison.OrdinalIgnoreCase)       // Print Screen without Snipping Tool
            || exe.Equals("rdpclip", StringComparison.OrdinalIgnoreCase);
    }

    private static BitmapSource? ClipboardPicture()
    {
        // Prefer the PNG copy many tools include: Windows' own bitmap copy can come back see-through.
        BitmapSource? image = null;
        if (Clipboard.GetData("PNG") is MemoryStream png)
        {
            try { image = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); } catch { }
        }
        image ??= Clipboard.GetImage();
        if (image is null || image.PixelWidth < 8 || image.PixelHeight < 8) return null;
        if (image.CanFreeze) image.Freeze();
        return image;
    }

    private string? _lastSnipHash;
    private int _lastSnipWidth, _lastSnipHeight;

    /// <summary>A new snip gets saved and shown; the same one again is ignored; an edit of the last one updates its file.</summary>
    private void HandleSnip(BitmapSource image)
    {
        string hash = PictureHash(image);
        if (hash == _lastSnipHash) return; // the same picture copied again

        bool isEdit = _snipSavedPath is not null && File.Exists(_snipSavedPath)
            && image.PixelWidth == _lastSnipWidth && image.PixelHeight == _lastSnipHeight
            && DateTime.Now - _lastSnipSaved < TimeSpan.FromMinutes(3);
        _lastSnipHash = hash;
        _lastSnipWidth = image.PixelWidth;
        _lastSnipHeight = image.PixelHeight;

        if (isEdit)
        {
            // Drawing on the snip in Snipping Tool updates the clipboard: keep the saved file up to date quietly.
            try
            {
                _captures.Ignore(_snipSavedPath!);
                WritePng(image, _snipSavedPath!, overwrite: true);
                _lastSnipSaved = DateTime.Now;
                App.LogNote($"updated {Path.GetFileName(_snipSavedPath)} with your edits");
            }
            catch (Exception ex)
            {
                App.LogNote($"couldn't update the snip: {ex.Message}");
            }
            return;
        }
        SaveSnip(image);
    }

    private static string PictureHash(BitmapSource image)
    {
        try
        {
            var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int stride = pixels.PixelWidth * 4;
            var bytes = new byte[stride * pixels.PixelHeight];
            pixels.CopyPixels(bytes, stride, 0);
            return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes));
        }
        catch
        {
            return Guid.NewGuid().ToString();
        }
    }

    private static void WritePng(BitmapSource image, string path, bool overwrite)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        encoder.Save(stream);
    }

    private void SaveSnip(BitmapSource image)
    {
        string? path = null;
        foreach (var folder in new[]
        {
            _captures.ScreenshotsFolder,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "Captures"),
        })
        {
            if (string.IsNullOrEmpty(folder)) continue;
            try
            {
                Directory.CreateDirectory(folder);
                string baseName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}";
                string target = Path.Combine(folder, baseName + ".png");
                for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{baseName} ({n}).png");
                _captures.Ignore(target); // we're reporting it ourselves
                WritePng(image, target, overwrite: false);
                path = target;
                break;
            }
            catch (Exception ex)
            {
                App.LogNote($"couldn't save the snip in {folder}: {ex.Message}");
            }
        }
        if (path is null) return;

        App.LogNote($"saved the copied snip as {path}");
        _lastSnipSaved = DateTime.Now;
        _snipSavedPath = path;
        OnCaptureReady(path, CaptureWatcher.Kind.Screenshot);
    }

    private void OnCaptureReady(string path, CaptureWatcher.Kind kind)
    {
        bool screenshot = kind == CaptureWatcher.Kind.Screenshot;
        if (screenshot)
        {
            // Snipping Tool saved its own copy just after we saved one from the clipboard: don't show it twice.
            if (path != _snipSavedPath && DateTime.Now - _lastSnipSaved < TimeSpan.FromSeconds(8))
            {
                App.LogNote($"screenshot {Path.GetFileName(path)} skipped (already saved from the clipboard)");
                return;
            }
            _lastScreenshotNoticed = DateTime.Now;
            App.LogNote($"screenshot noticed: {Path.GetFileName(path)}");
        }
        if (!screenshot) MarkDownloadsNew();
        if (screenshot ? !_settings.ScreenshotPopups : !_settings.DownloadPopups) return;

        bool onShelf = _settings.CapturesToShelf && _shelf.Add(new[] { path }) > 0;
        ImageSource? art = screenshot ? LoadThumbnail(path) : AppCatalog.GetShellImage(path, 96);
        string name = Path.GetFileName(path);

        if (screenshot)
        {
            ShowActivity("\uE722", art, "Screenshot", onShelf ? "On your Shelf · click to open" : "Saved to Screenshots", null, seconds: 7, width: 440);
            if (_activityShowing)
            {
                _activityFile = path;
                ActivityActions.Visibility = Visibility.Visible;
            }
        }
        else
            ShowActivity("\uE896", art, "Download finished", onShelf ? $"{name} · on your Shelf" : name, null);
        if (onShelf && _activityShowing) _activityOpensTab = "Shelf";
    }

    /// <summary>A small preview of an image file (read once, so the file isn't kept open).</summary>
    private static ImageSource? LoadThumbnail(string path)
    {
        try
        {
            // Read through a stream that doesn't mind the file being open elsewhere (Snipping Tool's editor).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    // ---- Tabs: which ones show, and in what order ----

    private static readonly string[] DefaultTabOrder = { "Today", "Controls", "Apps", "Clipboard", "Shelf", "Media", "Stats", "Power" };

    private RadioButton TabButtonFor(string tab) => tab switch
    {
        "Today" => TodayTabButton, "Controls" => ControlsTabButton, "Apps" => AppsTabButton, "Clipboard" => ClipboardTabButton,
        "Shelf" => ShelfTabButton, "Media" => MediaTabButton, "Stats" => StatsTabButton, _ => PowerTabButton,
    };

    /// <summary>The saved order, tidied: unknown names dropped and any missing tabs added at the end.</summary>
    private List<string> TabOrder()
    {
        var order = _settings.TabOrder.Where(t => DefaultTabOrder.Contains(t)).Distinct().ToList();
        order.AddRange(DefaultTabOrder.Where(t => !order.Contains(t)));
        return order;
    }

    private bool IsTabHidden(string tab) => _settings.HiddenTabs.Contains(tab);

    private string FirstVisibleTab() => TabOrder().FirstOrDefault(t => !IsTabHidden(t)) ?? "Today";

    /// <summary>Puts the tab buttons along the top in the chosen order and hides the hidden ones.</summary>
    private void ApplyTabLayout()
    {
        TabBar.Children.Clear();
        foreach (var tab in TabOrder())
        {
            var button = TabButtonFor(tab);
            button.Visibility = IsTabHidden(tab) ? Visibility.Collapsed : Visibility.Visible;
            TabBar.Children.Add(button);
        }
    }

    /// <summary>Settings › Tabs: one row per tab, with up/down arrows and a show switch.</summary>
    private void BuildTabsList()
    {
        TabsList.Children.Clear();
        var order = TabOrder();
        int visibleCount = order.Count(t => !IsTabHidden(t));
        for (int i = 0; i < order.Count; i++)
        {
            string tab = order[i];
            int index = i;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            var show = new CheckBox
            {
                Style = (Style)FindResource("Switch"),
                IsChecked = !IsTabHidden(tab),
                VerticalAlignment = VerticalAlignment.Center,
                // The last visible tab can't be hidden.
                IsEnabled = IsTabHidden(tab) || visibleCount > 1,
                ToolTip = "Show this tab",
            };
            show.Click += (_, _) =>
            {
                if (show.IsChecked == true) _settings.HiddenTabs.Remove(tab);
                else if (!_settings.HiddenTabs.Contains(tab)) _settings.HiddenTabs.Add(tab);
                SaveTabLayout();
            };
            DockPanel.SetDock(show, Dock.Right);
            row.Children.Add(show);

            Button Arrow(string glyph, string tip, int move, bool enabled)
            {
                var b = new Button
                {
                    Style = (Style)FindResource("IslandButton"),
                    Width = 28, Height = 28, Margin = new Thickness(0, 0, 2, 0),
                    IsEnabled = enabled, Opacity = enabled ? 1 : 0.25, ToolTip = tip,
                    Content = new TextBlock { Style = (Style)FindResource("Glyph"), FontSize = 11, Text = glyph },
                };
                b.Click += (_, _) =>
                {
                    var list = TabOrder();
                    (list[index], list[index + move]) = (list[index + move], list[index]);
                    _settings.TabOrder = list;
                    SaveTabLayout();
                };
                DockPanel.SetDock(b, Dock.Right);
                return b;
            }
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
            arrows.Children.Add(Arrow("\uE70E", "Move earlier (further left)", -1, i > 0));
            arrows.Children.Add(Arrow("\uE70D", "Move later (further right)", +1, i < order.Count - 1));
            DockPanel.SetDock(arrows, Dock.Right);
            row.Children.Add(arrows);

            row.Children.Add(new TextBlock
            {
                Text = tab,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = IsTabHidden(tab) ? 0.45 : 1,
            });
            TabsList.Children.Add(row);
        }
    }

    private void SaveTabLayout()
    {
        _settings.Save();
        ApplyTabLayout();
        BuildTabsList();
        ResizeExpandedIfNeeded();
    }

    // ---- Updates from GitHub ----

    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private UpdateInfo? _availableUpdate;
    private string? _downloadedUpdate;
    private bool _updateBusy;
    private bool _toldAboutUpdate;
    private string _updateMessage = "Checks GitHub for new versions";

    /// <summary>Starts the regular checks: the first a minute after starting, then every 6 hours.</summary>
    private void StartUpdateChecks()
    {
        // Finish off a previous update: remove the old copy, and say so if we've just been updated.
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(10)); UpdateService.CleanUpOldVersion(); });
        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == UpdateService.UpdatedArgument))
        {
            var justUpdated = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            justUpdated.Tick += (_, _) =>
            {
                justUpdated.Stop();
                ShowActivity("\uE895", null, $"Updated to {UpdateService.Current.ToString(3)}", "Dynamic Island is up to date", null);
            };
            justUpdated.Start();
        }

        _updateTimer.Tick += (_, _) => CheckForUpdate(manual: false);
        _updateTimer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        first.Tick += (_, _) => { first.Stop(); CheckForUpdate(manual: false); };
        first.Start();
    }

    private async void CheckForUpdate(bool manual)
    {
        if (_updateBusy || UpdateService.Applied) return;
        _updateBusy = true;
        SetUpdateMessage("Checking for updates…");
        try
        {
            var update = await UpdateService.CheckAsync();
            _availableUpdate = update;
            if (update is null)
            {
                SetUpdateMessage($"You're on the latest version ({UpdateService.Current.ToString(3)}). Last checked {DateTime.Now:t}");
            }
            else if (_settings.AutoUpdate && UpdateService.CanUpdateInPlace)
            {
                _updateBusy = false;
                InstallUpdate(update, automatic: true);
                return;
            }
            else
            {
                SetUpdateMessage(UpdateService.CanUpdateInPlace
                    ? $"Version {update.Version.ToString(3)} is available"
                    : $"Version {update.Version.ToString(3)} is available. This folder can't be written to, so download it from the project page");
                if (!_toldAboutUpdate && !manual)
                {
                    _toldAboutUpdate = true;
                    ShowActivity("\uE896", null, $"Version {update.Version.ToString(3)} is available", "Settings › About to update", null);
                }
            }
        }
        catch (Exception ex)
        {
            SetUpdateMessage(manual ? "Couldn't reach GitHub just now. Try again later" : _updateMessage);
            App.LogNote($"update check failed: {ex.Message}");
        }
        finally
        {
            _updateBusy = false;
            ShowUpdateState();
        }
    }

    /// <summary>Downloads the update, then installs it (restarting the island) as soon as you're not using it.</summary>
    private async void InstallUpdate(UpdateInfo update, bool automatic)
    {
        if (_updateBusy || UpdateService.Applied) return;
        if (!UpdateService.CanUpdateInPlace)
        {
            SystemActions.Open(update.PageUrl);
            return;
        }
        _updateBusy = true;
        try
        {
            if (_downloadedUpdate is null || !File.Exists(_downloadedUpdate))
            {
                SetUpdateMessage($"Downloading version {update.Version.ToString(3)}…");
                _downloadedUpdate = await UpdateService.DownloadAsync(update);
            }
            SetUpdateMessage($"Version {update.Version.ToString(3)} is ready; installing when the island is closed");

            // Wait until the island isn't being used (closed, no menu), then swap and restart.
            while (_expanded || _menuOpen || _peeking)
                await Task.Delay(TimeSpan.FromSeconds(automatic ? 5 : 1));
            if (!UpdateService.ApplyAndRestart(_downloadedUpdate, update.Version))
            {
                _downloadedUpdate = null;
                SetUpdateMessage("The update couldn't be installed. Details are in the error log");
            }
        }
        catch (Exception ex)
        {
            _downloadedUpdate = null;
            App.LogNote($"update download failed: {ex.Message}");
            SetUpdateMessage("The update couldn't be downloaded. It'll try again later");
        }
        finally
        {
            _updateBusy = false;
            ShowUpdateState();
        }
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not null)
        {
            Collapse(); // it installs as soon as the island is closed
            InstallUpdate(_availableUpdate, automatic: false);
        }
        else
        {
            CheckForUpdate(manual: true);
        }
    }

    private void SetUpdateMessage(string message)
    {
        _updateMessage = message;
        ShowUpdateState();
    }

    private void ShowUpdateState()
    {
        UpdateStatusText.Text = _updateMessage;
        CheckUpdatesText.Text = _availableUpdate is not null
            ? (UpdateService.CanUpdateInPlace ? "Update now" : "Open download page")
            : "Check now";
        CheckUpdatesButton.IsEnabled = !_updateBusy && !UpdateService.Applied;
    }

    // ---- Settings sections and the About page ----

    private void SettingsSection_Checked(object sender, RoutedEventArgs e)
    {
        if (SettingsAppearance is null || SettingsTabs is null) return; // still being built
        if (sender == SettingsNavTabs) BuildTabsList();
        foreach (var (nav, panel) in new (RadioButton, FrameworkElement)[]
                 {
                     (SettingsNavAppearance, SettingsAppearance), (SettingsNavClock, SettingsClock),
                     (SettingsNavBehaviour, SettingsBehaviour), (SettingsNavStorage, SettingsStorage),
                     (SettingsNavPopups, SettingsPopups), (SettingsNavTabs, SettingsTabs),
                     (SettingsNavWeather, SettingsWeather), (SettingsNavAbout, SettingsAbout),
                 })
            panel.Visibility = nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (_currentTab == "Settings")
        {
            ResizeExpandedIfNeeded();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ResizeExpandedIfNeeded));
        }
    }

    /// <summary>"Set your town" on the Today tab: straight to the Weather settings.</summary>
    private void WeatherSetup_Click(object sender, RoutedEventArgs e)
    {
        SettingsNavWeather.IsChecked = true;
        Settings_Click(sender, e);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.GetDirectoryName(App.CrashLogPath)!;
        Directory.CreateDirectory(folder);
        Collapse();
        SystemActions.Open(folder);
    }

    private void OpenProjectPage_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        SystemActions.Open("https://github.com/samround73-cpu/dynamic-island-windows");
    }

    /// <summary>
    /// Removes everything the island has added to this PC (startup entry, Start menu shortcut, settings and logs,
    /// and the files .NET unpacks to run it), then quits with the normal taskbar back. Only the exe itself is left.
    /// </summary>
    private void RemoveFromPc_Click(object sender, RoutedEventArgs e)
    {
        _menuOpen = true; // keep the island open while the question is up
        var answer = MessageBox.Show(
            "This removes Dynamic Island's startup entry, Start menu shortcut, settings, pinned clips and logs, " +
            "then closes it and brings your normal taskbar back.\n\n" +
            "Afterwards you can delete DynamicIsland.exe itself.\n\nRemove it now?",
            "Remove Dynamic Island", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        _menuOpen = false;
        if (answer != MessageBoxResult.Yes) return;

        try { StartupManager.SetEnabled(false); } catch { }
        StartMenuShortcut.Remove();

        // The settings folder (and the .NET files unpacked to run the exe) can only go once the island has closed,
        // so a tiny hidden command waits a few seconds and then deletes them.
        string data = Path.GetDirectoryName(App.CrashLogPath)!;
        string unpacked = Path.Combine(Path.GetTempPath(), ".net", "DynamicIsland");
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping -n 5 127.0.0.1 >nul & rmdir /s /q \"{data}\" & rmdir /s /q \"{unpacked}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { }

        _settings.DoNotSave = true; // don't recreate the settings file while closing
        Close();
    }

    private void StartupFix_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        SystemActions.Open("ms-settings:startupapps");
    }

    // ---- Start with Windows (checking it runs a small Windows tool, so it's done off the UI thread) ----

    private bool _startupEnabled;

    private async void RefreshStartupStatus()
    {
        StartupManager.Method method;
        bool blocked;
        try
        {
            (method, blocked) = await Task.Run(() => (StartupManager.Current, StartupManager.BlockedByWindows));
        }
        catch { return; }

        _startupEnabled = method != StartupManager.Method.Off;
        _loadingSettings = true;
        StartWithWindowsSwitch.IsChecked = _startupEnabled;
        _loadingSettings = false;
        StartWithWindowsMenuItem.IsChecked = _startupEnabled;

        StartupStatusText.Text = method switch
        {
            StartupManager.Method.RunEntry when blocked => "Windows has switched this off in its Startup apps list",
            StartupManager.Method.RunEntry => "Opens when you sign in (Windows didn't allow reopening on reconnect)",
            _ => "Opens when you sign in, and again when you reconnect or unlock if it isn't running",
        };
        StartupStatusText.Foreground = blocked ? new SolidColorBrush(Theme.Light ? Color.FromRgb(0xC0, 0x39, 0x2B) : Color.FromRgb(0xFF, 0x9A, 0x94)) : (Brush)FindResource("SubtleText");
        StartupFixButton.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SetStartup(bool enabled)
    {
        _startupEnabled = enabled;
        StartWithWindowsMenuItem.IsChecked = enabled;
        try { await Task.Run(() => StartupManager.SetEnabled(enabled)); } catch { }
        RefreshStartupStatus();
    }

    private void ApplyBehaviourSettings()
    {
        _peekOpenTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.PeekDelayMs, 100, 1500));
        SetEqualizer(_media.IsPlaying);
    }

    private void PlaceBox_TextChanged(object sender, TextChangedEventArgs e) =>
        PlacePlaceholder.Visibility = PlaceBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void PlaceBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SavePlace_Click(sender, e);
    }

    /// <summary>Looks up the town you typed (or switches back to Windows' location if the box is empty).</summary>
    private async void SavePlace_Click(object sender, RoutedEventArgs e)
    {
        string query = PlaceBox.Text.Trim();
        if (query.Length == 0)
        {
            _settings.WeatherPlace = "";
            _settings.WeatherLatitude = null;
            _settings.WeatherLongitude = null;
            _settings.Save();
            PlaceStatus.Text = "Using Windows' location.";
            RefreshWeather(force: true);
            return;
        }

        PlaceStatus.Text = "Looking that up…";
        var found = await WeatherService.FindPlaceAsync(query);
        if (found is null)
        {
            PlaceStatus.Text = $"Couldn't find \"{query}\". Try adding the country, e.g. \"Gravelines, France\".";
            return;
        }
        _settings.WeatherPlace = found.Value.Name;
        _settings.WeatherLatitude = found.Value.Latitude;
        _settings.WeatherLongitude = found.Value.Longitude;
        _settings.Save();
        PlaceBox.Text = found.Value.Name;
        PlaceStatus.Text = $"Using {found.Value.Name}.";
        RefreshWeather(force: true);
    }
}
