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
    partial void OnLocalFilterTextChanged(string value)
    {
        ApplyLocalFilter();
    }

    private void ApplyLocalFilter()
    {
        var filter = LocalFilterText?.Trim();
        LocalFiles.Clear();
        var source = string.IsNullOrEmpty(filter)
            ? _allLocalFiles
            : _allLocalFiles.Where(f => PinyinSearchHelper.MatchesFilter(f.Name, filter));

        foreach (var item in source)
        {
            LocalFiles.Add(item);
        }

        OnPropertyChanged(nameof(ShowLocalEmptyState));
        RefreshLocalSummary();
    }


    partial void OnLocalPathChanged(string value)
    {
        if (_suppressAutoLoad)
        {
            return;
        }

        _localPathDebounceCts?.Cancel();
        _localPathDebounceCts?.Dispose();
        _localPathDebounceCts = new CancellationTokenSource();
        var token = _localPathDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600, token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Directory.Exists(LocalPath))
                    {
                        LoadLocalDirectoryCore(LocalPath);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }


    [RelayCommand]
    private void RefreshLocalDirectory()
    {
        LoadLocalDirectoryCore(LocalPath);
    }

    [RelayCommand]
    private void GoLocalParentDirectory()
    {
        if (string.IsNullOrWhiteSpace(LocalPath))
        {
            return;
        }

        var parent = Directory.GetParent(LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is not null)
        {
            SetLocalPathWithoutAutoLoad(parent.FullName);
            LoadLocalDirectoryCore(parent.FullName);
        }
    }

    [RelayCommand]
    private async Task BrowseLocalFolderAsync()
    {
        if (PickFolderAsync is null)
        {
            return;
        }

        var folder = await PickFolderAsync("选择本地工作目录");
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            SetLocalPathWithoutAutoLoad(folder);
            LoadLocalDirectoryCore(folder);
        }
    }

    public void EnterLocalDirectory(LocalFileItem item)
    {
        if (!item.IsDirectory || !Directory.Exists(item.FullPath))
        {
            return;
        }

        SetLocalPathWithoutAutoLoad(item.FullPath);
        LoadLocalDirectoryCore(item.FullPath);
    }

    public void OpenLocalFile(LocalFileItem item)
    {
        if (item.IsDirectory || !File.Exists(item.FullPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.FullPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"打开本地文件失败（{item.Name}）：{ex.Message}");
        }
    }

    private void LoadLocalDirectoryCore(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            AppendLog($"本地目录不存在：{path}");
            return;
        }

        _ = LoadLocalDirectoryAsync(path);
    }

    private async Task LoadLocalDirectoryAsync(string path)
    {
        try
        {
            var sorted = await Task.Run(() =>
            {
                var dirInfo = new DirectoryInfo(path);
                var entries = new List<LocalFileItem>();

                foreach (var dir in dirInfo.EnumerateDirectories())
                {
                    if ((dir.Attributes & FileAttributes.Hidden) != 0 || (dir.Attributes & FileAttributes.System) != 0)
                    {
                        continue;
                    }

                    entries.Add(new LocalFileItem
                    {
                        Name = dir.Name,
                        FullPath = dir.FullName,
                        IsDirectory = true,
                        SizeBytes = 0,
                        ModifiedAt = dir.LastWriteTime
                    });
                }

                foreach (var file in dirInfo.EnumerateFiles())
                {
                    if ((file.Attributes & FileAttributes.Hidden) != 0 || (file.Attributes & FileAttributes.System) != 0)
                    {
                        continue;
                    }

                    entries.Add(new LocalFileItem
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsDirectory = false,
                        SizeBytes = file.Length,
                        ModifiedAt = file.LastWriteTime
                    });
                }

                return entries
                    .OrderByDescending(i => i.ModifiedAt)
                    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allLocalFiles.Clear();
                _allLocalFiles.AddRange(sorted);
                ApplyLocalFilter();
                StartLocalWatcher(path);
            });
        }
        catch (Exception ex)
        {
            AppendLog($"读取本地目录失败：{ex.Message}");
        }
    }

    private void StartLocalWatcher(string path)
    {
        if (_localWatcher is not null && string.Equals(_localWatcher.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _localWatcher?.Dispose();
        _localWatcher = null;

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            FileSystemEventHandler onChanged = (_, _) => ScheduleLocalWatcherRefresh();
            RenamedEventHandler onRenamed = (_, _) => ScheduleLocalWatcherRefresh();
            watcher.Created += onChanged;
            watcher.Deleted += onChanged;
            watcher.Changed += onChanged;
            watcher.Renamed += onRenamed;
            _localWatcher = watcher;
        }
        catch
        {
            // Ignore watcher errors on restricted folders.
        }
    }

    private void ScheduleLocalWatcherRefresh()
    {
        _localWatchDebounceCts?.Cancel();
        _localWatchDebounceCts?.Dispose();
        _localWatchDebounceCts = new CancellationTokenSource();
        var token = _localWatchDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Directory.Exists(LocalPath))
                    {
                        LoadLocalDirectoryCore(LocalPath);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void SetLocalPathWithoutAutoLoad(string path)
    {
        _suppressAutoLoad = true;
        LocalPath = path;
        _suppressAutoLoad = false;
    }


    public IReadOnlyList<LocalFileItem> GetSelectedLocalItems()
    {
        return (SelectedLocalFiles.Count > 0
                ? SelectedLocalFiles
                : SelectedLocalFile is not null ? new List<LocalFileItem> { SelectedLocalFile } : new List<LocalFileItem>())
            .ToList();
    }

    [RelayCommand]
    private async Task CreateLocalDirectoryAsync()
    {
        if (PromptInputDialogAsync is null || string.IsNullOrWhiteSpace(LocalPath) || !Directory.Exists(LocalPath))
        {
            return;
        }

        var folderName = await PromptInputDialogAsync("新建本地文件夹", "请输入新文件夹名称：");
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        folderName = folderName.Trim();
        if (folderName is "." or ".." || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            AppendLog($"文件夹名称不合法：{folderName}");
            return;
        }

        var target = Path.Combine(LocalPath, folderName);
        if (Directory.Exists(target) || File.Exists(target))
        {
            AppendLog($"同名文件或文件夹已存在：{folderName}");
            return;
        }

        try
        {
            Directory.CreateDirectory(target);
            AppendLog($"已创建本地文件夹：{target}");
            LoadLocalDirectoryCore(LocalPath);
        }
        catch (Exception ex)
        {
            AppendLog($"创建本地文件夹失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RenameSelectedLocalFileAsync()
    {
        var item = SelectedLocalFile ?? GetSelectedLocalItems().FirstOrDefault();
        if (item is null || PromptInputWithDefaultDialogAsync is null || string.IsNullOrWhiteSpace(LocalPath))
        {
            return;
        }

        var newName = await PromptInputWithDefaultDialogAsync("重命名本地项目", $"请输入「{item.Name}」的新名称：", item.Name);
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        newName = newName.Trim();
        if (string.Equals(newName, item.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (newName is "." or ".." || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            AppendLog($"名称不合法：{newName}");
            return;
        }

        var parentDir = Path.GetDirectoryName(item.FullPath) ?? LocalPath;
        var targetPath = Path.Combine(parentDir, newName);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            AppendLog($"目标名称已存在：{newName}");
            return;
        }

        try
        {
            if (item.IsDirectory)
            {
                Directory.Move(item.FullPath, targetPath);
            }
            else
            {
                File.Move(item.FullPath, targetPath);
            }

            AppendLog($"已重命名本地项目：{item.Name} → {newName}");
            LoadLocalDirectoryCore(LocalPath);
            SelectedLocalFile = LocalFiles.FirstOrDefault(f => string.Equals(f.Name, newName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            AppendLog($"重命名本地项目失败：{ex.Message}");
        }
    }

    [RelayCommand]

    private async Task CopySelectedLocalPathAsync()
    {
        if (CopyToClipboardAsync is null)
        {
            return;
        }

        var items = GetSelectedLocalItems();
        var text = items.Count > 0
            ? string.Join(Environment.NewLine, items.Select(i => i.FullPath))
            : LocalPath;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await CopyToClipboardAsync(text);
        if (!IsTransferring)
        {
            TransferStage = items.Count > 1 ? $"已复制 {items.Count} 个本地路径" : $"已复制路径：{text}";
        }
    }

    [RelayCommand]

    private void OpenCurrentLocalFolder()
    {
        if (!Directory.Exists(LocalPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LocalPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"打开本地目录失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedLocalFilesAsync()
    {
        var items = GetSelectedLocalItems();
        if (items.Count == 0)
        {
            AppendLog("请先在左侧列表中选择要删除的本地文件或目录。");
            return;
        }

        if (ConfirmDialogAsync is not null)
        {
            var msg = items.Count == 1
                ? $"确定将本地{(items[0].IsDirectory ? "目录" : "文件")}「{items[0].Name}」移动到回收站吗？"
                : $"确定将选中的 {items.Count} 个本地项目移动到回收站吗？";
            if (!await ConfirmDialogAsync("确认删除本地项目", msg, "移至回收站", true))
            {
                return;
            }
        }

        var deleted = 0;
        foreach (var item in items)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (item.IsDirectory)
                    {
                        FileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                }
                else
                {
                    MoveToMacTrashOrDelete(item.FullPath);
                }
                deleted++;
            }
            catch (Exception ex)
            {
                AppendLog($"删除本地项目失败（{item.Name}）：{ex.Message}");
            }
        }

        if (deleted > 0)
        {
            AppendLog($"已将 {deleted} 个本地项目移至回收站。");
            LoadLocalDirectoryCore(LocalPath);
        }
    }

    private static void MoveToMacTrashOrDelete(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var psi = new ProcessStartInfo("osascript", $"-e \"tell application \\\"Finder\\\" to delete POSIX file \\\"{escaped}\\\"\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            if (proc is { ExitCode: 0 } || (!File.Exists(path) && !Directory.Exists(path)))
            {
                return;
            }
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
