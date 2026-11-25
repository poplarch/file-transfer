using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTransfer.App.Models;
using FileTransfer.App.Services;
using Microsoft.VisualBasic.FileIO;

namespace FileTransfer.App.ViewModels;

public partial class MainWindowViewModel
{
    partial void OnRemoteFilterTextChanged(string value)
    {
        ApplyRemoteFilter();
    }


    private void ApplyRemoteFilter()
    {
        var filter = RemoteFilterText?.Trim();
        RemoteFiles.Clear();
        var source = string.IsNullOrEmpty(filter)
            ? _allRemoteFiles
            : _allRemoteFiles.Where(f => PinyinSearchHelper.MatchesFilter(f.Name, filter));

        foreach (var item in source)
        {
            RemoteFiles.Add(item);
        }

        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateMessage));
        RefreshRemoteSummary();
    }


    partial void OnRemotePathChanged(string value)
    {
        if (_suppressAutoLoad || !IsConnected)
        {
            return;
        }

        _pathDebounceCts?.Cancel();
        _pathDebounceCts?.Dispose();
        _pathDebounceCts = new CancellationTokenSource();
        var token = _pathDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(750, token);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (IsConnected && !IsBusy)
                    {
                        await LoadDirectoryAsync();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Debounced by newer input.
            }
        }, token);
    }


    [RelayCommand]
    private async Task LoadDirectoryAsync()
    {
        if (!IsConnected || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await LoadDirectoryCoreAsync(RemotePath);
        }
        finally
        {
            IsTransferring = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task GoParentDirectoryAsync()
    {
        if (!IsConnected || IsBusy)
        {
            return;
        }

        var current = SftpListingParser.NormalizeRemoteDirectory(RemotePath);
        if (current == "/")
        {
            return;
        }

        var idx = current.LastIndexOf('/');
        var parent = idx <= 0 ? "/" : current[..idx];
        SetRemotePathWithoutAutoLoad(parent);
        await LoadDirectoryAsync();
    }

    [RelayCommand]

    public async Task EnterDirectoryAsync(RemoteFileItem item)
    {
        if (!item.IsDirectory || !IsConnected || IsBusy)
        {
            return;
        }

        SetRemotePathWithoutAutoLoad(item.FullPath);
        await LoadDirectoryAsync();
    }

    public IReadOnlyList<RemoteFileItem> GetSelectedRemoteItems()
    {
        return (SelectedFiles.Count > 0
                ? SelectedFiles
                : SelectedFile is not null ? new List<RemoteFileItem> { SelectedFile } : new List<RemoteFileItem>())
            .ToList();
    }

    [RelayCommand]
    private async Task RenameSelectedRemoteFileAsync()
    {
        if (!CanOperateRemote || PromptInputWithDefaultDialogAsync is null)
        {
            return;
        }

        var item = SelectedFile ?? GetSelectedRemoteItems().FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var newName = await PromptInputWithDefaultDialogAsync("重命名远端项目", $"请输入「{item.Name}」的新名称：", item.Name);
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        newName = newName.Trim();
        if (string.Equals(newName, item.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (newName is "." or ".." || newName.Contains('/') || newName.Contains('\\') ||
            newName.Any(ch => char.IsControl(ch)))
        {
            AppendLog($"远端名称不合法：{newName}");
            return;
        }

        if (_allRemoteFiles.Any(f => string.Equals(f.Name, newName, StringComparison.Ordinal)))
        {
            AppendLog($"远端同名项目已存在：{newName}");
            return;
        }

        var baseDir = RemotePath == "/" ? string.Empty : RemotePath.TrimEnd('/');
        var newPath = $"{baseDir}/{newName}";

        IsBusy = true;
        try
        {
            await _relayService.RenameRemoteItemAsync(item.FullPath, newPath, msg => AppendLog(msg));
            SuccessBannerText = $"已重命名：{item.Name} → {newName}";
            AppendLog($"已重命名远端项目：{item.Name} → {newName}");
            await LoadDirectoryCoreAsync(RemotePath);
            SelectedFile = RemoteFiles.FirstOrDefault(f => string.Equals(f.Name, newName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            AppendLog($"重命名远端项目失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]

    private async Task CopySelectedRemotePathAsync()
    {
        if (CopyToClipboardAsync is null)
        {
            return;
        }

        var items = GetSelectedRemoteItems();
        var text = items.Count > 0
            ? string.Join("\n", items.Select(i => i.FullPath))
            : RemotePath;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await CopyToClipboardAsync(text);
        if (!IsTransferring)
        {
            TransferStage = items.Count > 1 ? $"已复制 {items.Count} 个远端路径" : $"已复制远端路径：{text}";
        }
    }

    [RelayCommand]
    private async Task CreateRemoteDirectoryAsync()
    {
        if (!CanOperateRemote || PromptInputDialogAsync is null)
        {
            return;
        }

        var folderName = await PromptInputDialogAsync("新建远端目录", "请输入新目录名称：");
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        folderName = folderName.Trim();
        if (folderName is "." or ".." || folderName.Contains('/') || folderName.Contains('\\') ||
            folderName.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            AppendLog($"远端目录名称不合法：{folderName}");
            return;
        }

        var targetDir = SftpListingParser.CombineRemotePath(SftpListingParser.NormalizeRemoteDirectory(RemotePath), folderName);
        IsBusy = true;
        try
        {
            await _relayService.EnsureRemoteDirectoryAsync(targetDir, msg => AppendLog(msg));
            SuccessBannerText = $"✅ 已创建远端目录：{targetDir}";
            AppendLog($"已创建远端目录：{targetDir}");
            await LoadDirectoryCoreAsync(RemotePath);
        }
        catch (Exception ex)
        {
            AppendLog($"创建远端目录失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]

    private async Task DeleteSelectedRemoteFilesAsync()
    {
        if (!CanOperateRemote)
        {
            return;
        }

        var items = GetSelectedRemoteItems();
        if (items.Count == 0)
        {
            AppendLog("请先在右侧列表中选择要删除的文件服务器文件或目录。");
            return;
        }

        if (ConfirmDialogAsync is not null)
        {
            var msg = items.Count == 1
                ? $"确定永久删除文件服务器上的{(items[0].IsDirectory ? "目录（含内部所有内容）" : "文件")}「{items[0].Name}」吗？\n此操作不可恢复！"
                : $"确定永久删除文件服务器上的 {items.Count} 个项目（含目录内所有内容）吗？\n此操作不可恢复！";
            if (!await ConfirmDialogAsync("确认删除远端项目", msg, "确认删除", true))
            {
                return;
            }
        }

        IsBusy = true;
        var deleted = 0;
        try
        {
            foreach (var item in items)
            {
                try
                {
                    await _relayService.DeleteRemoteItemAsync(item, msg => AppendLog(msg));
                    deleted++;
                }
                catch (Exception ex)
                {
                    AppendLog($"删除远端项目失败（{item.Name}）：{ex.Message}");
                }
            }
        }
        finally
        {
            IsTransferring = false;
            IsBusy = false;
        }

        if (deleted > 0)
        {
            SuccessBannerText = $"已成功删除 {deleted} 个远端项目。";
            AppendLog($"已删除 {deleted} 个远端项目。");
            await LoadDirectoryAsync();
        }
    }

    [RelayCommand]

    private async Task LoadDirectoryCoreAsync(string path)
    {
        _directoryCts?.Cancel();
        _directoryCts?.Dispose();
        _directoryCts = new CancellationTokenSource();
        var ct = _directoryCts.Token;

        try
        {
            DirectoryError = null;
            var normalized = SftpListingParser.NormalizeRemoteDirectory(path);
            AppendLog($"加载文件服务器目录：{normalized}");
            var items = await _relayService.ListDirectoryAsync(normalized, msg => AppendLog(msg), ct);

            _allRemoteFiles.Clear();
            _allRemoteFiles.AddRange(items);
            ApplyRemoteFilter();
            SetRemotePathWithoutAutoLoad(normalized);
            SaveLastPath();
            TransferStage = "就绪";
            AppendLog($"目录加载完成，共 {items.Count} 项。");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer directory request.
        }
        catch (DirectoryNotFoundException ex)
        {
            _allRemoteFiles.Clear();
            RemoteFiles.Clear();
            DirectoryError = ex.Message;
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(EmptyStateMessage));
            TransferStage = "路径不存在，请修正";
            AppendLog($"提示：{ex.Message}");
        }
        catch (Exception ex)
        {
            _allRemoteFiles.Clear();
            RemoteFiles.Clear();
            DirectoryError = $"读取目录失败：{ex.Message}";
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(EmptyStateMessage));
            AppendLog($"读取目录失败：{ex.Message}");
        }
    }

    private void SetRemotePathWithoutAutoLoad(string path)
    {
        _suppressAutoLoad = true;
        RemotePath = path;
        _suppressAutoLoad = false;
    }

}
