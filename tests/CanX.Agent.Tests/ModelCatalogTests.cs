// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using CanX.Agent;
using Xunit;

namespace CanX.Agent.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public async Task ModelCatalog_UsesEnteredKeyAndReadsContextMetadata()
    {
        var handler = new CatalogHandler();
        var savedKey = new CountingKeyProvider();
        var catalog = new OpenAiCompatibleModelCatalog(new HttpClient(handler), savedKey);
        var baseUri = new Uri("https://gateway.test/openai/v1");

        var models = await catalog.GetModelsAsync(baseUri, "entered-key");
        var details = await catalog.GetModelAsync(baseUri, "provider/model-a", "entered-key");

        var model = Assert.Single(models);
        Assert.Equal("provider/model-a", model.Id);
        Assert.Equal(128_000, model.ContextWindowTokens);
        Assert.Equal(0.7, model.DefaultTemperature);
        Assert.Equal(128_000, details?.ContextWindowTokens);
        Assert.Equal(0, savedKey.ReadCount);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer entered-key", request.Authorization));
        Assert.Equal("https://gateway.test/openai/v1/models", handler.Requests[0].Uri);
        Assert.Contains("models/provider%2Fmodel-a", handler.Requests[1].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelCatalog_UsesOriginBoundSavedKeyWhenInputIsEmpty()
    {
        var handler = new CatalogHandler();
        var savedKey = new CountingKeyProvider();
        var catalog = new OpenAiCompatibleModelCatalog(new HttpClient(handler), savedKey);

        await catalog.GetModelsAsync(new Uri("https://gateway.test/v1"));

        Assert.Equal(1, savedKey.ReadCount);
        Assert.Equal("Bearer stored-key", Assert.Single(handler.Requests).Authorization);
    }

    private sealed class CountingKeyProvider : IAgentApiKeyProvider
    {
        public int ReadCount { get; private set; }

        public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromResult<string?>("stored-key");
        }
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
            var body = request.RequestUri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                ? """{"data":[{"id":"provider/model-a","context_length":128000,"default_temperature":0.7}]}"""
                : """{"id":"provider/model-a","top_provider":{"context_length":128000}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
