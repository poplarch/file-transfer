using CommunityToolkit.Mvvm.ComponentModel;

namespace FileTransfer.App.Models;

public enum TransferDirection
{
    Download,
    Upload
}

public enum TransferStatus
{
    Queued,
    Running,
    Cleaning,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Represents a single file transfer task in the transfer queue.
/// Designed to support both single-file and future multi-file batch transfers.
/// </summary>
public partial class TransferTask : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public TransferDirection Direction { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string LocalPath { get; init; } = string.Empty;
    public string RemotePath { get; init; } = string.Empty;
    public long TotalBytes { get; init; }

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _stageText = "等待中";

    [ObservableProperty]
    private TransferStatus _status = TransferStatus.Queued;

    [ObservableProperty]
    private string? _errorMessage;

    public string DirectionLabel => Direction == TransferDirection.Download ? "下载" : "上传";
}
