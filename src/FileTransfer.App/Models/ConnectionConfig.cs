using System.Text.Json.Serialization;

namespace FileTransfer.App.Models;

/// <summary>
/// Holds connection settings for the jump host and target file server.
/// </summary>
public sealed class ConnectionConfig
{
    public string JumpHost { get; set; } = string.Empty;
    public int JumpPort { get; set; } = 22;
    public string JumpUsername { get; set; } = string.Empty;
    public string RelayBasePath { get; set; } = "/tmp";

    public string FileServerHost { get; set; } = string.Empty;
    public int FileServerPort { get; set; } = 22;
    public string FileServerUsername { get; set; } = string.Empty;
    public string LastRemotePath { get; set; } = "/";
    public string LastLocalPath { get; set; } = string.Empty;

    public bool RememberPasswords { get; set; } = true;
    public bool VimModeEnabled { get; set; } = false;
    public string ThemeMode { get; set; } = "System";
    public string? EncryptedJumpPassword { get; set; }
    public string? EncryptedFileServerPassword { get; set; }

    [JsonIgnore]
    public string JumpPassword { get; set; } = string.Empty;

    [JsonIgnore]
    public string FileServerPassword { get; set; } = string.Empty;

    /// <summary>
    /// Returns the normalized relay base path on the jump host, defaulting to /tmp.
    /// </summary>
    public string GetEffectiveRelayBasePath()
    {
        var trimmed = RelayBasePath?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "/tmp";
        }

        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }
}
