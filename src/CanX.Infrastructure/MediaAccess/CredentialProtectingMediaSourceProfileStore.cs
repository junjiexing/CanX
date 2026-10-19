// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanX.Core.Security;
using CanX.MediaAccess;

namespace CanX.Infrastructure.MediaAccess;

public sealed class CredentialProtectingMediaSourceProfileStore(
    string path,
    ICredentialStore credentialStore) : IMediaSourceProfileStore, IMediaSourceFavoriteStore
{
    internal const int CurrentSchemaVersion = 2;
    private const string CredentialNamespace = "canx.media.";
    private readonly string _path = PrivateFileSystem.PreparePrivateFilePath(path);
    private readonly ICredentialStore _credentialStore =
        credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<IReadOnlyList<MediaSourceConnectionProfile>> ListProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var profiles = new List<MediaSourceConnectionProfile>(document.Profiles.Count);
            foreach (var stored in document.Profiles)
            {
                profiles.Add(ToProfile(stored));
            }

            return profiles
                .OrderBy(static profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static profile => profile.Endpoint.AbsoluteUri, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<MediaSourceConnectionProfile?> LoadProfileAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ValidateProfileId(id);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var stored = document.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, id, StringComparison.Ordinal));
            return stored is null
                ? null
                : await ToProfileAsync(stored, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveProfileAsync(
        MediaSourceConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);
        if (string.IsNullOrWhiteSpace(profile.UserName) && !string.IsNullOrEmpty(profile.Password))
        {
            throw new ArgumentException("A media source password cannot be saved without a username.", nameof(profile));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var protocolName = GetProtocolName(profile.Protocol);
            var previous = document.Profiles.FirstOrDefault(item =>
                string.Equals(item.Id, profile.Id, StringComparison.Ordinal));
            var duplicate = document.Profiles.FirstOrDefault(item =>
                !string.Equals(item.Id, profile.Id, StringComparison.Ordinal) &&
                string.Equals(item.Protocol, protocolName, StringComparison.Ordinal) &&
                string.Equals(item.Endpoint, profile.Endpoint.AbsoluteUri, StringComparison.Ordinal));
            if (duplicate is not null)
            {
                throw new InvalidOperationException("The same media source has already been added.");
            }

            var reference = profile.Protocol == MediaSourceProtocol.LocalFolder ||
                string.IsNullOrWhiteSpace(profile.UserName)
                ? null
                : CreateCredentialReference(profile);
            var wroteNewCredential = false;

            try
            {
                if (reference is not null)
                {
                    await _credentialStore.SetAsync(reference, profile.Password, cancellationToken).ConfigureAwait(false);
                    wroteNewCredential = previous?.CredentialReference?.Id != reference.Id;
                }

                var profiles = document.Profiles
                    .Where(item => !string.Equals(item.Id, profile.Id, StringComparison.Ordinal))
                    .Append(new MediaSourceProfileEntry
                    {
                        Id = profile.Id,
                        Name = profile.Name.Trim(),
                        Protocol = protocolName,
                        Endpoint = profile.Endpoint.AbsoluteUri,
                        UserName = profile.UserName,
                        CredentialReference = reference,
                    })
                    .OrderBy(static item => item.Protocol, StringComparer.Ordinal)
                    .ToList();
                await SaveDocumentAsync(
                    document with { Profiles = profiles },
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (wroteNewCredential && reference is not null)
                {
                    await TryDeleteCredentialAsync(reference).ConfigureAwait(false);
                }

                throw;
            }

            if (previous?.CredentialReference is { } previousReference &&
                previousReference.Id != reference?.Id)
            {
                ValidateCredentialReference(previousReference, profile.Protocol);
                await _credentialStore.DeleteAsync(previousReference, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> DeleteProfileAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ValidateProfileId(id);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var previous = document.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, id, StringComparison.Ordinal));
            if (previous is null)
            {
                return false;
            }

            await SaveDocumentAsync(
                document with
                {
                    Profiles = document.Profiles
                        .Where(profile => !string.Equals(profile.Id, id, StringComparison.Ordinal))
                        .ToList(),
                    Favorites = document.Favorites
                        .Where(favorite =>
                            !string.Equals(favorite.Protocol, previous.Protocol, StringComparison.Ordinal) ||
                            !string.Equals(favorite.Endpoint, previous.Endpoint, StringComparison.Ordinal))
                        .ToList(),
                },
                cancellationToken).ConfigureAwait(false);
            if (previous.CredentialReference is { } reference)
            {
                var protocol = ParseProtocolName(previous.Protocol);
                ValidateCredentialReference(reference, protocol);
                await _credentialStore.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<MediaSourceFavorite>> ListAsync(
        MediaSourceProtocol protocol,
        Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateEndpoint(endpoint);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var protocolName = GetProtocolName(protocol);
            var endpointText = endpoint.AbsoluteUri;
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            return document.Favorites
                .Where(favorite =>
                    string.Equals(favorite.Protocol, protocolName, StringComparison.Ordinal) &&
                    string.Equals(favorite.Endpoint, endpointText, StringComparison.Ordinal))
                .Select(favorite => ToFavorite(favorite, protocol, endpoint))
                .OrderBy(static favorite => favorite.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static favorite => favorite.Location.AbsoluteUri, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<MediaSourceFavorite> AddAsync(
        MediaSourceProtocol protocol,
        Uri endpoint,
        Uri location,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(location);
        ValidateEndpoint(endpoint);
        ValidateEndpoint(location);
        if (!IsWithinEndpoint(endpoint, location))
        {
            throw new ArgumentException("A favorite location must remain inside its media source root.", nameof(location));
        }

        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("A favorite location must have a display name.", nameof(name));
        }

        if (normalizedName.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(name), "A favorite display name cannot exceed 256 characters.");
        }

        var favorite = new MediaSourceFavoriteEntry
        {
            Id = CreateFavoriteId(protocol, endpoint, location),
            Protocol = GetProtocolName(protocol),
            Endpoint = endpoint.AbsoluteUri,
            Location = location.AbsoluteUri,
            Name = normalizedName,
        };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var favorites = document.Favorites
                .Where(item => !string.Equals(item.Id, favorite.Id, StringComparison.Ordinal))
                .Append(favorite)
                .OrderBy(static item => item.Protocol, StringComparer.Ordinal)
                .ThenBy(static item => item.Endpoint, StringComparer.Ordinal)
                .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            await SaveDocumentAsync(document with { Favorites = favorites }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return ToFavorite(favorite, protocol, endpoint);
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var favorites = document.Favorites
                .Where(favorite => !string.Equals(favorite.Id, id, StringComparison.Ordinal))
                .ToList();
            if (favorites.Count == document.Favorites.Count)
            {
                return false;
            }

            await SaveDocumentAsync(document with { Favorites = favorites }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<MediaSourceProfileDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new MediaSourceProfileDocument();
        }

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync(
                stream,
                MediaSourceProfileJsonContext.Default.MediaSourceProfileDocument,
                cancellationToken).ConfigureAwait(false)
                ?? new MediaSourceProfileDocument();
            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Unsupported media source profile schema {document.SchemaVersion}.");
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The media source profile file is not valid JSON.", exception);
        }
    }

    private async ValueTask SaveDocumentAsync(
        MediaSourceProfileDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        PrivateFileSystem.EnsurePrivateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                PrivateFileSystem.HardenFile(temporaryPath);
                await JsonSerializer.SerializeAsync(
                    stream,
                    document with { SchemaVersion = CurrentSchemaVersion },
                    MediaSourceProfileJsonContext.Default.MediaSourceProfileDocument,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            PrivateFileSystem.HardenFile(_path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async ValueTask TryDeleteCredentialAsync(CredentialRef reference)
    {
        try
        {
            await _credentialStore.DeleteAsync(reference).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The profile was not committed; leaving an inaccessible key is safer than hiding the original failure.
        }
    }

    private static CredentialRef CreateCredentialReference(MediaSourceConnectionProfile profile)
    {
        var identity = $"{profile.Id}\0{GetProtocolName(profile.Protocol)}\0{profile.Endpoint.AbsoluteUri}\0{profile.UserName}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return new CredentialRef { Id = $"{CredentialNamespace}{GetProtocolName(profile.Protocol)}.{hash}" };
    }

    private static string CreateFavoriteId(MediaSourceProtocol protocol, Uri endpoint, Uri location)
    {
        var identity = $"{GetProtocolName(protocol)}\0{endpoint.AbsoluteUri}\0{location.AbsoluteUri}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static MediaSourceFavorite ToFavorite(
        MediaSourceFavoriteEntry favorite,
        MediaSourceProtocol protocol,
        Uri expectedEndpoint)
    {
        if (string.IsNullOrWhiteSpace(favorite.Id) ||
            !Uri.TryCreate(favorite.Endpoint, UriKind.Absolute, out var endpoint) ||
            !Uri.TryCreate(favorite.Location, UriKind.Absolute, out var location))
        {
            throw new InvalidDataException("A saved media source favorite is invalid.");
        }

        ValidateEndpoint(endpoint);
        ValidateEndpoint(location);
        if (!UriEquals(endpoint, expectedEndpoint) || !IsWithinEndpoint(endpoint, location))
        {
            throw new InvalidDataException("A saved media source favorite is outside its media source root.");
        }

        return new MediaSourceFavorite
        {
            Id = favorite.Id,
            Protocol = protocol,
            Endpoint = endpoint,
            Location = location,
            Name = favorite.Name,
        };
    }

    private static void ValidateCredentialReference(CredentialRef reference, MediaSourceProtocol protocol)
    {
        var expectedPrefix = $"{CredentialNamespace}{GetProtocolName(protocol)}.";
        if (string.IsNullOrWhiteSpace(reference.Id) ||
            !reference.Id.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A media source credential reference is outside the allowed namespace.");
        }
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("A saved media source endpoint must be an absolute URI.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "Connection addresses containing embedded credentials, query parameters, or fragments cannot be persisted.",
                nameof(endpoint));
        }
    }

    private async ValueTask<MediaSourceConnectionProfile> ToProfileAsync(
        MediaSourceProfileEntry stored,
        CancellationToken cancellationToken)
    {
        var profile = ToProfile(stored);
        if (stored.CredentialReference is not { } reference)
        {
            return profile;
        }

        var password = await _credentialStore.GetAsync(reference, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("A saved media source credential is missing from the operating-system store.");
        return profile with { Password = password };
    }

    private static MediaSourceConnectionProfile ToProfile(MediaSourceProfileEntry stored)
    {
        ValidateProfileId(stored.Id);
        if (string.IsNullOrWhiteSpace(stored.Name) || stored.Name.Length > 256 ||
            !Uri.TryCreate(stored.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidDataException("A saved media source profile is invalid.");
        }

        var protocol = ParseProtocolName(stored.Protocol);
        var profile = new MediaSourceConnectionProfile
        {
            Id = stored.Id,
            Name = stored.Name,
            Protocol = protocol,
            Endpoint = endpoint,
            UserName = stored.UserName,
            Password = string.Empty,
        };
        ValidateProfile(profile);
        if (stored.CredentialReference is { } reference)
        {
            ValidateCredentialReference(reference, protocol);
        }

        return profile;
    }

    private static void ValidateProfile(MediaSourceConnectionProfile profile)
    {
        ValidateProfileId(profile.Id);
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Trim().Length > 256)
        {
            throw new ArgumentException("A media source name must contain 1 to 256 characters.", nameof(profile));
        }

        ValidateEndpoint(profile.Endpoint);
        var validScheme = profile.Protocol switch
        {
            MediaSourceProtocol.LocalFolder => profile.Endpoint.IsFile,
            MediaSourceProtocol.WebDav => profile.Endpoint.Scheme is "http" or "https",
            MediaSourceProtocol.Ftp => profile.Endpoint.Scheme is "ftp" or "ftps",
            MediaSourceProtocol.Smb => profile.Endpoint.Scheme == "smb",
            _ => false,
        };
        if (!validScheme)
        {
            throw new ArgumentException("The media source address does not match its source type.", nameof(profile));
        }

        if (profile.Protocol == MediaSourceProtocol.LocalFolder &&
            (!string.IsNullOrWhiteSpace(profile.UserName) || !string.IsNullOrEmpty(profile.Password)))
        {
            throw new ArgumentException("A local media folder cannot contain credentials.", nameof(profile));
        }
    }

    private static void ValidateProfileId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 ||
            id.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("A media source ID is invalid.", nameof(id));
        }
    }

    private static bool IsWithinEndpoint(Uri endpoint, Uri location) =>
        UriEquals(endpoint, location) || endpoint.IsBaseOf(location);

    private static bool UriEquals(Uri left, Uri right) =>
        Uri.Compare(
            left,
            right,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped,
            StringComparison.Ordinal) == 0;

    private static string GetProtocolName(MediaSourceProtocol protocol) => protocol switch
    {
        MediaSourceProtocol.LocalFolder => "local-folder",
        MediaSourceProtocol.WebDav => "webdav",
        MediaSourceProtocol.Ftp => "ftp",
        MediaSourceProtocol.Smb => "smb",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };

    private static MediaSourceProtocol ParseProtocolName(string protocol) => protocol switch
    {
        "local-folder" => MediaSourceProtocol.LocalFolder,
        "webdav" => MediaSourceProtocol.WebDav,
        "ftp" => MediaSourceProtocol.Ftp,
        "smb" => MediaSourceProtocol.Smb,
        _ => throw new InvalidDataException("A saved media source protocol is invalid."),
    };
}

internal sealed record MediaSourceProfileDocument
{
    public int SchemaVersion { get; init; } = CredentialProtectingMediaSourceProfileStore.CurrentSchemaVersion;

    public IReadOnlyList<MediaSourceProfileEntry> Profiles { get; init; } = [];

    public IReadOnlyList<MediaSourceFavoriteEntry> Favorites { get; init; } = [];
}

internal sealed record MediaSourceProfileEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Protocol { get; init; }

    public required string Endpoint { get; init; }

    public string UserName { get; init; } = string.Empty;

    public CredentialRef? CredentialReference { get; init; }
}

internal sealed record MediaSourceFavoriteEntry
{
    public required string Id { get; init; }

    public required string Protocol { get; init; }

    public required string Endpoint { get; init; }

    public required string Location { get; init; }

    public required string Name { get; init; }
}
