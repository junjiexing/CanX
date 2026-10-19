// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Streaming;

namespace CanX.MediaAccess;

public enum MediaSourceProtocol
{
    LocalFolder,
    WebDav,
    Ftp,
    Smb,
}

public sealed record MediaSourceConnectionProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required MediaSourceProtocol Protocol { get; init; }

    public required Uri Endpoint { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}

public interface IMediaSourceProfileStore
{
    // Listing returns profile metadata only. LoadProfileAsync resolves the saved password when it is needed.
    ValueTask<IReadOnlyList<MediaSourceConnectionProfile>> ListProfilesAsync(
        CancellationToken cancellationToken = default);

    ValueTask<MediaSourceConnectionProfile?> LoadProfileAsync(
        string id,
        CancellationToken cancellationToken = default);

    ValueTask SaveProfileAsync(
        MediaSourceConnectionProfile profile,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteProfileAsync(
        string id,
        CancellationToken cancellationToken = default);
}

public sealed record MediaSourceFavorite
{
    public required string Id { get; init; }

    public required MediaSourceProtocol Protocol { get; init; }

    public required Uri Endpoint { get; init; }

    public required Uri Location { get; init; }

    public required string Name { get; init; }
}

public interface IMediaSourceFavoriteStore
{
    ValueTask<IReadOnlyList<MediaSourceFavorite>> ListAsync(
        MediaSourceProtocol protocol,
        Uri endpoint,
        CancellationToken cancellationToken = default);

    ValueTask<MediaSourceFavorite> AddAsync(
        MediaSourceProtocol protocol,
        Uri endpoint,
        Uri location,
        string name,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default);
}

public sealed record MediaResource
{
    public required Uri Uri { get; init; }

    public required string Name { get; init; }

    public required bool IsDirectory { get; init; }

    public long? Length { get; init; }

    public string? ContentType { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public string? EntityTag { get; init; }
}

public interface IMediaSourceClient : IAsyncDisposable
{
    MediaSourceProtocol Protocol { get; }

    Uri Root { get; }

    ValueTask<IReadOnlyList<MediaResource>> BrowseAsync(
        Uri? directory = null,
        CancellationToken cancellationToken = default);

    ValueTask<MediaResource> GetAsync(
        Uri resource,
        CancellationToken cancellationToken = default);

    ValueTask<IMediaByteStream> OpenReadAsync(
        MediaResource resource,
        CancellationToken cancellationToken = default);
}
