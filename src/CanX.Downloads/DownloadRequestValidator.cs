// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Downloads;

namespace CanX.Downloads;

internal static class DownloadRequestValidator
{
    private static readonly HashSet<string> SupportedUriSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        Uri.UriSchemeFtp,
        "sftp",
        "magnet",
    };

    public static AddDownloadRequest ValidateAndNormalize(AddDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Source))
        {
            throw new ArgumentException("A download source is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            throw new ArgumentException("A destination directory is required.", nameof(request));
        }

        var source = request.Source.Trim();
        var mirrorUris = new List<string>();
        if (request.Kind == DownloadSourceKind.Uri)
        {
            var sourceUri = ValidateUri(source, request);
            if (sourceUri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase) && request.MirrorUris.Count > 0)
            {
                throw new ArgumentException("Magnet downloads cannot have mirror URIs.", nameof(request));
            }

            foreach (var mirror in request.MirrorUris)
            {
                var normalizedMirror = mirror.Trim();
                var mirrorUri = ValidateUri(normalizedMirror, request);
                if (mirrorUri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("A mirror cannot be a magnet URI.", nameof(request));
                }

                if (!string.Equals(normalizedMirror, source, StringComparison.Ordinal) &&
                    !mirrorUris.Contains(normalizedMirror, StringComparer.Ordinal))
                {
                    mirrorUris.Add(normalizedMirror);
                }
            }
        }
        else if (request.MirrorUris.Count > 0)
        {
            throw new ArgumentException("Mirror URIs are only valid for URI downloads.", nameof(request));
        }
        else
        {
            source = Path.GetFullPath(source);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("The metadata file does not exist.", source);
            }
        }

        if (request.OutputName is { } outputName &&
            (Path.IsPathRooted(outputName) || outputName != Path.GetFileName(outputName)))
        {
            throw new ArgumentException("The output name must be a file name without a directory.", nameof(request));
        }

        var options = new Dictionary<string, string>(request.Options, StringComparer.Ordinal);
        ValidateOptions(options);
        if (request.StartPaused && request.ScheduledStartAt is not null)
        {
            throw new ArgumentException("A task cannot be both manually paused and scheduled.", nameof(request));
        }

        return request with
        {
            Source = source,
            MirrorUris = mirrorUris,
            DestinationDirectory = Path.GetFullPath(request.DestinationDirectory),
            OutputName = string.IsNullOrWhiteSpace(request.OutputName) ? null : request.OutputName.Trim(),
            Options = options,
            ScheduledStartAt = request.ScheduledStartAt?.ToUniversalTime(),
        };
    }

    private static Uri ValidateUri(string source, AddDownloadRequest request)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !SupportedUriSchemes.Contains(uri.Scheme))
        {
            throw new ArgumentException("The source must be an HTTP(S), FTP, SFTP, or magnet URI.", nameof(request));
        }

        return uri;
    }

    public static void ValidateOptions(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Key) || option.Key.Any(char.IsWhiteSpace) || option.Key.StartsWith('-'))
            {
                throw new ArgumentException($"Invalid aria2 option name '{option.Key}'.", nameof(options));
            }

            if (option.Value.Contains('\0'))
            {
                throw new ArgumentException($"Option '{option.Key}' contains a null character.", nameof(options));
            }
        }
    }
}
