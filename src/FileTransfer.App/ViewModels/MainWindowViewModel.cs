using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTransfer.App.Models;
using FileTransfer.App.Services;
using Microsoft.VisualBasic.FileIO;

namespace FileTransfer.App.ViewModels;

public enum OverwriteDecision
{
    OverwriteAll,
    SkipExisting,
    Cancel
}

public sealed record LogEntry(string Time, string Message);

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    public const string DefaultAppVersion = "1.0";
    public const long LargeFileWarningThresholdBytes = 200L * 1024 * 1024; // 200 MB

    public string AppVersion { get; } = GetAppVersion();
    public string WindowTitle => $"跳板机 SFTP 文件中转工具 v{AppVersion}";

    private static string GetAppVersion()
    {
        var ver = typeof(MainWindowViewModel).Assembly.GetName().Version;
        return ver is not null ? $"{ver.Major}.{ver.Minor}" : DefaultAppVersion;
    }

    private readonly ConfigService _configService = new();
    private readonly IRelayTransferService _relayService;
    private CancellationTokenSource? _transferCts;
    private CancellationTokenSource? _directoryCts;
    private CancellationTokenSource? _pathDebounceCts;
    private CancellationTokenSource? _localWatchDebounceCts;
    private CancellationTokenSource? _bannerAutoDismissCts;
    private FileSystemWatcher? _localWatcher;
    private bool _suppressAutoLoad;

    [ObservableProperty] private string _jumpHost = string.Empty;
    [ObservableProperty] private string _jumpPortText = "22";
    [ObservableProperty] private string _jumpUsername = string.Empty;
    [ObservableProperty] private string _jumpPassword = string.Empty;
    [ObservableProperty] private string _relayBasePath = "/tmp";

    [ObservableProperty] private string _fileServerHost = string.Empty;
    [ObservableProperty] private string _fileServerPortText = "22";
    [ObservableProperty] private string _fileServerUsername = string.Empty;
    [ObservableProperty] private string _fileServerPassword = string.Empty;

    [ObservableProperty] private bool _rememberPasswords = true;
    [ObservableProperty] private bool _vimModeEnabled;
    [ObservableProperty] private string _themeMode = "System";
    [ObservableProperty] private string _localPath = string.Empty;
    [ObservableProperty] private string _localFilterText = string.Empty;
    [ObservableProperty] private LocalFileItem? _selectedLocalFile;
    [ObservableProperty] private string _remotePath = "/";
    [ObservableProperty] private string _remoteFilterText = string.Empty;
    [ObservableProperty] private RemoteFileItem? _selectedFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperateRemote))]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    [NotifyPropertyChangedFor(nameof(ConnectionSummary))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(EmptyStateMessage))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperateRemote))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isTransferring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleConfigButtonText))]
    private bool _isConfigExpanded = true;

    [ObservableProperty] private double _transferProgress;
    [ObservableProperty] private string _transferStage = "就绪";
    [ObservableProperty] private string _statusBanner = "未连接";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessBanner))]
    private string? _successBannerText;
    public bool ShowSuccessBanner => !string.IsNullOrEmpty(SuccessBannerText);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateMessage))]
    private string? _directoryError;

    public bool CanOperateRemote => IsConnected && !IsBusy;
    public string ConnectButtonText => IsConnected ? "断开连接" : "连接";
    public string ToggleConfigButtonText => IsConfigExpanded ? "收起配置" : "展开配置";
    public string ConnectionSummary => IsConnected
        ? $"已连接：跳板机 {JumpUsername}@{JumpHost} → 文件服务器 {FileServerUsername}@{FileServerHost}（中转：{RelayBasePath}）"
        : "未连接";
    public bool ShowEmptyState => RemoteFiles.Count == 0;
    public bool ShowLocalEmptyState => LocalFiles.Count == 0;
    public string EmptyStateMessage => !IsConnected
        ? "请先在上方填写跳板机与文件服务器信息并点击「连接」"
        : !string.IsNullOrEmpty(DirectoryError)
            ? DirectoryError
            : "当前目录暂无文件（可从左侧拖拽或点击「上传」）";

    private readonly List<LocalFileItem> _allLocalFiles = new();
    private readonly List<RemoteFileItem> _allRemoteFiles = new();

    public ObservableCollection<LocalFileItem> LocalFiles { get; } = new();
    public List<LocalFileItem> SelectedLocalFiles { get; } = new();

    public ObservableCollection<RemoteFileItem> RemoteFiles { get; } = new();
    public List<RemoteFileItem> SelectedFiles { get; } = new();

    public ObservableCollection<TransferTask> TransferQueue { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();

    // UI interaction delegates wired by MainWindow.axaml.cs
    public Func<string, Task<string?>>? PickSaveFileAsync { get; set; }
    public Func<string, Task<string?>>? PickFolderAsync { get; set; }
    public Func<Task<IReadOnlyList<string>>>? PickOpenFilesAsync { get; set; }
    public Func<string, string, string, bool, Task<bool>>? ConfirmDialogAsync { get; set; }
    public Func<string, string, Task<OverwriteDecision>>? ConfirmOverwriteDialogAsync { get; set; }
    public Func<string, string, Task<string?>>? PromptInputDialogAsync { get; set; }
    public Func<string, string, string, Task<string?>>? PromptInputWithDefaultDialogAsync { get; set; }
    public Func<string, Task>? CopyToClipboardAsync { get; set; }
    public Action<string>? ApplyThemeAction { get; set; }

    [ObservableProperty] private string _selectedCountText = string.Empty;
    [ObservableProperty] private string _selectedLocalCountText = string.Empty;

    public void UpdateSelectedFiles(IEnumerable<RemoteFileItem> items)
    {
        SelectedFiles.Clear();
        SelectedFiles.AddRange(items);
        RefreshRemoteSummary();
    }

    public void UpdateSelectedLocalFiles(IEnumerable<LocalFileItem> items)
    {
        SelectedLocalFiles.Clear();
        SelectedLocalFiles.AddRange(items);
        RefreshLocalSummary();
    }

    private void RefreshLocalSummary()
    {
        var selectedBytes = SelectedLocalFiles.Where(f => !f.IsDirectory).Sum(f => f.SizeBytes);
        var selectedDirs = SelectedLocalFiles.Count(f => f.IsDirectory);
        SelectedLocalCountText = FormatPaneSummary(LocalFiles.Count, SelectedLocalFiles.Count, selectedBytes, selectedDirs);
    }

    private void RefreshRemoteSummary()
    {
        var selectedBytes = SelectedFiles.Where(f => !f.IsDirectory).Sum(f => f.SizeBytes);
        var selectedDirs = SelectedFiles.Count(f => f.IsDirectory);
        SelectedCountText = FormatPaneSummary(RemoteFiles.Count, SelectedFiles.Count, selectedBytes, selectedDirs);
    }

    private static string FormatPaneSummary(int totalCount, int selectedCount, long selectedBytes, int selectedDirs)
    {
        if (totalCount == 0 && selectedCount == 0)
        {
            return string.Empty;
        }

        if (selectedCount == 0)
        {
            return $"共 {totalCount} 项";
        }

        if (selectedDirs == selectedCount)
        {
            return $"共 {totalCount} 项 · 已选 {selectedCount} 个文件夹";
        }

        return $"共 {totalCount} 项 · 已选 {selectedCount} 项（{FormatBytes(selectedBytes)}）";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }

    public MainWindowViewModel(IRelayTransferService? relayService = null)
    {
        _relayService = relayService ?? new RelayTransferService();
        var cfg = _configService.Load();
        _jumpHost = cfg.JumpHost;
        _jumpPortText = cfg.JumpPort.ToString();
        _jumpUsername = cfg.JumpUsername;
        _jumpPassword = cfg.JumpPassword;
        _relayBasePath = cfg.GetEffectiveRelayBasePath();

        _fileServerHost = cfg.FileServerHost;
        _fileServerPortText = cfg.FileServerPort.ToString();
        _fileServerUsername = cfg.FileServerUsername;
        _fileServerPassword = cfg.FileServerPassword;
        _rememberPasswords = cfg.RememberPasswords;
        _vimModeEnabled = cfg.VimModeEnabled;
        _themeMode = string.IsNullOrWhiteSpace(cfg.ThemeMode) ? "System" : cfg.ThemeMode;

        _suppressAutoLoad = true;
        _remotePath = string.IsNullOrWhiteSpace(cfg.LastRemotePath) ? "/" : cfg.LastRemotePath;
        var initialLocal = !string.IsNullOrWhiteSpace(cfg.LastLocalPath) && Directory.Exists(cfg.LastLocalPath)
            ? cfg.LastLocalPath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _localPath = initialLocal;
        _suppressAutoLoad = false;

        LoadLocalDirectoryCore(initialLocal);
        AppendLog("工具已就绪。首次连接请填写跳板机与文件服务器信息。");
    }

    private CancellationTokenSource? _localPathDebounceCts;


    [RelayCommand]
    private void ToggleConfigExpanded()
    {
        IsConfigExpanded = !IsConfigExpanded;
    }


    [RelayCommand]
    private void DismissSuccessBanner()
    {
        _bannerAutoDismissCts?.Cancel();
        SuccessBannerText = null;
    }

    partial void OnSuccessBannerTextChanged(string? value)
    {
        _bannerAutoDismissCts?.Cancel();
        _bannerAutoDismissCts?.Dispose();
        _bannerAutoDismissCts = null;

        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _bannerAutoDismissCts = cts;
        var delayMs = value.StartsWith("❌", StringComparison.Ordinal) || value.StartsWith("⚠️", StringComparison.Ordinal)
            ? 8000
            : 5000;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested && string.Equals(SuccessBannerText, value, StringComparison.Ordinal))
                    {
                        SuccessBannerText = null;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Replaced by newer notification or manually dismissed.
            }
        }, cts.Token);
    }

    [RelayCommand]

    private void SaveLastPath()
    {
        SaveCurrentInputs();
    }

    public void SaveCurrentInputs()
    {
        var jp = int.TryParse(JumpPortText, out var parsedJp) && parsedJp is >= 1 and <= 65535 ? parsedJp : 22;
        var fp = int.TryParse(FileServerPortText, out var parsedFp) && parsedFp is >= 1 and <= 65535 ? parsedFp : 22;
        var cfg = BuildConfig(jp, fp);
        _configService.Save(cfg);
    }

    private ConnectionConfig BuildConfig(int jumpPort, int fsPort)
    {
        return new ConnectionConfig
        {
            JumpHost = JumpHost.Trim(),
            JumpPort = jumpPort,
            JumpUsername = JumpUsername.Trim(),
            JumpPassword = JumpPassword,
            RelayBasePath = string.IsNullOrWhiteSpace(RelayBasePath) ? "/tmp" : RelayBasePath.Trim(),
            FileServerHost = FileServerHost.Trim(),
            FileServerPort = fsPort,
            FileServerUsername = FileServerUsername.Trim(),
            FileServerPassword = FileServerPassword,
            LastRemotePath = RemotePath,
            LastLocalPath = LocalPath,
            RememberPasswords = RememberPasswords,
            VimModeEnabled = VimModeEnabled,
            ThemeMode = ThemeMode
        };
    }

    partial void OnThemeModeChanged(string value)
    {
        ApplyThemeAction?.Invoke(value);
        if (!_suppressAutoLoad)
        {
            SaveCurrentInputs();
        }
    }

    partial void OnVimModeEnabledChanged(bool value)
    {
        if (!_suppressAutoLoad)
        {
            SaveCurrentInputs();
            if (!IsTransferring)
            {
                TransferStage = value
                    ? "已开启 Vim 键盘模式（j/k 上下，h/l 上级/进入，g/G 首尾，/ 搜索，Tab 切栏）"
                    : "已关闭 Vim 键盘模式（恢复首字母快速定位）";
            }
        }
    }

    private void AppendLog(string message)
    {
        var entry = new LogEntry($"[{DateTime.Now:HH:mm:ss}]", message);
        void AddEntry()
        {
            Logs.Insert(0, entry);
            while (Logs.Count > 300)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            AddEntry();
        }
        else
        {
            Dispatcher.UIThread.Post(AddEntry);
        }
    }

}
