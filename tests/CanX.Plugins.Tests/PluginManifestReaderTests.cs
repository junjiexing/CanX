// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using CanX.Plugins.Manifest;
using CanX.Plugins.Sdk;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginManifestReaderTests
{
    [Fact]
    public void CurrentApiVersion_MatchesPublishedSdkTypeContract()
    {
        Assert.Equal(PluginSdk.ApiVersion, PluginManifestReader.CurrentApiVersion.ToString());
    }

    [Fact]
    public void Parse_ValidManifest_PreservesValidatedCapabilities()
    {
        var manifest = Parse(
            """
            {
              "manifestVersion": 1,
              "id": "org.canx.example-search",
              "name": "Example Search",
              "description": "Searches an example catalog.",
              "version": "1.2.3-beta.1+desktop",
              "apiVersion": "2.0",
              "entry": "dist/main.mjs",
              "permissions": [
                { "capability": "services.provide", "scopes": ["org.canx.search"] },
                { "capability": "http", "scopes": ["api.example.test", "*.cdn.example.test"] }
              ]
            }
            """);

        Assert.Equal("org.canx.example-search", manifest.Id);
        Assert.Equal("1.2.3-beta.1+desktop", manifest.Version);
        Assert.Equal(new PluginApiVersion(2, 0), manifest.ApiVersion);
        Assert.Equal("dist/main.mjs", manifest.Entry);
        Assert.Collection(
            manifest.Permissions,
            permission =>
            {
                Assert.Equal(PluginCapabilities.ServicesProvide, permission.Capability);
                Assert.Equal(["org.canx.search"], permission.Scopes);
            },
            permission =>
            {
                Assert.Equal(PluginCapabilities.Http, permission.Capability);
                Assert.Equal(["api.example.test", "*.cdn.example.test"], permission.Scopes);
            });
    }

    [Fact]
    public void Parse_MissingPermissions_DefaultsToNoAccess()
    {
        var manifest = Parse(MinimalManifest);

        Assert.Empty(manifest.Permissions);
    }

    [Theory]
    [InlineData("1.0", false)]
    [InlineData("1.1", false)]
    [InlineData("1.2", false)]
    [InlineData("1.3", false)]
    [InlineData("2.0", true)]
    [InlineData("0.9", false)]
    public void Parse_ApiVersionSupport_IsExplicit(string apiVersion, bool supported)
    {
        var json = MinimalManifest.Replace(
            "\"apiVersion\": \"2.0\"",
            $"\"apiVersion\": \"{apiVersion}\"",
            StringComparison.Ordinal);

        if (supported)
        {
            Assert.Equal(apiVersion, Parse(json).ApiVersion.ToString());
        }
        else
        {
            var exception = Assert.Throws<PluginManifestException>(() => Parse(json));
            Assert.Contains("not supported", exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("\"manifestVersion\": 1", "\"manifestVersion\": 2", "manifestVersion must be 1")]
    [InlineData("\"id\": \"org.canx.example\"", "\"id\": \"Example\"", "lowercase reverse-domain")]
    [InlineData("\"version\": \"1.0.0\"", "\"version\": \"1.0\"", "SemVer 2.0")]
    [InlineData("\"apiVersion\": \"2.0\"", "\"apiVersion\": \"2.1\"", "not supported")]
    [InlineData("\"entry\": \"main.mjs\"", "\"entry\": \"../main.mjs\"", "cannot contain")]
    [InlineData("\"entry\": \"main.mjs\"", "\"entry\": \"C:\\\\main.mjs\"", "portable relative path")]
    public void Parse_InvalidCoreField_ReportsActionableError(
        string original,
        string replacement,
        string expectedError)
    {
        var json = MinimalManifest.Replace(original, replacement, StringComparison.Ordinal);

        var exception = Assert.Throws<PluginManifestException>(() => Parse(json));

        Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("filesystem", "is not supported")]
    [InlineData("http", "requires at least one scope")]
    public void Parse_InvalidPermission_IsRejected(string capability, string expectedError)
    {
        var json = MinimalManifest.Replace(
            "\"entry\": \"main.mjs\"",
            $"\"entry\": \"main.mjs\", \"permissions\": [{{ \"capability\": \"{capability}\" }}]",
            StringComparison.Ordinal);

        var exception = Assert.Throws<PluginManifestException>(() => Parse(json));

        Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_HttpPermissionRejectsUrls()
    {
        var json = MinimalManifest.Replace(
            "\"entry\": \"main.mjs\"",
            "\"entry\": \"main.mjs\", \"permissions\": [{ \"capability\": \"http\", \"scopes\": [\"https://example.test\"] }]",
            StringComparison.Ordinal);

        var exception = Assert.Throws<PluginManifestException>(() => Parse(json));

        Assert.Contains("a hostname", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_HttpPermissionAcceptsGlobalWildcard()
    {
        var json = MinimalManifest.Replace(
            "\"entry\": \"main.mjs\"",
            "\"entry\": \"main.mjs\", \"permissions\": [{ \"capability\": \"http\", \"scopes\": [\"*\"] }]",
            StringComparison.Ordinal);

        Assert.Equal("*", Assert.Single(Parse(json).Permissions).Scopes[0]);
    }

    [Theory]
    [InlineData("images.example.test", true)]
    [InlineData("*.cdn.example.test", true)]
    [InlineData("*", true)]
    [InlineData("https://images.example.test", false)]
    [InlineData("cdn.*.example.test", false)]
    public void Parse_RemoteImagePermissionAcceptsHostsAndGlobalWildcard(string scope, bool valid)
    {
        var json = MinimalManifest.Replace(
            "\"entry\": \"main.mjs\"",
            $"\"entry\": \"main.mjs\", \"permissions\": [{{ \"capability\": \"ui.remoteImages\", \"scopes\": [\"{scope}\"] }}]",
            StringComparison.Ordinal);

        if (valid)
        {
            Assert.Equal(scope, Assert.Single(Parse(json).Permissions).Scopes[0]);
        }
        else
        {
            Assert.Throws<PluginManifestException>(() => Parse(json));
        }
    }

    [Theory]
    [InlineData("org.canx.search", true)]
    [InlineData("org.canx.search.knaben/main", true)]
    [InlineData("*", true)]
    [InlineData("Invalid", false)]
    [InlineData("org.canx.search/", false)]
    public void Parse_ServiceScope_RequiresContractServiceIdOrWildcard(string scope, bool valid)
    {
        var json = MinimalManifest.Replace(
            "\"entry\": \"main.mjs\"",
            $"\"entry\": \"main.mjs\", \"permissions\": [{{ \"capability\": \"services.invoke\", \"scopes\": [\"{scope}\"] }}]",
            StringComparison.Ordinal);

        if (valid)
        {
            Assert.Equal(scope, Assert.Single(Parse(json).Permissions).Scopes[0]);
        }
        else
        {
            Assert.Throws<PluginManifestException>(() => Parse(json));
        }
    }

    [Theory]
    [InlineData("{ \"builtin\": \"search\" }", PluginUiIconKind.BuiltIn, "search")]
    [InlineData("{ \"asset\": \"assets/icon.png\" }", PluginUiIconKind.Asset, "assets/icon.png")]
    public void Parse_UiDeclaration_RequiresTitleIconAndPermission(
        string icon,
        PluginUiIconKind expectedKind,
        string expectedValue)
    {
        var json = MinimalManifest.Replace(
            "\"entry\": \"main.mjs\"",
            $$"""
            "entry": "main.mjs",
            "ui": { "title": "Resource Search", "icon": {{icon}} },
            "permissions": [{ "capability": "ui.page" }]
            """,
            StringComparison.Ordinal);

        var manifest = Parse(json);

        Assert.Equal("Resource Search", manifest.Ui?.Title);
        Assert.Equal(expectedKind, manifest.Ui?.Icon.Kind);
        Assert.Equal(expectedValue, manifest.Ui?.Icon.Value);
    }

    [Theory]
    [InlineData("{ \"builtin\": \"search\" }", "[]", "requires permission 'ui.page'")]
    [InlineData("{ \"builtin\": \"unknown\" }", "[{ \"capability\": \"ui.page\" }]", "is not supported")]
    [InlineData("{ \"asset\": \"../icon.png\" }", "[{ \"capability\": \"ui.page\" }]", "portable PNG")]
    [InlineData("{ \"asset\": \"assets/icon.svg\" }", "[{ \"capability\": \"ui.page\" }]", "portable PNG")]
    [InlineData("{ \"builtin\": \"search\", \"asset\": \"assets/icon.png\" }", "[{ \"capability\": \"ui.page\" }]", "exactly one")]
    public void Parse_InvalidUiDeclaration_IsRejected(string icon, string permissions, string expectedError)
    {
        var json = MinimalManifest.Replace(
            "\"entry\": \"main.mjs\"",
            $$"""
            "entry": "main.mjs",
            "ui": { "title": "Resource Search", "icon": {{icon}} },
            "permissions": {{permissions}}
            """,
            StringComparison.Ordinal);

        var exception = Assert.Throws<PluginManifestException>(() => Parse(json));

        Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ConfigurationDeclaration_AllowsHeadlessPluginSettings()
    {
        var json = MinimalManifest.Replace(
            "\"entry\": \"main.mjs\"",
            "\"entry\": \"main.mjs\", \"configuration\": { \"title\": \"Source settings\" }, \"permissions\": [{ \"capability\": \"ui.configuration\" }]",
            StringComparison.Ordinal);

        var manifest = Parse(json);

        Assert.Null(manifest.Ui);
        Assert.Equal("Source settings", manifest.Configuration?.Title);
    }

    [Theory]
    [InlineData("\"configuration\": {}", "\"permissions\": []", "requires permission")]
    [InlineData("", "\"permissions\": [{ \"capability\": \"ui.configuration\" }]", "requires a manifest configuration")]
    public void Parse_ConfigurationDeclarationAndPermission_MustAgree(
        string configuration,
        string permissions,
        string expectedError)
    {
        var suffix = string.IsNullOrEmpty(configuration)
            ? $", {permissions}"
            : $", {configuration}, {permissions}";
        var json = MinimalManifest.Replace(
            "\"entry\": \"main.mjs\"",
            $"\"entry\": \"main.mjs\"{suffix}",
            StringComparison.Ordinal);

        var exception = Assert.Throws<PluginManifestException>(() => Parse(json));

        Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownJsonMember_IsRejected()
    {
        var json = MinimalManifest.Replace(
            "\"entry\": \"main.mjs\"",
            "\"entry\": \"main.mjs\", \"unsafeNativeCode\": true",
            StringComparison.Ordinal);

        var exception = Assert.Throws<PluginManifestException>(() => Parse(json));

        Assert.Contains("unsafeNativeCode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OversizedManifest_IsRejectedBeforeJsonParsing()
    {
        var bytes = new byte[PluginManifestReader.MaximumManifestBytes + 1];

        var exception = Assert.Throws<PluginManifestException>(() => PluginManifestReader.Parse(bytes));

        Assert.Contains("exceeds", exception.Message, StringComparison.Ordinal);
    }

    private static PluginManifest Parse(string json) =>
        PluginManifestReader.Parse(Encoding.UTF8.GetBytes(json));

    private const string MinimalManifest =
        """
        {
          "manifestVersion": 1,
          "id": "org.canx.example",
          "name": "Example",
          "version": "1.0.0",
          "apiVersion": "2.0",
          "entry": "main.mjs"
        }
        """;
}
