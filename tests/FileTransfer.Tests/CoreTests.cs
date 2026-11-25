using FileTransfer.App.Models;
using FileTransfer.App.Security;
using FileTransfer.App.Services;
using Xunit;

namespace FileTransfer.Tests;

public class CoreTests
{
    [Fact]
    public void SftpListingParser_ParsesDirectoriesFilesAndSymlinks()
    {
        var raw = """
                  sftp> ls -la "/data/logs"
                  drwxr-xr-x    2 root     root         4096 Nov 23 14:20 .
                  drwxr-xr-x    5 root     root         4096 Nov 23 14:10 ..
                  drwxr-xr-x    2 app      app          4096 2025-11-20 14:15 archive
                  -rw-r--r--    1 app      app       2097152 2025-11-23 14:18 app log.tar.gz
                  lrwxrwxrwx    1 app      app            12 2025-11-22 14:19 latest -> app.log
                  sftp>
                  """;

        var items = SftpListingParser.Parse(raw, "/data/logs");

        // Sorted by ModifiedAt descending by default: app log.tar.gz (Nov 23) -> latest (Nov 22) -> archive (Nov 20)
        Assert.Equal(3, items.Count);
        Assert.False(items[0].IsDirectory);
        Assert.Equal("app log.tar.gz", items[0].Name);
        Assert.Equal(2097152, items[0].SizeBytes);
        Assert.Equal("/data/logs/app log.tar.gz", items[0].FullPath);

        Assert.Equal("latest", items[1].Name);

        Assert.True(items[2].IsDirectory);
        Assert.Equal("archive", items[2].Name);
        Assert.Equal("/data/logs/archive", items[2].FullPath);
    }

    [Fact]
    public void SftpListingParser_RejectsControlCharsInQuote()
    {
        Assert.Throws<ArgumentException>(() => SftpListingParser.QuoteSftpArg("/tmp/bad\nrm -rf /"));
    }

    [Fact]
    public void ConnectionConfig_DefaultsRelayBasePathToTmp()
    {
        var cfg = new ConnectionConfig { RelayBasePath = "   " };
        Assert.Equal("/tmp", cfg.GetEffectiveRelayBasePath());

        cfg.RelayBasePath = "/data/relay/";
        Assert.Equal("/data/relay", cfg.GetEffectiveRelayBasePath());
    }

    [Fact]
    public void CredentialProtector_RoundTripsOnCurrentPlatform()
    {
        var secret = "P@ssw0rd!#2026";
        var cipher = CredentialProtector.Protect(secret);
        Assert.NotNull(cipher);
        Assert.NotEqual(secret, cipher);

        var plain = CredentialProtector.Unprotect(cipher);
        Assert.Equal(secret, plain);
    }

    [Fact]
    public void ConfigService_SavesAndClearsPasswords()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ft_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new ConfigService(tempFile);
            var cfg = new ConnectionConfig
            {
                JumpHost = "10.0.0.1",
                JumpUsername = "ops",
                JumpPassword = "jump-secret",
                FileServerHost = "10.0.0.2",
                FileServerUsername = "sftpuser",
                FileServerPassword = "fs-secret",
                RememberPasswords = true
            };

            service.Save(cfg);
            var loaded = service.Load();
            Assert.Equal("jump-secret", loaded.JumpPassword);
            Assert.Equal("fs-secret", loaded.FileServerPassword);
            Assert.Equal("/tmp", loaded.GetEffectiveRelayBasePath());

            service.ClearSavedPasswords(loaded);
            var afterClear = service.Load();
            Assert.Equal(string.Empty, afterClear.JumpPassword);
            Assert.Equal(string.Empty, afterClear.FileServerPassword);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void PinyinSearchHelper_MatchesEnglishAndChinesePinyin()
    {
        var items = new[] { "app.log", "测试报告.pdf", "archive.zip", "产品需求.docx" };

        Assert.True(PinyinSearchHelper.MatchesPrefix("测试报告.pdf", "c"));
        Assert.True(PinyinSearchHelper.MatchesPrefix("测试报告.pdf", "csbg"));
        Assert.True(PinyinSearchHelper.MatchesPrefix("测试报告.pdf", "ceshi"));
        Assert.True(PinyinSearchHelper.MatchesFilter("产品需求.docx", "cpxq"));

        // Cycling with 'a' should move from index 0 ("app.log") to index 2 ("archive.zip")
        var idx1 = PinyinSearchHelper.FindNextMatchIndex(items, s => s, "a", -1, true);
        var idx2 = PinyinSearchHelper.FindNextMatchIndex(items, s => s, "a", idx1, true);
        Assert.Equal(0, idx1);
        Assert.Equal(2, idx2);
    }

    [Fact]
    public void PinyinSearchHelper_NaturalSortKeyOrdersEnglishChineseAndNumbers()
    {
        var names = new[] { "zebra.txt", "file10.log", "测试.pdf", "file2.log", "报告.pdf", "app.log" };
        var sorted = names.OrderBy(PinyinSearchHelper.BuildNaturalSortKey, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "app.log", "报告.pdf", "测试.pdf", "file2.log", "file10.log", "zebra.txt" }, sorted);

        var weekdays = new[] { "周一下午第一节", "周一上午第一节", "周五", "周三", "周四" };
        var sortedWeekdays = weekdays.OrderBy(PinyinSearchHelper.BuildNaturalSortKey, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "周一上午第一节", "周一下午第一节", "周三", "周四", "周五" }, sortedWeekdays);
    }

    [Fact]
    public void FileServerPtySession_BlocksProtectedDirectories()
    {
        Assert.True(FileServerPtySession.IsProtectedDirectory("/"));
        Assert.True(FileServerPtySession.IsProtectedDirectory("/etc/"));
        Assert.True(FileServerPtySession.IsProtectedDirectory("/var"));
        Assert.True(FileServerPtySession.IsProtectedDirectory("/data"));
        Assert.False(FileServerPtySession.IsProtectedDirectory("/data/project_a"));
    }
}
