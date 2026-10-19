// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Downloads;
using CanX.Core.Security;

namespace CanX.Downloads.Persistence;

public sealed record DownloadSessionDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Dictionary<string, string> GlobalOptions { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, CredentialRef> GlobalOptionCredentialReferences { get; init; } =
        new(StringComparer.Ordinal);

    public List<DownloadSessionGroup> Groups { get; init; } = [];
}

public sealed record DownloadSessionGroup
{
    public required Guid GroupId { get; init; }

    public required AddDownloadRequest Request { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public List<DownloadTaskSnapshot> Tasks { get; init; } = [];
}

public interface IDownloadSessionStore
{
    ValueTask<DownloadSessionDocument> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(DownloadSessionDocument document, CancellationToken cancellationToken = default);
}
