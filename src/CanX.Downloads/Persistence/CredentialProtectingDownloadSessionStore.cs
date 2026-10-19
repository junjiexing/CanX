// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using CanX.Core.Downloads;
using CanX.Core.Security;

namespace CanX.Downloads.Persistence;

public sealed class CredentialProtectingDownloadSessionStore(
    IDownloadSessionStore inner,
    ICredentialStore credentialStore) : IDownloadSessionStore
{
    private readonly IDownloadSessionStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ICredentialStore _credentialStore =
        credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));

    public async ValueTask<DownloadSessionDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        var document = await _inner.LoadAsync(cancellationToken).ConfigureAwait(false);
        var protectedDocument = await ProtectAsync(document, cancellationToken).ConfigureAwait(false);
        await _inner.SaveAsync(protectedDocument, cancellationToken).ConfigureAwait(false);
        return await HydrateAsync(protectedDocument, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveAsync(
        DownloadSessionDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var previousReferences = await ReadExistingReferencesAsync(cancellationToken).ConfigureAwait(false);
        var protectedDocument = await ProtectAsync(document, cancellationToken).ConfigureAwait(false);
        await _inner.SaveAsync(protectedDocument, cancellationToken).ConfigureAwait(false);

        previousReferences.ExceptWith(CollectReferences(protectedDocument));
        foreach (var reference in previousReferences)
        {
            await _credentialStore.DeleteAsync(new CredentialRef { Id = reference }, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<DownloadSessionDocument> ProtectAsync(
        DownloadSessionDocument document,
        CancellationToken cancellationToken)
    {
        var globalOptions = new Dictionary<string, string>(document.GlobalOptions, StringComparer.Ordinal);
        var globalReferences = new Dictionary<string, CredentialRef>(
            document.GlobalOptionCredentialReferences,
            StringComparer.Ordinal);
        foreach (var option in globalOptions.ToArray())
        {
            if (!DownloadSecretClassifier.IsSensitiveOption(option.Key) &&
                !globalReferences.ContainsKey(option.Key))
            {
                continue;
            }

            var reference = GetGlobalOptionReference(option.Key);
            await _credentialStore.SetAsync(reference, option.Value, cancellationToken).ConfigureAwait(false);
            globalReferences[option.Key] = reference;
            globalOptions.Remove(option.Key);
        }

        var groups = new List<DownloadSessionGroup>(document.Groups.Count);
        foreach (var group in document.Groups)
        {
            var request = group.Request;
            var references = request.CredentialReferences;
            var source = request.Source;
            var sourceReference = references.Source;
            if (DownloadSecretClassifier.IsSensitiveUri(source))
            {
                sourceReference ??= GetGroupReference(group.GroupId, "source");
                await _credentialStore.SetAsync(sourceReference, source, cancellationToken).ConfigureAwait(false);
                source = DownloadSecretClassifier.RemoveSecretsFromUri(source);
            }

            var mirrors = request.MirrorUris.ToList();
            var mirrorReferences = new Dictionary<int, CredentialRef>(references.Mirrors);
            for (var index = 0; index < mirrors.Count; index++)
            {
                if (!DownloadSecretClassifier.IsSensitiveUri(mirrors[index]))
                {
                    continue;
                }

                mirrorReferences.TryGetValue(index, out var reference);
                reference ??= GetGroupReference(group.GroupId, $"mirror-{index}");
                await _credentialStore.SetAsync(reference, mirrors[index], cancellationToken).ConfigureAwait(false);
                mirrorReferences[index] = reference;
                mirrors[index] = DownloadSecretClassifier.RemoveSecretsFromUri(mirrors[index]);
            }

            var options = new Dictionary<string, string>(request.Options, StringComparer.Ordinal);
            var optionReferences = new Dictionary<string, CredentialRef>(references.Options, StringComparer.Ordinal);
            foreach (var option in options.ToArray())
            {
                if (!DownloadSecretClassifier.IsSensitiveOption(option.Key) &&
                    !optionReferences.ContainsKey(option.Key))
                {
                    continue;
                }

                var reference = GetGroupReference(group.GroupId, $"option-{HashName(option.Key)}");
                await _credentialStore.SetAsync(reference, option.Value, cancellationToken).ConfigureAwait(false);
                optionReferences[option.Key] = reference;
                options.Remove(option.Key);
            }

            groups.Add(group with
            {
                Request = request with
                {
                    Source = source,
                    MirrorUris = mirrors,
                    Options = options,
                    CredentialReferences = new DownloadCredentialReferences
                    {
                        Source = sourceReference,
                        Mirrors = mirrorReferences,
                        Options = optionReferences,
                    },
                },
                Tasks = group.Tasks.Select(SanitizeSnapshot).ToList(),
            });
        }

        return document with
        {
            SchemaVersion = DownloadSessionDocument.CurrentSchemaVersion,
            GlobalOptions = globalOptions,
            GlobalOptionCredentialReferences = globalReferences,
            Groups = groups,
        };
    }

    private async ValueTask<DownloadSessionDocument> HydrateAsync(
        DownloadSessionDocument document,
        CancellationToken cancellationToken)
    {
        var globalOptions = new Dictionary<string, string>(document.GlobalOptions, StringComparer.Ordinal);
        foreach (var reference in document.GlobalOptionCredentialReferences)
        {
            globalOptions[reference.Key] = await RequireSecretAsync(reference.Value, cancellationToken).ConfigureAwait(false);
        }

        var groups = new List<DownloadSessionGroup>(document.Groups.Count);
        foreach (var group in document.Groups)
        {
            var request = group.Request;
            var source = request.CredentialReferences.Source is { } sourceReference
                ? await RequireSecretAsync(sourceReference, cancellationToken).ConfigureAwait(false)
                : request.Source;
            var mirrors = request.MirrorUris.ToList();
            foreach (var reference in request.CredentialReferences.Mirrors)
            {
                if (reference.Key < 0 || reference.Key >= mirrors.Count)
                {
                    throw new InvalidDataException($"Credential mirror index {reference.Key} is outside the saved URI list.");
                }

                mirrors[reference.Key] = await RequireSecretAsync(reference.Value, cancellationToken).ConfigureAwait(false);
            }

            var options = new Dictionary<string, string>(request.Options, StringComparer.Ordinal);
            foreach (var reference in request.CredentialReferences.Options)
            {
                options[reference.Key] = await RequireSecretAsync(reference.Value, cancellationToken).ConfigureAwait(false);
            }

            groups.Add(group with
            {
                Request = request with { Source = source, MirrorUris = mirrors, Options = options },
            });
        }

        return document with { GlobalOptions = globalOptions, Groups = groups };
    }

    private async ValueTask<string> RequireSecretAsync(
        CredentialRef reference,
        CancellationToken cancellationToken)
    {
        if (!reference.Id.StartsWith("canx.download.", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Credential reference '{reference.Id}' is outside the download namespace.");
        }

        return await _credentialStore.GetAsync(reference, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Credential '{reference.Id}' is missing from the operating-system store.");
    }

    private async ValueTask<HashSet<string>> ReadExistingReferencesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return CollectReferences(await _inner.LoadAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    private static HashSet<string> CollectReferences(DownloadSessionDocument document)
    {
        var result = document.GlobalOptionCredentialReferences.Values
            .Select(static reference => reference.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var references in document.Groups.Select(static group => group.Request.CredentialReferences))
        {
            if (references.Source is not null)
            {
                result.Add(references.Source.Id);
            }

            result.UnionWith(references.Mirrors.Values.Select(static reference => reference.Id));
            result.UnionWith(references.Options.Values.Select(static reference => reference.Id));
        }

        return result;
    }

    private static CredentialRef GetGlobalOptionReference(string optionName) =>
        new() { Id = $"canx.download.global.option.{HashName(optionName)}" };

    private static CredentialRef GetGroupReference(Guid groupId, string field) =>
        new() { Id = $"canx.download.{groupId:N}.{field}" };

    private static string HashName(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static DownloadTaskSnapshot SanitizeSnapshot(DownloadTaskSnapshot snapshot) => snapshot with
    {
        Source = DownloadSecretClassifier.RemoveSecretsFromUri(snapshot.Source),
        MirrorUris = snapshot.MirrorUris.Select(DownloadSecretClassifier.RemoveSecretsFromUri).ToList(),
        Options = snapshot.Options
            .Where(static option => !DownloadSecretClassifier.IsSensitiveOption(option.Key))
            .ToDictionary(static option => option.Key, static option => option.Value, StringComparer.Ordinal),
        Files = snapshot.Files.Select(static file => file with
        {
            Uris = file.Uris.Select(static uri => uri with
            {
                Uri = DownloadSecretClassifier.RemoveSecretsFromUri(uri.Uri),
            }).ToList(),
        }).ToList(),
        Torrent = snapshot.Torrent is null ? null : snapshot.Torrent with
        {
            AnnounceList = snapshot.Torrent.AnnounceList.Select(static tier =>
                tier.Select(DownloadSecretClassifier.RemoveSecretsFromUri).ToList()).ToList(),
        },
    };
}
