using System.Globalization;
using System.Text.RegularExpressions;
using FileTransfer.App.Models;

namespace FileTransfer.App.Services;

/// <summary>
/// Parses standard Linux OpenSSH sftp `ls -la` output and safely formats remote paths.
/// </summary>
public static partial class SftpListingParser
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"^(?<perm>[drwxlsStT\-]{10})\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<date>(?:[A-Za-z]{3}\s+\d{1,2}\s+(?:\d{2}:\d{2}|\d{4})|\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}))\s+(?<name>.+)$")]
    private static partial Regex LsLineRegex();

    public static string StripAnsi(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return AnsiRegex().Replace(input, string.Empty);
    }

    public static IReadOnlyList<RemoteFileItem> Parse(string rawOutput, string currentDirectory)
    {
        var items = new List<RemoteFileItem>();
        var cleaned = StripAnsi(rawOutput);
        var lines = cleaned.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) ||
                line.StartsWith("sftp>", StringComparison.Ordinal) ||
                line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = LsLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var perm = match.Groups["perm"].Value;
            var sizeStr = match.Groups["size"].Value;
            var dateText = match.Groups["date"].Value;
            var name = match.Groups["name"].Value.Trim();

            if (name == "." || name == "..")
            {
                continue;
            }

            if (perm.StartsWith('l'))
            {
                var arrowIndex = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrowIndex > 0)
                {
                    name = name[..arrowIndex];
                }
            }

            var isDirectory = perm.StartsWith('d');
            long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeBytes);
            var modifiedAt = ParseModifiedDate(dateText);

            items.Add(new RemoteFileItem
            {
                Name = name,
                FullPath = CombineRemotePath(currentDirectory, name),
                IsDirectory = isDirectory,
                SizeBytes = sizeBytes,
                ModifiedText = dateText,
                ModifiedAt = modifiedAt,
                Permissions = perm
            });
        }

        // Default sort: newest modified first, then name ascending.
        return items
            .OrderByDescending(i => i.ModifiedAt)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static DateTime ParseModifiedDate(string dateText)
    {
        var normalized = Regex.Replace(dateText.Trim(), @"\s+", " ");

        if (DateTime.TryParseExact(normalized, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }

        if (DateTime.TryParseExact(normalized, "MMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var withYear))
        {
            return withYear;
        }

        if (DateTime.TryParseExact(normalized, "MMM d HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthDayTime))
        {
            var now = DateTime.Now;
            var candidate = new DateTime(now.Year, monthDayTime.Month, monthDayTime.Day, monthDayTime.Hour, monthDayTime.Minute, 0);
            // Handle year-boundary files (e.g. Dec file viewed in Jan).
            if (candidate > now.AddDays(1))
            {
                candidate = candidate.AddYears(-1);
            }

            return candidate;
        }

        return DateTime.MinValue;
    }

    public static string CombineRemotePath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory == "/")
        {
            return "/" + name.TrimStart('/');
        }

        return directory.TrimEnd('/') + "/" + name.TrimStart('/');
    }

    public static string NormalizeRemoteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    public static string QuoteSftpArg(string path)
    {
        if (path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw new ArgumentException("路径包含非法控制字符。", nameof(path));
        }

        var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
