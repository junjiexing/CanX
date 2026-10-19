// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Diagnostics;
using CanX.Infrastructure.Diagnostics;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class FileApplicationLogTests
{
    [Fact]
    public void Write_SeparatesComponentsAndStopsImmediatelyWhenDisabled()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var log = new FileApplicationLog(directory);
            log.Write(LogComponent.App, ApplicationLogLevel.Information, "before-enable");
            Assert.False(Directory.Exists(directory));

            log.SetEnabled(true);
            log.Write(LogComponent.App, ApplicationLogLevel.Information, "app-entry");
            log.Write(LogComponent.Aria2, ApplicationLogLevel.Warning, "aria-entry");
            log.Write(LogComponent.Mpv, ApplicationLogLevel.Debug, "mpv-entry");
            log.Write(LogComponent.QuickJs, ApplicationLogLevel.Error, "quickjs-entry");
            log.SetEnabled(false);
            log.Write(LogComponent.Mpv, ApplicationLogLevel.Error, "after-disable");

            var appText = File.ReadAllText(log.GetFilePath(LogComponent.App));
            var ariaText = File.ReadAllText(log.GetFilePath(LogComponent.Aria2));
            var mpvText = File.ReadAllText(log.GetFilePath(LogComponent.Mpv));
            var quickJsText = File.ReadAllText(log.GetFilePath(LogComponent.QuickJs));
            Assert.Contains("app-entry", appText, StringComparison.Ordinal);
            Assert.DoesNotContain("aria-entry", appText, StringComparison.Ordinal);
            Assert.Equal(1, CountLinesContaining(ariaText, "aria-entry"));
            Assert.Equal(1, CountLinesContaining(mpvText, "mpv-entry"));
            Assert.DoesNotContain("after-disable", mpvText, StringComparison.Ordinal);
            Assert.Equal(1, CountLinesContaining(quickJsText, "quickjs-entry"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SetEnabled_NotifiesSubscribersOnlyWhenStateChanges()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var log = new FileApplicationLog(directory);
            var changes = new List<bool>();
            log.EnabledChanged += changes.Add;

            log.SetEnabled(true);
            log.SetEnabled(true);
            log.SetEnabled(false);
            log.SetEnabled(false);

            Assert.Equal([true, false], changes);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Write_RedactsCredentialsQueriesAndMultilineContent()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var log = new FileApplicationLog(directory);
            log.SetEnabled(true);
            log.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                "GET https://alice:secret@example.test/video?id=42&token=abc#part\nAuthorization: Bearer xyz");

            var text = File.ReadAllText(log.GetFilePath(LogComponent.App));
            Assert.DoesNotContain("alice", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("abc", text, StringComparison.Ordinal);
            Assert.DoesNotContain("xyz", text, StringComparison.Ordinal);
            Assert.Contains("<redacted>", text, StringComparison.Ordinal);
            Assert.Contains("\\n", text, StringComparison.Ordinal);
            Assert.Equal(2, File.ReadLines(log.GetFilePath(LogComponent.App)).Count());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Write_IsSafeUnderConcurrentComponentWrites()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var log = new FileApplicationLog(directory);
            log.SetEnabled(true);

            Parallel.For(0, 200, index =>
            {
                log.Write(LogComponent.Mpv, ApplicationLogLevel.Debug, $"mpv-{index}");
                log.Write(LogComponent.QuickJs, ApplicationLogLevel.Debug, $"quickjs-{index}");
            });

            Assert.Equal(200, File.ReadLines(log.GetFilePath(LogComponent.Mpv)).Count());
            Assert.Equal(200, File.ReadLines(log.GetFilePath(LogComponent.QuickJs)).Count());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static int CountLinesContaining(string text, string value) =>
        text.Split(Environment.NewLine).Count(line => line.Contains(value, StringComparison.Ordinal));

    private static string CreateTemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), $"canx-logs-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
