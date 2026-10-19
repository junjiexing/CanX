// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using CanX.Core.Downloads;

namespace CanX.Downloads;

internal static class DownloadIdentity
{
    public static string? GetInfoHash(AddDownloadRequest request, string? knownInfoHash = null)
    {
        if (TryNormalizeInfoHash(knownInfoHash, out var normalized))
        {
            return normalized;
        }

        if (TryGetMagnetInfoHash(request.Source, out normalized))
        {
            return normalized;
        }

        return request.Kind == DownloadSourceKind.Torrent
            ? TryReadTorrentInfoHash(request.Source)
            : null;
    }

    public static string? GetInfoHash(DownloadTaskSnapshot snapshot)
    {
        if (TryNormalizeInfoHash(snapshot.InfoHash, out var normalized) ||
            TryGetMagnetInfoHash(snapshot.Source, out normalized))
        {
            return normalized;
        }

        return snapshot.Kind == DownloadSourceKind.Torrent
            ? TryReadTorrentInfoHash(snapshot.Source)
            : null;
    }

    public static bool TryGetMagnetInfoHash(string source, out string infoHash)
    {
        infoHash = string.Empty;
        if (!source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var queryStart = source.IndexOf('?');
        if (queryStart < 0 || queryStart == source.Length - 1)
        {
            return false;
        }

        foreach (var parameter in source[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = parameter.IndexOf('=');
            if (separator <= 0 || !parameter[..separator].Equals("xt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value;
            try
            {
                value = Uri.UnescapeDataString(parameter[(separator + 1)..].Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                continue;
            }

            const string prefix = "urn:btih:";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                TryNormalizeInfoHash(value[prefix.Length..], out infoHash))
            {
                return true;
            }
        }

        return false;
    }

    public static string? GetCanonicalSource(AddDownloadRequest request) => request.Kind switch
    {
        DownloadSourceKind.Uri when Uri.TryCreate(request.Source, UriKind.Absolute, out var uri) &&
                                    !uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase) =>
            CanonicalizeUri(uri),
        DownloadSourceKind.Torrent or DownloadSourceKind.Metalink =>
            Path.GetFullPath(request.Source),
        _ => null,
    };

    public static string? GetCanonicalSource(DownloadTaskSnapshot snapshot) => GetCanonicalSource(new AddDownloadRequest
    {
        Kind = snapshot.Kind,
        Source = snapshot.Source,
        DestinationDirectory = snapshot.DestinationDirectory,
    });

    private static bool TryNormalizeInfoHash(string? value, out string infoHash)
    {
        infoHash = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.Length == 40 && value.All(Uri.IsHexDigit))
        {
            infoHash = value.ToLowerInvariant();
            return true;
        }

        if (value.Length != 32)
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[20];
        var bitBuffer = 0;
        var bitCount = 0;
        var outputIndex = 0;
        foreach (var character in value)
        {
            var digit = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= 'a' and <= 'z' => character - 'a',
                >= '2' and <= '7' => character - '2' + 26,
                _ => -1,
            };
            if (digit < 0)
            {
                return false;
            }

            bitBuffer = (bitBuffer << 5) | digit;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                decoded[outputIndex++] = (byte)(bitBuffer >> bitCount);
                bitBuffer &= (1 << bitCount) - 1;
            }
        }

        if (outputIndex != decoded.Length || bitCount != 0)
        {
            return false;
        }

        infoHash = Convert.ToHexStringLower(decoded);
        return true;
    }

    private static string? TryReadTorrentInfoHash(string path)
    {
        try
        {
            var content = File.ReadAllBytes(path);
            if (content.Length < 2 || content[0] != (byte)'d')
            {
                return null;
            }

            var offset = 1;
            while (offset < content.Length && content[offset] != (byte)'e')
            {
                var key = ReadByteString(content, ref offset);
                var valueStart = offset;
                SkipValue(content, ref offset);
                if (key.SequenceEqual("info"u8))
                {
                    return Convert.ToHexStringLower(SHA1.HashData(content.AsSpan(valueStart, offset - valueStart)));
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidDataException)
        {
        }

        return null;
    }

    private static ReadOnlySpan<byte> ReadByteString(byte[] content, ref int offset)
    {
        var length = ReadByteStringLength(content, ref offset);
        if (length > content.Length - offset)
        {
            throw new InvalidDataException("The bencoded byte string exceeds the torrent file.");
        }

        var value = content.AsSpan(offset, length);
        offset += length;
        return value;
    }

    private static int ReadByteStringLength(byte[] content, ref int offset)
    {
        if (offset >= content.Length || content[offset] is < (byte)'0' or > (byte)'9')
        {
            throw new InvalidDataException("Invalid bencoded byte string.");
        }

        var length = 0;
        while (offset < content.Length && content[offset] != (byte)':')
        {
            var digit = content[offset++] - (byte)'0';
            if (digit > 9 || length > (int.MaxValue - digit) / 10)
            {
                throw new InvalidDataException("Invalid bencoded byte string length.");
            }

            length = (length * 10) + digit;
        }

        if (offset >= content.Length || content[offset++] != (byte)':')
        {
            throw new InvalidDataException("Invalid bencoded byte string separator.");
        }

        return length;
    }

    private static void SkipValue(byte[] content, ref int offset)
    {
        if (offset >= content.Length)
        {
            throw new InvalidDataException("Unexpected end of bencoded data.");
        }

        switch (content[offset])
        {
            case (byte)'i':
                offset++;
                while (offset < content.Length && content[offset] != (byte)'e')
                {
                    offset++;
                }

                RequireEndMarker(content, ref offset);
                break;
            case (byte)'l':
                offset++;
                while (offset < content.Length && content[offset] != (byte)'e')
                {
                    SkipValue(content, ref offset);
                }

                RequireEndMarker(content, ref offset);
                break;
            case (byte)'d':
                offset++;
                while (offset < content.Length && content[offset] != (byte)'e')
                {
                    _ = ReadByteString(content, ref offset);
                    SkipValue(content, ref offset);
                }

                RequireEndMarker(content, ref offset);
                break;
            case >= (byte)'0' and <= (byte)'9':
                _ = ReadByteString(content, ref offset);
                break;
            default:
                throw new InvalidDataException("Invalid bencoded value.");
        }
    }

    private static void RequireEndMarker(byte[] content, ref int offset)
    {
        if (offset >= content.Length || content[offset++] != (byte)'e')
        {
            throw new InvalidDataException("Missing bencoded end marker.");
        }
    }

    private static string CanonicalizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }
}
