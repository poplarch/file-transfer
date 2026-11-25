using System.Collections.Concurrent;
using System.Text;
using TinyPinyin;

namespace FileTransfer.App.Services;

/// <summary>
/// Provides English prefix and Chinese Pinyin (initials + full) matching
/// for quick file filtering and incremental keyboard navigation.
/// </summary>
public static class PinyinSearchHelper
{
    private static readonly ConcurrentDictionary<string, (string Initials, string Full)> Cache = new(StringComparer.Ordinal);

    public static (string Initials, string Full) GetSearchKeys(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return (string.Empty, string.Empty);
        }

        return Cache.GetOrAdd(name, static key =>
        {
            var initials = new StringBuilder(key.Length);
            var full = new StringBuilder(key.Length * 3);

            foreach (var ch in key)
            {
                if (PinyinHelper.IsChinese(ch))
                {
                    var py = PinyinHelper.GetPinyin(ch);
                    if (!string.IsNullOrEmpty(py))
                    {
                        var lower = py.ToLowerInvariant();
                        initials.Append(lower[0]);
                        full.Append(lower);
                    }
                }
                else if (char.IsLetterOrDigit(ch))
                {
                    var lower = char.ToLowerInvariant(ch);
                    initials.Append(lower);
                    full.Append(lower);
                }
            }

            return (initials.ToString(), full.ToString());
        });
    }

    public static bool MatchesPrefix(string name, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return false;
        }

        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var (initials, full) = GetSearchKeys(name);
        var normalized = NormalizeQuery(prefix);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        return initials.StartsWith(normalized, StringComparison.Ordinal) ||
               full.StartsWith(normalized, StringComparison.Ordinal);
    }

    public static bool MatchesFilter(string name, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var (initials, full) = GetSearchKeys(name);
        var normalized = NormalizeQuery(query);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        return initials.Contains(normalized, StringComparison.Ordinal) ||
               full.Contains(normalized, StringComparison.Ordinal);
    }

    public static int FindNextMatchIndex<T>(
        IReadOnlyList<T> items,
        Func<T, string> nameSelector,
        string query,
        int currentIndex,
        bool advanceToNext)
    {
        if (items.Count == 0 || string.IsNullOrEmpty(query))
        {
            return -1;
        }

        var start = advanceToNext
            ? (currentIndex + 1) % items.Count
            : Math.Max(0, currentIndex);

        for (var offset = 0; offset < items.Count; offset++)
        {
            var idx = (start + offset) % items.Count;
            if (MatchesPrefix(nameSelector(items[idx]), query))
            {
                return idx;
            }
        }

        return -1;
    }

    private static string NormalizeQuery(string query)
    {
        var sb = new StringBuilder(query.Length);
        foreach (var ch in query)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString();
    }

    private static readonly ConcurrentDictionary<string, string> SortKeyCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds a natural sort key supporting English, Chinese Pinyin (A-Z), Arabic numerals,
    /// and Chinese numerals (e.g. 周一上午第一节 &lt; 周一下午第一节 &lt; 周三 &lt; 周四 &lt; 周五, file2 &lt; file10).
    /// </summary>
    public static string BuildNaturalSortKey(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        return SortKeyCache.GetOrAdd(name, static key =>
        {
            var sb = new StringBuilder(key.Length * 3);
            var i = 0;
            while (i < key.Length)
            {
                var ch = key[i];
                if (char.IsDigit(ch))
                {
                    var start = i;
                    while (i < key.Length && char.IsDigit(key[i]))
                    {
                        i++;
                    }

                    var numStr = key[start..i].TrimStart('0');
                    if (numStr.Length == 0) numStr = "0";
                    AppendNumericToken(sb, numStr);
                }
                else if (IsChineseNumeralChar(ch))
                {
                    var start = i;
                    while (i < key.Length && IsChineseNumeralChar(key[i]))
                    {
                        i++;
                    }

                    var cnToken = key[start..i];
                    if (TryParseChineseNumeral(cnToken, out var number))
                    {
                        AppendNumericToken(sb, number.ToString());
                    }
                    else
                    {
                        foreach (var c in cnToken)
                        {
                            var py = PinyinHelper.GetPinyin(c);
                            sb.Append(string.IsNullOrEmpty(py) ? c.ToString() : py.ToLowerInvariant());
                        }
                    }
                }
                else if (ch is '日' or '天' && i > 0 && key[i - 1] is '周' or '期')
                {
                    // 周日 / 星期日 / 星期天 sort after 周六 (6)
                    AppendNumericToken(sb, "7");
                    i++;
                }
                else if (PinyinHelper.IsChinese(ch))
                {
                    var py = PinyinHelper.GetPinyin(ch);
                    sb.Append(string.IsNullOrEmpty(py) ? ch.ToString() : py.ToLowerInvariant());
                    i++;
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    i++;
                }
            }

            sb.Append('\0');
            sb.Append(key.ToLowerInvariant());
            return sb.ToString();
        });
    }

    private static void AppendNumericToken(StringBuilder sb, string digits)
    {
        sb.Append('\u0001');
        sb.Append(digits.Length.ToString("D2"));
        sb.Append(digits);
    }

    private static bool IsChineseNumeralChar(char ch) =>
        ch is '零' or '〇' or '一' or '二' or '两' or '三' or '四' or '五' or '六' or '七' or '八' or '九' or '十' or '百' or '千';

    private static bool TryParseChineseNumeral(string text, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // Pure positional digits (e.g. 二零二六)
        if (!text.Contains('十') && !text.Contains('百') && !text.Contains('千'))
        {
            long acc = 0;
            foreach (var ch in text)
            {
                var d = ChineseDigitValue(ch);
                if (d < 0) return false;
                acc = acc * 10 + d;
            }
            value = acc;
            return true;
        }

        long total = 0;
        long current = 0;
        foreach (var ch in text)
        {
            var d = ChineseDigitValue(ch);
            if (d >= 0)
            {
                current = d;
                continue;
            }

            var unit = ch switch
            {
                '十' => 10L,
                '百' => 100L,
                '千' => 1000L,
                _ => 0L
            };
            if (unit == 0) return false;

            total += (current == 0 ? 1 : current) * unit;
            current = 0;
        }

        total += current;
        value = total;
        return true;
    }

    private static int ChineseDigitValue(char ch) => ch switch
    {
        '零' or '〇' => 0,
        '一' => 1,
        '二' or '两' => 2,
        '三' => 3,
        '四' => 4,
        '五' => 5,
        '六' => 6,
        '七' => 7,
        '八' => 8,
        '九' => 9,
        _ => -1
    };
}
