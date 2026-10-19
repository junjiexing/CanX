// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;

namespace CanX.MediaAccess;

internal static class NetworkMediaRetry
{
    public const int MaximumAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static bool IsTransient(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return false;
        }

        if (exception is SmbStatusException smbStatus)
        {
            return smbStatus.IsTransient;
        }

        if (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }

        if (exception is IOException or SocketException or TimeoutException)
        {
            return true;
        }

        return exception.InnerException is not null && IsTransient(exception.InnerException);
    }

    public static Task DelayAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(RetryDelay * attempt, cancellationToken);
}
