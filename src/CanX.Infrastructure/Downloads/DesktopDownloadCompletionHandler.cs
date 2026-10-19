// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using CanX.Core.Downloads;

namespace CanX.Infrastructure.Downloads;

public sealed class DesktopDownloadCompletionHandler : IDownloadCompletionHandler
{
    public async ValueTask<(bool NotificationSent, bool ActionExecuted)> HandleAsync(
        DownloadTaskSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var notificationSent = snapshot.NotifyOnCompletion &&
            await TrySendNotificationAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var actionExecuted = snapshot.CompletionAction != DownloadCompletionAction.None &&
            await ExecuteActionAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return (notificationSent, actionExecuted);
    }

    private static async ValueTask<bool> TrySendNotificationAsync(
        DownloadTaskSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            using var process = Start("notify-send", "--app-name=CanX", "下载完成", snapshot.DisplayName);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static async ValueTask<bool> ExecuteActionAsync(
        DownloadTaskSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var target = DownloadCompletionTargets.Resolve(snapshot);
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException("下载完成动作没有可打开的目标路径。");
        }

        using var process = OperatingSystem.IsLinux()
            ? Start("xdg-open", target)
            : OperatingSystem.IsMacOS()
                ? Start("open", target)
                : Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })
                    ?? throw new InvalidOperationException("无法启动系统文件打开程序。");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"系统文件打开程序退出，代码 {process.ExitCode}。");
        }

        return true;
    }

    private static Process Start(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动系统程序 '{fileName}'。");
    }
}
