// SPDX-License-Identifier: GPL-3.0-or-later

using Tmds.DBus.Protocol;

namespace CanX.App;

internal static class LinuxDisplaySleepInhibitor
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var address = DBusAddress.Session
            ?? throw new InvalidOperationException("No desktop session bus is available for display sleep inhibition.");
        List<Exception> failures = [];
        foreach (var backend in new[] { Backend.Gnome, Backend.Freedesktop, Backend.Portal })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = new DBusConnection(address);
            try
            {
                using var cancellation = cancellationToken.Register(connection.Dispose);
                await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
                switch (backend)
                {
                    case Backend.Gnome:
                        await ReadCookieAsync(connection, CreateGnomeRequest(connection), cancellationToken).ConfigureAwait(false);
                        break;
                    case Backend.Portal:
                        await AcquirePortalAsync(connection, cancellationToken).ConfigureAwait(false);
                        break;
                    case Backend.Freedesktop:
                        await ReadCookieAsync(connection, CreateFreedesktopRequest(connection, power: false), cancellationToken)
                            .ConfigureAwait(false);
                        try
                        {
                            await ReadCookieAsync(connection, CreateFreedesktopRequest(connection, power: true), cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (DBusErrorReplyException)
                        {
                            // Some desktops provide only ScreenSaver. Its display
                            // inhibitor is still useful without PowerManagement.
                        }
                        break;
                }
                cancellationToken.ThrowIfCancellationRequested();
                // These services scope inhibitors to the calling bus connection.
                // Keep it alive for the entire session, even with no visible window.
                // Disconnecting releases every request, including timed-out replies.
                return connection;
            }
            catch (Exception exception)
            {
                connection.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                failures.Add(exception);
            }
        }
        throw new AggregateException("No desktop service accepted the playback sleep inhibitor.", failures);
    }

    private static async Task ReadCookieAsync(DBusConnection connection, MessageBuffer request, CancellationToken cancellationToken) =>
        _ = await connection.CallMethodAsync(request, static (message, _) => message.GetBodyReader().ReadUInt32())
            .WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);

    private static MessageBuffer CreateGnomeRequest(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.gnome.SessionManager", path: "/org/gnome/SessionManager",
            @interface: "org.gnome.SessionManager", member: "Inhibit", signature: "susu");
        writer.WriteString("org.canx.app");
        writer.WriteUInt32(0); // No X11 window dependency; also works on Wayland/in the background.
        writer.WriteString(DesktopDisplaySleepInhibitor.Reason);
        writer.WriteUInt32(4 | 8); // Suspend and idle, not logout or explicit screen locking.
        return writer.CreateMessage();
    }

    private static async Task AcquirePortalAsync(DBusConnection connection, CancellationToken cancellationToken)
    {
        var token = "canx_" + Guid.NewGuid().ToString("N");
        var path = "/org/freedesktop/portal/desktop/request/" +
            connection.UniqueName!.TrimStart(':').Replace('.', '_') + "/" + token;
        var response = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Subscribe before calling Inhibit: its returned path is only a request,
        // and the later Response signal can report a backend failure.
        using var watch = await connection.AddMatchAsync(new MatchRule
        {
            Type = MessageType.Signal,
            Sender = "org.freedesktop.portal.Desktop",
            Path = path,
            Interface = "org.freedesktop.portal.Request",
            Member = "Response",
        }, static (message, _) => message.GetBodyReader().ReadUInt32(),
            (Notification<uint> notification) =>
            {
                if (notification.HasValue) response.TrySetResult(notification.Value);
                else if (notification.IsCompletion) response.TrySetException(notification.Exception);
            }, emitOnCapturedContext: false).AsTask()
            .WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
        var returnedPath = await connection.CallMethodAsync(CreatePortalRequest(connection, token),
                static (message, _) => message.GetBodyReader().ReadObjectPathAsString())
            .WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(returnedPath, path, StringComparison.Ordinal))
            throw new InvalidOperationException("The desktop portal returned an unexpected inhibition handle.");
        if (await response.Task.WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidOperationException("The desktop portal rejected the sleep inhibitor.");
    }

    private static MessageBuffer CreatePortalRequest(DBusConnection connection, string token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.portal.Desktop", path: "/org/freedesktop/portal/desktop",
            @interface: "org.freedesktop.portal.Inhibit", member: "Inhibit", signature: "sua{sv}");
        writer.WriteString(string.Empty);
        writer.WriteUInt32(4 | 8);
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["reason"] = DesktopDisplaySleepInhibitor.Reason,
            ["handle_token"] = token,
        });
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateFreedesktopRequest(DBusConnection connection, bool power)
    {
        var service = power ? "org.freedesktop.PowerManagement" : "org.freedesktop.ScreenSaver";
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: service,
            path: power ? "/org/freedesktop/PowerManagement/Inhibit" : "/org/freedesktop/ScreenSaver",
            @interface: power ? "org.freedesktop.PowerManagement.Inhibit" : service, member: "Inhibit", signature: "ss");
        writer.WriteString("CanX");
        writer.WriteString(DesktopDisplaySleepInhibitor.Reason);
        return writer.CreateMessage();
    }

    private enum Backend { Gnome, Portal, Freedesktop }
}
