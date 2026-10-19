// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CanX.Core.Diagnostics;
using CanX.Plugins.Http;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginHttpClientTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void NetworkGuard_RejectsNonPublicAddresses(string value) =>
        Assert.False(PluginNetworkGuard.IsPublicAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void NetworkGuard_AcceptsPublicAddresses(string value) =>
        Assert.True(PluginNetworkGuard.IsPublicAddress(IPAddress.Parse(value)));

    [Fact]
    public async Task NetworkGuard_RejectsDnsRebindingToPrivateAddress()
    {
        var guard = new PluginNetworkGuard((_, _) =>
            ValueTask.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Loopback }));

        var exception = await Assert.ThrowsAsync<PluginHttpException>(async () =>
            await guard.ResolvePublicAddressesAsync("approved.example.test", CancellationToken.None));

        Assert.Equal("private_network_denied", exception.Code);
    }

    [Fact]
    public async Task NetworkGuard_AllowsPrivateAddressOnlyForExplicitProxyConnection()
    {
        var guard = new PluginNetworkGuard((_, _) =>
            ValueTask.FromResult(new[] { IPAddress.Loopback }));

        Assert.Equal(IPAddress.Loopback, Assert.Single(
            await guard.ResolveProxyAddressesAsync("127.0.0.1", CancellationToken.None)));
        var exception = await Assert.ThrowsAsync<PluginHttpException>(async () =>
            await guard.ResolvePublicAddressesAsync("127.0.0.1", CancellationToken.None));
        Assert.Equal("private_network_denied", exception.Code);
    }

    [Fact]
    public void PluginWebProxy_DoesNotBypassLoopbackDestinations()
    {
        var proxy = new PluginWebProxy(new Uri("http://127.0.0.1:7890"));

        Assert.False(proxy.IsBypassed(new Uri("http://127.0.0.1:8765")));
        Assert.Equal(
            "http://127.0.0.1:7890/",
            proxy.GetProxy(new Uri("http://127.0.0.1:8765")).AbsoluteUri);
    }

    [Fact]
    public async Task SendAsync_ReachesExplicitLoopbackProxyWithGlobalHttpPermission()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(timeout.Token);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
                await stream.WriteAsync(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(),
                    timeout.Token);
                return requestLine;
            }, timeout.Token);
            var manifest = new PluginManifest(
                1, "org.canx.proxy-test", "Proxy test", null, "1.0.0",
                new PluginApiVersion(2, 0), "main.mjs",
                [new PluginPermission(PluginCapabilities.Http, ["*"])]);
            using var client = new PluginHttpClient(
                PluginPermissionSet.FromManifest(manifest), new PluginHttpOptions());

            var result = await client.SendAsync(
                $"{{\"url\":\"http://api.example.test/data\",\"proxy\":\"http://127.0.0.1:{port}\"}}",
                timeout.Token);

            Assert.Contains("\"body\":\"ok\"", result, StringComparison.Ordinal);
            Assert.StartsWith("GET http://api.example.test/data HTTP/1.1", await server, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SendAsync_ReturnsBoundedTextResponse()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("hello", Encoding.UTF8, "text/plain"),
            RequestMessage = request,
        }));
        using var client = CreateClient(handler, ["api.example.test"]);

        var result = await client.SendAsync(
            "{\"url\":\"https://api.example.test/data\"}",
            CancellationToken.None);

        Assert.Contains("\"status\":200", result, StringComparison.Ordinal);
        Assert.Contains("\"body\":\"hello\"", result, StringComparison.Ordinal);
        Assert.Contains("\"bodyEncoding\":\"text\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_LogsSafeTransportDiagnostics()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "TLS failed"));
        var log = new CapturingApplicationLog();
        var manifest = new PluginManifest(
            1,
            "org.canx.http",
            "HTTP",
            null,
            "1.0.0",
            new PluginApiVersion(2, 0),
            "main.mjs",
            [new PluginPermission(PluginCapabilities.Http, ["api.example.test"])]);
        using var client = new PluginHttpClient(
            PluginPermissionSet.FromManifest(manifest),
            new PluginHttpOptions(),
            handler,
            log: log,
            pluginId: manifest.Id);

        var exception = await Assert.ThrowsAsync<PluginHttpException>(async () =>
            await client.SendAsync(
                "{\"url\":\"https://api.example.test/search?token=secret\"}",
                CancellationToken.None));

        Assert.Equal("http_failure", exception.Code);
        Assert.Contains("SecureConnectionError", exception.Message, StringComparison.Ordinal);
        var entry = Assert.Single(log.Entries);
        Assert.Contains("org.canx.http -> api.example.test:443", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_ReturnsBinaryResponseAsBase64()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]),
            RequestMessage = request,
        }));
        using var client = CreateClient(handler, ["api.example.test"]);

        var result = await client.SendAsync(
            "{\"url\":\"https://api.example.test/cover.png\",\"responseType\":\"base64\"}",
            CancellationToken.None);

        Assert.Contains("\"body\":\"iVBORw==\"", result, StringComparison.Ordinal);
        Assert.Contains("\"bodyEncoding\":\"base64\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_ExplicitContentType_ReplacesGeneratedContentType()
    {
        string[]? contentTypes = null;
        using var handler = new StubHandler((request, _) =>
        {
            contentTypes = request.Content?.Headers.GetValues("Content-Type").ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
                RequestMessage = request,
            });
        });
        using var client = CreateClient(handler, ["api.example.test"]);

        await client.SendAsync(
            """
            {"url":"https://api.example.test/search","method":"POST","headers":{"content-type":"application/json"},"body":"{\"query\":\"ubuntu\"}"}
            """,
            CancellationToken.None);

        Assert.Equal(["application/json"], Assert.IsType<string[]>(contentTypes));
    }

    [Fact]
    public async Task SendAsync_RejectsUnknownResponseType()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var client = CreateClient(handler, ["api.example.test"]);

        var exception = await Assert.ThrowsAsync<PluginHttpException>(async () =>
            await client.SendAsync(
                "{\"url\":\"https://api.example.test/cover.png\",\"responseType\":\"stream\"}",
                CancellationToken.None));

        Assert.Equal("invalid_request", exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_UsesAllowedConfiguredHttpProxy()
    {
        using var directHandler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var proxyHandler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("proxied"),
            RequestMessage = request,
        }));
        Uri? selectedProxy = null;
        using var client = CreateClient(
            directHandler,
            ["api.example.test", "proxy.example.test"],
            proxyHandlerFactory: proxy =>
            {
                selectedProxy = proxy;
                return proxyHandler;
            });

        var result = await client.SendAsync(
            "{\"url\":\"https://api.example.test/data\",\"proxy\":\"http://proxy.example.test:7890\"}",
            CancellationToken.None);

        Assert.Equal("http://proxy.example.test:7890/", selectedProxy?.AbsoluteUri);
        Assert.Empty(directHandler.Requests);
        Assert.Single(proxyHandler.Requests);
        Assert.Contains("\"body\":\"proxied\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_RequiresHttpPermissionForProxyHost()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var client = CreateClient(handler, ["api.example.test"]);

        await Assert.ThrowsAsync<PluginPermissionDeniedException>(async () =>
            await client.SendAsync(
                "{\"url\":\"https://api.example.test/data\",\"proxy\":\"http://127.0.0.1:7890\"}",
                CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://proxy.example.test:7890")]
    [InlineData("http://user:secret@proxy.example.test:7890")]
    [InlineData("http://proxy.example.test:7890/path")]
    public async Task SendAsync_RejectsInvalidHttpProxy(string proxy)
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var client = CreateClient(handler, ["api.example.test", "proxy.example.test"]);

        var exception = await Assert.ThrowsAsync<PluginHttpException>(async () =>
            await client.SendAsync(
                $"{{\"url\":\"https://api.example.test/data\",\"proxy\":\"{proxy}\"}}",
                CancellationToken.None));

        Assert.Equal("invalid_request", exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_RechecksRedirectTargetBeforeSecondRequest()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://blocked.example.test/private") },
            RequestMessage = request,
        }));
        using var client = CreateClient(handler, ["api.example.test"]);

        await Assert.ThrowsAsync<PluginPermissionDeniedException>(async () =>
            await client.SendAsync("{\"url\":\"https://api.example.test/start\"}", CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_StripsCredentialsOnCrossOriginRedirect()
    {
        using var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.example.test")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("https://cdn.example.test/data") },
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
                RequestMessage = request,
            });
        });
        using var client = CreateClient(handler, ["api.example.test", "cdn.example.test"]);

        await client.SendAsync(
            "{\"url\":\"https://api.example.test/start\",\"headers\":{\"Authorization\":\"Bearer secret\"}}",
            CancellationToken.None);

        Assert.True(handler.Requests[0].Headers.Contains("Authorization"));
        Assert.False(handler.Requests[1].Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task SendAsync_RejectsEmbeddedCredentialsAndNonHttpSchemes()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        using var client = CreateClient(handler, ["api.example.test"]);

        await Assert.ThrowsAsync<PluginHttpException>(async () =>
            await client.SendAsync(
                "{\"url\":\"https://user:secret@api.example.test/data\"}",
                CancellationToken.None));
        await Assert.ThrowsAsync<PluginHttpException>(async () =>
            await client.SendAsync("{\"url\":\"file:///etc/passwd\"}", CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_StopsStreamingResponseAtLimit()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[9]),
            RequestMessage = request,
        }));
        var options = new PluginHttpOptions { MaximumResponseBodyBytes = 8 };
        using var client = CreateClient(handler, ["api.example.test"], options);

        var exception = await Assert.ThrowsAsync<PluginHttpException>(async () =>
            await client.SendAsync("{\"url\":\"https://api.example.test/data\"}", CancellationToken.None));

        Assert.Equal("response_too_large", exception.Code);
    }

    [Fact]
    public async Task SendAsync_CanReturnAnExplicitlyBoundedResponsePrefix()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("search-results-followed-by-large-release-list"u8.ToArray()),
            RequestMessage = request,
        }));
        var options = new PluginHttpOptions { MaximumResponseBodyBytes = 64 };
        using var client = CreateClient(handler, ["api.example.test"], options);

        var result = await client.SendAsync(
            """
            {"url":"https://api.example.test/search","maximumResponseBytes":15,"truncateResponse":true}
            """,
            CancellationToken.None);
        using var document = JsonDocument.Parse(result);

        Assert.Equal("search-results-", document.RootElement.GetProperty("body").GetString());
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task SendAsync_RejectsPerRequestLimitOutsideHostBounds(int maximumResponseBytes)
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException());
        var options = new PluginHttpOptions { MaximumResponseBodyBytes = 64 };
        using var client = CreateClient(handler, ["api.example.test"], options);

        var exception = await Assert.ThrowsAsync<PluginHttpException>(async () =>
            await client.SendAsync(
                $$"""{"url":"https://api.example.test/search","maximumResponseBytes":{{maximumResponseBytes}}}""",
                CancellationToken.None));

        Assert.Equal("invalid_request", exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_QueuesRequestsAtConcurrencyLimit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (request, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
                RequestMessage = request,
            };
        });
        var options = new PluginHttpOptions { MaximumConcurrentRequests = 1 };
        using var client = CreateClient(handler, ["api.example.test"], options);

        var first = client.SendAsync(
            "{\"url\":\"https://api.example.test/one\"}",
            CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = client.SendAsync(
            "{\"url\":\"https://api.example.test/two\"}",
            CancellationToken.None).AsTask();
        await Task.Delay(50);

        Assert.Single(handler.Requests);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_CancelsRequestAtConfiguredTimeout()
    {
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        var options = new PluginHttpOptions { Timeout = TimeSpan.FromMilliseconds(50) };
        using var client = CreateClient(handler, ["api.example.test"], options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.SendAsync("{\"url\":\"https://api.example.test/data\"}", CancellationToken.None));
    }

    [Fact]
    public async Task LoadRemoteImageAsync_UsesDedicatedPermissionValidatesSignatureAndCachesBytes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3 };
        using var handler = new StubHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(png),
                RequestMessage = request,
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        });
        using var client = CreateRemoteImageClient(handler, ["images.example.test"]);

        var first = await client.LoadRemoteImageAsync(
            "https://images.example.test/cover.png",
            CancellationToken.None);
        var second = await client.LoadRemoteImageAsync(
            "https://images.example.test/cover.png",
            CancellationToken.None);

        Assert.Equal(png, first.ToArray());
        Assert.Equal(png, second.ToArray());
        Assert.Single(handler.Requests);
        Assert.Empty(handler.Requests[0].Headers);
    }

    [Fact]
    public async Task LoadRemoteImageAsync_UsesConfiguredProxyAndKeepsCachesSeparate()
    {
        var directPng = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1 };
        var proxyPng = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 2 };
        using var direct = new StubHandler((request, _) => Task.FromResult(ImageResponse(request, directPng)));
        using var throughProxy = new StubHandler((request, _) => Task.FromResult(ImageResponse(request, proxyPng)));
        var manifest = new PluginManifest(
            1, "org.canx.http", "HTTP", null, "1.0.0", new PluginApiVersion(2, 0), "main.mjs",
            [new PluginPermission(PluginCapabilities.Http, ["*"]),
             new PluginPermission(PluginCapabilities.UiRemoteImages, ["*"])]);
        Uri? selectedProxy = null;
        using var client = new PluginHttpClient(
            PluginPermissionSet.FromManifest(manifest),
            new PluginHttpOptions(),
            direct,
            proxy =>
            {
                selectedProxy = proxy;
                return throughProxy;
            });
        const string cover = "https://lain.bgm.tv/pic/cover/s/example.jpg";

        Assert.Equal(directPng, (await client.LoadRemoteImageAsync(cover, CancellationToken.None)).ToArray());
        client.SetDefaultProxy("http://127.0.0.1:7890");
        Assert.Equal(proxyPng, (await client.LoadRemoteImageAsync(cover, CancellationToken.None)).ToArray());
        client.SetDefaultProxy(null);
        Assert.Equal(directPng, (await client.LoadRemoteImageAsync(cover, CancellationToken.None)).ToArray());

        Assert.Equal("http://127.0.0.1:7890/", selectedProxy?.AbsoluteUri);
        Assert.Single(direct.Requests);
        Assert.Single(throughProxy.Requests);
    }

    private static HttpResponseMessage ImageResponse(HttpRequestMessage request, byte[] image)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(image),
            RequestMessage = request,
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return response;
    }

    [Fact]
    public async Task LoadRemoteImageAsync_RechecksRedirectScope()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://other.example.test/cover.webp") },
            RequestMessage = request,
        }));
        using var client = CreateRemoteImageClient(handler, ["images.example.test"]);

        await Assert.ThrowsAsync<PluginPermissionDeniedException>(async () =>
            await client.LoadRemoteImageAsync(
                "https://images.example.test/cover.webp",
                CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    public async Task LoadRemoteImageAsync_RejectsUnsupportedOrSpoofedContent(string contentType)
    {
        using var handler = new StubHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("<svg></svg>"u8.ToArray()),
                RequestMessage = request,
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        });
        using var client = CreateRemoteImageClient(handler, ["*"]);

        var exception = await Assert.ThrowsAsync<PluginHttpException>(async () =>
            await client.LoadRemoteImageAsync(
                "https://arbitrary.example/cover",
                CancellationToken.None));

        Assert.Equal("invalid_remote_image", exception.Code);
    }

    private static PluginHttpClient CreateClient(
        HttpMessageHandler handler,
        string[] domains,
        PluginHttpOptions? options = null,
        Func<Uri, HttpMessageHandler>? proxyHandlerFactory = null)
    {
        var manifest = new PluginManifest(
            1,
            "org.canx.http",
            "HTTP",
            null,
            "1.0.0",
            new PluginApiVersion(2, 0),
            "main.mjs",
            [new PluginPermission(PluginCapabilities.Http, domains)]);
        return new PluginHttpClient(
            PluginPermissionSet.FromManifest(manifest),
            options ?? new PluginHttpOptions(),
            handler,
            proxyHandlerFactory);
    }

    private static PluginHttpClient CreateRemoteImageClient(
        HttpMessageHandler handler,
        string[] domains)
    {
        var manifest = new PluginManifest(
            1,
            "org.canx.http",
            "HTTP",
            null,
            "1.0.0",
            new PluginApiVersion(2, 0),
            "main.mjs",
            [new PluginPermission(PluginCapabilities.UiRemoteImages, domains)]);
        return new PluginHttpClient(
            PluginPermissionSet.FromManifest(manifest),
            new PluginHttpOptions(),
            handler);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(Clone(request));
            return callback(request, cancellationToken);
        }

        private static HttpRequestMessage Clone(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }

    private sealed class CapturingApplicationLog : IApplicationLog
    {
        public bool IsEnabled => true;

        public string DirectoryPath => string.Empty;

        public List<(LogComponent Component, ApplicationLogLevel Level, string Message)> Entries { get; } = [];

        public event Action<bool>? EnabledChanged;

        public string GetFilePath(LogComponent component) => string.Empty;

        public void SetEnabled(bool enabled) => EnabledChanged?.Invoke(enabled);

        public void Write(
            LogComponent component,
            ApplicationLogLevel level,
            string message,
            Exception? exception = null) => Entries.Add((component, level, message));

        public void Dispose()
        {
        }
    }
}
