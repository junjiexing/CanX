// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanX.App;

/// <summary>
/// Keeps one desktop process per CanX data directory on Windows. Explorer starts
/// a new process for every opened file; a second process forwards its file
/// arguments to the running instance and exits instead of opening the same
/// download session, settings and state files concurrently.
/// </summary>
/// <remarks>Only created on Windows; the named mutex and pipe APIs are portable.</remarks>
internal sealed class SingleInstanceGate : IDisposable
{
    private const int MaximumMessageBytes = 256 * 1024;
    private const int MaximumForwardedFiles = 256;
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _serverCancellation;

    private SingleInstanceGate(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Returns the gate for the primary instance, or <see langword="null"/> after
    /// forwarding <paramref name="files"/> to an already running instance.
    /// </summary>
    public static SingleInstanceGate? TryAcquire(string dataDirectory, IReadOnlyList<string> files)
    {
        var identity = CreateIdentity(dataDirectory);
        var mutex = new Mutex(initiallyOwned: true, $@"Local\CanX.App.{identity}", out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceGate(mutex, $"CanX.App.{identity}");
        }

        mutex.Dispose();
        TryForward($"CanX.App.{identity}", files);
        return null;
    }

    /// <summary>Accepts forwarded file lists until disposed.</summary>
    public void StartServer(Action<IReadOnlyList<string>> filesReceived)
    {
        ArgumentNullException.ThrowIfNull(filesReceived);
        _serverCancellation = new CancellationTokenSource();
        var cancellation = _serverCancellation.Token;
        _ = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                    var files = await ReadMessageAsync(server, cancellation).ConfigureAwait(false);
                    filesReceived(files);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
                {
                    // A malformed or interrupted client must not stop the listener.
                }
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _serverCancellation?.Cancel();
        _serverCancellation?.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released on another thread or already abandoned.
        }
        _mutex.Dispose();
    }

    internal static string CreateIdentity(string dataDirectory)
    {
        var normalized = Path.GetFullPath(dataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    internal static byte[] EncodeMessage(IReadOnlyList<string> files) =>
        JsonSerializer.SerializeToUtf8Bytes(
            files.Take(MaximumForwardedFiles).ToArray(),
            SingleInstanceJsonContext.Default.StringArray);

    internal static IReadOnlyList<string> DecodeMessage(ReadOnlySpan<byte> message)
    {
        var files = JsonSerializer.Deserialize(message, SingleInstanceJsonContext.Default.StringArray) ?? [];
        return files
            .Where(static file => !string.IsNullOrWhiteSpace(file) && Path.IsPathFullyQualified(file))
            .Take(MaximumForwardedFiles)
            .ToArray();
    }

    private static void TryForward(string pipeName, IReadOnlyList<string> files)
    {
        var fullPaths = files
            .Where(static file => !string.IsNullOrWhiteSpace(file))
            .Select(static file => Path.GetFullPath(file))
            .ToArray();
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(TimeSpan.FromSeconds(5));
            var message = EncodeMessage(fullPaths);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BitConverter.TryWriteBytes(length, message.Length);
            client.Write(length);
            client.Write(message);
            client.Flush();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The running instance is still starting or shutting down. Exiting is
            // safer than opening the shared data directory from a second process.
        }
    }

    private static async Task<IReadOnlyList<string>> ReadMessageAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length is <= 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("Forwarded file list has an invalid length.");
        }

        var message = new byte[length];
        await stream.ReadExactlyAsync(message, cancellation).ConfigureAwait(false);
        return DecodeMessage(message);
    }
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class SingleInstanceJsonContext : JsonSerializerContext;
