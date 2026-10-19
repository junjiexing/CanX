// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text;
using System.Net;
using CanX.Plugins.Manifest;
using CanX.Plugins.Downloads;
using CanX.Plugins.Media;
using CanX.Plugins.Packaging;
using CanX.Plugins.Runtime;
using CanX.Plugins.Security;
using CanX.Plugins.Services;
using CanX.Plugins.Ui;
using CanX.Core.Security;
using CanX.Core.Diagnostics;
using CanX.Streaming;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class QuickJsPluginHostTests
{
    [Fact]
    public async Task SetDefaultProxy_UsesHttpPermissionAndResolvesNativeCall()
    {
        using var fixture = new HostFixture(http: true);
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                1, "canx:http/set-default-proxy", "{\"proxy\":\"http://api.example.test:7890\"}"),
        };
        await using var host = fixture.CreateHost(factory, new StubToolDispatcher());

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(resolution.IsError);
        Assert.Equal("null", resolution.Json);
    }

    [Fact]
    public async Task HttpCallbacks_RunConcurrentlyUpToConfiguredLimit()
    {
        using var fixture = new HostFixture(http: true);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;
        using var handler = new CallbackHttpHandler(async (request, cancellationToken) =>
        {
            if (Interlocked.Increment(ref enteredCount) == 4)
            {
                allEntered.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
                RequestMessage = request,
            };
        });
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime =>
            {
                for (ulong requestId = 1; requestId <= 4; requestId++)
                {
                    runtime.RequestTool(
                        requestId,
                        "canx:http/request",
                        $"{{\"url\":\"https://api.example.test/{requestId}\"}}");
                }
            },
        };
        await using var host = fixture.CreateHost(
            factory,
            new StubToolDispatcher(),
            httpHandler: handler);

        await host.StartAsync();
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(4, Volatile.Read(ref enteredCount));
        release.TrySetResult();
    }

    [Fact]
    public async Task ManifestPage_AllowsImagePayloadBeyondGeneralToolLimit()
    {
        using var fixture = new HostFixture(uiPage: true);
        var imageBytes = new byte[100 * 1024];
        imageBytes[0] = 0xFF;
        imageBytes[1] = 0xD8;
        imageBytes[2] = 0xFF;
        var image = $"data:image/jpeg;base64,{Convert.ToBase64String(imageBytes)}";
        var input = JsonSerializer.Serialize(new
        {
            content = new
            {
                type = "gallery",
                props = new
                {
                    items = Enumerable.Range(1, 4).Select(index => new
                    {
                        id = $"item-{index}",
                        title = $"Item {index}",
                        image,
                    }),
                },
            },
            interactive = false,
        });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(input) > 256 * 1024);
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(5, "canx:ui/page/mount", input),
        };
        var registry = new PluginUiRegistry();
        await using var host = fixture.CreateHost(
            factory,
            new StubToolDispatcher(),
            uiRegistry: registry);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(resolution.IsError);
        Assert.True(Assert.Single(registry.Pages).IsMounted);
    }

    [Fact]
    public async Task StartAsync_CreatesAndEvaluatesRuntimeOnDedicatedThread()
    {
        using var fixture = new HostFixture();
        var factory = new FakeRuntimeFactory();
        await using var host = fixture.CreateHost(factory, new StubToolDispatcher());

        await host.StartAsync();

        Assert.Equal(PluginHostState.Running, host.State);
        Assert.Equal("export const plugin = true;", factory.Runtime.Source);
        Assert.Contains("apiVersion: \"2.0\"", factory.Runtime.BootstrapSource, StringComparison.Ordinal);
        Assert.Contains(
            "typeof error.message === \"string\"",
            factory.Runtime.BootstrapSource,
            StringComparison.Ordinal);
        Assert.NotEqual(Environment.CurrentManagedThreadId, factory.Runtime.EvaluationThreadId);
        Assert.Equal(factory.Runtime.CreationThreadId, factory.Runtime.EvaluationThreadId);
    }

    [Fact]
    public async Task StartAsync_PreloadsAllPackageModulesWithPortableNames()
    {
        using var fixture = new HostFixture();
        fixture.WriteModule("lib/math.mjs", "export const add = (a, b) => a + b;");
        var factory = new FakeRuntimeFactory();
        await using var host = fixture.CreateHost(factory, new StubToolDispatcher());

        await host.StartAsync();

        Assert.Equal("export const plugin = true;", factory.Runtime.Modules["main.mjs"]);
        Assert.Contains("add =", factory.Runtime.Modules["lib/math.mjs"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolCallback_AllowedToolDispatchesAndResolvesOnRuntimeThread()
    {
        using var fixture = new HostFixture(["playback.open"]);
        var dispatcher = new StubToolDispatcher("{\"opened\":true}");
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(7, "playback.open", "{\"uri\":\"canx://media/1\"}"),
        };
        await using var host = fixture.CreateHost(factory, dispatcher);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("org.canx.runtime", dispatcher.LastRequest?.PluginId);
        Assert.Equal("playback.open", dispatcher.LastRequest?.ToolName);
        Assert.Equal("{\"opened\":true}", resolution.Json);
        Assert.False(resolution.IsError);
        Assert.Equal(factory.Runtime.EvaluationThreadId, resolution.ThreadId);
        Assert.True(factory.Runtime.PumpCount >= 2);
    }

    [Fact]
    public async Task ToolCallback_DeniedToolNeverReachesDispatcher()
    {
        using var fixture = new HostFixture();
        var dispatcher = new StubToolDispatcher();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(9, "downloads.add", "{}"),
        };
        await using var host = fixture.CreateHost(factory, dispatcher);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(dispatcher.LastRequest);
        Assert.True(resolution.IsError);
        Assert.Contains("permission_denied", resolution.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolCallback_InvalidJsonIsIsolatedAsToolError()
    {
        using var fixture = new HostFixture(["playback.open"]);
        var dispatcher = new StubToolDispatcher();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(11, "playback.open", "not-json"),
        };
        await using var host = fixture.CreateHost(factory, dispatcher);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(dispatcher.LastRequest);
        Assert.True(resolution.IsError);
        Assert.Contains("tool_error", resolution.Json, StringComparison.Ordinal);
        Assert.Equal(PluginHostState.Running, host.State);
    }

    [Fact]
    public async Task StorageCallback_RequiresCapabilityAndNeverReachesToolDispatcher()
    {
        using var fixture = new HostFixture();
        var dispatcher = new StubToolDispatcher();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(13, "canx:storage/get", "{\"key\":\"test\"}"),
        };
        await using var host = fixture.CreateHost(factory, dispatcher);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(dispatcher.LastRequest);
        Assert.True(resolution.IsError);
        Assert.Contains("permission_denied", resolution.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StorageCallback_PersistsValueOutsidePluginPackage()
    {
        using var fixture = new HostFixture(storage: true);
        var dispatcher = new StubToolDispatcher();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                15,
                "canx:storage/set",
                "{\"key\":\"position\",\"value\":42}"),
        };
        await using var host = fixture.CreateHost(factory, dispatcher);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(resolution.IsError);
        Assert.Null(dispatcher.LastRequest);
        Assert.True(File.Exists(Path.Combine(fixture.DataRoot, "org.canx.runtime", "storage.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "storage.json")));
    }

    [Fact]
    public async Task SecretCallback_RequiresExactManifestScope()
    {
        using var fixture = new HostFixture(secretNames: ["api-token"]);
        var credentials = new PluginSecretStoreTests.MemoryCredentialStore();
        var dispatcher = new StubToolDispatcher();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                17,
                "canx:secrets/set",
                "{\"name\":\"other-token\",\"value\":\"must-not-leak\"}"),
        };
        await using var host = fixture.CreateHost(factory, dispatcher, credentialStore: credentials);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(resolution.IsError);
        Assert.Contains("permission_denied", resolution.Json, StringComparison.Ordinal);
        Assert.Empty(credentials.Values);
        Assert.Null(dispatcher.LastRequest);
    }

    [Fact]
    public async Task SecretCallback_UsesCredentialStoreWithoutToolDispatch()
    {
        using var fixture = new HostFixture(secretNames: ["api-token"]);
        var credentials = new PluginSecretStoreTests.MemoryCredentialStore();
        var dispatcher = new StubToolDispatcher();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                19,
                "canx:secrets/set",
                "{\"name\":\"api-token\",\"value\":\"secret\"}"),
        };
        await using var host = fixture.CreateHost(factory, dispatcher, credentialStore: credentials);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(resolution.IsError);
        Assert.Equal("secret", Assert.Single(credentials.Values).Value);
        Assert.Null(dispatcher.LastRequest);
    }

    [Fact]
    public async Task ServiceRegistration_InvokesPluginAndUnregistersOnDispose()
    {
        using var fixture = new HostFixture(serviceProvideScopes: ["org.canx.test.echo"]);
        var registry = new PluginServiceRegistry();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                121,
                "canx:services/register",
                """
                {"id":"echo","contract":"org.canx.test.echo","contractVersion":"1.0","title":"Echo","risk":"readOnly","audiences":["plugin","ai"],"inputSchema":{"type":"object","required":["value"]},"outputSchema":{"type":"object","required":["value"]}}
                """),
            OnCallbackInvoke = (runtime, requestId, registrationId, inputJson) =>
            {
                Assert.Equal("service:echo", registrationId);
                Assert.Contains("\"value\":42", inputJson, StringComparison.Ordinal);
                runtime.RequestTool(
                    122,
                    "canx:callback/complete",
                    $"{{\"requestId\":{requestId},\"ok\":true,\"result\":{{\"value\":42}}}}");
            },
        };
        var host = fixture.CreateHost(
            factory,
            new StubToolDispatcher(),
            serviceRegistry: registry);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(resolution.IsError);
        Assert.Equal("org.canx.runtime/echo", Assert.Single(registry.Services).Id);

        using var input = JsonDocument.Parse("{\"value\":42}");
        var result = await registry.InvokeAsync(
            "org.canx.runtime/echo",
            input.RootElement,
            PluginServiceCallContext.ForPlugin("org.canx.client"));
        Assert.Equal(42, result.GetProperty("value").GetInt32());

        await host.DisposeAsync();
        Assert.Empty(registry.Services);
    }

    [Fact]
    public async Task ConfigurationMount_RequiresManifestDeclarationAndTracksRuntimeLifetime()
    {
        using var fixture = new HostFixture(uiConfiguration: true);
        var registry = new PluginUiRegistry();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                123,
                "canx:ui/configuration/mount",
                "{\"content\":{\"type\":\"text\",\"props\":{\"text\":\"Settings\"}}}"),
        };
        var host = fixture.CreateHost(factory, new StubToolDispatcher(), uiRegistry: registry);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(resolution.IsError);
        var configuration = Assert.Single(registry.Configurations);
        Assert.True(configuration.IsMounted);
        Assert.Equal("Runtime settings", configuration.Title);
        await host.DisposeAsync();
        Assert.Empty(registry.Configurations);
    }

    [Fact]
    public async Task ConsoleLog_IsWrittenWithPluginIdentity()
    {
        using var fixture = new HostFixture();
        var log = new CapturingApplicationLog();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                124,
                "canx:log/write",
                "{\"level\":\"info\",\"message\":\"hello from javascript\"}"),
        };
        await using var host = fixture.CreateHost(factory, new StubToolDispatcher(), log: log);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(resolution.IsError);
        Assert.Contains(log.Entries, entry =>
            entry.Level == ApplicationLogLevel.Information &&
            entry.Message.Contains("[plugin:org.canx.runtime] hello from javascript", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConsoleLog_IsLimitedToSixteenKiBUtf8()
    {
        using var fixture = new HostFixture();
        var log = new CapturingApplicationLog();
        var message = new string('界', 10_000);
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                125,
                "canx:log/write",
                JsonSerializer.Serialize(new { level = "warn", message })),
        };
        await using var host = fixture.CreateHost(factory, new StubToolDispatcher(), log: log);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(resolution.IsError);
        var entry = Assert.Single(log.Entries, entry =>
            entry.Message.StartsWith("[plugin:org.canx.runtime]", StringComparison.Ordinal));
        Assert.Equal(ApplicationLogLevel.Warning, entry.Level);
        Assert.True(Encoding.UTF8.GetByteCount(entry.Message) <= 16 * 1024);
        Assert.DoesNotContain('\uFFFD', entry.Message);
    }

    [Fact]
    public async Task PlaybackSdkAction_MapsToStableToolAndCapability()
    {
        using var fixture = new HostFixture(playbackControl: true);
        var dispatcher = new StubToolDispatcher("{\"position\":12}");
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                27,
                "canx:playback/invoke",
                "{\"action\":\"seek\",\"input\":{\"seconds\":12}}"),
        };
        await using var host = fixture.CreateHost(factory, dispatcher);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(resolution.IsError);
        Assert.Equal("playback.seek", dispatcher.LastRequest?.ToolName);
        Assert.Contains("seconds", dispatcher.LastRequest?.InputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaybackSdkAction_DeniesControlWithReadOnlyCapability()
    {
        using var fixture = new HostFixture(playbackRead: true);
        var dispatcher = new StubToolDispatcher();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                29,
                "canx:playback/invoke",
                "{\"action\":\"seek\",\"input\":{\"seconds\":12}}"),
        };
        await using var host = fixture.CreateHost(factory, dispatcher);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(resolution.IsError);
        Assert.Contains("permission_denied", resolution.Json, StringComparison.Ordinal);
        Assert.Null(dispatcher.LastRequest);
    }

    [Fact]
    public async Task Observation_IsRateLimitedAndCanBeCancelled()
    {
        using var fixture = new HostFixture(playbackRead: true);
        var dispatcher = new StubToolDispatcher("{\"state\":\"playing\"}");
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observationCount = 0;
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                31,
                "canx:observe/subscribe",
                "{\"area\":\"playback\",\"id\":\"playback-1\",\"intervalMs\":100}"),
            OnCallbackInvoke = (runtime, requestId, registrationId, _) =>
            {
                Assert.Equal("observe:playback-1", registrationId);
                Interlocked.Increment(ref observationCount);
                runtime.RequestTool(
                    100 + requestId,
                    "canx:callback/complete",
                    $"{{\"requestId\":{requestId},\"ok\":true,\"result\":null}}");
                observed.TrySetResult();
            },
        };
        var options = new PluginRuntimeOptions
        {
            MinimumSubscriptionInterval = TimeSpan.FromMilliseconds(100),
        };
        await using var host = fixture.CreateHost(factory, dispatcher, options);

        await host.StartAsync();
        await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Runtime.RequestTool(33, "canx:observe/cancel", "{\"id\":\"playback-1\"}");
        await Task.Delay(250);
        var countAfterCancel = Volatile.Read(ref observationCount);
        await Task.Delay(250);

        Assert.InRange(countAfterCancel, 1, 2);
        Assert.Equal(countAfterCancel, Volatile.Read(ref observationCount));
        Assert.All(
            dispatcher.Requests,
            dispatched => Assert.Equal("playback.get", dispatched.ToolName));
    }

    [Fact]
    public async Task UiContribution_IsLifecycleBoundAndInvokesPluginHandler()
    {
        using var fixture = new HostFixture(uiContribute: true);
        var registry = new PluginUiRegistry();
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                35,
                "canx:ui/contribute",
                "{\"id\":\"hello\",\"title\":\"Hello\",\"kind\":\"command\",\"slot\":\"command.palette\",\"content\":{\"type\":\"button\",\"props\":{\"label\":\"Run\",\"action\":\"run\"}},\"interactive\":true}"),
            OnCallbackInvoke = (runtime, requestId, registrationId, _) =>
            {
                Assert.Equal("ui:hello", registrationId);
                runtime.RequestTool(
                    200 + requestId,
                    "canx:callback/complete",
                    $"{{\"requestId\":{requestId},\"ok\":true,\"result\":{{\"handled\":true}}}}");
            },
        };
        var host = fixture.CreateHost(
            factory,
            new StubToolDispatcher(),
            uiRegistry: registry);

        await host.StartAsync();
        await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var contribution = Assert.Single(registry.Contributions);
        var result = await registry.InvokeAsync(contribution.Id);

        Assert.True(result?.GetProperty("handled").GetBoolean());
        await host.DisposeAsync();
        Assert.Empty(registry.Contributions);
    }

    [Fact]
    public async Task ManifestPage_MountsUpdatesInvokesAndUnregistersWithHost()
    {
        using var fixture = new HostFixture(uiPage: true);
        var registry = new PluginUiRegistry();
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Changed += (_, _) =>
        {
            if (registry.Pages.SingleOrDefault()?.BadgeText == "2")
            {
                updated.TrySetResult();
            }
        };
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                37,
                "canx:ui/page/mount",
                "{\"content\":{\"type\":\"text\",\"props\":{\"text\":\"Loading\"}},\"interactive\":true}"),
            OnCallbackInvoke = (runtime, requestId, registrationId, inputJson) =>
            {
                Assert.Equal("ui-page:main", registrationId);
                Assert.Contains("refresh", inputJson, StringComparison.Ordinal);
                runtime.RequestTool(
                    300 + requestId,
                    "canx:callback/complete",
                    $"{{\"requestId\":{requestId},\"ok\":true,\"result\":{{\"handled\":true}}}}");
            },
        };
        var host = fixture.CreateHost(
            factory,
            new StubToolDispatcher(),
            uiRegistry: registry);

        await host.StartAsync();
        var mountResolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(mountResolution.IsError);
        var mounted = Assert.Single(registry.Pages);
        Assert.True(mounted.IsMounted);
        Assert.Equal(fixture.Root, mounted.PluginDirectory);

        factory.Runtime.RequestTool(
            39,
            "canx:ui/page/render",
            "{\"content\":{\"id\":\"status\",\"type\":\"text\",\"props\":{\"text\":\"Ready\"}}}");
        factory.Runtime.RequestTool(
            40,
            "canx:ui/page/update",
            "{\"patches\":[{\"target\":\"status\",\"props\":{\"text\":\"Updated\"}}]}");
        factory.Runtime.RequestTool(
            41,
            "canx:ui/page/badge",
            "{\"text\":\"2\",\"tone\":\"accent\"}");
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var current = Assert.Single(registry.Pages);
        Assert.Equal("Updated", current.Content?.GetProperty("props").GetProperty("text").GetString());
        Assert.Equal("accent", current.BadgeTone);

        using var input = JsonDocument.Parse("{\"type\":\"action\",\"action\":\"refresh\"}");
        var result = await registry.InvokePageAsync(fixture.Installation.Manifest.Id, input.RootElement);
        Assert.True(result?.GetProperty("handled").GetBoolean());

        await host.DisposeAsync();
        Assert.Empty(registry.Pages);
    }

    [Fact]
    public async Task DownloadPrompt_RequiresActivePluginPageUserEvent()
    {
        using var fixture = new HostFixture(uiPage: true, downloadsPrompt: true);
        var registry = new PluginUiRegistry();
        var broker = new PluginDownloadPromptBroker();
        PluginDownloadPromptRequest? prompted = null;
        using var promptRegistration = broker.RegisterHandler((request, _) =>
        {
            prompted = request;
            return ValueTask.CompletedTask;
        });
        var source = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                43,
                "canx:ui/page/mount",
                "{\"content\":{\"type\":\"button\",\"props\":{\"label\":\"Add\",\"action\":\"add\"}},\"interactive\":true}"),
            OnCallbackInvoke = (runtime, requestId, registrationId, _) =>
            {
                Assert.Equal("ui-page:main", registrationId);
                runtime.RequestTool(
                    45,
                    "canx:downloads/requestAdd",
                    $"{{\"kind\":\"magnet\",\"source\":\"{source}\",\"gestureToken\":{requestId}}}");
                runtime.RequestTool(
                    47,
                    "canx:callback/complete",
                    $"{{\"requestId\":{requestId},\"ok\":true,\"result\":null}}");
            },
        };
        await using var host = fixture.CreateHost(
            factory,
            new StubToolDispatcher(),
            uiRegistry: registry,
            downloadPromptBroker: broker);
        await host.StartAsync();
        await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var input = JsonDocument.Parse("{\"type\":\"action\",\"action\":\"add\"}");

        await registry.InvokePageAsync(fixture.Installation.Manifest.Id, input.RootElement);

        Assert.NotNull(prompted);
        Assert.Equal(fixture.Installation.Manifest.Id, prompted.PluginId);
        Assert.Equal(source, prompted.Source);
    }

    [Fact]
    public async Task DownloadPrompt_DeniesCallsWithoutUserGesture()
    {
        using var fixture = new HostFixture(uiPage: true, downloadsPrompt: true);
        var broker = new PluginDownloadPromptBroker();
        var prompted = false;
        using var promptRegistration = broker.RegisterHandler((_, _) =>
        {
            prompted = true;
            return ValueTask.CompletedTask;
        });
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                49,
                "canx:downloads/requestAdd",
                "{\"kind\":\"http\",\"source\":\"https://example.test/file.iso\",\"gestureToken\":1}"),
        };
        await using var host = fixture.CreateHost(
            factory,
            new StubToolDispatcher(),
            downloadPromptBroker: broker);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(resolution.IsError);
        Assert.Contains("user_gesture_required", resolution.Json, StringComparison.Ordinal);
        Assert.False(prompted);
    }

    [Fact]
    public async Task MediaPlay_RequiresActivePluginPageUserEvent()
    {
        using var fixture = new HostFixture(uiPage: true, mediaStream: true, http: true);
        var registry = new PluginUiRegistry();
        using var streams = new MediaStreamBroker();
        var playback = new PluginMediaPlaybackBroker();
        PluginMediaPlaybackRequest? requested = null;
        using var playbackRegistration = playback.RegisterHandler((request, _) =>
        {
            requested = request;
            return ValueTask.CompletedTask;
        });
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                148,
                "canx:ui/page/mount",
                "{\"content\":{\"type\":\"button\",\"props\":{\"label\":\"Play\",\"action\":\"play\"}},\"interactive\":true}"),
            OnCallbackInvoke = (runtime, requestId, registrationId, _) =>
            {
                Assert.Equal("ui-page:main", registrationId);
                runtime.RequestTool(
                    149,
                    "canx:media/stream/play",
                    $$"""{"recipe":{"url":"https://api.example.test/video.mp4"},"gestureToken":{{requestId}}}""");
                runtime.RequestTool(
                    150,
                    "canx:callback/complete",
                    $$"""{"requestId":{{requestId}},"ok":true,"result":null}""");
            },
        };
        await using var host = fixture.CreateHost(
            factory,
            new StubToolDispatcher(),
            uiRegistry: registry,
            mediaStreamBroker: streams,
            mediaPlaybackBroker: playback);
        await host.StartAsync();
        await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var input = JsonDocument.Parse("{\"type\":\"action\",\"action\":\"play\"}");

        await registry.InvokePageAsync(fixture.Installation.Manifest.Id, input.RootElement);

        Assert.NotNull(requested);
        Assert.Equal(fixture.Installation.Manifest.Id, requested.PluginId);
        Assert.StartsWith("canx://stream/", requested.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MediaPlay_DeniesCallsWithoutUserGesture()
    {
        using var fixture = new HostFixture(mediaStream: true, http: true);
        using var streams = new MediaStreamBroker();
        var playback = new PluginMediaPlaybackBroker();
        var requested = false;
        using var playbackRegistration = playback.RegisterHandler((_, _) =>
        {
            requested = true;
            return ValueTask.CompletedTask;
        });
        var factory = new FakeRuntimeFactory
        {
            OnEvaluate = runtime => runtime.RequestTool(
                151,
                "canx:media/stream/play",
                "{\"recipe\":{\"url\":\"https://api.example.test/video.mp4\"},\"gestureToken\":1}"),
        };
        await using var host = fixture.CreateHost(
            factory,
            new StubToolDispatcher(),
            mediaStreamBroker: streams,
            mediaPlaybackBroker: playback);

        await host.StartAsync();
        var resolution = await factory.Runtime.Resolution.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(resolution.IsError);
        Assert.Contains("user_gesture_required", resolution.Json, StringComparison.Ordinal);
        Assert.False(requested);
    }

    [Fact]
    public async Task StartAsync_OversizedEntryFailsBeforeCreatingRuntime()
    {
        var options = new PluginRuntimeOptions { EntryModuleLimitBytes = 8 };
        using var fixture = new HostFixture();
        var factory = new FakeRuntimeFactory();
        await using var host = fixture.CreateHost(factory, new StubToolDispatcher(), options);

        await Assert.ThrowsAsync<PluginRuntimeException>(() => host.StartAsync());

        Assert.Equal(PluginHostState.Faulted, host.State);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task DisposeAsync_DestroysRuntimeOnItsOwningThread()
    {
        using var fixture = new HostFixture();
        var factory = new FakeRuntimeFactory();
        var host = fixture.CreateHost(factory, new StubToolDispatcher());
        await host.StartAsync();

        await host.DisposeAsync();

        Assert.Equal(PluginHostState.Stopped, host.State);
        Assert.Equal(factory.Runtime.CreationThreadId, factory.Runtime.DisposeThreadId);
    }

    [Fact]
    public async Task DisposeAsync_IsolatesRegistrationEventHandlerFailuresAndContinuesCleanup()
    {
        using var fixture = new HostFixture(uiPage: true, uiConfiguration: true);
        var registry = new PluginUiRegistry();
        var log = new CapturingApplicationLog();
        registry.PageChanged += (_, eventArgs) =>
        {
            if (eventArgs.Kind == PluginUiPageChangeKind.Removed)
            {
                throw new InvalidOperationException("Broken host UI subscriber.");
            }
        };
        var host = fixture.CreateHost(
            new FakeRuntimeFactory(),
            new StubToolDispatcher(),
            uiRegistry: registry,
            log: log);
        await host.StartAsync();

        await host.DisposeAsync();

        Assert.Empty(registry.Pages);
        Assert.Empty(registry.Configurations);
        Assert.Contains(log.Entries, entry =>
            entry.Level == ApplicationLogLevel.Error &&
            entry.Message.Contains("UI registration cleanup failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuntimeLoop_IsolatesUnhandledRuntimeCommandFailure()
    {
        using var fixture = new HostFixture();
        var log = new CapturingApplicationLog();
        var factory = new FakeRuntimeFactory();
        var host = fixture.CreateHost(factory, new StubToolDispatcher(), log: log);
        await host.StartAsync();
        var pumpCalls = 0;
        factory.Runtime.PumpJobsBehavior = maximumJobs =>
            Interlocked.Increment(ref pumpCalls) == 1
                ? maximumJobs
                : throw new InvalidOperationException("Broken plugin job.");

        factory.Runtime.RequestTool(999, "not-allowed", "{}");
        await WaitForStateAsync(host, PluginHostState.Faulted);

        Assert.Contains(log.Entries, entry =>
            entry.Level == ApplicationLogLevel.Error &&
            entry.Message.Contains("runtime command failed", StringComparison.Ordinal));
        await host.DisposeAsync();
        Assert.Equal(PluginHostState.Stopped, host.State);
    }

    private static async Task WaitForStateAsync(QuickJsPluginHost host, PluginHostState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (host.State != expected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class HostFixture : IDisposable
    {
        public HostFixture(
            string[]? tools = null,
            bool storage = false,
            string[]? secretNames = null,
            string[]? serviceProvideScopes = null,
            string[]? serviceInvokeScopes = null,
            bool playbackRead = false,
            bool playbackControl = false,
            bool uiContribute = false,
            bool uiPage = false,
            bool uiConfiguration = false,
            bool downloadsPrompt = false,
            bool mediaStream = false,
            bool http = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "CanX.PluginHostTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "main.mjs"), "export const plugin = true;");
            var permissions = new List<PluginPermission>();
            if (tools is not null)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.ToolsInvoke, tools));
            }
            if (storage)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.Storage, []));
            }
            if (secretNames is not null)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.Secrets, secretNames));
            }
            if (serviceProvideScopes is not null)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.ServicesProvide, serviceProvideScopes));
            }
            if (serviceInvokeScopes is not null)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.ServicesInvoke, serviceInvokeScopes));
            }
            if (playbackRead)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.PlaybackRead, []));
            }
            if (playbackControl)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.PlaybackControl, []));
            }
            if (uiContribute)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.UiContribute, []));
            }
            if (uiPage)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.UiPage, []));
            }
            if (uiConfiguration)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.UiConfiguration, []));
            }
            if (downloadsPrompt)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.DownloadsPrompt, []));
            }
            if (mediaStream)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.MediaStream, []));
            }
            if (http)
            {
                permissions.Add(new PluginPermission(PluginCapabilities.Http, ["api.example.test"]));
            }
            var manifest = new PluginManifest(
                1,
                "org.canx.runtime",
                "Runtime",
                null,
                "1.0.0",
                new PluginApiVersion(2, 0),
                "main.mjs",
                permissions,
                uiPage
                    ? new PluginUiManifest(
                        "Tools",
                        new PluginUiIcon(PluginUiIconKind.BuiltIn, "settings"))
                    : null,
                uiConfiguration ? new PluginConfigurationManifest("Runtime settings") : null);
            Installation = new PluginInstallation(manifest, Root, new string('0', 64), DateTimeOffset.UtcNow);
            DataRoot = Path.Combine(Path.GetDirectoryName(Root)!, $"data-{Guid.NewGuid():N}");
        }

        public string Root { get; }

        public PluginInstallation Installation { get; }

        public string DataRoot { get; }

        public QuickJsPluginHost CreateHost(
            IQuickJsRuntimeFactory factory,
            IPluginToolDispatcher dispatcher,
            PluginRuntimeOptions? options = null,
            ICredentialStore? credentialStore = null,
            PluginUiRegistry? uiRegistry = null,
            PluginDownloadPromptBroker? downloadPromptBroker = null,
            HttpMessageHandler? httpHandler = null,
            PluginServiceRegistry? serviceRegistry = null,
            IApplicationLog? log = null,
            MediaStreamBroker? mediaStreamBroker = null,
            PluginMediaPlaybackBroker? mediaPlaybackBroker = null,
            Func<HttpMessageHandler>? mediaHttpHandlerFactory = null) =>
            new(
                Installation,
                dispatcher,
                options,
                log,
                factory,
                DataRoot,
                credentialStore,
                httpHandler,
                uiRegistry,
                downloadPromptBroker,
                serviceRegistry,
                mediaStreamBroker,
                mediaPlaybackBroker,
                mediaHttpHandlerFactory);

        public void WriteModule(string relativePath, string source)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
            if (Directory.Exists(DataRoot))
            {
                Directory.Delete(DataRoot, recursive: true);
            }
        }
    }

    private sealed class CallbackHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }

    private sealed class StubToolDispatcher(string result = "{}") : IPluginToolDispatcher
    {
        public PluginToolRequest? LastRequest { get; private set; }

        public List<PluginToolRequest> Requests { get; } = [];

        public ValueTask<PluginToolDispatchResult> InvokeAsync(
            PluginToolRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            lock (Requests)
            {
                Requests.Add(request);
            }
            return ValueTask.FromResult(PluginToolDispatchResult.Success(result));
        }
    }

    private sealed class FakeRuntimeFactory : IQuickJsRuntimeFactory
    {
        public FakeQuickJsRuntime Runtime { get; private set; } = null!;

        public Action<FakeQuickJsRuntime>? OnEvaluate { get; init; }

        public Action<FakeQuickJsRuntime, ulong, string, string>? OnCallbackInvoke { get; init; }

        public int CreateCount { get; private set; }

        public IQuickJsRuntime Create(
            PluginRuntimeOptions options,
            Action<ulong, string, string> toolCallback)
        {
            CreateCount++;
            Runtime = new FakeQuickJsRuntime(toolCallback, OnEvaluate, OnCallbackInvoke);
            return Runtime;
        }
    }

    private sealed class FakeQuickJsRuntime(
        Action<ulong, string, string> toolCallback,
        Action<FakeQuickJsRuntime>? onEvaluate,
        Action<FakeQuickJsRuntime, ulong, string, string>? onCallbackInvoke) : IQuickJsRuntime
    {
        public int CreationThreadId { get; } = Environment.CurrentManagedThreadId;

        public int EvaluationThreadId { get; private set; }

        public int DisposeThreadId { get; private set; }

        public int PumpCount { get; private set; }

        public Func<int, int>? PumpJobsBehavior { get; set; }

        public string? Source { get; private set; }

        public string? BootstrapSource { get; private set; }

        public Dictionary<string, string> Modules { get; } = new(StringComparer.Ordinal);

        public TaskCompletionSource<ToolResolution> Resolution { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AddModule(string moduleName, string source)
        {
            Modules.Add(moduleName, source);
            Assert.Equal(CreationThreadId, Environment.CurrentManagedThreadId);
        }

        public void EvaluateModule(string source, string filename, TimeSpan timeout)
        {
            Source = source;
            EvaluationThreadId = Environment.CurrentManagedThreadId;
            onEvaluate?.Invoke(this);
        }

        public void EvaluateScript(string source, string filename, TimeSpan timeout)
        {
            BootstrapSource = source;
            Assert.Equal("canx:sdk/v2", filename);
            Assert.Equal(CreationThreadId, Environment.CurrentManagedThreadId);
        }

        public void RequestTool(ulong requestId, string toolName, string inputJson) =>
            toolCallback(requestId, toolName, inputJson);

        public void ResolveTool(ulong requestId, string resultJson, bool isError) =>
            Resolution.TrySetResult(
                new ToolResolution(resultJson, isError, Environment.CurrentManagedThreadId));

        public void InvokeCallback(ulong requestId, string registrationId, string inputJson, TimeSpan timeout) =>
            onCallbackInvoke?.Invoke(this, requestId, registrationId, inputJson);

        public int PumpJobs(int maximumJobs, TimeSpan timeout)
        {
            PumpCount++;
            return PumpJobsBehavior?.Invoke(maximumJobs) ?? 0;
        }

        public void Dispose()
        {
            DisposeThreadId = Environment.CurrentManagedThreadId;
        }
    }

    private sealed record ToolResolution(string Json, bool IsError, int ThreadId);

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
