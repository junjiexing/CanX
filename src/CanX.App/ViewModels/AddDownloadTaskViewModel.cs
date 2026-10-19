// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CanX.Application.Downloads;
using CanX.Core.Downloads;

namespace CanX.App.ViewModels;

public sealed class AddDownloadTaskViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IDownloadManager _manager;
    private DownloadPreparation? _preparation;
    private Guid? _preparationId;
    private CancellationTokenSource? _preparationCancellation;
    private string _sourceText = string.Empty;
    private string _destinationDirectory;
    private string _outputName = string.Empty;
    private bool _isBusy;
    private bool _isCommittingPendingMetadata;
    private bool _isCommitted;
    private bool _isDisposed;

    public AddDownloadTaskViewModel(IDownloadManager manager, string defaultDownloadDirectory)
    {
        _manager = manager;
        _destinationDirectory = defaultDownloadDirectory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DownloadPreparationFileViewModel> Files { get; } = [];

    public string SourceText
    {
        get => _sourceText;
        set
        {
            if (SetField(ref _sourceText, value))
            {
                OnPropertyChanged(nameof(CanCommitPendingMetadata));
                OnPropertyChanged(nameof(CanContinue));
                OnPropertyChanged(nameof(BusyText));
            }
        }
    }

    public string DestinationDirectory
    {
        get => _destinationDirectory;
        set
        {
            if (SetField(ref _destinationDirectory, value))
            {
                OnPropertyChanged(nameof(CanCommitPendingMetadata));
                OnPropertyChanged(nameof(CanContinue));
                OnPropertyChanged(nameof(CanAdd));
            }
        }
    }

    public string OutputName
    {
        get => _outputName;
        set => SetField(ref _outputName, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanContinue));
                OnPropertyChanged(nameof(CanAdd));
                OnPropertyChanged(nameof(CanCommitPendingMetadata));
                OnPropertyChanged(nameof(BusyText));
            }
        }
    }

    public bool IsSourceStep => _preparation is null;

    public bool IsDetailsStep => _preparation is not null;

    public bool CanContinue =>
        !_isCommitted && !_isDisposed &&
        !string.IsNullOrWhiteSpace(SourceText) && (!IsBusy || CanCommitPendingMetadata);

    public bool CanCommitPendingMetadata =>
        IsBusy &&
        !_isCommitted &&
        !_isDisposed &&
        !_isCommittingPendingMetadata &&
        _preparationId is not null &&
        SourceText.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(DestinationDirectory);

    public bool CanAdd => !_isCommitted && !_isDisposed && !IsBusy && _preparation is not null &&
        (!ShowFileList || Files.Any(static file => file.IsSelected)) &&
        !string.IsNullOrWhiteSpace(DestinationDirectory);

    public string BusyText => CanCommitPendingMetadata
        ? "正在获取元数据；再次点击“确定”可立即添加，元数据完成后将自动下载全部文件。"
        : IsBusy ? "正在读取下载信息…" : string.Empty;

    public string DisplayName => _preparation?.DisplayName ?? string.Empty;

    public string SourceType => _preparation switch
    {
        { IsTorrent: true } => "BitTorrent",
        { Kind: DownloadSourceKind.Metalink } => "Metalink",
        { Kind: DownloadSourceKind.Uri } => "URL 下载",
        _ => string.Empty,
    };

    public string TotalSize => FormatBytes(_preparation?.TotalLength ?? 0);

    public bool ShowFileList => _preparation?.IsTorrent == true && Files.Count > 1;

    public bool ShowOutputName => _preparation is { IsTorrent: false, Kind: DownloadSourceKind.Uri };

    public bool AllFilesSelected
    {
        get => Files.Count > 0 && Files.All(static file => file.IsSelected);
        set
        {
            foreach (var file in Files)
            {
                file.IsSelected = value;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAdd));
        }
    }

    public void SetSource(string source) => SourceText = source.Trim();

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (!CanContinue)
        {
            return;
        }

        await CancelCurrentPreparationAsync().ConfigureAwait(true);
        IsBusy = true;
        var preparationId = Guid.NewGuid();
        _preparationId = preparationId;
        var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _preparationCancellation = preparationCancellation;
        OnPropertyChanged(nameof(CanCommitPendingMetadata));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(BusyText));
        try
        {
            var preparation = await _manager.PrepareAsync(
                SourceText,
                preparationCancellation.Token,
                preparationId).ConfigureAwait(true);
            _preparation = preparation;
            _preparationId = preparation.Id;
            SourceText = preparation.Source;
            OutputName = preparation.IsTorrent ? string.Empty : preparation.DisplayName;
            Files.Clear();
            foreach (var file in preparation.Files)
            {
                var row = new DownloadPreparationFileViewModel(file);
                row.PropertyChanged += OnFilePropertyChanged;
                Files.Add(row);
            }

            RaiseStepProperties();
        }
        catch (OperationCanceledException) when (_isCommittingPendingMetadata || _isCommitted)
        {
            // The pending metadata request was promoted to a regular download.
        }
        finally
        {
            if (ReferenceEquals(_preparationCancellation, preparationCancellation))
            {
                _preparationCancellation = null;
            }

            preparationCancellation.Dispose();
            if (_preparation is null && !_isCommittingPendingMetadata && !_isCommitted)
            {
                _preparationId = null;
            }

            IsBusy = false;
        }
    }

    public async Task<bool> CommitPendingMetadataAsync(
        bool allowDuplicate = false,
        CancellationToken cancellationToken = default)
    {
        if (!CanCommitPendingMetadata || _preparationId is not { } preparationId)
        {
            return false;
        }

        _isCommittingPendingMetadata = true;
        OnPropertyChanged(nameof(CanCommitPendingMetadata));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(BusyText));
        try
        {
            await _manager.CommitPreparationAsync(new CommitDownloadPreparationRequest
            {
                PreparationId = preparationId,
                DestinationDirectory = DestinationDirectory,
                AllowDuplicate = allowDuplicate,
            }, cancellationToken).ConfigureAwait(true);
            _isCommitted = true;
            _preparationId = null;
            return true;
        }
        finally
        {
            _isCommittingPendingMetadata = false;
            OnPropertyChanged(nameof(CanCommitPendingMetadata));
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(BusyText));
        }
    }

    public async Task BackAsync()
    {
        await CancelCurrentPreparationAsync().ConfigureAwait(true);
        _preparation = null;
        _preparationId = null;
        Files.Clear();
        RaiseStepProperties();
    }

    public async Task<bool> AddAsync(
        bool allowDuplicate = false,
        CancellationToken cancellationToken = default)
    {
        if (!CanAdd || _preparation is null)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            await _manager.CommitPreparationAsync(new CommitDownloadPreparationRequest
            {
                PreparationId = _preparation.Id,
                DestinationDirectory = DestinationDirectory,
                OutputName = string.IsNullOrWhiteSpace(OutputName) ? null : OutputName.Trim(),
                AllowDuplicate = allowDuplicate,
                Files = Files.Select(static file => new DownloadFilePreference
                {
                    Index = file.Index,
                    IsSelected = file.IsSelected,
                    Priority = file.PriorityValue,
                }).ToList(),
            }, cancellationToken).ConfigureAwait(true);
            _isCommitted = true;
            _preparationId = null;
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _preparationCancellation?.Cancel();
        _preparationCancellation?.Dispose();
        _preparationCancellation = null;
        if (!_isCommitted)
        {
            await CancelCurrentPreparationAsync().ConfigureAwait(false);
        }
    }

    private async Task CancelCurrentPreparationAsync()
    {
        if (_preparation is not null)
        {
            await _manager.CancelPreparationAsync(_preparation.Id).ConfigureAwait(false);
            _preparation = null;
            _preparationId = null;
        }
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(DownloadPreparationFileViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(AllFilesSelected));
            OnPropertyChanged(nameof(CanAdd));
        }
    }

    private void RaiseStepProperties()
    {
        OnPropertyChanged(nameof(IsSourceStep));
        OnPropertyChanged(nameof(IsDetailsStep));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SourceType));
        OnPropertyChanged(nameof(TotalSize));
        OnPropertyChanged(nameof(ShowFileList));
        OnPropertyChanged(nameof(ShowOutputName));
        OnPropertyChanged(nameof(AllFilesSelected));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(CanAdd));
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var amount = (double)value;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} B" : $"{amount:0.##} {units[unit]}";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DownloadPreparationFileViewModel : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _priority = "普通";

    public DownloadPreparationFileViewModel(DownloadPreparationFile file)
    {
        Index = file.Index;
        Path = file.Path;
        Length = file.Length;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    public string Path { get; }

    public long Length { get; }

    public string Size => FormatBytes(Length);

    public IReadOnlyList<string> Priorities { get; } = ["低", "普通", "高"];

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Priority
    {
        get => _priority;
        set
        {
            if (_priority == value)
            {
                return;
            }

            _priority = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority)));
        }
    }

    public DownloadFilePriority PriorityValue => Priority switch
    {
        "低" => DownloadFilePriority.Low,
        "高" => DownloadFilePriority.High,
        _ => DownloadFilePriority.Normal,
    };

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var amount = (double)value;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} B" : $"{amount:0.##} {units[unit]}";
    }
}
