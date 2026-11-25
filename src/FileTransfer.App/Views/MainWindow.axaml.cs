using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FileTransfer.App.Models;
using FileTransfer.App.ViewModels;

namespace FileTransfer.App.Views;

public partial class MainWindow : Window
{
    private const string LocalDragToken = "FT_LOCAL_DRAG";
    private const string RemoteDragToken = "FT_REMOTE_DRAG";

    private readonly GridNavigationHelper _gridNav = new();
    private Point _dragStartPoint;
    private bool _isTrackingLocalDrag;
    private bool _isTrackingRemoteDrag;
    private bool _isDragging;
    private LocalFileItem? _dragCandidateLocalItem;
    private RemoteFileItem? _dragCandidateRemoteItem;
    private IReadOnlyList<LocalFileItem>? _dragSnapshotLocalItems;
    private IReadOnlyList<RemoteFileItem>? _dragSnapshotRemoteItems;
    private IReadOnlyList<string>? _pendingLocalDragPaths;
    private IReadOnlyList<RemoteFileItem>? _pendingRemoteDragItems;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        WireDragAndDrop();
        // Use Tunnel routing so Window & DataGrid shortcuts run before DataGrid's built-in Enter/Up handling.
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        LocalFilesGrid.AddHandler(KeyDownEvent, LocalFilesGrid_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        RemoteFilesGrid.AddHandler(KeyDownEvent, RemoteFilesGrid_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.RefreshLocalDirectoryCommand.Execute(null);
            }
        };
    }

    private async void LoginPanel_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm && !vm.IsConnected && !vm.IsBusy)
        {
            e.Handled = true;
            await vm.ToggleConnectionCommand.ExecuteAsync(null);
        }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var ctrlOrCmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (ctrlOrCmd && e.Key == Key.OemComma)
        {
            e.Handled = true;
            await WindowDialogHelper.ShowSettingsDialogAsync(this, vm);
            return;
        }

        if (!vm.IsConnected)
        {
            return;
        }

        if (e.Key == Key.F1)
        {
            e.Handled = true;
            await WindowDialogHelper.ShowHelpDialogAsync(this);
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            vm.RefreshLocalDirectoryCommand.Execute(null);
            if (vm.CanOperateRemote)
            {
                await vm.LoadDirectoryCommand.ExecuteAsync(null);
            }
            return;
        }

        if (ctrlOrCmd && shift && e.Key == Key.N)
        {
            e.Handled = true;
            if (LocalFilesGrid.IsKeyboardFocusWithin || LocalPathBox.IsKeyboardFocusWithin)
            {
                await vm.CreateLocalDirectoryCommand.ExecuteAsync(null);
            }
            else if (vm.CanOperateRemote)
            {
                await vm.CreateRemoteDirectoryCommand.ExecuteAsync(null);
            }
        }
        else if (ctrlOrCmd && shift && e.Key == Key.C)
        {
            e.Handled = true;
            if (LocalFilesGrid.IsKeyboardFocusWithin || LocalPathBox.IsKeyboardFocusWithin || LocalFilterBox.IsKeyboardFocusWithin)
            {
                await vm.CopySelectedLocalPathCommand.ExecuteAsync(null);
            }
            else
            {
                await vm.CopySelectedRemotePathCommand.ExecuteAsync(null);
            }
        }
        else if (alt && e.Key == Key.Up)
        {
            e.Handled = true;
            if (LocalFilesGrid.IsKeyboardFocusWithin || LocalPathBox.IsKeyboardFocusWithin)
            {
                vm.GoLocalParentDirectoryCommand.Execute(null);
            }
            else if (vm.CanOperateRemote)
            {
                await vm.GoParentDirectoryCommand.ExecuteAsync(null);
            }
        }
        else if (ctrlOrCmd && e.Key == Key.U && vm.CanOperateRemote)
        {
            e.Handled = true;
            await vm.UploadSelectedLocalFilesCommand.ExecuteAsync(null);
        }
        else if (ctrlOrCmd && e.Key == Key.D && vm.CanOperateRemote)
        {
            e.Handled = true;
            await vm.DownloadSelectedToCurrentLocalDirCommand.ExecuteAsync(null);
        }
        else if (ctrlOrCmd && e.Key == Key.F)
        {
            e.Handled = true;
            if (LocalFilesGrid.IsKeyboardFocusWithin || LocalPathBox.IsKeyboardFocusWithin || LocalFilterBox.IsKeyboardFocusWithin)
            {
                LocalFilterBox.Focus();
                LocalFilterBox.SelectAll();
            }
            else
            {
                RemoteFilterBox.Focus();
                RemoteFilterBox.SelectAll();
            }
        }
        else if (ctrlOrCmd && e.Key == Key.L)
        {
            e.Handled = true;
            if (LocalFilesGrid.IsKeyboardFocusWithin || LocalPathBox.IsKeyboardFocusWithin)
            {
                LocalPathBox.Focus();
                LocalPathBox.SelectAll();
            }
            else
            {
                RemotePathBox.Focus();
                RemotePathBox.SelectAll();
            }
        }
        else if (alt && e.Key == Key.D1)
        {
            e.Handled = true;
            LocalFilesGrid.Focus();
        }
        else if (alt && e.Key == Key.D2)
        {
            e.Handled = true;
            RemoteFilesGrid.Focus();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        vm.PickSaveFileAsync = async suggestedFileName =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "选择本地保存位置",
                SuggestedFileName = suggestedFileName
            });
            return file?.TryGetLocalPath();
        };

        vm.PickFolderAsync = async title =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        };

        vm.PickOpenFilesAsync = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要上传的本地文件（支持多选）",
                AllowMultiple = true
            });
            return files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Cast<string>()
                .ToList();
        };

        vm.ConfirmDialogAsync = (title, message, confirmText, isDanger) =>
            WindowDialogHelper.ShowConfirmDialogAsync(this, title, message, confirmText, isDanger);
        vm.ConfirmOverwriteDialogAsync = (title, message) =>
            WindowDialogHelper.ShowOverwriteDialogAsync(this, title, message);
        vm.PromptInputDialogAsync = (title, label) =>
            WindowDialogHelper.ShowPromptInputDialogAsync(this, title, label);
        vm.PromptInputWithDefaultDialogAsync = (title, label, initial) =>
            WindowDialogHelper.ShowPromptInputDialogAsync(this, title, label, initial);
        vm.CopyToClipboardAsync = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        };
        vm.ApplyThemeAction = App.ApplyThemeMode;
    }

    private void WireDragAndDrop()
    {
        LocalFilesGrid.AddHandler(PointerPressedEvent, OnLocalGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        LocalFilesGrid.AddHandler(PointerMovedEvent, OnLocalGridPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        LocalFilesGrid.AddHandler(PointerReleasedEvent, (_, _) => _isTrackingLocalDrag = false, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        LocalPaneDropTarget.AddHandler(DragDrop.DragOverEvent, OnLocalGridDragOver);
        LocalPaneDropTarget.AddHandler(DragDrop.DropEvent, OnLocalGridDrop);
        LocalFilesGrid.AddHandler(DragDrop.DragOverEvent, OnLocalGridDragOver);
        LocalFilesGrid.AddHandler(DragDrop.DropEvent, OnLocalGridDrop);

        RemoteFilesGrid.AddHandler(PointerPressedEvent, OnRemoteGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        RemoteFilesGrid.AddHandler(PointerMovedEvent, OnRemoteGridPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        RemoteFilesGrid.AddHandler(PointerReleasedEvent, (_, _) => _isTrackingRemoteDrag = false, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        RemotePaneDropTarget.AddHandler(DragDrop.DragOverEvent, OnRemoteGridDragOver);
        RemotePaneDropTarget.AddHandler(DragDrop.DropEvent, OnRemoteGridDrop);
        RemoteFilesGrid.AddHandler(DragDrop.DragOverEvent, OnRemoteGridDragOver);
        RemoteFilesGrid.AddHandler(DragDrop.DropEvent, OnRemoteGridDrop);
    }


    private void OnLocalGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (GridNavigationHelper.IsInsideControl<ScrollBar>(e.Source) || GridNavigationHelper.IsInsideControl<DataGridColumnHeader>(e.Source))
        {
            _isTrackingLocalDrag = false;
            return;
        }

        if (e.GetCurrentPoint(LocalFilesGrid).Properties.IsLeftButtonPressed && DataContext is MainWindowViewModel vm)
        {
            _dragCandidateLocalItem = GridNavigationHelper.FindDataContext<LocalFileItem>(e.Source);
            if (_dragCandidateLocalItem is null)
            {
                _isTrackingLocalDrag = false;
                return;
            }

            _dragStartPoint = e.GetPosition(LocalFilesGrid);
            _isTrackingLocalDrag = true;
            // Snapshot multi-selection before DataGrid collapses it on pointer down.
            var currentSel = vm.GetSelectedLocalItems();
            _dragSnapshotLocalItems = currentSel.Contains(_dragCandidateLocalItem)
                ? currentSel
                : new[] { _dragCandidateLocalItem };
        }
    }

    private async void OnLocalGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isTrackingLocalDrag || _isDragging || DataContext is not MainWindowViewModel vm || !vm.CanOperateRemote)
        {
            return;
        }

        if (!e.GetCurrentPoint(LocalFilesGrid).Properties.IsLeftButtonPressed)
        {
            _isTrackingLocalDrag = false;
            return;
        }

        var current = e.GetPosition(LocalFilesGrid);
        if (Math.Abs(current.X - _dragStartPoint.X) < 6 && Math.Abs(current.Y - _dragStartPoint.Y) < 6)
        {
            return;
        }

        _isTrackingLocalDrag = false;
        var selected = _dragSnapshotLocalItems is { Count: > 0 }
            ? _dragSnapshotLocalItems
            : vm.GetSelectedLocalItems();

        if (selected.Count == 0)
        {
            return;
        }

        RestoreGridSelection(LocalFilesGrid, selected);
        _pendingLocalDragPaths = selected.Select(f => f.FullPath).ToList();
        var data = new DataObject();
        data.Set(DataFormats.Text, LocalDragToken);
        _isDragging = true;
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        finally
        {
            _isDragging = false;
            _pendingLocalDragPaths = null;
            _dragSnapshotLocalItems = null;
        }
    }

    private void OnRemoteGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (GridNavigationHelper.IsInsideControl<ScrollBar>(e.Source) || GridNavigationHelper.IsInsideControl<DataGridColumnHeader>(e.Source))
        {
            _isTrackingRemoteDrag = false;
            return;
        }

        if (e.GetCurrentPoint(RemoteFilesGrid).Properties.IsLeftButtonPressed && DataContext is MainWindowViewModel vm)
        {
            _dragCandidateRemoteItem = GridNavigationHelper.FindDataContext<RemoteFileItem>(e.Source);
            if (_dragCandidateRemoteItem is null)
            {
                _isTrackingRemoteDrag = false;
                return;
            }

            _dragStartPoint = e.GetPosition(RemoteFilesGrid);
            _isTrackingRemoteDrag = true;
            var currentSel = vm.GetSelectedRemoteItems();
            _dragSnapshotRemoteItems = currentSel.Contains(_dragCandidateRemoteItem)
                ? currentSel
                : new[] { _dragCandidateRemoteItem };
        }
    }

    private async void OnRemoteGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isTrackingRemoteDrag || _isDragging || DataContext is not MainWindowViewModel vm || !vm.CanOperateRemote)
        {
            return;
        }

        if (!e.GetCurrentPoint(RemoteFilesGrid).Properties.IsLeftButtonPressed)
        {
            _isTrackingRemoteDrag = false;
            return;
        }

        var current = e.GetPosition(RemoteFilesGrid);
        if (Math.Abs(current.X - _dragStartPoint.X) < 6 && Math.Abs(current.Y - _dragStartPoint.Y) < 6)
        {
            return;
        }

        _isTrackingRemoteDrag = false;
        var selected = _dragSnapshotRemoteItems is { Count: > 0 }
            ? _dragSnapshotRemoteItems
            : vm.GetSelectedRemoteItems();

        if (selected.Count == 0)
        {
            return;
        }

        RestoreGridSelection(RemoteFilesGrid, selected);
        _pendingRemoteDragItems = selected.ToList();
        var data = new DataObject();
        data.Set(DataFormats.Text, RemoteDragToken);
        _isDragging = true;
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        finally
        {
            _isDragging = false;
            _pendingRemoteDragItems = null;
            _dragSnapshotRemoteItems = null;
        }
    }

    private static void RestoreGridSelection<T>(DataGrid grid, IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            return;
        }

        grid.SelectedItems.Clear();
        foreach (var item in items)
        {
            grid.SelectedItems.Add(item);
        }
    }

    private void OnLocalGridDragOver(object? sender, DragEventArgs e)
    {
        var isRemoteDrag = _pendingRemoteDragItems is { Count: > 0 } &&
                           string.Equals(e.Data.GetText(), RemoteDragToken, StringComparison.Ordinal);
        e.DragEffects = isRemoteDrag ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnLocalGridDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainWindowViewModel vm || !vm.CanOperateRemote)
        {
            return;
        }

        if (_pendingRemoteDragItems is { Count: > 0 } &&
            string.Equals(e.Data.GetText(), RemoteDragToken, StringComparison.Ordinal))
        {
            var items = _pendingRemoteDragItems.ToList();
            _pendingRemoteDragItems = null;
            await vm.DownloadRemoteFilesToCurrentLocalDirAsync(items);
        }
    }

    private void OnRemoteGridDragOver(object? sender, DragEventArgs e)
    {
        var isLocalDrag = _pendingLocalDragPaths is { Count: > 0 } &&
                          string.Equals(e.Data.GetText(), LocalDragToken, StringComparison.Ordinal);
        if (isLocalDrag || e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void OnRemoteGridDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainWindowViewModel vm || !vm.CanOperateRemote)
        {
            return;
        }

        if (_pendingLocalDragPaths is { Count: > 0 } &&
            string.Equals(e.Data.GetText(), LocalDragToken, StringComparison.Ordinal))
        {
            var paths = _pendingLocalDragPaths.ToList();
            _pendingLocalDragPaths = null;
            await vm.UploadLocalFilePathsAsync(paths);
            return;
        }

        var droppedItems = e.Data.GetFiles();
        if (droppedItems is not null)
        {
            var paths = droppedItems
                .Select(i => i.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
                .Cast<string>()
                .ToList();

            if (paths.Count > 0)
            {
                await vm.UploadLocalFilePathsAsync(paths);
            }
        }
    }

    private async void SettingsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await WindowDialogHelper.ShowSettingsDialogAsync(this, vm);
        }
    }

    private void FileNameText_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not TextBlock tb)
        {
            return;
        }

        var isTruncated = tb.TextLayout?.TextLines.Any(l => l.HasCollapsed) == true;
        ToolTip.SetTip(tb, isTruncated
            ? new TextBlock
            {
                Text = tb.Text,
                TextWrapping = Avalonia.Media.TextWrapping.WrapWithOverflow,
                MaxWidth = 460
            }
            : null);
    }

    private void LocalFilterBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            vm.LocalFilterText = string.Empty;
            LocalFilesGrid.Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            GridNavigationHelper.FocusGridWithSelection(LocalFilesGrid, vm.LocalFiles);
            e.Handled = true;
        }
    }

    private void RemoteFilterBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            vm.RemoteFilterText = string.Empty;
            RemoteFilesGrid.Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            GridNavigationHelper.FocusGridWithSelection(RemoteFilesGrid, vm.RemoteFiles);
            e.Handled = true;
        }
    }


    private void LocalPathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.RefreshLocalDirectoryCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            GridNavigationHelper.FocusGridWithSelection(LocalFilesGrid, vm.LocalFiles);
        }
    }

    private void LocalFilesGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            GridNavigationHelper.IsInsideControl<DataGridColumnHeader>(e.Source) ||
            GridNavigationHelper.IsInsideControl<ScrollBar>(e.Source))
        {
            return;
        }

        var rowItem = GridNavigationHelper.FindDataContext<LocalFileItem>(e.Source);
        if (rowItem is null)
        {
            vm.GoLocalParentDirectoryCommand.Execute(null);
            return;
        }

        if (rowItem.IsDirectory)
        {
            vm.EnterLocalDirectory(rowItem);
        }
        else
        {
            vm.OpenLocalFile(rowItem);
        }
    }

    private async void LocalFilesGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            GridNavigationHelper.FocusGridWithSelection(RemoteFilesGrid, vm.RemoteFiles);
            return;
        }

        if (GridNavigationHelper.IsSlashSearchKey(e))
        {
            e.Handled = true;
            LocalFilterBox.Focus();
            LocalFilterBox.SelectAll();
            return;
        }

        if (vm.VimModeEnabled && await GridNavigationHelper.TryHandleVimNavigationAsync(
                e,
                LocalFilesGrid,
                vm.LocalFiles,
                () =>
                {
                    vm.GoLocalParentDirectoryCommand.Execute(null);
                    return Task.CompletedTask;
                },
                item =>
                {
                    if (item.IsDirectory)
                    {
                        vm.EnterLocalDirectory(item);
                    }
                    else
                    {
                        vm.OpenLocalFile(item);
                    }
                    return Task.CompletedTask;
                }))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back)
        {
            e.Handled = true;
            vm.GoLocalParentDirectoryCommand.Execute(null);
        }
        else if (e.Key == Key.Enter && vm.SelectedLocalFile is not null)
        {
            e.Handled = true;
            if (vm.SelectedLocalFile.IsDirectory)
            {
                vm.EnterLocalDirectory(vm.SelectedLocalFile);
            }
            else
            {
                vm.OpenLocalFile(vm.SelectedLocalFile);
            }
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            await vm.DeleteSelectedLocalFilesCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.F2)
        {
            e.Handled = true;
            await vm.RenameSelectedLocalFileCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.F7)
        {
            e.Handled = true;
            await vm.CreateLocalDirectoryCommand.ExecuteAsync(null);
        }
        else if (!vm.VimModeEnabled && GridNavigationHelper.TryGetTypeAheadChar(e, out var ch))
        {
            e.Handled = true;
            _gridNav.HandleTypeAhead(LocalFilesGrid, vm.LocalFiles, f => f.Name, ch, vm);
        }
    }

    private void LocalFilesGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is DataGrid grid)
        {
            vm.UpdateSelectedLocalFiles(grid.SelectedItems.OfType<LocalFileItem>());
        }
    }

    private async void RemotePathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await vm.LoadDirectoryCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            GridNavigationHelper.FocusGridWithSelection(RemoteFilesGrid, vm.RemoteFiles);
        }
    }

    private async void RemoteFilesGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            !vm.CanOperateRemote ||
            GridNavigationHelper.IsInsideControl<DataGridColumnHeader>(e.Source) ||
            GridNavigationHelper.IsInsideControl<ScrollBar>(e.Source))
        {
            return;
        }

        var rowItem = GridNavigationHelper.FindDataContext<RemoteFileItem>(e.Source);
        if (rowItem is null)
        {
            await vm.GoParentDirectoryCommand.ExecuteAsync(null);
            return;
        }

        await ActivateRemoteItemAsync(vm, rowItem);
    }

    private async Task ActivateRemoteSelectionAsync(MainWindowViewModel vm)
    {
        var selected = vm.GetSelectedRemoteItems();
        if (selected.Count == 0)
        {
            return;
        }

        if (selected.Count == 1)
        {
            await ActivateRemoteItemAsync(vm, selected[0]);
            return;
        }

        var totalBytes = selected.Where(f => !f.IsDirectory).Sum(f => f.SizeBytes);
        var hasDirs = selected.Any(f => f.IsDirectory);
        var sizePart = totalBytes > 0
            ? (hasDirs ? $"（含文件夹，已知文件总大小 {FormatSize(totalBytes)}）" : $"（总大小 {FormatSize(totalBytes)}）")
            : "（仅文件夹）";

        var previewLines = selected.Take(3)
            .Select(f => f.IsDirectory ? $"• {f.Name}（文件夹）" : $"• {f.Name}（{f.DisplaySize}）")
            .ToList();
        if (selected.Count > 3)
        {
            previewLines.Add($"……等共 {selected.Count} 项");
        }

        var message = $"确定将选中的 {selected.Count} 个项目{sizePart}下载到左侧当前本地目录吗？\n\n{string.Join("\n", previewLines)}";
        var confirmed = await WindowDialogHelper.ShowConfirmDialogAsync(
            this,
            "确认批量下载",
            message,
            "立即下载",
            false);
        if (confirmed)
        {
            await vm.DownloadSelectedToCurrentLocalDirCommand.ExecuteAsync(null);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }

    private async Task ActivateRemoteItemAsync(MainWindowViewModel vm, RemoteFileItem item)
    {
        if (item.IsDirectory)
        {
            await vm.EnterDirectoryAsync(item);
            return;
        }

        var confirmed = await WindowDialogHelper.ShowConfirmDialogAsync(this, 
            "确认下载文件",
            $"确定将「{item.Name}」（{item.DisplaySize}）下载到左侧当前本地目录吗？",
            "立即下载",
            false);
        if (confirmed)
        {
            await vm.DownloadSelectedToCurrentLocalDirCommand.ExecuteAsync(null);
        }
    }

    private async void RemoteFilesGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            GridNavigationHelper.FocusGridWithSelection(LocalFilesGrid, vm.LocalFiles);
            return;
        }

        if (GridNavigationHelper.IsSlashSearchKey(e))
        {
            e.Handled = true;
            RemoteFilterBox.Focus();
            RemoteFilterBox.SelectAll();
            return;
        }

        if (!vm.CanOperateRemote)
        {
            return;
        }

        if (vm.VimModeEnabled && await GridNavigationHelper.TryHandleVimNavigationAsync(
                e,
                RemoteFilesGrid,
                vm.RemoteFiles,
                () => vm.GoParentDirectoryCommand.ExecuteAsync(null),
                item => ActivateRemoteItemAsync(vm, item)))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back)
        {
            e.Handled = true;
            await vm.GoParentDirectoryCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.Enter && (vm.SelectedFile is not null || vm.SelectedFiles.Count > 0))
        {
            e.Handled = true;
            await ActivateRemoteSelectionAsync(vm);
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            await vm.DeleteSelectedRemoteFilesCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.F2)
        {
            e.Handled = true;
            await vm.RenameSelectedRemoteFileCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.F7)
        {
            e.Handled = true;
            await vm.CreateRemoteDirectoryCommand.ExecuteAsync(null);
        }
        else if (!vm.VimModeEnabled && GridNavigationHelper.TryGetTypeAheadChar(e, out var ch))
        {
            e.Handled = true;
            _gridNav.HandleTypeAhead(RemoteFilesGrid, vm.RemoteFiles, f => f.Name, ch, vm);
        }
    }


    private void RemoteFilesGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is DataGrid grid)
        {
            vm.UpdateSelectedFiles(grid.SelectedItems.OfType<RemoteFileItem>());
        }
    }
}
