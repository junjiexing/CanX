// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;

namespace CanX.Streaming;

/// <summary>
/// Applies a short, cancellation-aware retry policy to idempotent HTTP reads.
/// A fresh request must be created for every attempt.
/// </summary>
public static class HttpReadRetry
{
    public const int MaximumAttempts = 3;
    private static readonly TimeSpan MaximumServerDelay = TimeSpan.FromSeconds(2);

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = requestFactory() ??
                throw new InvalidOperationException("The HTTP request factory returned null.");
            try
            {
                var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (attempt >= MaximumAttempts || !IsTransientStatus(response.StatusCode))
                {
                    return response;
                }

                var delay = GetDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (
                attempt < MaximumAttempts && IsTransientStatus(exception.StatusCode))
            {
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < MaximumAttempts)
            {
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                attempt < MaximumAttempts && !cancellationToken.IsCancellationRequested)
            {
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        HttpRequestException requestException => IsTransientStatus(requestException.StatusCode),
        InvalidDataException => false,
        IOException => true,
        _ => false,
    };

    internal static bool IsTransientStatus(HttpStatusCode? statusCode) => statusCode is null or
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var requested = retryAfter?.Delta ??
            (retryAfter?.Date - DateTimeOffset.UtcNow);
        if (requested is { } serverDelay && serverDelay > TimeSpan.Zero)
        {
            return serverDelay <= MaximumServerDelay ? serverDelay : MaximumServerDelay;
        }

        return GetDefaultDelay(attempt);
    }

    internal static Task DelayAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(GetDefaultDelay(attempt), cancellationToken);

    private static TimeSpan GetDefaultDelay(int attempt) =>
        TimeSpan.FromMilliseconds(attempt * 100);
}
