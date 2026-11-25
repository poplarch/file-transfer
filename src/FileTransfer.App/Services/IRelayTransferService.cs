using FileTransfer.App.Models;

namespace FileTransfer.App.Services;

/// <summary>
/// Abstraction for two-hop jump-host SFTP relay operations so ViewModels can be unit-tested.
/// </summary>
public interface IRelayTransferService : IDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(ConnectionConfig config, CancellationToken ct = default);
    Task EnsureAliveOrReconnectAsync(Action<string>? onReconnecting = null, CancellationToken ct = default);
    Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string remotePath, Action<string>? onReconnect = null, CancellationToken ct = default);
    Task DeleteRemoteItemAsync(RemoteFileItem item, Action<string>? onReconnect = null, CancellationToken ct = default);
    Task EnsureRemoteDirectoryAsync(string remoteDir, Action<string>? onReconnect = null, CancellationToken ct = default);
    Task RenameRemoteItemAsync(string oldPath, string newPath, Action<string>? onReconnect = null, CancellationToken ct = default);
    Task ExecuteTaskAsync(
        TransferTask task,
        IProgress<(double Percent, string Stage)> progress,
        Action<string>? onReconnect = null,
        CancellationToken ct = default);
    void Disconnect();
}
