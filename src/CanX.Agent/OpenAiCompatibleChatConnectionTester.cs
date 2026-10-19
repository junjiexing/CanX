// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Tooling;

namespace CanX.Agent;

public interface IAgentChatConnectionTester
{
    Task TestAsync(
        AgentProviderConfiguration configuration,
        string? enteredApiKey = null,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAiCompatibleChatConnectionTester(
    HttpClient httpClient,
    IAgentApiKeyProvider apiKeyProvider,
    Func<IReadOnlyList<ToolDescriptor>> tools) : IAgentChatConnectionTester
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IAgentApiKeyProvider _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    private readonly Func<IReadOnlyList<ToolDescriptor>> _tools = tools ?? throw new ArgumentNullException(nameof(tools));

    public async Task TestAsync(
        AgentProviderConfiguration configuration,
        string? enteredApiKey = null,
        CancellationToken cancellationToken = default)
    {
        configuration.Validate();
        var keyProvider = string.IsNullOrWhiteSpace(enteredApiKey)
            ? _apiKeyProvider : new EnteredKeyProvider(enteredApiKey.Trim());
        var provider = new OpenAiCompatibleChatProvider(_httpClient, keyProvider);
        var completed = false;
        await foreach (var item in provider.StreamAsync(new AgentProviderRequest(
            [AgentMessage.User("Reply with OK. Do not call any tools.")],
            _tools(),
            configuration), cancellationToken).ConfigureAwait(false))
        {
            if (item is AgentProviderCompleted response &&
                (!string.IsNullOrWhiteSpace(response.Content) || response.ToolCalls.Count > 0))
                completed = true;
        }
        if (!completed)
            throw new AgentProviderException("incomplete_response", "聊天接口没有完成测试响应。");
    }

    private sealed class EnteredKeyProvider(string value) : IAgentApiKeyProvider
    {
        public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(value);
        }
    }
}
