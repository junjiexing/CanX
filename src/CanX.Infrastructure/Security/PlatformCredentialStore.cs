// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Security;
using Latchkey;

namespace CanX.Infrastructure.Security;

public sealed class PlatformCredentialStore : ICredentialStore
{
    private readonly string _serviceName;
    private readonly Lock _gate = new();
    private ILatchkey? _client;

    public PlatformCredentialStore(string serviceName = "io.github.junjiexing.canx")
    {
        _serviceName = serviceName;
    }

    public ValueTask SetAsync(
        CredentialRef reference,
        string secret,
        CancellationToken cancellationToken = default) =>
        GetClient().SetAsync(reference.Id, secret, cancellationToken);

    public ValueTask<string?> GetAsync(
        CredentialRef reference,
        CancellationToken cancellationToken = default) =>
        GetClient().GetAsync(reference.Id, cancellationToken);

    public ValueTask<bool> DeleteAsync(
        CredentialRef reference,
        CancellationToken cancellationToken = default) =>
        GetClient().DeleteAsync(reference.Id, cancellationToken);

    private ILatchkey GetClient()
    {
        lock (_gate)
        {
            return _client ??= LatchkeyFactory.Create(new LatchkeyOptions
            {
                ServiceName = _serviceName,
                DisplayName = "CanX",
            });
        }
    }
}
