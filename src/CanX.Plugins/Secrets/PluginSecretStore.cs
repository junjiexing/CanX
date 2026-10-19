// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using CanX.Core.Security;

namespace CanX.Plugins.Secrets;

internal sealed class PluginSecretStore(
    string pluginId,
    ICredentialStore? credentialStore,
    int maximumSecretBytes = 64 * 1024)
{
    private const string CredentialNamespace = "canx.plugin.";

    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var store = RequireStore();
        try
        {
            return await store.GetAsync(CreateReference(name), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw Unavailable();
        }
    }

    public async ValueTask SetAsync(string name, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (Encoding.UTF8.GetByteCount(secret) > maximumSecretBytes)
        {
            throw new PluginSecretException(
                "secret_too_large",
                $"Secret must fit {maximumSecretBytes} UTF-8 bytes.");
        }

        var store = RequireStore();
        try
        {
            await store.SetAsync(CreateReference(name), secret, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw Unavailable();
        }
    }

    public async ValueTask<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var store = RequireStore();
        try
        {
            return await store.DeleteAsync(CreateReference(name), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw Unavailable();
        }
    }

    private ICredentialStore RequireStore() => credentialStore ?? throw Unavailable();

    private CredentialRef CreateReference(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new CredentialRef { Id = $"{CredentialNamespace}{pluginId}.{name}" };
    }

    private static PluginSecretException Unavailable() =>
        new("secrets_unavailable", "The operating-system credential store is unavailable.");
}

internal sealed class PluginSecretException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
