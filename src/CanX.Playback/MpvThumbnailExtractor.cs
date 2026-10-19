// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using CanX.Playback.Engine;
using CanX.Playback.Interop;
using CanX.Streaming;

namespace CanX.Playback;

/// <summary>Extracts small preview frames with a separate, paused libmpv instance.</summary>
public sealed class MpvThumbnailExtractor : IAsyncDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MediaStreamBroker _streamBroker;
    private readonly bool _ownsStreamBroker;
    private nint _handle;
    private MpvStreamCallbackBridge? _streamCallbackBridge;
    private string? _loadedSource;
    private int _disposed;

    public MpvThumbnailExtractor(MediaStreamBroker? streamBroker = null)
    {
        _streamBroker = streamBroker ?? new MediaStreamBroker();
        _ownsStreamBroker = streamBroker is null;
    }

    public async Task<byte[]> CaptureAsync(string source, double seconds, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OperationTimeout);
            return await Task.Run(() => CaptureCore(source, seconds, timeout.Token), timeout.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(DisposeCore).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private byte[] CaptureCore(string source, double seconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        if (!string.Equals(_loadedSource, source, StringComparison.Ordinal))
        {
            try
            {
                RunCommand("loadfile", _streamBroker.GetPlaybackSource(source), "replace");
                WaitForFileLoaded();
            }
            catch
            {
                ResetHandle();
                throw;
            }
            _loadedSource = source;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RunCommand("seek", seconds.ToString("R", CultureInfo.InvariantCulture), "absolute+keyframes");
            // Finish the native seek even if the hover request was canceled.
            // Otherwise its completion event can be mistaken for the next seek.
            WaitForSeek();
        }
        catch
        {
            ResetHandle();
            throw;
        }

        var directory = Path.Combine(Path.GetTempPath(), "CanX", "preview");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.png");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunCommand("screenshot-to-file", path, "video");
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) throw new InvalidDataException("mpv returned an empty preview frame.");
            return bytes;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private void EnsureInitialized()
    {
        if (_handle != 0) return;
        var handle = MpvNative.Create();
        if (handle == 0) throw new InvalidOperationException("mpv_create failed for preview.");
        MpvStreamCallbackBridge? bridge = null;
        try
        {
            bridge = new MpvStreamCallbackBridge(_streamBroker, new MpvStreamCallbackCounters());
            bridge.Register(handle);
            SetOption(handle, "config", "no");
            SetOption(handle, "terminal", "no");
            SetOption(handle, "vo", "null");
            SetOption(handle, "audio", "no");
            SetOption(handle, "pause", "yes");
            SetOption(handle, "hwdec", "no");
            SetOption(handle, "vd-lavc-threads", "2");
            SetOption(handle, "screenshot-sw", "yes");
            SetOption(handle, "screenshot-format", "png");
            SetOption(handle, "vf", "lavfi=[scale=320:-2]");
            SetOption(handle, "keep-open", "yes");
            EnsureSuccess(MpvNative.Initialize(handle), "mpv_initialize");
            _handle = handle;
            _streamCallbackBridge = bridge;
        }
        catch
        {
            MpvNative.TerminateDestroy(handle);
            bridge?.Dispose();
            throw;
        }
    }

    private unsafe void WaitForFileLoaded()
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(OperationTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var eventPointer = MpvNative.WaitEvent(_handle, 0.05);
            if (eventPointer == 0) continue;
            var playbackEvent = *(MpvEvent*)eventPointer;
            if (playbackEvent.EventId == MpvEventId.FileLoaded) return;
            if (playbackEvent.EventId == MpvEventId.EndFile && playbackEvent.Data != 0)
            {
                var endFile = *(MpvEventEndFile*)playbackEvent.Data;
                if (endFile.Reason == MpvEndFileReason.Error)
                    throw new InvalidOperationException($"Preview media failed to load: {MpvNative.GetError(endFile.Error)}.");
            }
        }

        throw new TimeoutException("Preview media did not load in time.");
    }

    private unsafe void WaitForSeek()
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(OperationTimeout.TotalSeconds * Stopwatch.Frequency);
        var seekStarted = false;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var eventPointer = MpvNative.WaitEvent(_handle, 0.05);
            if (eventPointer == 0) continue;
            var playbackEvent = *(MpvEvent*)eventPointer;
            if (playbackEvent.EventId == MpvEventId.Seek) seekStarted = true;
            if (playbackEvent.EventId == MpvEventId.PlaybackRestart && seekStarted) return;
        }

        throw new TimeoutException("Preview seek did not finish in time.");
    }

    private void RunCommand(params string[] arguments) =>
        EnsureSuccess(MpvNative.RunCommand(_handle, arguments), $"mpv command '{arguments[0]}'");

    private static void SetOption(nint handle, string name, string value) =>
        EnsureSuccess(MpvNative.SetOptionString(handle, name, value), $"mpv option '{name}'");

    private static void EnsureSuccess(int result, string action)
    {
        if (result < 0) throw new InvalidOperationException($"{action} failed: {MpvNative.GetError(result)} ({result}).");
    }

    private void DisposeCore()
    {
        ResetHandle();
        if (_ownsStreamBroker) _streamBroker.Dispose();
    }

    private void ResetHandle()
    {
        var handle = _handle;
        _handle = 0;
        _loadedSource = null;
        if (handle != 0) MpvNative.TerminateDestroy(handle);
        _streamCallbackBridge?.Dispose();
        _streamCallbackBridge = null;
    }
}
