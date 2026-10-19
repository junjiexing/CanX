// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Security;

namespace CanX.Core.Downloads;

public enum DownloadSourceKind
{
    Uri,
    Torrent,
    Metalink,
}

public enum DownloadState
{
    Active,
    Waiting,
    Paused,
    Complete,
    Error,
    Removed,
}

public enum DownloadPositionMode
{
    Set,
    Current,
    End,
}

public enum DownloadCompletionAction
{
    None,
    OpenFile,
    OpenDirectory,
}

public enum DownloadFilePriority
{
    Low,
    Normal,
    High,
}

public sealed record DownloadPreparation
{
    public required Guid Id { get; init; }

    public required DownloadSourceKind Kind { get; init; }

    public required string Source { get; init; }

    public required string DisplayName { get; init; }

    public long TotalLength { get; init; }

    public bool IsTorrent { get; init; }

    public string InfoHash { get; init; } = string.Empty;

    public List<DownloadPreparationFile> Files { get; init; } = [];
}

public sealed record DownloadPreparationFile
{
    public int Index { get; init; }

    public required string Path { get; init; }

    public long Length { get; init; }
}

public sealed record DownloadFilePreference
{
    public int Index { get; init; }

    public bool IsSelected { get; init; } = true;

    public DownloadFilePriority Priority { get; init; } = DownloadFilePriority.Normal;
}

public sealed record CommitDownloadPreparationRequest
{
    public required Guid PreparationId { get; init; }

    public required string DestinationDirectory { get; init; }

    public string? OutputName { get; init; }

    public bool StartPaused { get; init; }

    public bool AllowDuplicate { get; init; }

    public List<DownloadFilePreference> Files { get; init; } = [];

    public Dictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);
}

public sealed record AddDownloadRequest
{
    public DownloadSourceKind Kind { get; init; } = DownloadSourceKind.Uri;

    public required string Source { get; init; }

    public List<string> MirrorUris { get; init; } = [];

    public required string DestinationDirectory { get; init; }

    public string? OutputName { get; init; }

    public bool StartPaused { get; init; }

    public bool AllowDuplicate { get; init; }

    public int Position { get; init; } = -1;

    public Dictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);

    public DownloadCredentialReferences CredentialReferences { get; init; } = new();

    public bool NotifyOnCompletion { get; init; }

    public DownloadCompletionAction CompletionAction { get; init; }

    public DateTimeOffset? ScheduledStartAt { get; init; }
}

public sealed class DuplicateDownloadException : InvalidOperationException
{
    public DuplicateDownloadException(DownloadTaskSnapshot existingTask)
        : base(CreateMessage(existingTask))
    {
        ExistingTaskId = existingTask.Id;
        ExistingDisplayName = existingTask.DisplayName;
        ExistingState = existingTask.State;
    }

    public Guid ExistingTaskId { get; }

    public string ExistingDisplayName { get; }

    public DownloadState ExistingState { get; }

    private static string CreateMessage(DownloadTaskSnapshot existingTask)
    {
        ArgumentNullException.ThrowIfNull(existingTask);
        return $"下载任务“{existingTask.DisplayName}”已经存在。";
    }
}

public sealed record DownloadCredentialReferences
{
    public CredentialRef? Source { get; init; }

    public Dictionary<int, CredentialRef> Mirrors { get; init; } = [];

    public Dictionary<string, CredentialRef> Options { get; init; } = new(StringComparer.Ordinal);
}

public sealed record DownloadUriSnapshot
{
    public required string Uri { get; init; }

    public int EngineStatus { get; init; }

    public bool IsUsed { get; init; }
}

public sealed record DownloadFileSnapshot
{
    public int Index { get; init; }

    public required string Path { get; init; }

    public long Length { get; init; }

    public long CompletedLength { get; init; }

    public bool IsSelected { get; init; }

    public double ProgressPercent => Length <= 0
        ? 0
        : Math.Clamp((double)CompletedLength / Length * 100, 0, 100);

    public List<DownloadUriSnapshot> Uris { get; init; } = [];
}

public sealed record TorrentSnapshot
{
    public string Name { get; init; } = string.Empty;

    public string Comment { get; init; } = string.Empty;

    public DateTimeOffset? CreationDate { get; init; }

    public string Mode { get; init; } = "None";

    public List<List<string>> AnnounceList { get; init; } = [];
}

public sealed record DownloadTaskSnapshot
{
    public required Guid Id { get; init; }

    public required string EngineGid { get; init; }

    public required DownloadSourceKind Kind { get; init; }

    public required string Source { get; init; }

    public List<string> MirrorUris { get; init; } = [];

    public required string DestinationDirectory { get; init; }

    public string? OutputName { get; init; }

    public required string DisplayName { get; init; }

    public DownloadState State { get; init; }

    public int EngineStatus { get; init; }

    public long TotalLength { get; init; }

    public long CompletedLength { get; init; }

    public long UploadedLength { get; init; }

    public long DownloadSpeed { get; init; }

    public long UploadSpeed { get; init; }

    public long? EtaSeconds { get; init; }

    public long PieceLength { get; init; }

    public int PieceCount { get; init; }

    public int Connections { get; init; }

    public bool IsSeeder { get; init; }

    public int SeederCount { get; init; }

    public int ErrorCode { get; init; }

    public string ErrorMessage { get; init; } = string.Empty;

    public string InfoHash { get; init; } = string.Empty;

    public string Bitfield { get; init; } = string.Empty;

    public List<string> FollowedBy { get; init; } = [];

    public string Following { get; init; } = string.Empty;

    public string BelongsTo { get; init; } = string.Empty;

    public int QueuePosition { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public Dictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);

    public List<DownloadFileSnapshot> Files { get; init; } = [];

    public TorrentSnapshot? Torrent { get; init; }

    public bool NotifyOnCompletion { get; init; }

    public DownloadCompletionAction CompletionAction { get; init; }

    public DateTimeOffset? ScheduledStartAt { get; init; }
}

public sealed record DownloadGlobalSnapshot
{
    public long DownloadSpeed { get; init; }

    public long UploadSpeed { get; init; }

    public int ActiveCount { get; init; }

    public int WaitingCount { get; init; }

    public int StoppedCount { get; init; }

    public Dictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);
}

public sealed record DownloadOptionDefinition
{
    public required string Name { get; init; }

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

public sealed record DownloadCapabilityManifest
{
    public int AbiVersion { get; init; }

    public bool ExtendedApi { get; init; }

    public List<string> Features { get; init; } = [];

    public List<DownloadOptionDefinition> Options { get; init; } = [];
}

public sealed record DownloadPeerSnapshot
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

public sealed record DownloadServerSnapshot
{
    public int FileIndex { get; init; }

    public string Uri { get; init; } = string.Empty;

    public string CurrentUri { get; init; } = string.Empty;

    public int DownloadSpeed { get; init; }
}

public sealed record DownloadLiveDetails
{
    public List<DownloadPeerSnapshot> Peers { get; init; } = [];

    public List<DownloadServerSnapshot> Servers { get; init; } = [];
}
