// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.Plugins.Manifest;
using CanX.Plugins.Sdk;
using CanX.Plugins.Security;
using CanX.Plugins.Ui;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginApiContractTests
{
    [Fact]
    public void PublishedSdkContract_MatchesHostVersionAndCapabilities()
    {
        using var contract = ReadContract();
        var root = contract.RootElement;

        Assert.Equal(PluginManifestReader.CurrentManifestVersion, root.GetProperty("manifestVersion").GetInt32());
        Assert.Equal(PluginSdk.ApiVersion, root.GetProperty("apiVersion").GetString());
        Assert.Equal(
            PluginCapabilities.All.Order(StringComparer.Ordinal),
            Strings(root.GetProperty("capabilities")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PublishedSdkContract_MatchesEveryHostUiSlot()
    {
        using var contract = ReadContract();
        var documented = contract.RootElement.GetProperty("uiSlots");
        var permissions = PluginPermissionSet.FromManifest(new PluginManifest(
            1,
            "org.canx.contract",
            "Contract",
            null,
            "1.0.0",
            new PluginApiVersion(2, 0),
            "main.mjs",
            [new PluginPermission(PluginCapabilities.UiContribute, [])]));

        foreach (var (kind, slots) in PluginUiSchema.SupportedSlots)
        {
            var kindName = KindName(kind);
            Assert.Equal(
                slots.Order(StringComparer.Ordinal),
                Strings(documented.GetProperty(kindName)).Order(StringComparer.Ordinal));
            foreach (var slot in slots)
            {
                var registry = new PluginUiRegistry();
                var interactive = kind is PluginUiContributionKind.Command or PluginUiContributionKind.Menu;
                using var lease = registry.Register(
                    "org.canx.contract",
                    $$"""
                      {
                        "id": "{{kindName.ToLowerInvariant()}}-{{slot.Replace('.', '-')}}",
                        "title": "Contract",
                        "kind": "{{kindName}}",
                        "slot": "{{slot}}",
                        "content": { "type": "text", "props": { "text": "Contract" } },
                        "interactive": {{interactive.ToString().ToLowerInvariant()}}
                      }
                      """,
                    permissions,
                    interactive ? (_, _) => ValueTask.FromResult("null") : null).Lease;
            }
        }

        Assert.Equal(
            PluginUiSchema.SupportedSlots.Count,
            documented.EnumerateObject().Count());
    }

    [Fact]
    public void PublishedSdkContract_MatchesEveryHostUiComponent()
    {
        using var contract = ReadContract();

        Assert.Equal(
            PluginUiSchema.SupportedComponentTypes.Order(StringComparer.Ordinal),
            Strings(contract.RootElement.GetProperty("uiComponents")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PublishedSdkContract_MatchesEveryStableHostAction()
    {
        using var contract = ReadContract();
        var documented = contract.RootElement.GetProperty("actions");

        foreach (var (capability, actions) in PluginSdkToolPolicy.SupportedActions)
        {
            Assert.True(documented.TryGetProperty(capability, out var documentedActions));
            Assert.Equal(
                actions.Order(StringComparer.Ordinal),
                Strings(documentedActions).Order(StringComparer.Ordinal));
            var area = capability.StartsWith("playback.", StringComparison.Ordinal)
                ? "playback"
                : "downloads";
            foreach (var action in actions)
            {
                Assert.Equal(
                    (capability, $"{area}.{action}"),
                    PluginSdkToolPolicy.Resolve(area, action));
            }
        }

        Assert.Equal(
            PluginSdkToolPolicy.SupportedActions.Count,
            documented.EnumerateObject().Count());
    }

    private static JsonDocument ReadContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "sdk", "canx-plugin-api", "contract.json");
            if (File.Exists(path))
            {
                return JsonDocument.Parse(File.ReadAllBytes(path));
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate sdk/canx-plugin-api/contract.json.");
    }

    private static IEnumerable<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString()!);

    private static string KindName(PluginUiContributionKind kind) => kind switch
    {
        PluginUiContributionKind.Page => "page",
        PluginUiContributionKind.Panel => "panel",
        PluginUiContributionKind.Command => "command",
        PluginUiContributionKind.Menu => "menu",
        PluginUiContributionKind.Setting => "setting",
        PluginUiContributionKind.StatusCard => "statusCard",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
