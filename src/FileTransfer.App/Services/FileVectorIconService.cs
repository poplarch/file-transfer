using System.Collections.Concurrent;
using Avalonia.Media;

namespace FileTransfer.App.Services;

/// <summary>
/// Provides crisp resolution-independent SVG vector geometries and semantic colors
/// for folders and common file types across all DPI scales.
/// </summary>
public static class FileVectorIconService
{
    private static readonly ConcurrentDictionary<string, Geometry> GeometryCache = new(StringComparer.Ordinal);

    // Unified 16x16 viewBox SVG path data:
    // - Folder uses a consistent 14x12 horizontal folder silhouette (x:1..15, y:2..14)
    // - All file types share an identical 12x15 vertical document silhouette (x:2..14, y:0.5..15.5)
    private const string FolderPath =
        "M1.5 3.5A1.5 1.5 0 0 1 3 2h3.4a1.5 1.5 0 0 1 1.15.54L8.6 3.8H13A1.5 1.5 0 0 1 14.5 5.3v7.2A1.5 1.5 0 0 1 13 14H3a1.5 1.5 0 0 1-1.5-1.5v-9Z";

    private const string ArchivePath =
        "M3.5.5h6.5L14 4.5V14a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 2 14V2A1.5 1.5 0 0 1 3.5.5ZM7 2.2v1.4h2V2.2H7Zm0 2.4V6h2V4.6H7ZM7 7v1.4h2V7H7Zm-.5 2.5v2.6h3V9.5h-3Z";

    private const string DocumentPath =
        "M3.5.5h6.5L14 4.5V14a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 2 14V2A1.5 1.5 0 0 1 3.5.5Zm6 1.5v2.5H12L9.5 2Zm-5 5.2v1.2h7V7.2h-7Zm0 2.6V11h7V9.8h-7Zm0 2.6v1.2H9v-1.2H4.5Z";

    private const string CodeConfigPath =
        "M3.5.5h6.5L14 4.5V14a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 2 14V2A1.5 1.5 0 0 1 3.5.5Zm6 1.5v2.5H12L9.5 2Zm-3 5.4L4.2 9.7l2.3 2.3.85-.85L5.9 9.7l1.45-1.45-.85-.85Zm3 0-.85.85L10.1 9.7l-1.45 1.45.85.85 2.3-2.3-2.3-2.3Z";

    private const string ImagePath =
        "M3.5.5h6.5L14 4.5V14a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 2 14V2A1.5 1.5 0 0 1 3.5.5Zm6 1.5v2.5H12L9.5 2Zm-4 5.3a1.1 1.1 0 1 1 0-2.2 1.1 1.1 0 0 1 0 2.2Zm6 5.2H4.5l2.2-2.8 1.5 1.8 1.2-1.3 2.1 2.3Z";

    private const string ExecutablePath =
        "M3.5.5h6.5L14 4.5V14a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 2 14V2A1.5 1.5 0 0 1 3.5.5Zm6 1.5v2.5H12L9.5 2ZM4.6 7.5 6.9 9.7l-2.3 2.2.85.85 3.15-3.05-3.15-3.05-.85.85Zm3.6 3.5v1.2h3.2V11H8.2Z";

    private const string GenericFilePath =
        "M3.5.5h6.5L14 4.5V14a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 2 14V2A1.5 1.5 0 0 1 3.5.5Zm6 1.5v2.5H12L9.5 2Z";

    public static (Geometry Geometry, IBrush Color) GetIcon(string fileName, bool isDirectory)
    {
        if (isDirectory)
        {
            return (Parse(FolderPath), SolidColorBrush.Parse("#E3A018"));
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".zip" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".7z" or ".rar" =>
                (Parse(ArchivePath), SolidColorBrush.Parse("#8250DF")),
            ".txt" or ".log" or ".md" or ".csv" =>
                (Parse(DocumentPath), SolidColorBrush.Parse("#0969DA")),
            ".json" or ".xml" or ".yaml" or ".yml" or ".conf" or ".ini" or ".cs" or ".java" or ".py" or ".js" =>
                (Parse(CodeConfigPath), SolidColorBrush.Parse("#1A7F37")),
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" or ".webp" or ".ico" =>
                (Parse(ImagePath), SolidColorBrush.Parse("#BF3989")),
            ".exe" or ".msi" or ".dll" or ".sh" or ".bat" or ".ps1" =>
                (Parse(ExecutablePath), SolidColorBrush.Parse("#D97706")),
            ".pdf" =>
                (Parse(DocumentPath), SolidColorBrush.Parse("#CF222E")),
            ".xls" or ".xlsx" =>
                (Parse(DocumentPath), SolidColorBrush.Parse("#1A7F37")),
            ".doc" or ".docx" or ".ppt" or ".pptx" =>
                (Parse(DocumentPath), SolidColorBrush.Parse("#0969DA")),
            _ =>
                (Parse(GenericFilePath), SolidColorBrush.Parse("#8C959F"))
        };
    }

    private static Geometry Parse(string pathData) =>
        GeometryCache.GetOrAdd(pathData, static data => Geometry.Parse(data));
}
