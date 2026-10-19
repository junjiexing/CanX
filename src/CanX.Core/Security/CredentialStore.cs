// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Core.Security;

public sealed record CredentialRef
{
    public required string Id { get; init; }
}

public interface ICredentialStore
{
    ValueTask SetAsync(
        CredentialRef reference,
        string secret,
        CancellationToken cancellationToken = default);

    ValueTask<string?> GetAsync(
        CredentialRef reference,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(
        CredentialRef reference,
        CancellationToken cancellationToken = default);
}
