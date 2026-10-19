// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Agent;
using CanX.Core.Security;
using Xunit;

namespace CanX.Agent.Tests;

public sealed class AgentApiKeyBindingTests
{
    [Fact]
    public async Task ApiKeyIsOnlyReleasedForTheEndpointItWasSavedFor()
    {
        var provider = new CredentialAgentApiKeyProvider(new MemoryCredentialStore());
        await provider.SetApiKeyAsync("sk-test", new Uri("https://llm.example/v1"));

        Assert.Equal("sk-test", await provider.GetApiKeyForEndpointAsync(new Uri("https://LLM.example/v1/other")));
        var exception = await Assert.ThrowsAsync<AgentProviderException>(async () =>
            await provider.GetApiKeyForEndpointAsync(new Uri("https://attacker.example/v1")));
        Assert.Equal("api_key_endpoint_mismatch", exception.Code);
        await Assert.ThrowsAsync<AgentProviderException>(async () =>
            await provider.GetApiKeyForEndpointAsync(new Uri("http://llm.example/v1")));
    }

    [Fact]
    public async Task LegacyKeyWithoutBoundEndpointIsNotReleased()
    {
        var store = new MemoryCredentialStore();
        await store.SetAsync(new CredentialRef { Id = "agent.llm.api-key" }, "sk-legacy");
        var provider = new CredentialAgentApiKeyProvider(store);

        await Assert.ThrowsAsync<AgentProviderException>(async () =>
            await provider.GetApiKeyForEndpointAsync(new Uri("https://llm.example/v1")));
    }

    [Fact]
    public async Task DeletingTheKeyAlsoForgetsItsEndpoint()
    {
        var store = new MemoryCredentialStore();
        var provider = new CredentialAgentApiKeyProvider(store);
        await provider.SetApiKeyAsync("sk-test", new Uri("https://llm.example/v1"));

        await provider.DeleteApiKeyAsync();

        Assert.Null(await provider.GetApiKeyForEndpointAsync(new Uri("https://llm.example/v1")));
        Assert.Empty(store.Values);
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public ValueTask<string?> GetAsync(CredentialRef reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Values.TryGetValue(reference.Id, out var value) ? value : null);

        public ValueTask SetAsync(CredentialRef reference, string value, CancellationToken cancellationToken = default)
        {
            Values[reference.Id] = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteAsync(CredentialRef reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Values.Remove(reference.Id));
    }
}
