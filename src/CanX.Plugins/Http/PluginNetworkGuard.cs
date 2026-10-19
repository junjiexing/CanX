// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;

namespace CanX.Plugins.Http;

internal sealed class PluginNetworkGuard(
    Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolver = null)
{
    private readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> resolver =
        resolver ?? ResolveAsync;

    public ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken) => ConnectCoreAsync(context, cancellationToken, allowPrivate: false);

    // A proxy is an explicit endpoint selected by the plugin and approved by its
    // HTTP host permission. It may legitimately run on loopback or a private LAN.
    public ValueTask<Stream> ConnectProxyAsync(
        SocketsHttpConnectionContext context,
        Uri proxy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(proxy);
        if (!string.Equals(context.DnsEndPoint.Host, proxy.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            context.DnsEndPoint.Port != proxy.Port)
        {
            throw new PluginHttpException("invalid_proxy_connection", "The proxy connection endpoint changed.");
        }

        return ConnectCoreAsync(context, cancellationToken, allowPrivate: true);
    }

    private async ValueTask<Stream> ConnectCoreAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken,
        bool allowPrivate)
    {
        ArgumentNullException.ThrowIfNull(context);
        var addresses = await ResolveAddressesAsync(
            context.DnsEndPoint.Host, cancellationToken, allowPrivate).ConfigureAwait(false);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = exception;
                if (exception is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException("The approved plugin host could not be reached.", lastError);
    }

    internal ValueTask<IReadOnlyList<IPAddress>> ResolvePublicAddressesAsync(
        string host,
        CancellationToken cancellationToken) => ResolveAddressesAsync(host, cancellationToken, allowPrivate: false);

    internal ValueTask<IReadOnlyList<IPAddress>> ResolveProxyAddressesAsync(
        string host,
        CancellationToken cancellationToken) => ResolveAddressesAsync(host, cancellationToken, allowPrivate: true);

    private async ValueTask<IReadOnlyList<IPAddress>> ResolveAddressesAsync(
        string host,
        CancellationToken cancellationToken,
        bool allowPrivate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        IPAddress[] addresses;
        try
        {
            addresses = await resolver(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new HttpRequestException("The approved plugin host could not be resolved.", exception);
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException("The approved plugin host resolved to no addresses.");
        }
        if (!allowPrivate && addresses.Any(static address => !IsPublicAddress(address)))
        {
            throw new PluginHttpException(
                "private_network_denied",
                "Plugin HTTP requests cannot connect to loopback, private, link-local, or reserved addresses.");
        }

        return addresses.Distinct().ToArray();
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 0 && bytes[2] == 0 => false,
                192 when bytes[1] == 0 && bytes[2] == 2 => false,
                192 when bytes[1] == 168 => false,
                198 when bytes[1] is 18 or 19 => false,
                198 when bytes[1] == 51 && bytes[2] == 100 => false,
                203 when bytes[1] == 0 && bytes[2] == 113 => false,
                >= 224 => false,
                _ => true,
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) ||
            address.Equals(IPAddress.IPv6Loopback) || address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast || address.IsIPv6SiteLocal)
        {
            return false;
        }

        // Unique-local fc00::/7 and documentation 2001:db8::/32 are never Internet destinations.
        return (bytes[0] & 0xfe) != 0xfc &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }

    private static async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}
