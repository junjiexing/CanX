// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Plugins.Storage;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginStorageStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "CanX.PluginStorageTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Set_PersistsJsonAtomicallyAndCanBeReloaded()
    {
        var store = new PluginStorageStore(root, "org.canx.one");

        store.Set("profile", "{\"name\":\"CanX\",\"count\":2}");
        var reopened = new PluginStorageStore(root, "org.canx.one");

        Assert.Equal("{\"name\":\"CanX\",\"count\":2}", reopened.Get("profile"));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Stores_AreIsolatedByPluginId()
    {
        var first = new PluginStorageStore(root, "org.canx.one");
        var second = new PluginStorageStore(root, "org.canx.two");

        first.Set("shared-name", "1");
        second.Set("shared-name", "2");

        Assert.Equal("1", first.Get("shared-name"));
        Assert.Equal("2", second.Get("shared-name"));
    }

    [Fact]
    public void Set_EnforcesValueAndTotalQuotasWithoutChangingExistingData()
    {
        var options = new PluginStorageOptions
        {
            MaximumKeyBytes = 32,
            MaximumValueBytes = 8,
            MaximumEntries = 2,
            MaximumTotalBytes = 12,
        };
        var store = new PluginStorageStore(root, "org.canx.quota", options);
        store.Set("a", "1234");

        var oversized = Assert.Throws<PluginStorageException>(() => store.Set("b", "12345678"));

        Assert.Equal("quota_exceeded", oversized.Code);
        Assert.Equal("1234", store.Get("a"));
        Assert.Equal("null", store.Get("b"));
    }

    [Fact]
    public void RemoveKeysAndClear_ArePersistent()
    {
        var store = new PluginStorageStore(root, "org.canx.mutable");
        store.Set("b", "true");
        store.Set("a", "false");

        Assert.Equal(["a", "b"], store.Keys());
        Assert.True(store.Remove("a"));
        Assert.False(store.Remove("missing"));
        store.Clear();

        Assert.Empty(new PluginStorageStore(root, "org.canx.mutable").Keys());
    }

    [Theory]
    [InlineData("")]
    [InlineData("line\nbreak")]
    public void Keys_RejectUnsafeValues(string key)
    {
        var store = new PluginStorageStore(root, "org.canx.keys");

        var exception = Assert.Throws<PluginStorageException>(() => store.Set(key, "null"));

        Assert.Equal("invalid_key", exception.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
