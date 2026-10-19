// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using CanX.Plugins.Ui;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginUiRegistryTests
{
    [Theory]
    [InlineData("page", "navigation.plugins")]
    [InlineData("panel", "playback.sidebar")]
    [InlineData("command", "command.palette")]
    [InlineData("menu", "menu.playback")]
    [InlineData("setting", "settings.plugins")]
    [InlineData("statusCard", "home.status")]
    public void Register_AcceptsEveryContributionKindInFixedSlot(string kind, string slot)
    {
        var registry = new PluginUiRegistry();
        var interactive = kind is "command" or "menu";
        var id = kind.ToLowerInvariant();

        using var lease = registry.Register(
            "org.canx.ui",
            $$"""
              {
                "id": "{{id}}",
                "title": "Test",
                "kind": "{{kind}}",
                "slot": "{{slot}}",
                "content": { "type": "text", "props": { "text": "Hello", "tone": "accent" } },
                "interactive": {{interactive.ToString().ToLowerInvariant()}}
              }
              """,
            Permissions(),
            interactive ? (_, _) => ValueTask.FromResult("null") : null).Lease;

        var contribution = Assert.Single(registry.Contributions);
        Assert.Equal(slot, contribution.Slot);
        Assert.Equal(interactive, contribution.IsInteractive);
    }

    [Theory]
    [InlineData("{\"id\":\"bad\",\"title\":\"Bad\",\"kind\":\"page\",\"slot\":\"menu.app\",\"content\":{\"type\":\"text\",\"props\":{\"text\":\"x\"}},\"interactive\":false}")]
    [InlineData("{\"id\":\"bad\",\"title\":\"Bad\",\"kind\":\"page\",\"slot\":\"navigation.plugins\",\"content\":{\"type\":\"Avalonia.Controls.Button\"},\"interactive\":false}")]
    [InlineData("{\"id\":\"bad\",\"title\":\"Bad\",\"kind\":\"panel\",\"slot\":\"home.panel\",\"content\":{\"type\":\"text\",\"props\":{\"class\":\"arbitrary\"}},\"interactive\":false}")]
    [InlineData("{\"id\":\"bad\",\"title\":\"Bad\",\"kind\":\"panel\",\"slot\":\"home.panel\",\"content\":{\"type\":\"toggle\",\"props\":{\"label\":\"x\",\"key\":\"x\",\"default\":true}},\"interactive\":false}")]
    [InlineData("{\"id\":\"bad\",\"title\":\"Bad\",\"kind\":\"page\",\"slot\":\"navigation.plugins\",\"content\":{\"type\":\"dataTable\",\"props\":{\"columns\":[{\"key\":\"x\",\"title\":\"X\"}],\"rows\":[]}},\"interactive\":false}")]
    public void Register_RejectsUnknownSlotsComponentsPropertiesAndContextualControls(string json)
    {
        var registry = new PluginUiRegistry();

        var exception = Assert.Throws<PluginUiException>(() =>
            registry.Register("org.canx.ui", json, Permissions(), null));

        Assert.Equal("invalid_ui_contribution", exception.Code);
        Assert.Empty(registry.Contributions);
    }

    [Fact]
    public async Task InvokeAsync_CallsOnlyDeclaredInteractiveHandler()
    {
        var registry = new PluginUiRegistry();
        string? received = null;
        using var lease = registry.Register(
            "org.canx.ui",
            "{\"id\":\"action\",\"title\":\"Action\",\"kind\":\"command\",\"slot\":\"command.palette\",\"content\":{\"type\":\"button\",\"props\":{\"label\":\"Run\",\"action\":\"run\",\"variant\":\"primary\"}},\"interactive\":true}",
            Permissions(),
            (input, _) =>
            {
                received = input;
                return ValueTask.FromResult("{\"ok\":true}");
            }).Lease;
        using var inputDocument = JsonDocument.Parse("{\"action\":\"run\"}");

        var result = await registry.InvokeAsync(
            "org.canx.ui/action",
            inputDocument.RootElement);

        Assert.Contains("run", received, StringComparison.Ordinal);
        Assert.True(result?.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void RegistrationLifecycle_NotifiesUiHosts()
    {
        var registry = new PluginUiRegistry();
        var changeCount = 0;
        registry.Changed += (_, _) => changeCount++;

        var lease = registry.Register(
            "org.canx.ui",
            "{\"id\":\"status\",\"title\":\"Status\",\"kind\":\"statusCard\",\"slot\":\"playback.status\",\"content\":{\"type\":\"text\",\"props\":{\"text\":\"Ready\"}},\"interactive\":false}",
            Permissions(),
            null).Lease;
        lease.Dispose();

        Assert.Equal(2, changeCount);
        Assert.Empty(registry.Contributions);
    }

    [Fact]
    public async Task PageLifecycle_DeclaresMountsRendersInvokesAndRemovesPage()
    {
        var registry = new PluginUiRegistry();
        var changeCount = 0;
        string? received = null;
        registry.Changed += (_, _) => changeCount++;
        var declaration = new PluginUiManifest(
            "Tools",
            new PluginUiIcon(PluginUiIconKind.Asset, "assets/icon.png"));

        var lease = registry.DeclarePage(
            "org.canx.ui",
            declaration,
            ".",
            PagePermissions());
        var declared = Assert.Single(registry.Pages);
        Assert.False(declared.IsMounted);
        Assert.Equal(Path.GetFullPath("."), declared.PluginDirectory);

        var mounted = registry.MountPage(
            "org.canx.ui",
            "{\"type\":\"textInput\",\"props\":{\"label\":\"Query\",\"key\":\"query\",\"default\":\"CanX\"}}",
            PagePermissions(),
            (input, _) =>
            {
                received = input;
                return ValueTask.FromResult("{\"handled\":true}");
            });
        Assert.True(mounted.IsMounted);
        Assert.True(mounted.IsInteractive);

        var rendered = registry.RenderPage(
            "org.canx.ui",
            "{\"type\":\"text\",\"props\":{\"text\":\"Ready\"}}",
            PagePermissions());
        Assert.Equal("Ready", rendered.Content?.GetProperty("props").GetProperty("text").GetString());

        var badged = registry.SetPageBadge("org.canx.ui", " 3 ", "accent", PagePermissions());
        Assert.Equal("3", badged.BadgeText);
        Assert.Equal("accent", badged.BadgeTone);

        using var inputDocument = JsonDocument.Parse("{\"type\":\"action\",\"action\":\"refresh\"}");
        var result = await registry.InvokePageAsync("org.canx.ui", inputDocument.RootElement);
        Assert.Contains("refresh", received, StringComparison.Ordinal);
        Assert.True(result?.GetProperty("handled").GetBoolean());

        lease.Dispose();
        Assert.Empty(registry.Pages);
        Assert.Equal(5, changeCount);
    }

    [Fact]
    public void PageOperations_RejectMissingPermissionInvalidContentAndInvalidBadge()
    {
        var registry = new PluginUiRegistry();
        var declaration = new PluginUiManifest(
            "Tools",
            new PluginUiIcon(PluginUiIconKind.BuiltIn, "settings"));

        Assert.Throws<PluginPermissionDeniedException>(() =>
            registry.DeclarePage("org.canx.ui", declaration, ".", Permissions()));

        using var lease = registry.DeclarePage(
            "org.canx.ui",
            declaration,
            ".",
            PagePermissions());
        var contentException = Assert.Throws<PluginUiException>(() => registry.MountPage(
            "org.canx.ui",
            "{\"type\":\"unknown\"}",
            PagePermissions(),
            null));
        Assert.Equal("invalid_ui_contribution", contentException.Code);

        var badgeException = Assert.Throws<PluginUiException>(() =>
            registry.SetPageBadge("org.canx.ui", "123456789", "accent", PagePermissions()));
        Assert.Equal("invalid_ui_page", badgeException.Code);
    }

    [Fact]
    public void MountPage_AcceptsRichLayoutInputAndDataComponents()
    {
        var registry = new PluginUiRegistry();
        using var lease = registry.DeclarePage(
            "org.canx.ui",
            new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "tools")),
            ".",
            PagePermissions());

        var page = registry.MountPage(
            "org.canx.ui",
            """
            {
              "id": "root",
              "type": "stack",
              "props": { "spacing": "md" },
              "children": [
                {
                  "type": "grid",
                  "props": { "columns": 2, "spacing": "sm" },
                  "children": [
                    { "id": "query", "type": "searchInput", "props": { "label": "Query", "key": "query" } },
                    { "id": "token", "type": "textInput", "props": { "label": "Token", "key": "token", "password": true } },
                    { "type": "slider", "props": { "label": "Limit", "key": "limit", "default": 20, "min": 1, "max": 100 } }
                  ]
                },
                {
                  "id": "results",
                  "type": "dataTable",
                  "props": {
                    "columns": [{ "key": "title", "title": "Title", "width": 240 }],
                    "rows": [{ "id": "one", "title": "CanX", "available": true }],
                    "action": "open"
                  }
                },
                {
                  "type": "list",
                  "props": {
                    "action": "open",
                    "actionLabel": "Open",
                    "items": [{
                      "id": "cover-one",
                      "title": "Cover",
                      "description": "Inline cover",
                      "image": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
                    }]
                  }
                },
                {
                  "id": "gallery",
                  "type": "gallery",
                  "props": {
                    "items": [{ "id": "poster-one", "title": "Poster" }],
                    "action": "open",
                    "itemWidth": 144,
                    "imageHeight": 192,
                    "spacing": "sm"
                  }
                },
                {
                  "type": "image",
                  "props": {
                    "source": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
                    "alt": "Inline image",
                    "width": 100
                  }
                },
                { "type": "link", "props": { "label": "Get token", "url": "https://example.test/token" } },
                { "type": "image", "props": { "asset": "assets/banner.webp", "alt": "Banner", "width": 320 } }
              ]
            }
            """,
            PagePermissions(),
            null);

        Assert.True(page.IsMounted);
        Assert.Equal("root", page.Content?.GetProperty("id").GetString());
    }

    [Fact]
    public void ViewStack_RetainsValidatedViewsAndUpdatesActiveView()
    {
        var registry = new PluginUiRegistry();
        using var lease = registry.DeclarePage(
            "org.canx.ui",
            new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "tools")),
            ".",
            PagePermissions());
        registry.MountPage(
            "org.canx.ui",
            """
            {
              "id": "root-view",
              "type": "viewStack",
              "props": { "active": "home-view" },
              "children": [
                { "id": "home-view", "type": "stack", "children": [{ "type": "text", "props": { "text": "Home" } }] },
                { "id": "detail-view", "type": "stack", "children": [{ "type": "text", "props": { "text": "Details" } }] }
              ]
            }
            """,
            PagePermissions(),
            null);

        var updated = registry.UpdatePage(
            "org.canx.ui",
            "[{\"target\":\"root-view\",\"props\":{\"active\":\"detail-view\"}}]",
            PagePermissions());

        Assert.Equal("detail-view", updated.Content?.GetProperty("props").GetProperty("active").GetString());
        Assert.Equal(2, updated.Content?.GetProperty("children").GetArrayLength());
    }

    [Theory]
    [InlineData("{\"type\":\"stack\",\"children\":[{\"id\":\"same\",\"type\":\"text\",\"props\":{\"text\":\"a\"}},{\"id\":\"same\",\"type\":\"text\",\"props\":{\"text\":\"b\"}}]}")]
    [InlineData("{\"type\":\"image\",\"props\":{\"asset\":\"assets/icon.svg\"}}")]
    [InlineData("{\"type\":\"list\",\"props\":{\"items\":[{\"id\":\"one\",\"title\":\"Bad\",\"image\":\"data:image/svg+xml;base64,PHN2Zy8+\"}]}}")]
    [InlineData("{\"type\":\"list\",\"props\":{\"items\":[{\"id\":\"one\",\"title\":\"Bad\",\"image\":\"data:image/png;base64,bm90LXBuZw==\"}]}}")]
    [InlineData("{\"type\":\"gallery\",\"props\":{\"items\":[],\"itemWidth\":48}}")]
    [InlineData("{\"type\":\"image\",\"props\":{\"asset\":\"assets/icon.png\",\"source\":\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=\"}}")]
    [InlineData("{\"type\":\"tabs\",\"children\":[{\"type\":\"text\",\"props\":{\"text\":\"not a tab\"}}]}")]
    [InlineData("{\"type\":\"viewStack\",\"props\":{\"active\":\"home\"},\"children\":[{\"type\":\"text\",\"props\":{\"text\":\"missing id\"}}]}")]
    [InlineData("{\"type\":\"viewStack\",\"props\":{\"active\":\"missing\"},\"children\":[{\"id\":\"home\",\"type\":\"text\",\"props\":{\"text\":\"home\"}}]}")]
    [InlineData("{\"type\":\"link\",\"props\":{\"label\":\"Unsafe\",\"url\":\"javascript:alert(1)\"}}")]
    [InlineData("{\"type\":\"link\",\"props\":{\"label\":\"Insecure\",\"url\":\"http://example.test/token\"}}")]
    [InlineData("{\"type\":\"textInput\",\"props\":{\"label\":\"Token\",\"key\":\"token\",\"password\":\"true\"}}")]
    public void MountPage_RejectsUnsafeOrAmbiguousRichContent(string content)
    {
        var registry = new PluginUiRegistry();
        using var lease = registry.DeclarePage(
            "org.canx.ui",
            new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "tools")),
            ".",
            PagePermissions());

        Assert.Throws<PluginUiException>(() => registry.MountPage(
            "org.canx.ui",
            content,
            PagePermissions(),
            null));
    }

    [Fact]
    public async Task PageRemoteImages_RequireScopeAndUseOnlyTheRegisteredHostLoader()
    {
        var registry = new PluginUiRegistry();
        var permissions = RemoteImagePagePermissions("images.example.test");
        string? requested = null;
        using var lease = registry.DeclarePage(
            "org.canx.ui",
            new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "tools")),
            ".",
            permissions,
            (source, _) =>
            {
                requested = source;
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1, 2, 3 });
            });

        registry.MountPage(
            "org.canx.ui",
            "{\"type\":\"gallery\",\"props\":{\"items\":[{\"id\":\"one\",\"title\":\"Cover\",\"image\":\"https://images.example.test/cover.webp\"}]}}",
            permissions,
            null);
        var bytes = await registry.LoadRemoteImageAsync(
            "org.canx.ui",
            "https://images.example.test/cover.webp",
            isConfiguration: false);

        Assert.Equal("https://images.example.test/cover.webp", requested);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());
        Assert.Throws<PluginPermissionDeniedException>(() => registry.RenderPage(
            "org.canx.ui",
            "{\"type\":\"image\",\"props\":{\"source\":\"https://other.example.test/cover.png\"}}",
            permissions));
    }

    [Fact]
    public void PageRemoteImages_GlobalWildcardAllowsArbitraryHttpHosts()
    {
        var registry = new PluginUiRegistry();
        var permissions = RemoteImagePagePermissions("*");
        using var lease = registry.DeclarePage(
            "org.canx.ui",
            new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "tools")),
            ".",
            permissions);

        var page = registry.MountPage(
            "org.canx.ui",
            "{\"type\":\"image\",\"props\":{\"source\":\"https://unrelated.example/cover.png\"}}",
            permissions,
            null);

        Assert.Equal(
            "https://unrelated.example/cover.png",
            page.Content?.GetProperty("props").GetProperty("source").GetString());
    }

    [Fact]
    public void UpdatePage_MergesValidatedPropsAtomicallyByComponentId()
    {
        var registry = new PluginUiRegistry();
        PluginUiPageChangedEventArgs? changed = null;
        registry.PageChanged += (_, eventArgs) =>
        {
            if (eventArgs.Kind == PluginUiPageChangeKind.Updated)
            {
                changed = eventArgs;
            }
        };
        using var lease = registry.DeclarePage(
            "org.canx.ui",
            new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "tools")),
            ".",
            PagePermissions());
        registry.MountPage(
            "org.canx.ui",
            "{\"type\":\"stack\",\"children\":[{\"id\":\"status\",\"type\":\"text\",\"props\":{\"text\":\"Loading\",\"tone\":\"muted\"}},{\"id\":\"progress\",\"type\":\"progress\",\"props\":{\"value\":0}}]}",
            PagePermissions(),
            null);

        var updated = registry.UpdatePage(
            "org.canx.ui",
            "[{\"target\":\"status\",\"props\":{\"text\":\"Ready\",\"tone\":null}},{\"target\":\"progress\",\"props\":{\"value\":1}}]",
            PagePermissions());

        var children = updated.Content?.GetProperty("children");
        Assert.Equal("Ready", children?.EnumerateArray().First().GetProperty("props").GetProperty("text").GetString());
        Assert.False(children?.EnumerateArray().First().GetProperty("props").TryGetProperty("tone", out _));
        Assert.Equal(1, children?.EnumerateArray().Last().GetProperty("props").GetProperty("value").GetDouble());
        Assert.Equal(["status", "progress"], changed?.UpdatedComponentIds);
        Assert.Same(updated, changed?.Page);

        Assert.Throws<PluginUiException>(() => registry.UpdatePage(
            "org.canx.ui",
            "[{\"target\":\"missing\",\"props\":{\"text\":\"No\"}}]",
            PagePermissions()));
        Assert.Equal("Ready", registry.Pages.Single().Content?.GetProperty("children")[0]
            .GetProperty("props").GetProperty("text").GetString());
    }

    [Fact]
    public void UpdatePage_RateLimitsExcessiveRefreshes()
    {
        var registry = new PluginUiRegistry();
        using var lease = registry.DeclarePage(
            "org.canx.ui",
            new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "tools")),
            ".",
            PagePermissions());
        registry.MountPage(
            "org.canx.ui",
            "{\"id\":\"status\",\"type\":\"text\",\"props\":{\"text\":\"0\"}}",
            PagePermissions(),
            null);
        for (var index = 0; index < 30; index++)
        {
            registry.UpdatePage(
                "org.canx.ui",
                $"[{{\"target\":\"status\",\"props\":{{\"text\":\"{index}\"}}}}]",
                PagePermissions());
        }

        var exception = Assert.Throws<PluginUiException>(() => registry.UpdatePage(
            "org.canx.ui",
            "[{\"target\":\"status\",\"props\":{\"text\":\"limited\"}}]",
            PagePermissions()));
        Assert.Equal("ui_rate_limited", exception.Code);
    }

    [Fact]
    public async Task ConfigurationLifecycle_DeclaresMountsInvokesUpdatesAndCloses()
    {
        var registry = new PluginUiRegistry();
        string? received = null;
        string? closedPlugin = null;
        registry.ConfigurationCloseRequested += (_, eventArgs) => closedPlugin = eventArgs.PluginId;
        var lease = registry.DeclareConfiguration(
            "org.canx.ui",
            new PluginConfigurationManifest("Settings"),
            ".",
            ConfigurationPermissions());
        registry.MountConfiguration(
            "org.canx.ui",
            "{\"id\":\"value\",\"type\":\"textInput\",\"props\":{\"label\":\"Value\",\"key\":\"value\"}}",
            ConfigurationPermissions(),
            (input, _) =>
            {
                received = input;
                return ValueTask.FromResult("null");
            });
        using var input = JsonDocument.Parse("{\"type\":\"open\"}");

        await registry.InvokeConfigurationAsync("org.canx.ui", input.RootElement);
        var updated = registry.UpdateConfiguration(
            "org.canx.ui",
            "[{\"target\":\"value\",\"props\":{\"default\":\"saved\"}}]",
            ConfigurationPermissions());
        registry.RequestConfigurationClose("org.canx.ui", ConfigurationPermissions());

        Assert.Contains("open", received, StringComparison.Ordinal);
        Assert.Equal("saved", updated.Content?.GetProperty("props").GetProperty("default").GetString());
        Assert.Equal("org.canx.ui", closedPlugin);
        lease.Dispose();
        Assert.Empty(registry.Configurations);
    }

    private static PluginPermissionSet Permissions() => PluginPermissionSet.FromManifest(new PluginManifest(
        1,
        "org.canx.ui",
        "UI",
        null,
        "1.0.0",
        new PluginApiVersion(2, 0),
        "main.mjs",
        [new PluginPermission(PluginCapabilities.UiContribute, [])]));

    private static PluginPermissionSet PagePermissions() => PluginPermissionSet.FromManifest(new PluginManifest(
        1,
        "org.canx.ui",
        "UI",
        null,
        "1.0.0",
        new PluginApiVersion(2, 0),
        "main.mjs",
        [new PluginPermission(PluginCapabilities.UiPage, [])],
        new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "settings"))));

    private static PluginPermissionSet RemoteImagePagePermissions(params string[] scopes) =>
        PluginPermissionSet.FromManifest(new PluginManifest(
            1,
            "org.canx.ui",
            "UI",
            null,
            "1.0.0",
            new PluginApiVersion(2, 0),
            "main.mjs",
            [
                new PluginPermission(PluginCapabilities.UiPage, []),
                new PluginPermission(PluginCapabilities.UiRemoteImages, scopes),
            ],
            new PluginUiManifest("Tools", new PluginUiIcon(PluginUiIconKind.BuiltIn, "settings"))));

    private static PluginPermissionSet ConfigurationPermissions() => PluginPermissionSet.FromManifest(new PluginManifest(
        1,
        "org.canx.ui",
        "UI",
        null,
        "1.0.0",
        new PluginApiVersion(2, 0),
        "main.mjs",
        [new PluginPermission(PluginCapabilities.UiConfiguration, [])],
        Configuration: new PluginConfigurationManifest("Settings")));
}
