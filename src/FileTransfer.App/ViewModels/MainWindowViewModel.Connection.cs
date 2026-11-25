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
    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            _relayService.Disconnect();
            IsConnected = false;
            IsConfigExpanded = true;
            _allRemoteFiles.Clear();
            RemoteFiles.Clear();
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(EmptyStateMessage));
            if (!RememberPasswords)
            {
                JumpPassword = string.Empty;
                FileServerPassword = string.Empty;
                SaveCurrentInputs();
            }
            StatusBanner = "已断开";
            AppendLog("已断开跳板机与文件服务器连接。");
            return;
        }

        if (!int.TryParse(JumpPortText, out var jumpPort) || jumpPort is < 1 or > 65535 ||
            !int.TryParse(FileServerPortText, out var fsPort) || fsPort is < 1 or > 65535)
        {
            AppendLog("端口格式无效，请输入 1-65535 之间的数字。");
            return;
        }

        if (string.IsNullOrWhiteSpace(JumpHost) || string.IsNullOrWhiteSpace(JumpUsername) ||
            string.IsNullOrWhiteSpace(JumpPassword) || string.IsNullOrWhiteSpace(FileServerHost) ||
            string.IsNullOrWhiteSpace(FileServerUsername) || string.IsNullOrWhiteSpace(FileServerPassword))
        {
            AppendLog("请完整填写跳板机和文件服务器的地址、用户名与密码。");
            return;
        }

        var config = BuildConfig(jumpPort, fsPort);
        _configService.Save(config);
        IsBusy = true;
        StatusBanner = "正在连接...";
        AppendLog($"正在连接跳板机 {config.JumpUsername}@{config.JumpHost}:{config.JumpPort} ...");

        try
        {
            await _relayService.ConnectAsync(config);

            IsConnected = true;
            IsConfigExpanded = false;
            StatusBanner = $"已连接：{config.FileServerHost}";
            AppendLog($"连接成功！中转目录：{config.GetEffectiveRelayBasePath()}");
            await LoadDirectoryCoreAsync(RemotePath);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusBanner = "连接失败";
            AppendLog($"连接失败：{ex.Message}");
        }
        finally
        {
            IsTransferring = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearSavedPasswords()
    {
        var cfg = BuildConfig(22, 22);
        _configService.ClearSavedPasswords(cfg);
        RememberPasswords = false;
        JumpPassword = string.Empty;
        FileServerPassword = string.Empty;
        AppendLog("已清除本地保存的加密密码。");
    }

    public void Dispose()
    {
        if (!RememberPasswords)
        {
            JumpPassword = string.Empty;
            FileServerPassword = string.Empty;
        }
        SaveCurrentInputs();
        _localWatcher?.Dispose();
        _localWatchDebounceCts?.Cancel();
        _localWatchDebounceCts?.Dispose();
        _bannerAutoDismissCts?.Cancel();
        _bannerAutoDismissCts?.Dispose();
        _localPathDebounceCts?.Cancel();
        _localPathDebounceCts?.Dispose();
        _pathDebounceCts?.Cancel();
        _pathDebounceCts?.Dispose();
        _transferCts?.Cancel();
        _transferCts?.Dispose();
        _relayService.Dispose();
    }
}
