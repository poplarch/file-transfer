using System.Net.Sockets;
using FileTransfer.App.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FileTransfer.App.Services;

/// <summary>
/// Coordinates two-hop file transfers (Local <-> JumpHost SFTP <-> FileServer PTY SFTP)
/// with direct non-hidden staging files, keep-alive, and automatic reconnect.
/// </summary>
public sealed class RelayTransferService : IRelayTransferService
{
    private const string RelayPrefix = "ft_relay_";

    private SshClient? _jumpSsh;
    private SftpClient? _jumpSftp;
    private FileServerPtySession? _fileServerPty;
    private ConnectionConfig? _activeConfig;
    private CancellationTokenSource? _keepAliveCts;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private volatile bool _isTransferring;
    private volatile bool _connectionFaulted;

    public bool IsConnected =>
        _jumpSsh is { IsConnected: true } &&
        _jumpSftp is { IsConnected: true } &&
        _fileServerPty is not null;

    public Task ConnectAsync(ConnectionConfig config, CancellationToken ct = default)
    {
        _activeConfig = config;
        return ConnectInternalAsync(config, resetKeepAlive: true, ct);
    }

    private async Task ConnectInternalAsync(ConnectionConfig config, bool resetKeepAlive, CancellationToken ct)
    {
        if (resetKeepAlive)
        {
            _keepAliveCts?.Cancel();
            _keepAliveCts?.Dispose();
            _keepAliveCts = null;
        }

        DisposeTransportClients();

        var jumpConnInfo = new ConnectionInfo(
            config.JumpHost.Trim(),
            config.JumpPort,
            config.JumpUsername.Trim(),
            new PasswordAuthenticationMethod(config.JumpUsername.Trim(), config.JumpPassword))
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        var ssh = new SshClient(jumpConnInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        };
        var sftp = new SftpClient(jumpConnInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        };
        ssh.ErrorOccurred += (_, _) => _connectionFaulted = true;
        sftp.ErrorOccurred += (_, _) => _connectionFaulted = true;

        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                ssh.Connect();
                ct.ThrowIfCancellationRequested();
                sftp.Connect();
            }, ct);

            var relayBase = config.GetEffectiveRelayBasePath();
            var quotedBase = SftpListingParser.QuoteSftpArg(relayBase);

            var existResult = await Task.Run(() => ssh.RunCommand($"test -d {quotedBase}"), ct);
            if (existResult.ExitStatus != 0)
            {
                throw new DirectoryNotFoundException($"跳板机中转路径不存在：{relayBase}，请修改跳板机中转目录后重试。");
            }

            var writeResult = await Task.Run(() => ssh.RunCommand($"test -w {quotedBase}"), ct);
            if (writeResult.ExitStatus != 0)
            {
                throw new IOException($"跳板机中转路径不可写：{relayBase}，请修改为有写权限的中转目录。");
            }

            // Clean up any stale relay files older than 24 hours via SFTP API.
            await Task.Run(() => CleanupStaleRelayFiles(sftp, relayBase), ct);

            var pty = await FileServerPtySession.OpenAsync(
                ssh,
                config.FileServerHost.Trim(),
                config.FileServerPort,
                config.FileServerUsername.Trim(),
                config.FileServerPassword,
                ct);

            _jumpSsh = ssh;
            _jumpSftp = sftp;
            _fileServerPty = pty;
            _activeConfig = config;
            _connectionFaulted = false;
            if (resetKeepAlive)
            {
                StartKeepAliveLoop();
            }
        }
        catch
        {
            sftp.Dispose();
            ssh.Dispose();
            throw;
        }
    }

    private void StartKeepAliveLoop()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = new CancellationTokenSource();
        var token = _keepAliveCts.Token;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    if (_isTransferring || _activeConfig is null)
                    {
                        continue;
                    }

                    try
                    {
                        await EnsureAliveOrReconnectAsync(null, token);
                    }
                    catch
                    {
                        // Next foreground operation will also attempt reconnect.
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    public async Task EnsureAliveOrReconnectAsync(Action<string>? onReconnecting = null, CancellationToken ct = default)
    {
        await _reconnectLock.WaitAsync(ct);
        try
        {
            if (_activeConfig is null)
            {
                throw new InvalidOperationException("尚未连接到跳板机与文件服务器，请先连接。");
            }

            var jumpAlive = !_connectionFaulted && _jumpSsh is { IsConnected: true } && _jumpSftp is { IsConnected: true };
            if (jumpAlive)
            {
                try
                {
                    // Send a real SFTP STAT packet on _jumpSftp so firewalls/bastions don't drop idle SFTP channels.
                    var relayBase = _activeConfig.GetEffectiveRelayBasePath();
                    await Task.Run(() => _jumpSftp!.Exists(relayBase), ct);
                }
                catch
                {
                    jumpAlive = false;
                }
            }

            var ptyAlive = jumpAlive && _fileServerPty is { IsAlive: true } && await _fileServerPty.PingAsync(ct);

            if (jumpAlive && ptyAlive)
            {
                return;
            }

            onReconnecting?.Invoke("检测到连接已断开，正在自动重连跳板机与文件服务器...");
            var savedConfig = _activeConfig;
            await ConnectInternalAsync(savedConfig, resetKeepAlive: false, ct);
            onReconnecting?.Invoke("自动重连成功。");
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private async Task<T> ExecuteWithReconnectAsync<T>(Func<Task<T>> action, Action<string>? onReconnect, CancellationToken ct)
    {
        await EnsureAliveOrReconnectAsync(onReconnect, ct);
        try
        {
            return await action();
        }
        catch (Exception ex) when (IsConnectionException(ex) && !ct.IsCancellationRequested && _activeConfig is not null)
        {
            onReconnect?.Invoke("连接意外中断，正在自动重连并重试...");
            var savedConfig = _activeConfig;
            await ConnectInternalAsync(savedConfig, resetKeepAlive: false, ct);
            return await action();
        }
    }

    private static bool IsConnectionException(Exception ex) =>
        ex is SshException or SshConnectionException or SshOperationTimeoutException
            or TimeoutException or IOException or SocketException or ObjectDisposedException;

    public async Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string remotePath, Action<string>? onReconnect = null, CancellationToken ct = default)
    {
        return await ExecuteWithReconnectAsync(async () =>
        {
            var normalized = SftpListingParser.NormalizeRemoteDirectory(remotePath);
            var raw = await _fileServerPty!.ListDirectoryRawAsync(normalized, ct);
            return SftpListingParser.Parse(raw, normalized);
        }, onReconnect, ct);
    }

    public async Task DeleteRemoteItemAsync(RemoteFileItem item, Action<string>? onReconnect = null, CancellationToken ct = default)
    {
        await ExecuteWithReconnectAsync(async () =>
        {
            if (item.IsDirectory)
            {
                await _fileServerPty!.DeleteDirectoryRecursiveAsync(item.FullPath, ct);
            }
            else
            {
                await _fileServerPty!.DeleteFileAsync(item.FullPath, ct);
            }
            return true;
        }, onReconnect, ct);
    }

    public async Task EnsureRemoteDirectoryAsync(string remoteDir, Action<string>? onReconnect = null, CancellationToken ct = default)
    {
        await ExecuteWithReconnectAsync(async () =>
        {
            await _fileServerPty!.EnsureDirectoryAsync(remoteDir, ct);
            return true;
        }, onReconnect, ct);
    }

    public async Task RenameRemoteItemAsync(string oldPath, string newPath, Action<string>? onReconnect = null, CancellationToken ct = default)
    {
        await ExecuteWithReconnectAsync(async () =>
        {
            await _fileServerPty!.RenameAsync(oldPath, newPath, ct);
            return true;
        }, onReconnect, ct);
    }

    public async Task ExecuteTaskAsync(
        TransferTask task,
        IProgress<(double Percent, string Stage)> progress,
        Action<string>? onReconnect = null,
        CancellationToken ct = default)
    {
        _isTransferring = true;
        task.Status = TransferStatus.Running;
        task.ErrorMessage = null;

        try
        {
            await ExecuteWithReconnectAsync(async () =>
            {
                if (task.Direction == TransferDirection.Download)
                {
                    await DownloadSingleAsync(task, progress, ct);
                }
                else
                {
                    await UploadSingleAsync(task, progress, ct);
                }
                return true;
            }, onReconnect, ct);

            task.ProgressPercent = 100;
            task.StageText = "已完成";
            task.Status = TransferStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            task.Status = TransferStatus.Cancelled;
            task.StageText = "已取消";
            throw;
        }
        catch (Exception ex)
        {
            task.Status = TransferStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.StageText = "失败";
            throw;
        }
        finally
        {
            _isTransferring = false;
        }
    }

    private async Task DownloadSingleAsync(
        TransferTask task,
        IProgress<(double Percent, string Stage)> progress,
        CancellationToken ct)
    {
        var relayBase = _activeConfig!.GetEffectiveRelayBasePath();
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var relayFile = SftpListingParser.CombineRemotePath(relayBase, $"{RelayPrefix}{stamp}_{task.Id}_{task.FileName}");

        try
        {
            // Stage 1 (0% -> 50%): FileServer -> JumpHost relay
            progress.Report((2, "阶段 1/2：跳板机从文件服务器拉取中..."));
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pollTask = PollRelayFileSizeAsync(relayFile, task.TotalBytes, 0, 50, "阶段 1/2：跳板机拉取中", progress, pollCts.Token);

            try
            {
                await _fileServerPty!.GetFileToRelayAsync(
                    task.RemotePath,
                    relayFile,
                    ptyPct => progress.Report((ptyPct * 0.5, $"阶段 1/2：跳板机拉取中 ({ptyPct:F0}%)")),
                    ct);
            }
            finally
            {
                await pollCts.CancelAsync();
                try { await pollTask; } catch (OperationCanceledException) { }
            }

            // Stage 2 (50% -> 100%): JumpHost relay -> Local machine
            progress.Report((50, "阶段 2/2：从跳板机下载到本地..."));
            var localDir = Path.GetDirectoryName(task.LocalPath);
            if (!string.IsNullOrEmpty(localDir))
            {
                Directory.CreateDirectory(localDir);
            }

            var totalBytes = task.TotalBytes > 0 ? task.TotalBytes : GetRelayFileSize(relayFile);
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var fs = File.Create(task.LocalPath);
                _jumpSftp!.DownloadFile(relayFile, fs, downloaded =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }

                    var pct = totalBytes > 0 ? 50.0 + Math.Min(50.0, (double)downloaded / totalBytes * 50.0) : 75.0;
                    progress.Report((pct, $"阶段 2/2：下载到本地 ({RemoteFileItem.FormatBytes((long)downloaded)})"));
                });
            }, ct);
        }
        finally
        {
            task.Status = TransferStatus.Cleaning;
            progress.Report((98, "正在清理跳板机中转文件..."));
            await CleanupRelayFileAsync(relayFile);
        }
    }

    private async Task UploadSingleAsync(
        TransferTask task,
        IProgress<(double Percent, string Stage)> progress,
        CancellationToken ct)
    {
        var relayBase = _activeConfig!.GetEffectiveRelayBasePath();
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var relayFile = SftpListingParser.CombineRemotePath(relayBase, $"{RelayPrefix}{stamp}_{task.Id}_{task.FileName}");

        try
        {
            // Stage 1 (0% -> 50%): Local machine -> JumpHost relay
            progress.Report((2, "阶段 1/2：上传本地文件到跳板机..."));
            var totalBytes = new FileInfo(task.LocalPath).Length;

            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var fs = File.OpenRead(task.LocalPath);
                _jumpSftp!.UploadFile(fs, relayFile, true, uploaded =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }

                    var pct = totalBytes > 0 ? Math.Min(50.0, (double)uploaded / totalBytes * 50.0) : 25.0;
                    progress.Report((pct, $"阶段 1/2：上传到跳板机 ({RemoteFileItem.FormatBytes((long)uploaded)})"));
                });
            }, ct);

            // Stage 2 (50% -> 100%): JumpHost relay -> FileServer
            progress.Report((52, "阶段 2/2：跳板机推送到文件服务器..."));
            await _fileServerPty!.PutFileFromRelayAsync(
                relayFile,
                task.RemotePath,
                ptyPct => progress.Report((50.0 + ptyPct * 0.46, $"阶段 2/2：跳板机推送中 ({ptyPct:F0}%)")),
                ct);
            progress.Report((96, "阶段 2/2：推送完成"));
        }
        finally
        {
            task.Status = TransferStatus.Cleaning;
            progress.Report((98, "正在清理跳板机中转文件..."));
            await CleanupRelayFileAsync(relayFile);
        }
    }

    private async Task CleanupRelayFileAsync(string relayFile)
    {
        if (_jumpSftp is not { IsConnected: true })
        {
            return;
        }

        // Guard: only delete files directly prefixed with our relay marker.
        var fileName = relayFile.Split('/').LastOrDefault() ?? string.Empty;
        if (!fileName.StartsWith(RelayPrefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                if (_jumpSftp.Exists(relayFile))
                {
                    _jumpSftp.DeleteFile(relayFile);
                }
            });
        }
        catch
        {
            // Best-effort cleanup; stale-file sweep handles leftovers on next connect.
        }
    }

    private static void CleanupStaleRelayFiles(SftpClient sftp, string relayBase)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var entry in sftp.ListDirectory(relayBase))
            {
                if (entry.IsDirectory ||
                    !entry.Name.StartsWith(RelayPrefix, StringComparison.Ordinal) ||
                    entry.LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }

                try
                {
                    sftp.DeleteFile(entry.FullName);
                }
                catch
                {
                    // Skip locked files safely.
                }
            }
        }
        catch
        {
            // Best-effort sweep on connect.
        }
    }

    private long GetRelayFileSize(string relayFile)
    {
        try
        {
            return _jumpSftp!.GetAttributes(relayFile).Size;
        }
        catch
        {
            return 0;
        }
    }

    private async Task PollRelayFileSizeAsync(
        string relayFile,
        long totalBytes,
        double startPct,
        double spanPct,
        string stageLabel,
        IProgress<(double Percent, string Stage)> progress,
        CancellationToken ct)
    {
        if (totalBytes <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(450));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var current = GetRelayFileSize(relayFile);
            if (current > 0)
            {
                var ratio = Math.Min(1.0, (double)current / totalBytes);
                progress.Report((startPct + ratio * spanPct, $"{stageLabel} ({RemoteFileItem.FormatBytes(current)}/{RemoteFileItem.FormatBytes(totalBytes)})"));
            }
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("尚未连接到跳板机与文件服务器，请先连接。");
        }
    }

    private void DisposeTransportClients()
    {
        _fileServerPty?.Dispose();
        _fileServerPty = null;

        _jumpSftp?.Dispose();
        _jumpSftp = null;

        _jumpSsh?.Dispose();
        _jumpSsh = null;
    }

    public void Disconnect()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;

        DisposeTransportClients();
        _activeConfig = null;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
