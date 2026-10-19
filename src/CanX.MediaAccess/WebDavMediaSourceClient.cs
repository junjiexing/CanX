// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CanX.Streaming;

namespace CanX.MediaAccess;

public sealed class WebDavMediaSourceClient : IMediaSourceClient
{
    private const long MaximumListingCharacters = 8 * 1024 * 1024;
    private const string PropertyRequest = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:">
          <d:prop>
            <d:displayname />
            <d:resourcetype />
            <d:getcontentlength />
            <d:getcontenttype />
            <d:getlastmodified />
            <d:getetag />
          </d:prop>
        </d:propfind>
        """;

    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly XNamespace Dav = "DAV:";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private bool _disposed;

    public WebDavMediaSourceClient(HttpClient client, Uri root, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(root);
        ValidateHttpUri(root, nameof(root));

        _client = client;
        _ownsClient = ownsClient;
        Root = EnsureDirectoryUri(root);
    }

    public MediaSourceProtocol Protocol => MediaSourceProtocol.WebDav;

    public Uri Root { get; }

    public static WebDavMediaSourceClient Create(
        Uri root,
        NetworkCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateHttpUri(root, nameof(root));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = credential,
            PreAuthenticate = credential is not null,
            UseCookies = false,
        };
        return new WebDavMediaSourceClient(new HttpClient(handler, disposeHandler: true), root, ownsClient: true);
    }

    public async ValueTask<IReadOnlyList<MediaResource>> BrowseAsync(
        Uri? directory = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = EnsureDirectoryUri(directory ?? Root);
        EnsureWithinRoot(target);
        var resources = await QueryAsync(target, depth: 1, cancellationToken).ConfigureAwait(false);
        return resources
            .Where(resource => !UriEquals(resource.Uri, target))
            .OrderByDescending(static resource => resource.IsDirectory)
            .ThenBy(static resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async ValueTask<MediaResource> GetAsync(
        Uri resource,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resource);
        EnsureWithinRoot(resource);
        var resources = await QueryAsync(resource, depth: 0, cancellationToken).ConfigureAwait(false);
        return resources.FirstOrDefault(candidate => UriEquals(candidate.Uri, resource))
            ?? throw new IOException("The WebDAV server did not return metadata for the requested resource.");
    }

    public async ValueTask<IMediaByteStream> OpenReadAsync(
        MediaResource resource,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resource);
        EnsureWithinRoot(resource.Uri);
        if (resource.IsDirectory)
        {
            throw new ArgumentException("A WebDAV directory cannot be opened as media.", nameof(resource));
        }

        return await HttpMediaByteStream.OpenAsync(
            _client,
            resource.Uri,
            resource.Length,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<IReadOnlyList<MediaResource>> QueryAsync(
        Uri target,
        int depth,
        CancellationToken cancellationToken)
    {
        using var response = await HttpReadRetry.SendAsync(
            _client,
            () => CreatePropertyRequest(target, depth),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
        {
            throw new HttpRequestException(
                $"WebDAV PROPFIND failed with status {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        await using var content = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumListingCharacters,
        };
        using var reader = XmlReader.Create(content, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        return ParseResources(document, target);
    }

    private static HttpRequestMessage CreatePropertyRequest(Uri target, int depth)
    {
        var request = new HttpRequestMessage(PropFindMethod, target)
        {
            Content = new StringContent(PropertyRequest, Encoding.UTF8, "application/xml"),
        };
        request.Headers.TryAddWithoutValidation("Depth", depth.ToString(CultureInfo.InvariantCulture));
        return request;
    }

    private IReadOnlyList<MediaResource> ParseResources(XDocument document, Uri requestUri)
    {
        var resources = new List<MediaResource>();
        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href) ||
                !TryResolveResourceUri(requestUri, href, out var resourceUri))
            {
                continue;
            }

            var property = response
                .Elements(Dav + "propstat")
                .FirstOrDefault(IsSuccessfulPropertyStatus)
                ?.Element(Dav + "prop");
            if (property is null)
            {
                continue;
            }

            var isDirectory = property
                .Element(Dav + "resourcetype")
                ?.Element(Dav + "collection") is not null;
            if (isDirectory)
            {
                resourceUri = EnsureDirectoryUri(resourceUri);
            }

            var displayName = property.Element(Dav + "displayname")?.Value.Trim();
            resources.Add(new MediaResource
            {
                Uri = resourceUri,
                Name = string.IsNullOrWhiteSpace(displayName)
                    ? GetResourceName(resourceUri)
                    : displayName,
                IsDirectory = isDirectory,
                Length = ParseLength(property.Element(Dav + "getcontentlength")?.Value),
                ContentType = NullIfWhiteSpace(property.Element(Dav + "getcontenttype")?.Value),
                LastModified = ParseDate(property.Element(Dav + "getlastmodified")?.Value),
                EntityTag = NullIfWhiteSpace(property.Element(Dav + "getetag")?.Value),
            });
        }

        return resources;
    }

    private bool TryResolveResourceUri(Uri requestUri, string href, out Uri resourceUri)
    {
        if (!Uri.TryCreate(requestUri, href.Trim(), out resourceUri!) ||
            !resourceUri.IsAbsoluteUri ||
            !HasSameOrigin(resourceUri, Root) ||
            !IsWithinRoot(resourceUri))
        {
            resourceUri = null!;
            return false;
        }

        return true;
    }

    private void EnsureWithinRoot(Uri uri)
    {
        ValidateHttpUri(uri, nameof(uri));
        if (!HasSameOrigin(uri, Root) || !IsWithinRoot(uri))
        {
            throw new ArgumentException("The WebDAV resource must remain within the configured root.", nameof(uri));
        }
    }

    private bool IsWithinRoot(Uri uri)
    {
        var rootPath = Root.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var resourcePath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        return resourcePath.StartsWith(rootPath, StringComparison.Ordinal);
    }

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsSuccessfulPropertyStatus(XElement propertyStatus)
    {
        var status = propertyStatus.Element(Dav + "status")?.Value;
        return status is not null && status.Contains(" 2", StringComparison.Ordinal);
    }

    private static long? ParseLength(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length >= 0
            ? length
            : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var date)
            ? date
            : null;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetResourceName(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        var separator = path.LastIndexOf('/');
        var name = separator >= 0 ? path[(separator + 1)..] : path;
        return Uri.UnescapeDataString(name);
    }

    private static Uri EnsureDirectoryUri(Uri uri)
    {
        ValidateHttpUri(uri, nameof(uri));
        if (uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            return uri;
        }

        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath + "/" };
        return builder.Uri;
    }

    private static void ValidateHttpUri(Uri uri, string parameterName)
    {
        if (!uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The WebDAV endpoint must be an absolute HTTP or HTTPS URI.", parameterName);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Credentials must not be embedded in the WebDAV URI.", parameterName);
        }
    }

    private static bool UriEquals(Uri left, Uri right) =>
        Uri.Compare(
            left,
            right,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
}
