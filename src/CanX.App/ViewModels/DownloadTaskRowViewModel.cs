// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.App.Controls;
using CanX.Core.Downloads;
using System.ComponentModel;

namespace CanX.App.ViewModels;

public sealed class DownloadTaskRowViewModel(DownloadTaskSnapshot snapshot) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadTaskSnapshot Snapshot { get; private set; } = snapshot;

    public Guid Id => Snapshot.Id;

    public string Name => Snapshot.DisplayName;

    public string State => Snapshot.ScheduledStartAt is { } startAt
        ? $"计划 {startAt.ToLocalTime():MM-dd HH:mm}"
        : Snapshot.IsSeeder ? "做种中" : DownloadFormatting.State(Snapshot.State);

    public double Progress => Snapshot.TotalLength <= 0
        ? 0
        : Math.Clamp((double)Snapshot.CompletedLength / Snapshot.TotalLength * 100, 0, 100);

    public string ProgressText => Snapshot.TotalLength <= 0
        ? DownloadFormatting.Bytes(Snapshot.CompletedLength)
        : $"{Progress:0.0}%";

    public bool HasPieceMap => Snapshot.PieceCount > 0 &&
        !string.IsNullOrWhiteSpace(Snapshot.Bitfield);

    public string PieceSummary
    {
        get
        {
            if (!HasPieceMap)
            {
                return $"已下载 {DownloadFormatting.Bytes(Snapshot.CompletedLength)}";
            }

            var completed = DownloadPieceMap.CountCompletedPieces(
                Snapshot.Bitfield,
                Snapshot.PieceCount);
            var progress = (double)completed / Snapshot.PieceCount * 100;
            return $"已完成 {completed:N0}/{Snapshot.PieceCount:N0} 个分片（{progress:0.0}%）";
        }
    }

    public string PieceDetails => Snapshot.PieceCount <= 0
        ? "分片大小未知"
        : $"{Snapshot.PieceCount:N0} 个分片 · 每片 " +
          $"{(Snapshot.PieceLength > 0 ? DownloadFormatting.Bytes(Snapshot.PieceLength) : "大小未知")} · " +
          "从左到右、从上到下";

    public string TooltipStatusSummary => Snapshot.TotalLength > 0
        ? $"{State} · {DownloadFormatting.Bytes(Snapshot.CompletedLength)} / {Size} · {ProgressText}"
        : $"{State} · 已下载 {DownloadFormatting.Bytes(Snapshot.CompletedLength)}";

    public string TooltipTransferSummary =>
        $"下载 {DownloadSpeed} · 上传 {UploadSpeed} · ETA {Eta} · {Connections} 个连接";

    public string TooltipIdentitySummary =>
        $"GID {Snapshot.EngineGid} · {Snapshot.Files.Count} 个文件 · 保存到 {Destination}";

    public string Size => Snapshot.TotalLength <= 0 ? "未知" : DownloadFormatting.Bytes(Snapshot.TotalLength);

    public string DownloadSpeed => DownloadFormatting.Speed(Snapshot.DownloadSpeed);

    public string UploadSpeed => DownloadFormatting.Speed(Snapshot.UploadSpeed);

    public string Connections => Snapshot.Connections.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Eta => Snapshot.EtaSeconds is { } seconds
        ? DownloadFormatting.Duration(TimeSpan.FromSeconds(seconds))
        : "—";

    public string Destination => Snapshot.DestinationDirectory;

    public void Update(DownloadTaskSnapshot value)
    {
        if (!HasVisibleChange(Snapshot, value))
        {
            Snapshot = value;
            return;
        }

        Snapshot = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static bool HasVisibleChange(DownloadTaskSnapshot current, DownloadTaskSnapshot updated) =>
        current.DisplayName != updated.DisplayName ||
        current.State != updated.State ||
        current.TotalLength != updated.TotalLength ||
        current.CompletedLength != updated.CompletedLength ||
        current.DownloadSpeed != updated.DownloadSpeed ||
        current.UploadSpeed != updated.UploadSpeed ||
        current.EtaSeconds != updated.EtaSeconds ||
        current.PieceLength != updated.PieceLength ||
        current.PieceCount != updated.PieceCount ||
        current.Bitfield != updated.Bitfield ||
        current.Connections != updated.Connections ||
        current.IsSeeder != updated.IsSeeder ||
        current.EngineGid != updated.EngineGid ||
        current.DestinationDirectory != updated.DestinationDirectory ||
        current.ScheduledStartAt != updated.ScheduledStartAt ||
        current.Files.Count != updated.Files.Count;
}

internal static class DownloadFormatting
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var size = (double)value;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} {units[unit]}" : $"{size:0.##} {units[unit]}";
    }

    public static string Speed(long bytesPerSecond) => $"{Bytes(bytesPerSecond)}/s";

    public static string Duration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays} 天 {duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    public static string State(DownloadState state) => state switch
    {
        DownloadState.Active => "下载中",
        DownloadState.Waiting => "等待中",
        DownloadState.Paused => "已暂停",
        DownloadState.Complete => "已完成",
        DownloadState.Error => "错误",
        DownloadState.Removed => "已删除",
        _ => state.ToString(),
    };
}
