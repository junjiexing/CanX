// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanX.Plugins.Packaging;

public sealed record PluginSignatureInfo(string KeyId, string Algorithm, bool IsTrusted);

public sealed record PluginSignaturePolicy
{
    public bool RequireSignature { get; init; }

    public bool RequireTrustedSigner { get; init; }

    public IReadOnlySet<string> TrustedKeyIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public static class PluginPackageSigner
{
    public static byte[] CreateSignatureDocument(
        IReadOnlyDictionary<string, byte[]> files,
        ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.KeySize != 256)
        {
            throw new ArgumentException("Plugin packages must be signed with an ECDSA P-256 key.", nameof(signingKey));
        }
        if (files.Count == 0 || files.Keys.Any(path =>
                string.IsNullOrWhiteSpace(path) || path == PluginSignatureVerifier.SignatureFileName))
        {
            throw new ArgumentException("Signed files must be non-empty and cannot include the signature document.", nameof(files));
        }

        var hashes = files.ToDictionary(
            entry => entry.Key,
            entry => Convert.ToHexString(SHA256.HashData(entry.Value)).ToLowerInvariant(),
            StringComparer.Ordinal);
        var publicKey = signingKey.ExportSubjectPublicKeyInfo();
        var document = new PluginSignatureDocument
        {
            FormatVersion = 1,
            Algorithm = PluginSignatureVerifier.Algorithm,
            KeyId = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant(),
            PublicKey = Convert.ToBase64String(publicKey),
            Signature = Convert.ToBase64String(signingKey.SignData(
                PluginSignatureVerifier.CreatePayload(hashes),
                HashAlgorithmName.SHA256)),
            Files = hashes
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new PluginSignedFile { Path = entry.Key, Sha256 = entry.Value })
                .ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(
            document,
            PluginSignatureJsonContext.Default.PluginSignatureDocument);
    }
}

internal static class PluginSignatureVerifier
{
    internal const string SignatureFileName = "canx-signature.json";
    internal const string Algorithm = "ecdsa-p256-sha256";

    public static PluginSignatureInfo? Verify(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        PluginSignaturePolicy policy,
        long maximumEntryBytes)
    {
        if (!entries.TryGetValue(SignatureFileName, out var signatureEntry))
        {
            if (policy.RequireSignature)
            {
                throw new PluginPackageException("Plugin package must have a valid signature.");
            }
            return null;
        }

        PluginSignatureDocument signature;
        try
        {
            using var stream = signatureEntry.Open();
            signature = JsonSerializer.Deserialize(stream, PluginSignatureJsonContext.Default.PluginSignatureDocument)
                ?? throw Invalid("Signature document must be an object.");
        }
        catch (JsonException)
        {
            throw Invalid("Signature document is not valid JSON.");
        }

        if (signature.FormatVersion != 1 || signature.Algorithm != Algorithm ||
            string.IsNullOrWhiteSpace(signature.PublicKey) || string.IsNullOrWhiteSpace(signature.Signature) ||
            signature.Files.Length == 0)
        {
            throw Invalid("Signature document has missing or unsupported fields.");
        }

        byte[] publicKey;
        byte[] signatureBytes;
        try
        {
            publicKey = Convert.FromBase64String(signature.PublicKey);
            signatureBytes = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException)
        {
            throw Invalid("Signature document contains invalid base64 data.");
        }
        if (publicKey.Length is < 64 or > 1024 || signatureBytes.Length is < 32 or > 1024)
        {
            throw Invalid("Signature key or value has an invalid size.");
        }

        var keyId = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        if (!string.Equals(signature.KeyId, keyId, StringComparison.Ordinal))
        {
            throw Invalid("Signature key id does not match its public key.");
        }

        var expectedFiles = entries
            .Where(entry => entry.Key != SignatureFileName && !entry.Value.FullName.EndsWith('/'))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var signedFiles = signature.Files
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!expectedFiles.SequenceEqual(signedFiles, StringComparer.Ordinal))
        {
            throw Invalid("Signature file list does not exactly match package contents.");
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in signature.Files)
        {
            if (string.IsNullOrEmpty(file.Path) || !entries.TryGetValue(file.Path, out var entry) ||
                file.Sha256.Length != 64)
            {
                throw Invalid("Signature file entry is invalid.");
            }
            using var stream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                total += read;
                if (total > maximumEntryBytes)
                {
                    throw Invalid("Signed package entry exceeds the configured limit.");
                }
                hash.AppendData(buffer, 0, read);
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actual),
                    Encoding.ASCII.GetBytes(file.Sha256)))
            {
                throw Invalid($"Signed package entry '{file.Path}' was modified.");
            }
            if (!hashes.TryAdd(file.Path, actual))
            {
                throw Invalid("Signature file list contains duplicates.");
            }
        }

        var payload = CreatePayload(hashes);
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length || ecdsa.KeySize != 256 ||
                !ecdsa.VerifyData(payload, signatureBytes, HashAlgorithmName.SHA256))
            {
                throw Invalid("Plugin package signature is invalid.");
            }
        }
        catch (CryptographicException)
        {
            throw Invalid("Plugin package signature or public key is invalid.");
        }

        var trusted = policy.TrustedKeyIds.Contains(keyId);
        if (policy.RequireTrustedSigner && !trusted)
        {
            throw Invalid($"Plugin signer '{keyId}' is not trusted.");
        }
        return new PluginSignatureInfo(keyId, Algorithm, trusted);
    }

    internal static byte[] CreatePayload(IReadOnlyDictionary<string, string> hashes)
    {
        var builder = new StringBuilder("CanXPluginSignatureV1\n");
        foreach (var (path, hash) in hashes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            builder.Append(path).Append('\t').Append(hash).Append('\n');
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static PluginPackageException Invalid(string message) => new(message);
}

internal sealed class PluginSignatureDocument
{
    public int FormatVersion { get; init; }

    public string Algorithm { get; init; } = string.Empty;

    public string KeyId { get; init; } = string.Empty;

    public string PublicKey { get; init; } = string.Empty;

    public string Signature { get; init; } = string.Empty;

    public PluginSignedFile[] Files { get; init; } = [];
}

internal sealed class PluginSignedFile
{
    public string Path { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginSignatureDocument))]
internal sealed partial class PluginSignatureJsonContext : JsonSerializerContext;
