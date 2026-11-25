using System.Text.Json;
using FileTransfer.App.Models;
using FileTransfer.App.Security;

namespace FileTransfer.App.Services;

/// <summary>
/// Loads and persists connection settings and encrypted credentials in the user's AppData directory.
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _configFilePath;

    public ConfigService(string? customConfigPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customConfigPath))
        {
            _configFilePath = customConfigPath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "FileTransfer");
            _configFilePath = Path.Combine(dir, "config.json");
        }
    }

    public ConnectionConfig Load()
    {
        if (!File.Exists(_configFilePath))
        {
            return new ConnectionConfig();
        }

        try
        {
            var json = File.ReadAllText(_configFilePath);
            var config = JsonSerializer.Deserialize<ConnectionConfig>(json, JsonOptions) ?? new ConnectionConfig();
            config.RelayBasePath = config.GetEffectiveRelayBasePath();

            if (config.RememberPasswords)
            {
                config.JumpPassword = CredentialProtector.Unprotect(config.EncryptedJumpPassword);
                config.FileServerPassword = CredentialProtector.Unprotect(config.EncryptedFileServerPassword);
            }

            return config;
        }
        catch
        {
            return new ConnectionConfig();
        }
    }

    public void Save(ConnectionConfig config)
    {
        var dir = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        config.RelayBasePath = config.GetEffectiveRelayBasePath();

        if (config.RememberPasswords)
        {
            config.EncryptedJumpPassword = CredentialProtector.Protect(config.JumpPassword);
            config.EncryptedFileServerPassword = CredentialProtector.Protect(config.FileServerPassword);
        }
        else
        {
            config.EncryptedJumpPassword = null;
            config.EncryptedFileServerPassword = null;
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configFilePath, json);
    }

    public void ClearSavedPasswords(ConnectionConfig config)
    {
        config.EncryptedJumpPassword = null;
        config.EncryptedFileServerPassword = null;
        config.RememberPasswords = false;
        Save(config);
    }
}
