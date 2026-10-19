// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text.Json;
using CanX.Core.Diagnostics;
using CanX.Downloads.Interop;
using CanX.Downloads.Serialization;

namespace CanX.Downloads.Engine;

public sealed unsafe class NativeAria2Engine : IAria2Engine
{
    private static readonly Lock LibraryLock = new();
    private static bool s_libraryInitialized;

    private readonly IApplicationLog? _applicationLog;
    private nint _session;
    private bool _disposed;
    private int _desiredLoggingState;
    private int _appliedLoggingState = -1;

    public NativeAria2Engine(IApplicationLog? applicationLog = null)
    {
        _applicationLog = applicationLog;
        _desiredLoggingState = applicationLog?.IsEnabled == true ? 1 : 0;
        if (_applicationLog is not null)
        {
            _applicationLog.EnabledChanged += OnLoggingEnabledChanged;
        }
    }

    public bool IsInitialized => _session != 0;

    public void Initialize(IReadOnlyDictionary<string, string> globalOptions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsInitialized)
        {
            throw new InvalidOperationException("The aria2 engine is already initialized.");
        }

        if (Aria2Native.AbiVersion() != Aria2Native.ExpectedAbiVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported canx_aria2 ABI. Expected {Aria2Native.ExpectedAbiVersion}.");
        }

        EnsureLibraryInitialized();
        // CanX owns download persistence. Never import the machine-wide aria2
        // configuration, which may specify input-file/save-session and inject
        // unrelated tasks (or duplicates) into this embedded session.
        var isolatedOptions = new Dictionary<string, string>(globalOptions, StringComparer.Ordinal)
        {
            ["no-conf"] = "true",
        };
        var effectiveOptions = ConfigureLogging(isolatedOptions);
        using var options = new NativeOptionList(effectiveOptions);
        nint session = 0;
        try
        {
            Ensure(Aria2Native.SessionCreate(options.Pointer, options.Count, 0, 0, &session), "create session");
            _session = session;
            _appliedLoggingState = Volatile.Read(ref _desiredLoggingState);
        }
        catch
        {
            if (session != 0)
            {
                Aria2Native.SessionDestroy(session);
            }

            throw;
        }
    }

    public ulong AddUri(string uri, IReadOnlyDictionary<string, string> options, int position) =>
        AddUris([uri], options, position);

    public ulong AddUris(IReadOnlyList<string> uris, IReadOnlyDictionary<string, string> options, int position)
    {
        EnsureSession();
        if (uris.Count == 0)
        {
            throw new ArgumentException("At least one URI is required.", nameof(uris));
        }

        using var nativeUris = new NativeStringList(uris);
        using var nativeOptions = new NativeOptionList(options);
        ulong gid = 0;
        Ensure(
            Aria2Native.AddUris(
                _session,
                nativeUris.Pointer,
                nativeUris.Count,
                nativeOptions.Pointer,
                nativeOptions.Count,
                position,
                &gid),
            "add mirror URIs");
        return gid;
    }

    public ulong AddTorrent(string torrentFile, IReadOnlyDictionary<string, string> options, int position)
    {
        EnsureSession();
        using var nativeOptions = new NativeOptionList(options);
        ulong gid = 0;
        Ensure(
            Aria2Native.AddTorrent(_session, torrentFile, nativeOptions.Pointer, nativeOptions.Count, position, &gid),
            "add torrent");
        return gid;
    }

    public IReadOnlyList<ulong> AddMetalink(
        string metalinkFile,
        IReadOnlyDictionary<string, string> options,
        int position)
    {
        EnsureSession();
        using var nativeOptions = new NativeOptionList(options);
        var list = new NativeGidList();
        Ensure(
            Aria2Native.AddMetalink(_session, metalinkFile, nativeOptions.Pointer, nativeOptions.Count, position, &list),
            "add Metalink");
        try
        {
            var result = new ulong[checked((int)list.Count)];
            var source = (ulong*)list.Items;
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = source[index];
            }

            return result;
        }
        finally
        {
            Aria2Native.FreeGidList(&list);
        }
    }

    public bool RunOnce()
    {
        EnsureSession();
        ApplyPendingLoggingConfiguration();
        var result = Aria2Native.RunOnce(_session);
        EnsureNonNegative(result, "run event loop");
        return result > 0;
    }

    public Aria2DownloadDetails GetDownload(ulong gid)
    {
        EnsureSession();
        var json = GetNativeString(static (session, value, result) => Aria2Native.GetDownloadJson(session, value, result), gid);
        return JsonSerializer.Deserialize(json, Aria2JsonContext.Default.Aria2DownloadDetails)
            ?? throw new InvalidDataException("libaria2 returned an empty download snapshot.");
    }

    public Aria2GlobalStatus GetGlobalStatus()
    {
        EnsureSession();
        var status = new NativeGlobalStatus { StructSize = (uint)sizeof(NativeGlobalStatus) };
        Ensure(Aria2Native.GetGlobalStatus(_session, &status), "get global status");
        return new Aria2GlobalStatus(
            status.DownloadSpeed,
            status.UploadSpeed,
            status.ActiveCount,
            status.WaitingCount,
            status.StoppedCount);
    }

    public IReadOnlyDictionary<string, string> GetGlobalOptions()
    {
        EnsureSession();
        var json = GetNativeString(static (session, _, result) => Aria2Native.GetGlobalOptionsJson(session, result), 0);
        return JsonSerializer.Deserialize(json, Aria2JsonContext.Default.DictionaryStringString)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public Aria2CapabilityDetails GetCapabilities()
    {
        EnsureSession();
        var json = GetNativeString(static (session, _, result) => Aria2Native.GetCapabilitiesJson(session, result), 0);
        return JsonSerializer.Deserialize(json, Aria2JsonContext.Default.Aria2CapabilityDetails)
            ?? throw new InvalidDataException("libaria2 returned an empty capability manifest.");
    }

    public IReadOnlyList<Aria2PeerDetails> GetPeers(ulong gid)
    {
        EnsureSession();
        var json = GetNativeString(static (session, value, result) => Aria2Native.GetPeersJson(session, value, result), gid);
        return JsonSerializer.Deserialize(json, Aria2JsonContext.Default.ListAria2PeerDetails) ?? [];
    }

    public IReadOnlyList<Aria2ServerDetails> GetServers(ulong gid)
    {
        EnsureSession();
        var json = GetNativeString(
            static (session, value, result) => Aria2Native.GetServersJson(session, value, result),
            gid);
        return JsonSerializer.Deserialize(json, Aria2JsonContext.Default.ListAria2ServerDetails) ?? [];
    }

    public void Pause(ulong gid, bool force)
    {
        EnsureSession();
        Ensure(Aria2Native.Pause(_session, gid, force ? 1 : 0), "pause download");
    }

    public void Resume(ulong gid)
    {
        EnsureSession();
        Ensure(Aria2Native.Resume(_session, gid), "resume download");
    }

    public void Remove(ulong gid, bool force)
    {
        EnsureSession();
        Ensure(Aria2Native.Remove(_session, gid, force ? 1 : 0), "remove download");
    }

    public void RemoveResult(ulong gid)
    {
        EnsureSession();
        Ensure(Aria2Native.RemoveResult(_session, gid), "remove stopped download result");
    }

    public void ChangeOptions(ulong gid, IReadOnlyDictionary<string, string> options)
    {
        EnsureSession();
        using var nativeOptions = new NativeOptionList(options);
        Ensure(Aria2Native.ChangeOptions(_session, gid, nativeOptions.Pointer, nativeOptions.Count), "change options");
    }

    public void ChangeGlobalOptions(IReadOnlyDictionary<string, string> options)
    {
        EnsureSession();
        var effectiveOptions = ConfigureLogging(options);
        using var nativeOptions = new NativeOptionList(effectiveOptions);
        Ensure(
            Aria2Native.ChangeGlobalOptions(_session, nativeOptions.Pointer, nativeOptions.Count),
            "change global options");
        _appliedLoggingState = Volatile.Read(ref _desiredLoggingState);
    }

    public int ChangePosition(ulong gid, int offset, int mode)
    {
        EnsureSession();
        var position = 0;
        Ensure(Aria2Native.ChangePosition(_session, gid, offset, mode, &position), "change queue position");
        return position;
    }

    public void PrioritizePieceRange(ulong gid, int firstPiece, int lastPiece)
    {
        EnsureSession();
        ArgumentOutOfRangeException.ThrowIfNegative(firstPiece);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastPiece, firstPiece);
        Ensure(
            Aria2Native.PrioritizePieceRange(_session, gid, (nuint)firstPiece, (nuint)lastPiece),
            "prioritize piece range");
    }

    public void ClearPiecePriority(ulong gid)
    {
        EnsureSession();
        Ensure(Aria2Native.ClearPiecePriority(_session, gid), "clear piece priority");
    }

    public void Shutdown(bool force)
    {
        if (_session == 0)
        {
            return;
        }

        Ensure(Aria2Native.Shutdown(_session, force ? 1 : 0), "shut down session");
        while (Aria2Native.RunOnce(_session) > 0)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_session != 0)
        {
            Aria2Native.SessionDestroy(_session);
            _session = 0;
        }

        if (_applicationLog is not null)
        {
            _applicationLog.EnabledChanged -= OnLoggingEnabledChanged;
        }

        _disposed = true;
    }

    private IReadOnlyDictionary<string, string> ConfigureLogging(
        IReadOnlyDictionary<string, string> options)
    {
        if (_applicationLog is null)
        {
            return options;
        }

        var effectiveOptions = new Dictionary<string, string>(options, StringComparer.Ordinal)
        {
            ["log"] = Volatile.Read(ref _desiredLoggingState) == 1
                ? _applicationLog.GetFilePath(LogComponent.Aria2)
                : string.Empty,
        };
        effectiveOptions.TryAdd("log-level", "notice");
        return effectiveOptions;
    }

    private void OnLoggingEnabledChanged(bool enabled) =>
        Volatile.Write(ref _desiredLoggingState, enabled ? 1 : 0);

    private void ApplyPendingLoggingConfiguration()
    {
        var desiredState = Volatile.Read(ref _desiredLoggingState);
        if (_applicationLog is null || desiredState == _appliedLoggingState)
        {
            return;
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["log"] = desiredState == 1
                ? _applicationLog.GetFilePath(LogComponent.Aria2)
                : string.Empty,
        };
        using var nativeOptions = new NativeOptionList(options);
        Ensure(
            Aria2Native.ChangeGlobalOptions(_session, nativeOptions.Pointer, nativeOptions.Count),
            "change logging options");
        _appliedLoggingState = desiredState;
    }

    private delegate int NativeStringFactory(nint session, ulong value, NativeString* result);

    private string GetNativeString(NativeStringFactory factory, ulong value)
    {
        var result = new NativeString();
        Ensure(factory(_session, value, &result), "read JSON snapshot");
        try
        {
            return Marshal.PtrToStringUTF8(result.Data, checked((int)result.Length))
                ?? throw new InvalidDataException("libaria2 returned a null string.");
        }
        finally
        {
            Aria2Native.FreeString(&result);
        }
    }

    private void EnsureSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session == 0)
        {
            throw new InvalidOperationException("The aria2 engine has not been initialized.");
        }
    }

    private static void EnsureLibraryInitialized()
    {
        lock (LibraryLock)
        {
            if (s_libraryInitialized)
            {
                return;
            }

            var result = Aria2Native.LibraryInit();
            if (result != 0)
            {
                throw new Aria2Exception("initialize libaria2", result, Aria2Native.GetLastError(0));
            }

            s_libraryInitialized = true;
        }
    }

    private void Ensure(int result, string operation)
    {
        if (result != 0)
        {
            throw new Aria2Exception(operation, result, Aria2Native.GetLastError(_session));
        }
    }

    private void EnsureNonNegative(int result, string operation)
    {
        if (result < 0)
        {
            throw new Aria2Exception(operation, result, Aria2Native.GetLastError(_session));
        }
    }
}

public sealed class Aria2Exception(string operation, int errorCode, string nativeMessage)
    : Exception($"Could not {operation} ({errorCode}): {nativeMessage}")
{
    public int ErrorCode { get; } = errorCode;
}
