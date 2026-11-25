using Avalonia.Media;
using FileTransfer.App.Services;

namespace FileTransfer.App.Models;

/// <summary>
/// Represents a file or directory entry returned from the file server.
/// </summary>
public sealed class RemoteFileItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public string ModifiedText { get; init; } = string.Empty;
    public DateTime ModifiedAt { get; init; } = DateTime.MinValue;
    public string Permissions { get; init; } = string.Empty;

    public string TypeLabel => IsDirectory ? "目录" : "文件";

    public string TypeSortKey => IsDirectory
        ? "0:"
        : $"1:{Path.GetExtension(Name).ToLowerInvariant()}:{NameSortKey}";

    public string NameSortKey => PinyinSearchHelper.BuildNaturalSortKey(Name);

    public Geometry IconGeometry => FileVectorIconService.GetIcon(Name, IsDirectory).Geometry;

    public IBrush IconBrush => FileVectorIconService.GetIcon(Name, IsDirectory).Color;

    public string DisplaySize => IsDirectory ? "-" : FormatBytes(SizeBytes);

    public static string GetIconGlyph(string name, bool isDirectory)
    {
        if (isDirectory) return "📁";
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".zip" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".7z" or ".rar" => "📦",
            ".txt" or ".log" or ".md" or ".csv" or ".json" or ".xml" or ".yaml" or ".yml" or ".conf" or ".ini" => "📄",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" or ".webp" or ".ico" => "🖼️",
            ".exe" or ".msi" or ".dll" or ".sh" or ".bat" or ".ps1" or ".py" => "⚙️",
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => "📑",
            _ => "📃"
        };
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F1} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F2} MB";
        double gb = mb / 1024.0;
        return $"{gb:F2} GB";
    }
}
