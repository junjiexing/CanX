// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Security;
using CanX.Plugins.Secrets;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginSecretStoreTests
{
    [Fact]
    public async Task Operations_UseStablePluginScopedCredentialReference()
    {
        var credentials = new MemoryCredentialStore();
        var store = new PluginSecretStore("org.canx.video", credentials);

        await store.SetAsync("api-token", "secret", CancellationToken.None);
        var value = await store.GetAsync("api-token", CancellationToken.None);
        var removed = await store.RemoveAsync("api-token", CancellationToken.None);

        Assert.Equal("secret", value);
        Assert.True(removed);
        Assert.Equal("canx.plugin.org.canx.video.api-token", credentials.LastReference?.Id);
        Assert.Empty(credentials.Values);
    }

    [Fact]
    public async Task SetAsync_RejectsOversizedSecretBeforeCredentialStore()
    {
        var credentials = new MemoryCredentialStore();
        var store = new PluginSecretStore("org.canx.video", credentials, maximumSecretBytes: 4);

        var exception = await Assert.ThrowsAsync<PluginSecretException>(async () =>
            await store.SetAsync("api-token", "12345", CancellationToken.None));

        Assert.Equal("secret_too_large", exception.Code);
        Assert.Null(credentials.LastReference);
    }

    [Fact]
    public async Task MissingPlatformStore_FailsClosed()
    {
        var store = new PluginSecretStore("org.canx.video", null);

        var exception = await Assert.ThrowsAsync<PluginSecretException>(async () =>
            await store.GetAsync("api-token", CancellationToken.None));

        Assert.Equal("secrets_unavailable", exception.Code);
    }

    internal sealed class MemoryCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public CredentialRef? LastReference { get; private set; }

        public ValueTask SetAsync(
            CredentialRef reference,
            string secret,
            CancellationToken cancellationToken = default)
        {
            LastReference = reference;
            Values[reference.Id] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetAsync(
            CredentialRef reference,
            CancellationToken cancellationToken = default)
        {
            LastReference = reference;
            return ValueTask.FromResult(Values.GetValueOrDefault(reference.Id));
        }

        public ValueTask<bool> DeleteAsync(
            CredentialRef reference,
            CancellationToken cancellationToken = default)
        {
            LastReference = reference;
            return ValueTask.FromResult(Values.Remove(reference.Id));
        }
    }
}
