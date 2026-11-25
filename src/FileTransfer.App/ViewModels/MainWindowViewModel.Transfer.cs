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
    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var candidates = GetSelectedRemoteItems();
        if (candidates.Count == 0)
        {
            AppendLog("请先在右侧列表中选择至少一个文件或目录进行下载；按住 Ctrl/Cmd 或 Shift 可多选，或直接拖到左侧。");
            return;
        }

        if (candidates.Count == 1 && !candidates[0].IsDirectory)
        {
            if (PickSaveFileAsync is null) return;
            var single = candidates[0];
            var localPath = await PickSaveFileAsync(single.Name);
            if (string.IsNullOrWhiteSpace(localPath)) return;

            var tasks = new List<TransferTask>
            {
                new()
                {
                    Direction = TransferDirection.Download,
                    FileName = single.Name,
                    LocalPath = localPath,
                    RemotePath = single.FullPath,
                    TotalBytes = single.SizeBytes
                }
            };

            var completed = await RunTaskBatchAsync(tasks);
            if (completed > 0)
            {
                LoadLocalDirectoryCore(LocalPath);
            }
            return;
        }

        if (PickFolderAsync is null) return;
        var folder = await PickFolderAsync("选择本地保存目录（支持多文件/多目录下载）");
        if (string.IsNullOrWhiteSpace(folder)) return;

        await DownloadRemoteItemsToLocalDirAsync(candidates, folder);
    }

    public Task DownloadRemoteFilesToCurrentLocalDirAsync(IReadOnlyList<RemoteFileItem> remoteItems)
    {
        var targetLocalDir = Directory.Exists(LocalPath)
            ? LocalPath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return DownloadRemoteItemsToLocalDirAsync(remoteItems, targetLocalDir);
    }

    public async Task DownloadRemoteItemsToLocalDirAsync(IReadOnlyList<RemoteFileItem> remoteItems, string targetLocalDir)
    {
        if (!CanOperateRemote || remoteItems.Count == 0)
        {
            return;
        }

        var tasks = new List<TransferTask>();
        IsBusy = true;
        TransferStage = "正在扫描远端目录...";

        try
        {
            await CollectRemoteDownloadTasksAsync(remoteItems, targetLocalDir, tasks);
        }
        catch (Exception ex)
        {
            AppendLog($"扫描远端目录失败：{ex.Message}");
            IsBusy = false;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (tasks.Count == 0)
        {
            AppendLog("所选目录为空，没有可下载的文件。");
            LoadLocalDirectoryCore(LocalPath);
            return;
        }

        var existingLocal = tasks.Where(t => File.Exists(t.LocalPath)).ToList();
        if (existingLocal.Count > 0 && ConfirmOverwriteDialogAsync is not null)
        {
            var preview = string.Join("、", existingLocal.Take(3).Select(t => $"「{t.FileName}」"));
            var suffix = existingLocal.Count > 3 ? $" 等 {existingLocal.Count} 个文件" : string.Empty;
            var decision = await ConfirmOverwriteDialogAsync(
                "本地同名文件确认",
                $"本地目标目录已存在 {preview}{suffix}，是否覆盖？");

            if (decision == OverwriteDecision.Cancel)
            {
                AppendLog("已取消下载。");
                return;
            }

            if (decision == OverwriteDecision.SkipExisting)
            {
                tasks = tasks.Where(t => !File.Exists(t.LocalPath)).ToList();
                if (tasks.Count == 0)
                {
                    AppendLog("已跳过全部同名文件，无需下载。");
                    return;
                }
            }
        }

        var completed = await RunTaskBatchAsync(tasks);
        if (completed > 0)
        {
            LoadLocalDirectoryCore(LocalPath);
        }
    }

    private async Task CollectRemoteDownloadTasksAsync(
        IEnumerable<RemoteFileItem> items,
        string localBaseDir,
        List<TransferTask> tasks)
    {
        Directory.CreateDirectory(localBaseDir);

        foreach (var item in items)
        {
            if (item.IsDirectory)
            {
                var subLocalDir = Path.Combine(localBaseDir, item.Name);
                Directory.CreateDirectory(subLocalDir);
                var children = await _relayService.ListDirectoryAsync(item.FullPath, msg => AppendLog(msg));
                await CollectRemoteDownloadTasksAsync(children, subLocalDir, tasks);
            }
            else
            {
                tasks.Add(new TransferTask
                {
                    Direction = TransferDirection.Download,
                    FileName = item.Name,
                    LocalPath = Path.Combine(localBaseDir, item.Name),
                    RemotePath = item.FullPath,
                    TotalBytes = item.SizeBytes
                });
            }
        }
    }


    [RelayCommand]
    private async Task UploadSelectedLocalFilesAsync()
    {
        if (!CanOperateRemote)
        {
            return;
        }

        var selectedLocal = GetSelectedLocalItems();
        if (selectedLocal.Count > 0)
        {
            await UploadLocalFilePathsAsync(selectedLocal.Select(f => f.FullPath).ToList());
            return;
        }

        if (PickOpenFilesAsync is null)
        {
            return;
        }

        var picked = await PickOpenFilesAsync();
        await UploadLocalFilePathsAsync(picked);
    }

    public async Task UploadLocalFilePathsAsync(IReadOnlyList<string> selectedPaths)
    {
        if (!CanOperateRemote || selectedPaths.Count == 0)
        {
            return;
        }

        var remoteBaseDir = SftpListingParser.NormalizeRemoteDirectory(RemotePath);
        var uploadEntries = new List<(FileInfo LocalFile, string RemoteFilePath)>();
        var dirsToCreate = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in selectedPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                uploadEntries.Add((fi, SftpListingParser.CombineRemotePath(remoteBaseDir, fi.Name)));
            }
            else if (Directory.Exists(path))
            {
                var rootDirInfo = new DirectoryInfo(path);
                var remoteRootDir = SftpListingParser.CombineRemotePath(remoteBaseDir, rootDirInfo.Name);
                dirsToCreate.Add(remoteRootDir);

                foreach (var subDir in rootDirInfo.EnumerateDirectories("*", System.IO.SearchOption.AllDirectories))
                {
                    var relDir = Path.GetRelativePath(rootDirInfo.FullName, subDir.FullName).Replace('\\', '/');
                    dirsToCreate.Add(SftpListingParser.CombineRemotePath(remoteRootDir, relDir));
                }

                foreach (var file in rootDirInfo.EnumerateFiles("*", System.IO.SearchOption.AllDirectories))
                {
                    var relFile = Path.GetRelativePath(rootDirInfo.FullName, file.FullName).Replace('\\', '/');
                    uploadEntries.Add((file, SftpListingParser.CombineRemotePath(remoteRootDir, relFile)));
                }
            }
        }

        if (uploadEntries.Count == 0 && dirsToCreate.Count == 0)
        {
            AppendLog("请选择有效的本地文件或目录进行上传。");
            return;
        }

        var oversized = uploadEntries.Where(e => e.LocalFile.Length > LargeFileWarningThresholdBytes).ToList();
        if (oversized.Count > 0 && ConfirmDialogAsync is not null)
        {
            var maxBytes = oversized.Max(e => e.LocalFile.Length);
            var totalOversizedBytes = oversized.Sum(e => e.LocalFile.Length);
            var msg = oversized.Count == 1
                ? $"所选文件「{oversized[0].LocalFile.Name}」大小为 {RemoteFileItem.FormatBytes(oversized[0].LocalFile.Length)}（已超过 200 MB）。\n"
                : $"共有 {oversized.Count} 个文件超过 200 MB（最大 {RemoteFileItem.FormatBytes(maxBytes)}，超限合计 {RemoteFileItem.FormatBytes(totalOversizedBytes)}）。\n";

            var confirmed = await ConfirmDialogAsync(
                "大文件上传提醒",
                msg + $"上传将先经过跳板机中转目录 ({RelayBasePath})，可能占用较多空间与时间。\n\n确认继续上传吗？",
                "继续上传",
                false);

            if (!confirmed)
            {
                AppendLog($"已取消上传（含 {oversized.Count} 个超过 200 MB 的大文件）。");
                return;
            }
        }

        if (dirsToCreate.Count > 0)
        {
            IsBusy = true;
            TransferStage = "正在创建远端目录...";
            try
            {
                foreach (var dir in dirsToCreate.OrderBy(d => d.Length))
                {
                    await _relayService.EnsureRemoteDirectoryAsync(dir, msg => AppendLog(msg));
                }
            }
            catch (Exception ex)
            {
                AppendLog($"创建远端目录失败：{ex.Message}");
                IsBusy = false;
                return;
            }
            finally
            {
                IsBusy = false;
            }
        }

        if (uploadEntries.Count == 0)
        {
            SuccessBannerText = $"✅ 已成功在文件服务器创建 {dirsToCreate.Count} 个空目录。";
            await LoadDirectoryAsync();
            return;
        }

        var tasks = uploadEntries.Select(e => new TransferTask
        {
            Direction = TransferDirection.Upload,
            FileName = e.LocalFile.Name,
            LocalPath = e.LocalFile.FullName,
            RemotePath = e.RemoteFilePath,
            TotalBytes = e.LocalFile.Length
        }).ToList();

        var existingRemotePaths = _allRemoteFiles
            .Where(f => !f.IsDirectory)
            .Select(f => f.FullPath)
            .ToHashSet(StringComparer.Ordinal);

        var conflictingUploads = tasks.Where(t => existingRemotePaths.Contains(t.RemotePath)).ToList();
        if (conflictingUploads.Count > 0 && ConfirmOverwriteDialogAsync is not null)
        {
            var preview = string.Join("、", conflictingUploads.Take(3).Select(t => $"「{t.FileName}」"));
            var suffix = conflictingUploads.Count > 3 ? $" 等 {conflictingUploads.Count} 个文件" : string.Empty;
            var decision = await ConfirmOverwriteDialogAsync(
                "远端同名文件确认",
                $"文件服务器当前目录已存在 {preview}{suffix}，是否覆盖？");

            if (decision == OverwriteDecision.Cancel)
            {
                AppendLog("已取消上传。");
                return;
            }

            if (decision == OverwriteDecision.SkipExisting)
            {
                tasks = tasks.Where(t => !existingRemotePaths.Contains(t.RemotePath)).ToList();
                if (tasks.Count == 0)
                {
                    AppendLog("已跳过全部远端同名文件，无需上传。");
                    return;
                }
            }
        }

        var completedCount = await RunTaskBatchAsync(tasks);
        if (completedCount > 0)
        {
            await LoadDirectoryAsync();
        }
    }

    [RelayCommand]

    private async Task DownloadSelectedToCurrentLocalDirAsync()
    {
        var candidates = GetSelectedRemoteItems();
        if (candidates.Count == 0)
        {
            AppendLog("请先在右侧列表中选择至少一个文件或目录进行下载。");
            return;
        }

        await DownloadRemoteFilesToCurrentLocalDirAsync(candidates);
    }


    [RelayCommand]
    private void CancelTransfer()
    {
        _transferCts?.Cancel();
        AppendLog("正在取消传输并清理跳板机中转文件...");
    }

    private async Task<int> RunTaskBatchAsync(IReadOnlyList<TransferTask> tasks)
    {
        if (tasks.Count == 0)
        {
            return 0;
        }

        _transferCts?.Dispose();
        _transferCts = new CancellationTokenSource();
        IsBusy = true;
        IsTransferring = true;
        SuccessBannerText = null;
        TransferProgress = 0;

        var completedCount = 0;
        var totalCount = tasks.Count;

        try
        {
            for (var i = 0; i < totalCount; i++)
            {
                _transferCts.Token.ThrowIfCancellationRequested();

                var task = tasks[i];
                var index = i;
                var prefix = totalCount > 1 ? $"[{index + 1}/{totalCount}] " : string.Empty;
                TransferQueue.Insert(0, task);
                TransferStage = $"{prefix}准备中...";
                AppendLog($"{prefix}开始{task.DirectionLabel}：{task.FileName} ({RemoteFileItem.FormatBytes(task.TotalBytes)})");

                var progress = new Progress<(double Percent, string Stage)>(update =>
                {
                    var overall = (index + update.Percent / 100.0) / totalCount * 100.0;
                    TransferProgress = overall;
                    TransferStage = $"{prefix}{update.Stage}";
                    task.ProgressPercent = update.Percent;
                    task.StageText = update.Stage;
                });

                try
                {
                    await _relayService.ExecuteTaskAsync(task, progress, msg => AppendLog(msg), _transferCts.Token);
                    completedCount++;
                    AppendLog($"{prefix}{task.DirectionLabel}成功：{task.FileName}，跳板机中转文件已清理。");
                }
                catch (OperationCanceledException)
                {
                    AppendLog($"{prefix}{task.DirectionLabel}已取消：{task.FileName}，跳板机中转文件已清理。");
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog($"{prefix}{task.DirectionLabel}失败（{task.FileName}）：{ex.Message}");
                    if (totalCount == 1)
                    {
                        TransferStage = $"失败：{ex.Message}";
                    }
                }
            }

            if (completedCount == totalCount)
            {
                TransferProgress = 100;
                var actionLabel = tasks[0].DirectionLabel;
                TransferStage = totalCount > 1
                    ? $"全部完成（共 {completedCount} 个文件，中转已清理）"
                    : "已完成（跳板机中转文件已清理）";
                SuccessBannerText = totalCount > 1
                    ? $"✅ {actionLabel}成功：共 {completedCount} 个文件已传输完成，跳板机中转文件已清理。"
                    : $"✅ {actionLabel}成功：「{tasks[0].FileName}」已传输完成，跳板机中转文件已清理。";
            }
            else if (completedCount > 0)
            {
                TransferStage = $"部分完成（成功 {completedCount}/{totalCount} 个）";
                SuccessBannerText = $"⚠️ 部分完成：成功 {completedCount}/{totalCount} 个文件。";
            }
            else
            {
                SuccessBannerText = $"❌ 传输失败：共 {totalCount} 个文件全部失败，请查看下方日志。";
            }
        }
        catch (OperationCanceledException)
        {
            TransferStage = "已取消（跳板机中转文件已清理）";
        }
        finally
        {
            IsTransferring = false;
            IsBusy = false;
        }

        return completedCount;
    }

}
