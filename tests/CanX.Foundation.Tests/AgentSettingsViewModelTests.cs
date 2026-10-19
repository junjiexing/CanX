// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Agent;
using CanX.App.ViewModels;
using CanX.Core.Configuration;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class AgentSettingsViewModelTests
{
    [Fact]
    public async Task TestConnection_PreservesManuallyEnteredContextWhenApiOmitsIt()
    {
        var catalog = new StubCatalog([new AgentModelInfo("custom/model", null, null)]);
        var settings = new AgentSettings
        {
            BaseUrl = "https://gateway.test/v1",
            Model = "custom/model",
            ContextWindowTokens = 64_000,
        };
        var viewModel = new AgentSettingsViewModel(settings, modelCatalog: catalog)
        {
            ApiKeyInput = "unsaved-key",
        };

        await viewModel.TestConnectionAsync();

        Assert.Equal("custom/model", viewModel.Model);
        Assert.Equal(64_000, viewModel.ContextWindowTokens);
        Assert.Equal("unsaved-key", catalog.EnteredKey);
        Assert.Equal(1, catalog.DetailRequests);
        Assert.Equal("custom/model", Assert.Single(viewModel.ModelIds));
    }

    [Fact]
    public async Task SelectingFetchedModel_AppliesContextAndTemperature()
    {
        var catalog = new StubCatalog(
        [
            new AgentModelInfo("model-a", 128_000, 0.3),
            new AgentModelInfo("model-b", 32_000, 0.7),
        ]);
        var viewModel = new AgentSettingsViewModel(
            new AgentSettings { BaseUrl = "https://gateway.test/v1" }, modelCatalog: catalog);

        await viewModel.TestConnectionAsync();
        viewModel.SelectedModel = "model-b";

        Assert.Equal("model-b", viewModel.Model);
        Assert.Equal(32_000, viewModel.ContextWindowTokens);
        Assert.Equal(0.7, viewModel.Temperature);
        Assert.Equal(0, catalog.DetailRequests);
        Assert.Equal(32_000, viewModel.ToSettings().ContextWindowTokens);
    }

    [Fact]
    public async Task TestConnection_ValidatesChatEndpointWithSelectedModelAndEnteredKey()
    {
        var catalog = new StubCatalog([new AgentModelInfo("model-a", 128_000, 0.5)]);
        var tester = new StubChatTester();
        var viewModel = new AgentSettingsViewModel(
            new AgentSettings { BaseUrl = "https://gateway.test/v1" },
            modelCatalog: catalog,
            chatTester: tester)
        {
            ApiKeyInput = "entered-key",
        };

        await viewModel.TestConnectionAsync();

        Assert.Equal("model-a", tester.Configuration?.Model);
        Assert.Equal(128_000, tester.Configuration?.ContextWindowTokens);
        Assert.Equal(0.5, tester.Configuration?.Temperature);
        Assert.Equal("entered-key", tester.EnteredKey);
        Assert.Contains("聊天接口已验证", viewModel.ConnectionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_ReportsChatFailureEvenWhenModelListWorks()
    {
        var catalog = new StubCatalog([new AgentModelInfo("model-a", 128_000, null)]);
        var tester = new StubChatTester { Failure = new AgentProviderException("http_error", "HTTP 400: bad tool name") };
        var viewModel = new AgentSettingsViewModel(
            new AgentSettings { BaseUrl = "https://gateway.test/v1" },
            modelCatalog: catalog,
            chatTester: tester);

        await viewModel.TestConnectionAsync();

        Assert.Contains("HTTP 400: bad tool name", viewModel.ConnectionStatus, StringComparison.Ordinal);
        Assert.Equal("model-a", Assert.Single(viewModel.ModelIds));
    }

    private sealed class StubCatalog(IReadOnlyList<AgentModelInfo> models) : IAgentModelCatalog
    {
        public string? EnteredKey { get; private set; }

        public int DetailRequests { get; private set; }

        public Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(
            Uri baseUri, string? enteredApiKey = null, CancellationToken cancellationToken = default)
        {
            EnteredKey = enteredApiKey;
            return Task.FromResult(models);
        }

        public Task<AgentModelInfo?> GetModelAsync(
            Uri baseUri, string modelId, string? enteredApiKey = null,
            CancellationToken cancellationToken = default)
        {
            DetailRequests++;
            return Task.FromResult<AgentModelInfo?>(null);
        }
    }

    private sealed class StubChatTester : IAgentChatConnectionTester
    {
        public AgentProviderConfiguration? Configuration { get; private set; }

        public string? EnteredKey { get; private set; }

        public Exception? Failure { get; init; }

        public Task TestAsync(
            AgentProviderConfiguration configuration, string? enteredApiKey = null,
            CancellationToken cancellationToken = default)
        {
            Configuration = configuration;
            EnteredKey = enteredApiKey;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }
}
