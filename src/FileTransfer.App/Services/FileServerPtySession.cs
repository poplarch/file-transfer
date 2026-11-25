using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace FileTransfer.App.Services;

/// <summary>
/// Manages an interactive PTY `sftp` session from the Kylin V10 jump host to the Linux file server.
/// Password is written via PTY stdin so it never appears in CLI arguments or audit command logs.
/// </summary>
public sealed partial class FileServerPtySession : IDisposable
{
    private readonly ShellStream _shell;
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    [GeneratedRegex(@"(?:^|\n)sftp>\s*$", RegexOptions.Multiline)]
    private static partial Regex SftpPromptRegex();

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex SftpPercentRegex();

    [GeneratedRegex(@"(?:Connection to .+ closed|Broken pipe|Connection reset by peer|client_loop: send disconnect|Timeout, server .+ not responding)", RegexOptions.IgnoreCase)]
    private static partial Regex DisconnectRegex();

    // Detects if inner `sftp` exited back to the Kylin V10 bash prompt (e.g. `[user@host ~]$ `)
    [GeneratedRegex(@"(?:^|\n)[^\n]*[\$#]\s*$", RegexOptions.Multiline)]
    private static partial Regex JumpShellPromptRegex();

    public bool IsAlive => _shell.CanWrite;

    private FileServerPtySession(ShellStream shell)
    {
        _shell = shell;
    }

    public static async Task<FileServerPtySession> OpenAsync(
        SshClient jumpSshClient,
        string fileServerHost,
        int fileServerPort,
        string fileServerUser,
        string fileServerPassword,
        CancellationToken ct)
    {
        var shell = jumpSshClient.CreateShellStream("xterm", 220, 40, 800, 600, 16384);
        var session = new FileServerPtySession(shell);

        try
        {
            await session.ReadUntilAsync(
                buf => buf.Contains('$') || buf.Contains('#'),
                TimeSpan.FromSeconds(12),
                null,
                ct);

            var sftpCmd =
                $"exec sftp -o TCPKeepAlive=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=6 -o StrictHostKeyChecking=no -P {fileServerPort} {fileServerUser}@{fileServerHost}";
            shell.WriteLine(sftpCmd);

            var authOutput = await session.ReadUntilAsync(
                buf => buf.Contains("password:", StringComparison.OrdinalIgnoreCase) ||
                       SftpPromptRegex().IsMatch(buf) ||
                       buf.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                       buf.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase) ||
                       buf.Contains("No route to host", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(25),
                null,
                ct);

            if (authOutput.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                authOutput.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase) ||
                authOutput.Contains("No route to host", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"无法从跳板机连接到文件服务器：{authOutput.Trim()}");
            }

            if (authOutput.Contains("password:", StringComparison.OrdinalIgnoreCase))
            {
                shell.WriteLine(fileServerPassword);

                var loginResult = await session.ReadUntilAsync(
                    buf => SftpPromptRegex().IsMatch(buf) ||
                           buf.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                           buf.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
                           buf.Contains("password:", StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(25),
                    null,
                    ct);

                if (loginResult.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                    loginResult.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
                    loginResult.Contains("password:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("文件服务器密码认证失败，请检查用户名或密码。");
                }
            }

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await ExecuteSftpCommandAsync("pwd", TimeSpan.FromSeconds(5), null, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> ListDirectoryRawAsync(string remoteDir, CancellationToken ct)
    {
        var quoted = SftpListingParser.QuoteSftpArg(remoteDir);
        var output = await ExecuteSftpCommandAsync($"ls -la {quoted}", TimeSpan.FromSeconds(8), null, ct);

        if (output.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Can't ls:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Couldn't stat remote file", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("not a directory", StringComparison.OrdinalIgnoreCase))
        {
            throw new DirectoryNotFoundException($"文件服务器路径不存在或无法访问：{remoteDir}，请检查并修正路径。");
        }

        return output;
    }

    public async Task GetFileToRelayAsync(
        string remoteFilePath,
        string relayFilePath,
        Action<double>? onPtyPercent,
        CancellationToken ct)
    {
        var cmd = $"get {SftpListingParser.QuoteSftpArg(remoteFilePath)} {SftpListingParser.QuoteSftpArg(relayFilePath)}";
        var output = await ExecuteSftpCommandAsync(cmd, TimeSpan.FromHours(2), onPtyPercent, ct);

        if (HasSftpTransferError(output))
        {
            throw new IOException($"跳板机从文件服务器拉取文件失败：{ExtractLastErrorLine(output)}");
        }
    }

    public async Task PutFileFromRelayAsync(
        string relayFilePath,
        string remoteFilePath,
        Action<double>? onPtyPercent,
        CancellationToken ct)
    {
        var cmd = $"put {SftpListingParser.QuoteSftpArg(relayFilePath)} {SftpListingParser.QuoteSftpArg(remoteFilePath)}";
        var output = await ExecuteSftpCommandAsync(cmd, TimeSpan.FromHours(2), onPtyPercent, ct);

        if (HasSftpTransferError(output))
        {
            throw new IOException($"跳板机上传文件到文件服务器失败：{ExtractLastErrorLine(output)}");
        }
    }

    public async Task DeleteFileAsync(string remoteFilePath, CancellationToken ct)
    {
        var cmd = $"rm {SftpListingParser.QuoteSftpArg(remoteFilePath)}";
        var output = await ExecuteSftpCommandAsync(cmd, TimeSpan.FromSeconds(20), null, ct);

        if (HasSftpTransferError(output) || output.Contains("Couldn't delete file", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"删除文件服务器文件失败：{ExtractLastErrorLine(output)}");
        }
    }

    public async Task RenameAsync(string oldPath, string newPath, CancellationToken ct)
    {
        var normalizedOld = SftpListingParser.NormalizeRemoteDirectory(oldPath);
        if (IsProtectedDirectory(normalizedOld))
        {
            throw new InvalidOperationException($"安全拦截：禁止重命名系统或关键目录 ({normalizedOld})。");
        }

        var output = await ExecuteSftpCommandAsync(
            $"rename {SftpListingParser.QuoteSftpArg(oldPath)} {SftpListingParser.QuoteSftpArg(newPath)}",
            TimeSpan.FromSeconds(15),
            null,
            ct);
        if (HasSftpTransferError(output))
        {
            throw new IOException($"重命名远端项目失败：{ExtractLastErrorLine(output)}");
        }
    }

    public async Task EnsureDirectoryAsync(string remoteDir, CancellationToken ct)
    {
        var normalized = SftpListingParser.NormalizeRemoteDirectory(remoteDir);
        if (normalized == "/")
        {
            return;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        foreach (var part in parts)
        {
            current += "/" + part;
            // `mkdir` in sftp ignores existing directories with a harmless failure message; we verify at the end.
            await ExecuteSftpCommandAsync($"mkdir {SftpListingParser.QuoteSftpArg(current)}", TimeSpan.FromSeconds(15), null, ct);
        }
    }

    private static readonly HashSet<string> ProtectedRemoteDirectories = new(StringComparer.Ordinal)
    {
        "/", "/bin", "/boot", "/dev", "/etc", "/home", "/lib", "/lib64",
        "/media", "/mnt", "/opt", "/proc", "/root", "/run", "/sbin",
        "/srv", "/sys", "/tmp", "/usr", "/var", "/data"
    };

    public static bool IsProtectedDirectory(string remoteDir)
    {
        var normalized = SftpListingParser.NormalizeRemoteDirectory(remoteDir);
        return ProtectedRemoteDirectories.Contains(normalized);
    }

    public async Task DeleteDirectoryRecursiveAsync(string remoteDir, CancellationToken ct)
    {
        var normalized = SftpListingParser.NormalizeRemoteDirectory(remoteDir);
        if (IsProtectedDirectory(normalized))
        {
            throw new InvalidOperationException($"安全拦截：禁止删除系统或关键目录 ({normalized})。");
        }

        var raw = await ListDirectoryRawAsync(normalized, ct);
        var children = SftpListingParser.Parse(raw, normalized);

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            if (child.IsDirectory)
            {
                await DeleteDirectoryRecursiveAsync(child.FullPath, ct);
            }
            else
            {
                await DeleteFileAsync(child.FullPath, ct);
            }
        }

        var output = await ExecuteSftpCommandAsync($"rmdir {SftpListingParser.QuoteSftpArg(normalized)}", TimeSpan.FromSeconds(20), null, ct);
        if (output.Contains("Couldn't remove directory", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"删除远端目录失败：{ExtractLastErrorLine(output)}");
        }
    }

    private async Task<string> ExecuteSftpCommandAsync(
        string command,
        TimeSpan timeout,
        Action<double>? onPtyPercent,
        CancellationToken ct)
    {
        await _commandLock.WaitAsync(ct);
        try
        {
            while (_shell.DataAvailable)
            {
                _shell.Read();
            }

            _shell.WriteLine(command);
            var raw = await ReadUntilAsync(buf => SftpPromptRegex().IsMatch(buf), timeout, onPtyPercent, ct);
            return SftpListingParser.StripAnsi(raw);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<string> ReadUntilAsync(
        Func<string, bool> condition,
        TimeSpan timeout,
        Action<double>? onPtyPercent,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + timeout;
        var lastPct = -1.0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!_shell.CanWrite)
            {
                throw new IOException("跳板机 SFTP 通道已关闭，需要重新连接。");
            }

            if (_shell.DataAvailable)
            {
                var chunk = _shell.Read();
                if (!string.IsNullOrEmpty(chunk))
                {
                    var cleanChunk = SftpListingParser.StripAnsi(chunk)
                        .Replace("\r\n", "\n")
                        .Replace('\r', '\n');

                    if (onPtyPercent is not null)
                    {
                        var matches = SftpPercentRegex().Matches(cleanChunk);
                        if (matches.Count > 0 && double.TryParse(matches[^1].Groups[1].Value, out var pct))
                        {
                            pct = Math.Clamp(pct, 0, 100);
                            if (pct > lastPct)
                            {
                                lastPct = pct;
                                onPtyPercent(pct);
                            }
                        }

                        // Keep memory bounded during long transfers with many \r progress lines.
                        if (sb.Length > 32768)
                        {
                            sb.Remove(0, sb.Length - 8192);
                        }
                    }

                    sb.Append(cleanChunk);

                    // Inspect only the trailing 512 chars for O(1) prompt/disconnect detection.
                    var tailStart = Math.Max(0, sb.Length - 512);
                    var tail = sb.ToString(tailStart, sb.Length - tailStart);

                    if (DisconnectRegex().IsMatch(tail))
                    {
                        throw new IOException("跳板机到文件服务器的 SFTP 会话已断开，需要重新连接。");
                    }

                    if (condition(tail))
                    {
                        return sb.ToString();
                    }

                    // If we were waiting for `sftp>` but fell back to the jump host bash `$ ` prompt, reconnect.
                    if (onPtyPercent is null && !tail.Contains("password:", StringComparison.OrdinalIgnoreCase) &&
                        JumpShellPromptRegex().IsMatch(tail) && !SftpPromptRegex().IsMatch(tail))
                    {
                        throw new IOException("文件服务器 SFTP 会话已退出到跳板机终端，需要重新连接。");
                    }
                }
            }
            else
            {
                await Task.Delay(40, ct);
            }
        }

        throw new TimeoutException($"等待跳板机 SFTP 响应超时。当前输出：{SftpListingParser.StripAnsi(sb.ToString())}");
    }

    private static bool HasSftpTransferError(string output)
    {
        return output.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Couldn't", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Failure", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("No space left", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractLastErrorLine(string output)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("sftp>", StringComparison.Ordinal))
            .ToArray();
        return lines.Length > 0 ? lines[^1] : output.Trim();
    }

    public void Dispose()
    {
        try
        {
            // Send Ctrl+C first to interrupt any in-flight get/put, then exit sftp cleanly.
            _shell.Write("\x03");
            _shell.WriteLine("bye");
        }
        catch
        {
            // Ignore errors on teardown.
        }

        _shell.Dispose();
        _commandLock.Dispose();
    }
}
