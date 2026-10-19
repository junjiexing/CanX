// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.ViewModels;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class PluginDiagnosticConsoleTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "CanX.PluginDiagnosticConsoleTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Read_ShowsOnlyBoundedPluginLogsAndAuditTrail()
    {
        Directory.CreateDirectory(root);
        File.WriteAllLines(
            Path.Combine(root, "quickjs.log"),
            Enumerable.Range(0, 120).Select(index => $"runtime-{index}"));
        File.WriteAllText(Path.Combine(root, "plugin-audit.jsonl"), "{\"operation\":\"upgrade\"}");
        File.WriteAllText(Path.Combine(root, "app.log"), "must-not-be-shown");

        var result = PluginDiagnosticConsole.Read(root);

        Assert.DoesNotContain("runtime-0\n", result, StringComparison.Ordinal);
        Assert.Contains("runtime-119", result, StringComparison.Ordinal);
        Assert.Contains("plugin-audit.jsonl", result, StringComparison.Ordinal);
        Assert.Contains("\"operation\":\"upgrade\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-shown", result, StringComparison.Ordinal);
        Assert.True(result.Length <= 64 * 1024);
    }

    [Fact]
    public void Read_EmptyDirectoryExplainsHowToEnableLogs()
    {
        Directory.CreateDirectory(root);

        Assert.Contains("启用日志记录", PluginDiagnosticConsole.Read(root), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
