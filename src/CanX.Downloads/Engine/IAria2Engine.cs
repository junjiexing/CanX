// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Downloads.Engine;

public interface IAria2Engine : IDisposable
{
    bool IsInitialized { get; }

    void Initialize(IReadOnlyDictionary<string, string> globalOptions);

    ulong AddUri(string uri, IReadOnlyDictionary<string, string> options, int position);

    ulong AddUris(IReadOnlyList<string> uris, IReadOnlyDictionary<string, string> options, int position);

    ulong AddTorrent(string torrentFile, IReadOnlyDictionary<string, string> options, int position);

    IReadOnlyList<ulong> AddMetalink(string metalinkFile, IReadOnlyDictionary<string, string> options, int position);

    bool RunOnce();

    Aria2DownloadDetails GetDownload(ulong gid);

    Aria2GlobalStatus GetGlobalStatus();

    IReadOnlyDictionary<string, string> GetGlobalOptions();

    Aria2CapabilityDetails GetCapabilities();

    IReadOnlyList<Aria2PeerDetails> GetPeers(ulong gid);

    IReadOnlyList<Aria2ServerDetails> GetServers(ulong gid);

    void Pause(ulong gid, bool force);

    void Resume(ulong gid);

    void Remove(ulong gid, bool force);

    void RemoveResult(ulong gid);

    void ChangeOptions(ulong gid, IReadOnlyDictionary<string, string> options);

    void ChangeGlobalOptions(IReadOnlyDictionary<string, string> options);

    int ChangePosition(ulong gid, int offset, int mode);

    void PrioritizePieceRange(ulong gid, int firstPiece, int lastPiece);

    void ClearPiecePriority(ulong gid);

    void Shutdown(bool force);
}

public sealed record Aria2DownloadDetails
{
    public required string Gid { get; init; }

    public int Status { get; init; }

    public long TotalLength { get; init; }

    public long CompletedLength { get; init; }

    public long UploadedLength { get; init; }

    public int DownloadSpeed { get; init; }

    public int UploadSpeed { get; init; }

    public long PieceLength { get; init; }

    public int NumPieces { get; init; }

    public int Connections { get; init; }

    public bool Seeder { get; init; }

    public int NumSeeders { get; init; }

    public int ErrorCode { get; init; }

    public string Dir { get; init; } = string.Empty;

    public string InfoHash { get; init; } = string.Empty;

    public string Bitfield { get; init; } = string.Empty;

    public List<string> FollowedBy { get; init; } = [];

    public string Following { get; init; } = string.Empty;

    public string BelongsTo { get; init; } = string.Empty;

    public List<Aria2FileDetails> Files { get; init; } = [];

    public Dictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);

    public Aria2TorrentDetails Torrent { get; init; } = new();
}

public sealed record Aria2FileDetails
{
    public int Index { get; init; }

    public string Path { get; init; } = string.Empty;

    public long Length { get; init; }

    public long CompletedLength { get; init; }

    public bool Selected { get; init; }

    public List<Aria2UriDetails> Uris { get; init; } = [];
}

public sealed record Aria2UriDetails
{
    public string Uri { get; init; } = string.Empty;

    public int Status { get; init; }
}

public sealed record Aria2TorrentDetails
{
    public string Name { get; init; } = string.Empty;

    public string Comment { get; init; } = string.Empty;

    public long CreationDate { get; init; }

    public int Mode { get; init; }

    public List<List<string>> AnnounceList { get; init; } = [];
}

public readonly record struct Aria2GlobalStatus(
    int DownloadSpeed,
    int UploadSpeed,
    int ActiveCount,
    int WaitingCount,
    int StoppedCount);

public sealed record Aria2CapabilityDetails
{
    public int AbiVersion { get; init; }

    public bool ExtendedApi { get; init; }

    public List<string> Features { get; init; } = [];

    public List<Aria2OptionDetails> Options { get; init; } = [];
}

public sealed record Aria2OptionDetails
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string DefaultValue { get; init; } = string.Empty;

    public string PossibleValues { get; init; } = string.Empty;

    public string ShortName { get; init; } = string.Empty;

    public int ArgumentType { get; init; }

    public bool Hidden { get; init; }

    public bool Initial { get; init; }

    public bool ChangeActive { get; init; }

    public bool ChangeReserved { get; init; }

    public bool ChangeGlobal { get; init; }

    public bool Cumulative { get; init; }
}

public sealed record Aria2PeerDetails
{
    public string PeerId { get; init; } = string.Empty;

    public string Ip { get; init; } = string.Empty;

    public int Port { get; init; }

    public string Bitfield { get; init; } = string.Empty;

    public int DownloadSpeed { get; init; }

    public int UploadSpeed { get; init; }

    public bool AmChoking { get; init; }

    public bool PeerChoking { get; init; }

    public bool Seeder { get; init; }
}

public sealed record Aria2ServerDetails
{
    public int FileIndex { get; init; }

    public string Uri { get; init; } = string.Empty;

    public string CurrentUri { get; init; } = string.Empty;

    public int DownloadSpeed { get; init; }
}
