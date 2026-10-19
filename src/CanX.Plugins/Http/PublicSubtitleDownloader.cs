// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;

namespace CanX.Plugins.Http;

/// <summary>Downloads a small public subtitle file without exposing its URL to mpv.</summary>
public static class PublicSubtitleDownloader
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseProxy = false,
        ConnectCallback = new PluginNetworkGuard().ConnectAsync,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static async Task<byte[]> DownloadAsync(Uri source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var current = source;
        try
        {
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                Validate(current);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                    HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    current = response.Headers.Location is { } location
                        ? new Uri(current, location)
                        : throw new HttpRequestException("字幕下载地址发生了无效跳转。");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumBytes)
                {
                    throw new InvalidDataException("字幕文件超过 8 MiB，已取消加载。");
                }
                if (response.Content.Headers.ContentType?.MediaType is "text/html")
                {
                    throw new InvalidDataException("字幕地址返回了网页，而不是字幕文件。");
                }
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[32 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (output.Length + read > MaximumBytes)
                    {
                        throw new InvalidDataException("字幕文件超过 8 MiB，已取消加载。");
                    }
                    output.Write(buffer, 0, read);
                }
                return output.Length > 0
                    ? output.ToArray()
                    : throw new InvalidDataException("字幕文件内容为空。");
            }
            throw new HttpRequestException("字幕下载地址跳转次数过多。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("字幕下载超时，请稍后重试。");
        }
    }

    private static void Validate(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("字幕地址必须是公网 HTTP 或 HTTPS 地址。", nameof(uri));
        }
    }
}
