// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Security;

namespace CanX.Agent;

public sealed class CredentialAgentApiKeyProvider(
    ICredentialStore credentialStore,
    string referenceId = "agent.llm.api-key") : IAgentApiKeyProvider
{
    private readonly ICredentialStore _credentialStore =
        credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    private readonly CredentialRef _reference = new()
    {
        Id = ValidateReference(referenceId),
    };
    private readonly CredentialRef _originReference = new()
    {
        Id = ValidateReference(referenceId + ".origin"),
    };

    public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default) =>
        _credentialStore.GetAsync(_reference, cancellationToken);

    // The key is bound to the endpoint origin it was saved for, so changing the configured base URL
    // (by hand, by automation, or by a compromised settings file) never forwards it to a new host.
    public async ValueTask<string?> GetApiKeyForEndpointAsync(
        Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var apiKey = await _credentialStore.GetAsync(_reference, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        var boundOrigin = await _credentialStore.GetAsync(_originReference, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(boundOrigin, Origin(endpoint), StringComparison.Ordinal))
        {
            throw new AgentProviderException(
                "api_key_endpoint_mismatch",
                "The saved API key belongs to a different LLM endpoint. Save the API key again for the current Base URL.");
        }
        return apiKey;
    }

    public async ValueTask SetApiKeyAsync(string value, Uri endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentNullException.ThrowIfNull(endpoint);
        await _credentialStore.SetAsync(_originReference, Origin(endpoint), cancellationToken).ConfigureAwait(false);
        await _credentialStore.SetAsync(_reference, value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteApiKeyAsync(CancellationToken cancellationToken = default)
    {
        await _credentialStore.DeleteAsync(_originReference, cancellationToken).ConfigureAwait(false);
        return await _credentialStore.DeleteAsync(_reference, cancellationToken).ConfigureAwait(false);
    }

    internal static string Origin(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The LLM endpoint must be an absolute URI.", nameof(endpoint));
        }
        return $"{endpoint.Scheme.ToLowerInvariant()}://{endpoint.IdnHost.ToLowerInvariant()}:{endpoint.Port}";
    }

    private static string ValidateReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 200 || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException("Credential reference contains unsupported characters.", nameof(value));
        }
        return value;
    }
}
