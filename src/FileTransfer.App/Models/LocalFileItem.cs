using Avalonia.Media;
using FileTransfer.App.Services;

namespace FileTransfer.App.Models;

/// <summary>
/// Represents a local file or directory entry displayed in the left pane.
/// </summary>
public sealed class LocalFileItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTime ModifiedAt { get; init; }
    public string ModifiedText => ModifiedAt == DateTime.MinValue ? string.Empty : ModifiedAt.ToString("yyyy-MM-dd HH:mm");

    public string TypeLabel => IsDirectory ? "目录" : "文件";

    public string TypeSortKey => IsDirectory
        ? "0:"
        : $"1:{Path.GetExtension(Name).ToLowerInvariant()}:{NameSortKey}";

    public string NameSortKey => PinyinSearchHelper.BuildNaturalSortKey(Name);

    public Geometry IconGeometry => FileVectorIconService.GetIcon(Name, IsDirectory).Geometry;

    public IBrush IconBrush => FileVectorIconService.GetIcon(Name, IsDirectory).Color;

    public string DisplaySize => IsDirectory ? "-" : RemoteFileItem.FormatBytes(SizeBytes);
}
