// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CanX.Application.Downloads;
using CanX.Core.Downloads;
using CanX.Core.Diagnostics;
using CanX.Core.Events;
using CanX.Streaming;

namespace CanX.App.ViewModels;

public sealed class DownloadCenterViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IDownloadManager _manager;
    private readonly IApplicationLog _applicationLog;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly IDisposable _listSubscription;
    private readonly IDisposable _globalSubscription;
    private readonly IDisposable _completionSubscription;
    private readonly List<DownloadTaskRowViewModel> _allTasks = [];
    private DownloadTaskRowViewModel? _selectedTask;
    private DownloadSourceKind _selectedSourceKind;
    private string _sourceText = string.Empty;
    private string _mirrorUrisText = string.Empty;
    private string _destinationDirectory;
    private string _outputName = string.Empty;
    private string _newTaskOptionsText = string.Empty;
    private string _taskOptionsText = string.Empty;
    private string _globalOptionsText = string.Empty;
    private string _fileSelectionText = string.Empty;
    private string _optionSearchText = string.Empty;
    private string _selectedOptionValue = string.Empty;
    private string _scheduledStartText = string.Empty;
    private string _selectedScheduledStartText = string.Empty;
    private string _statusMessage = "正在启动下载引擎…";
    private string _globalSummary = "↓ 0 B/s  ↑ 0 B/s";
    private string _capabilitySummary = "正在读取 libaria2 能力…";
    private bool _startPaused;
    private bool _notifyOnCompletion;
    private bool _selectedNotifyOnCompletion;
    private bool _isReady;
    private bool _isDisposed;
    private DownloadOptionDefinition? _selectedOptionDefinition;
    private DownloadOptionDefinition? _selectedGlobalOptionDefinition;
    private string _selectedGlobalOptionValue = string.Empty;
    private DownloadCompletionAction _completionAction;
    private DownloadCompletionAction _selectedCompletionAction;
    private DownloadTaskFilter _taskFilter = DownloadTaskFilter.Downloading;
    private List<DownloadOptionDefinition> _allOptionDefinitions = [];
    private readonly HashSet<string> _pendingGlobalCommonOptions = new(StringComparer.Ordinal);
    private Dictionary<string, string> _synchronizedGlobalOptions = new(StringComparer.Ordinal);
    private bool _isSynchronizingGlobalOptions;
    private bool _globalOptionsTextDirty;
    private bool _selectedGlobalOptionDirty;
    private readonly Lock _taskListInitializationGate = new();
    private bool _taskListInitialized;

    public DownloadCenterViewModel(
        IDownloadManager manager,
        IEventBus eventBus,
        string defaultDownloadDirectory,
        SynchronizationContext? synchronizationContext = null,
        IApplicationLog? applicationLog = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _applicationLog = applicationLog ?? NullApplicationLog.Instance;
        ArgumentNullException.ThrowIfNull(eventBus);
        _destinationDirectory = Path.GetFullPath(defaultDownloadDirectory);
        _synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;

        PauseCommand = new AsyncCommand(PauseAsync, CanPause);
        ResumeCommand = new AsyncCommand(ResumeAsync, CanResume);
        MoveUpCommand = new AsyncCommand(() => MoveAsync(-1), () => IsReady && SelectedTask is not null);
        MoveDownCommand = new AsyncCommand(() => MoveAsync(1), () => IsReady && SelectedTask is not null);
        ApplyTaskOptionsCommand = new AsyncCommand(ApplyTaskOptionsAsync, () => IsReady && SelectedTask is not null);
        ApplyGlobalOptionsCommand = new AsyncCommand(ApplyGlobalOptionsAsync, () => IsReady);
        RefreshConnectionsCommand = new AsyncCommand(RefreshConnectionsAsync, CanRefreshConnections);
        ApplyFileSelectionCommand = new AsyncCommand(ApplyFileSelectionAsync, CanApplyFileSelection);
        StageNewTaskOptionCommand = new AsyncCommand(StageNewTaskOptionAsync, CanStageNewTaskOption);
        ApplySelectedTaskOptionCommand = new AsyncCommand(ApplySelectedTaskOptionAsync, CanApplySelectedTaskOption);
        ApplySelectedGlobalOptionCommand = new AsyncCommand(ApplySelectedGlobalOptionAsync, CanApplySelectedGlobalOption);
        StageCommonNewTaskOptionsCommand = new AsyncCommand(StageCommonNewTaskOptionsAsync, CanStageCommonNewTaskOptions);
        ApplyCommonTaskOptionsCommand = new AsyncCommand(ApplyCommonTaskOptionsAsync, CanApplyCommonTaskOptions);
        ApplyCommonGlobalOptionsCommand = new AsyncCommand(ApplyCommonGlobalOptionsAsync, CanApplyCommonGlobalOptions);
        ApplyCompletionPolicyCommand = new AsyncCommand(
            ApplyCompletionPolicyAsync,
            () => IsReady && SelectedTask is not null);
        ApplyScheduleCommand = new AsyncCommand(ApplyScheduleAsync, () => IsReady && SelectedTask is not null);

        _listSubscription = eventBus.Subscribe<DownloadListChanged>(OnDownloadListChanged);
        _globalSubscription = eventBus.Subscribe<DownloadGlobalStatusChanged>(message =>
            Post(() => ApplyGlobalStatus(message.Snapshot)));
        _completionSubscription = eventBus.Subscribe<DownloadCompletionHandled>(message =>
            Post(() => StatusMessage = message.ErrorMessage is null
                ? $"下载完成处理完成：通知 {(message.NotificationSent ? "已发送" : "未发送")}，" +
                    $"动作 {(message.ActionExecuted ? "已执行" : "未执行")}"
                : $"下载完成处理失败：{message.ErrorMessage}"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DownloadTaskRowViewModel> Tasks { get; } = [];

    public ObservableCollection<DownloadOptionDefinition> OptionDefinitions { get; } = [];

    public ObservableCollection<DownloadOptionDefinition> GlobalOptionDefinitions { get; } = [];

    public ObservableCollection<DownloadOptionDefinition> TaskOptionDefinitions { get; } = [];

    public ObservableCollection<DownloadPeerSnapshot> Peers { get; } = [];

    public ObservableCollection<DownloadServerSnapshot> Servers { get; } = [];

    public ObservableCollection<DownloadCommonOptionViewModel> CommonOptions { get; } = [];

    public ObservableCollection<DownloadCommonOptionViewModel> TaskCommonOptions { get; } = [];

    public ObservableCollection<DownloadCommonOptionViewModel> GlobalCommonOptions { get; } = [];

    public Array SourceKinds { get; } = Enum.GetValues<DownloadSourceKind>();

    public Array CompletionActions { get; } = Enum.GetValues<DownloadCompletionAction>();

    public AddDownloadTaskViewModel CreateAddTaskViewModel() => new(_manager, DestinationDirectory);

    public bool ShowDownloadingTasks
    {
        get => _taskFilter == DownloadTaskFilter.Downloading;
        set
        {
            if (value)
            {
                SetTaskFilter(DownloadTaskFilter.Downloading);
            }
        }
    }

    public bool ShowCompletedTasks
    {
        get => _taskFilter == DownloadTaskFilter.Completed;
        set
        {
            if (value)
            {
                SetTaskFilter(DownloadTaskFilter.Completed);
            }
        }
    }

    public DownloadTaskRowViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetField(ref _selectedTask, value))
            {
                return;
            }

            TaskOptionsText = value is null ? string.Empty : DownloadOptionText.Format(value.Snapshot.Options);
            UpdateSelectedOptionValue();
            OnPropertyChanged(nameof(SelectedDetails));
            OnPropertyChanged(nameof(SelectedDiagnosticSummary));
            OnPropertyChanged(nameof(SelectedErrorSummary));
            OnPropertyChanged(nameof(SelectedFiles));
            OnPropertyChanged(nameof(SelectedPlayableFile));
            OnPropertyChanged(nameof(CanPlaySelectedTask));
            OnPropertyChanged(nameof(TrackerSummary));
            FileSelectionText = value is null
                ? string.Empty
                : string.Join(',', value.Snapshot.Files.Where(static file => file.IsSelected)
                    .Select(static file => file.Index));
            SelectedNotifyOnCompletion = value?.Snapshot.NotifyOnCompletion ?? false;
            SelectedCompletionAction = value?.Snapshot.CompletionAction ?? DownloadCompletionAction.None;
            SelectedScheduledStartText = value?.Snapshot.ScheduledStartAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                ?? string.Empty;
            Peers.Clear();
            Servers.Clear();
            OnPropertyChanged(nameof(CanRemoveSelectedTask));
            RaiseCommandStates();
        }
    }

    public DownloadSourceKind SelectedSourceKind
    {
        get => _selectedSourceKind;
        set
        {
            if (SetField(ref _selectedSourceKind, value))
            {
                OnPropertyChanged(nameof(SourceHint));
            }
        }
    }

    public string SourceHint => SelectedSourceKind == DownloadSourceKind.Uri
        ? "每行一个 HTTP/HTTPS/FTP/SFTP 地址"
        : SelectedSourceKind == DownloadSourceKind.Torrent
            ? "选择一个 .torrent 文件"
            : "选择一个 .meta4/.metalink 文件";

    public string SourceText
    {
        get => _sourceText;
        set
        {
            if (SetField(ref _sourceText, value))
            {
                OnPropertyChanged(nameof(CanAddTask));
            }
        }
    }

    public string DestinationDirectory
    {
        get => _destinationDirectory;
        set => SetField(ref _destinationDirectory, value);
    }

    public string MirrorUrisText
    {
        get => _mirrorUrisText;
        set => SetField(ref _mirrorUrisText, value);
    }

    public string OutputName
    {
        get => _outputName;
        set => SetField(ref _outputName, value);
    }

    public string NewTaskOptionsText
    {
        get => _newTaskOptionsText;
        set => SetField(ref _newTaskOptionsText, value);
    }

    public string TaskOptionsText
    {
        get => _taskOptionsText;
        set => SetField(ref _taskOptionsText, value);
    }

    public string GlobalOptionsText
    {
        get => _globalOptionsText;
        set
        {
            if (SetField(ref _globalOptionsText, value) && !_isSynchronizingGlobalOptions)
            {
                _globalOptionsTextDirty = true;
            }
        }
    }

    public string FileSelectionText
    {
        get => _fileSelectionText;
        set => SetField(ref _fileSelectionText, value);
    }

    public string OptionSearchText
    {
        get => _optionSearchText;
        set
        {
            if (SetField(ref _optionSearchText, value))
            {
                RefreshOptionDefinitions();
            }
        }
    }

    public DownloadOptionDefinition? SelectedOptionDefinition
    {
        get => _selectedOptionDefinition;
        set
        {
            if (SetField(ref _selectedOptionDefinition, value))
            {
                UpdateSelectedOptionValue();
                OnPropertyChanged(nameof(SelectedOptionDetails));
                RaiseCommandStates();
            }
        }
    }

    public string SelectedOptionValue
    {
        get => _selectedOptionValue;
        set => SetField(ref _selectedOptionValue, value);
    }

    public string SelectedOptionDetails
    {
        get => FormatOptionDetails(SelectedOptionDefinition);
    }

    public DownloadOptionDefinition? SelectedGlobalOptionDefinition
    {
        get => _selectedGlobalOptionDefinition;
        set
        {
            if (SetField(ref _selectedGlobalOptionDefinition, value))
            {
                UpdateSelectedGlobalOptionValue();
                OnPropertyChanged(nameof(SelectedGlobalOptionDetails));
                RaiseCommandStates();
            }
        }
    }

    public string SelectedGlobalOptionValue
    {
        get => _selectedGlobalOptionValue;
        set
        {
            if (SetField(ref _selectedGlobalOptionValue, value) && !_isSynchronizingGlobalOptions)
            {
                _selectedGlobalOptionDirty = true;
            }
        }
    }

    public string SelectedGlobalOptionDetails => FormatOptionDetails(SelectedGlobalOptionDefinition);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string GlobalSummary
    {
        get => _globalSummary;
        private set => SetField(ref _globalSummary, value);
    }

    public string CapabilitySummary
    {
        get => _capabilitySummary;
        private set => SetField(ref _capabilitySummary, value);
    }

    public IReadOnlyList<DownloadFileSnapshot> SelectedFiles => SelectedTask?.Snapshot.Files ?? [];

    public DownloadFileSnapshot? SelectedPlayableFile => SelectedTask?.Snapshot.Files
        .Where(static file => file.IsSelected && file.Length > 0 && IsPlayableMediaFile(file.Path))
        .OrderByDescending(static file => file.Length)
        .FirstOrDefault();

    public bool CanPlaySelectedTask => IsReady &&
        SelectedTask?.Snapshot is { State: not DownloadState.Error and not DownloadState.Removed } snapshot &&
        SelectedPlayableFile is not null &&
        (snapshot.State == DownloadState.Complete ||
         (_manager.Capabilities.ExtendedApi &&
          _manager.Capabilities.Features.Contains("runtime-piece-priority", StringComparer.Ordinal)));

    public ValueTask<IMediaByteStream> OpenMediaStreamAsync(
        Guid taskId,
        int fileIndex,
        CancellationToken cancellationToken = default) =>
        _manager.OpenMediaStreamAsync(taskId, fileIndex, cancellationToken);

    public ValueTask ResumeTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        _manager.ResumeAsync(taskId, cancellationToken);

    public string TrackerSummary => SelectedTask?.Snapshot.Torrent is { } torrent
        ? string.Join(Environment.NewLine, torrent.AnnounceList.SelectMany(static tier => tier))
        : "此任务没有 BitTorrent tracker。";

    public string SelectedDetails
    {
        get
        {
            var task = SelectedTask?.Snapshot;
            if (task is null)
            {
                return "选择任务后可查看 GID、保存目录、错误码及 BitTorrent 信息。";
            }

            var torrent = task.Torrent is null ? string.Empty : $" · InfoHash {task.InfoHash}";
            return $"GID {task.EngineGid} · {task.Files.Count} 个文件 · {task.SeederCount} 个种子 · " +
                $"错误码 {task.ErrorCode}{torrent}";
        }
    }

    public string SelectedDiagnosticSummary
    {
        get
        {
            var task = SelectedTask?.Snapshot;
            if (task is null)
            {
                return "选择任务后可查看 ETA、分片和 aria2 原始状态。";
            }

            var eta = task.EtaSeconds is { } seconds
                ? DownloadFormatting.Duration(TimeSpan.FromSeconds(seconds))
                : "不可计算";
            return $"原始状态 {task.EngineStatus} · ETA {eta} · 分片 {task.PieceCount} × " +
                $"{DownloadFormatting.Bytes(task.PieceLength)} · 连接 {task.Connections}";
        }
    }

    public string SelectedErrorSummary
    {
        get
        {
            var task = SelectedTask?.Snapshot;
            if (task is null || task.ErrorCode <= 0)
            {
                return "当前没有错误。";
            }

            return $"错误 {task.ErrorCode}：{task.ErrorMessage}";
        }
    }

    public bool StartPaused
    {
        get => _startPaused;
        set => SetField(ref _startPaused, value);
    }

    public bool NotifyOnCompletion
    {
        get => _notifyOnCompletion;
        set => SetField(ref _notifyOnCompletion, value);
    }

    public DownloadCompletionAction CompletionAction
    {
        get => _completionAction;
        set => SetField(ref _completionAction, value);
    }

    public bool SelectedNotifyOnCompletion
    {
        get => _selectedNotifyOnCompletion;
        set => SetField(ref _selectedNotifyOnCompletion, value);
    }

    public DownloadCompletionAction SelectedCompletionAction
    {
        get => _selectedCompletionAction;
        set => SetField(ref _selectedCompletionAction, value);
    }

    public string ScheduledStartText
    {
        get => _scheduledStartText;
        set => SetField(ref _scheduledStartText, value);
    }

    public string SelectedScheduledStartText
    {
        get => _selectedScheduledStartText;
        set => SetField(ref _selectedScheduledStartText, value);
    }

    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (SetField(ref _isReady, value))
            {
                OnPropertyChanged(nameof(CanAddTask));
                OnPropertyChanged(nameof(CanRemoveSelectedTask));
                OnPropertyChanged(nameof(CanPlaySelectedTask));
                RaiseCommandStates();
            }
        }
    }

    public bool CanRemoveSelectedTask => IsReady && SelectedTask is not null;

    public bool CanAddTask => IsReady && !string.IsNullOrWhiteSpace(SourceText);

    public AsyncCommand PauseCommand { get; }

    public AsyncCommand ResumeCommand { get; }

    public AsyncCommand MoveUpCommand { get; }

    public AsyncCommand MoveDownCommand { get; }

    public AsyncCommand ApplyTaskOptionsCommand { get; }

    public AsyncCommand ApplyGlobalOptionsCommand { get; }

    public AsyncCommand RefreshConnectionsCommand { get; }

    public AsyncCommand ApplyFileSelectionCommand { get; }

    public AsyncCommand StageNewTaskOptionCommand { get; }

    public AsyncCommand ApplySelectedTaskOptionCommand { get; }

    public AsyncCommand ApplySelectedGlobalOptionCommand { get; }

    public AsyncCommand StageCommonNewTaskOptionsCommand { get; }

    public AsyncCommand ApplyCommonTaskOptionsCommand { get; }

    public AsyncCommand ApplyCommonGlobalOptionsCommand { get; }

    public AsyncCommand ApplyCompletionPolicyCommand { get; }

    public AsyncCommand ApplyScheduleCommand { get; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _manager.StartAsync(cancellationToken).ConfigureAwait(true);
            DownloadTaskSnapshot[] initialTasks;
            lock (_taskListInitializationGate)
            {
                // Capture the manager snapshot and enable subsequent events as one
                // operation. Without this gate an aria2 update can be published in
                // between ApplyTasks and the old event flag, leaving the UI one
                // snapshot behind until another task-list change happens.
                initialTasks = [.. _manager.Tasks];
                _taskListInitialized = true;
            }
            ApplyTasks(initialTasks);
            _allOptionDefinitions = _manager.Capabilities.Options.Where(static option => !option.Hidden).ToList();
            RefreshOptionDefinitions();
            InitializeCommonOptions();
            ApplyGlobalStatus(_manager.GlobalStatus);
            SynchronizeGlobalOptionEditors(_manager.GlobalStatus.Options, force: true);
            var coverage = DownloadOptionCoverage.Analyze(_manager.Capabilities.Options);
            CapabilitySummary = _manager.Capabilities.ExtendedApi
                ? $"ABI {_manager.Capabilities.AbiVersion} · 覆盖 {coverage.Covered}/{coverage.Total} · " +
                    $"隐藏 {coverage.Hidden} · 只读 {coverage.ReadOnly} · " +
                    string.Join(" / ", _manager.Capabilities.Features)
                : $"ABI {_manager.Capabilities.AbiVersion} · 当前动态库未启用 CanX 扩展 API";
            IsReady = true;
            StatusMessage = "下载引擎已就绪";
        }
        catch (Exception exception)
        {
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                "The download engine failed to start.",
                exception);
            IsReady = false;
            StatusMessage = $"下载引擎启动失败：{exception.Message}";
        }
    }

    public void SynchronizeTaskList()
    {
        if (!IsReady || _isDisposed)
        {
            return;
        }

        DownloadTaskSnapshot[] snapshots;
        lock (_taskListInitializationGate)
        {
            snapshots = [.. _manager.Tasks];
        }

        Post(() => ApplyTasks(snapshots));
    }

    public async ValueTask<bool> SavePendingGlobalOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (!_globalOptionsTextDirty &&
            _pendingGlobalCommonOptions.Count == 0 &&
            !_selectedGlobalOptionDirty)
        {
            return false;
        }

        if (!IsReady)
        {
            throw new InvalidOperationException("下载引擎尚未就绪，无法保存 aria2 设置。");
        }

        var options = _globalOptionsTextDirty
            ? DownloadOptionText.Parse(GlobalOptionsText)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in GlobalCommonOptions.Where(option =>
                     _pendingGlobalCommonOptions.Contains(option.Name) &&
                     !string.IsNullOrWhiteSpace(option.Value)))
        {
            options[option.Name] = option.ToAria2Value();
        }

        if (_selectedGlobalOptionDirty && SelectedGlobalOptionDefinition is { } selectedOption)
        {
            options[selectedOption.Name] = SelectedGlobalOptionValue;
        }

        if (options.Count == 0)
        {
            _pendingGlobalCommonOptions.Clear();
            _globalOptionsTextDirty = false;
            _selectedGlobalOptionDirty = false;
            return false;
        }

        await _manager.ChangeGlobalOptionsAsync(options, cancellationToken).ConfigureAwait(true);
        _pendingGlobalCommonOptions.ExceptWith(options.Keys);
        _globalOptionsTextDirty = false;
        _selectedGlobalOptionDirty = false;
        SetGlobalOptionsText(UpsertOptions(GlobalOptionsText, options));
        StatusMessage = FormatGlobalOptionsAppliedMessage(options, "aria2 设置已保存");
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _listSubscription.Dispose();
        _globalSubscription.Dispose();
        _completionSubscription.Dispose();
        await _manager.DisposeAsync().ConfigureAwait(false);
    }

    public void ImportSources(IEnumerable<string> sources)
    {
        var values = sources
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0)
        {
            StatusMessage = "剪贴板或拖放内容中没有可用地址。";
            return;
        }

        if (values.Length == 1 && Path.IsPathRooted(values[0]))
        {
            var extension = Path.GetExtension(values[0]);
            SelectedSourceKind = extension.Equals(".torrent", StringComparison.OrdinalIgnoreCase)
                ? DownloadSourceKind.Torrent
                : extension.Equals(".meta4", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".metalink", StringComparison.OrdinalIgnoreCase)
                    ? DownloadSourceKind.Metalink
                    : DownloadSourceKind.Uri;
        }
        else
        {
            SelectedSourceKind = DownloadSourceKind.Uri;
        }

        SourceText = string.Join(Environment.NewLine, values);
        StatusMessage = $"已导入 {values.Length} 个下载源";
    }

    public async Task<bool> AddSelectedSourcesAsync()
    {
        if (!CanAddTask)
        {
            return false;
        }

        var succeeded = false;
        await ExecuteAsync(async () =>
        {
            var sources = SourceText.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (SelectedSourceKind != DownloadSourceKind.Uri && sources.Length != 1)
            {
                throw new InvalidOperationException("Torrent 或 Metalink 一次只能选择一个文件。");
            }

            var options = DownloadOptionText.Parse(NewTaskOptionsText);
            var mirrors = ParseLines(MirrorUrisText);
            if (sources.Length > 1 && mirrors.Count > 0)
            {
                throw new InvalidOperationException("批量任务不能共用镜像列表；请逐个添加多镜像任务。");
            }

            foreach (var source in sources)
            {
                await _manager.AddAsync(new AddDownloadRequest
                {
                    Kind = SelectedSourceKind,
                    Source = source,
                    DestinationDirectory = DestinationDirectory,
                    OutputName = sources.Length == 1 && !string.IsNullOrWhiteSpace(OutputName) ? OutputName.Trim() : null,
                    StartPaused = StartPaused,
                    MirrorUris = mirrors,
                    Options = new Dictionary<string, string>(options, StringComparer.Ordinal),
                    NotifyOnCompletion = NotifyOnCompletion,
                    CompletionAction = CompletionAction,
                    ScheduledStartAt = ParseScheduledStart(ScheduledStartText),
                }).ConfigureAwait(true);
            }

            SourceText = string.Empty;
            MirrorUrisText = string.Empty;
            OutputName = string.Empty;
            NewTaskOptionsText = string.Empty;
            ScheduledStartText = string.Empty;
            StatusMessage = $"已添加 {sources.Length} 个下载源";
            succeeded = true;
        }).ConfigureAwait(true);
        return succeeded;
    }

    private Task PauseAsync() => ExecuteSelectedAsync(
        task => _manager.PauseAsync(task.Id, force: true).AsTask(),
        "任务已暂停");

    private Task ResumeAsync() => ExecuteSelectedAsync(
        task => _manager.ResumeAsync(task.Id).AsTask(),
        "任务已继续");

    public Task RemoveSelectedAsync(bool deleteFiles) => ExecuteSelectedAsync(
        task => _manager.RemoveAsync(task.Id, deleteFiles: deleteFiles).AsTask(),
        deleteFiles ? "任务及本地文件已删除" : "任务已删除");

    private Task MoveAsync(int offset) => ExecuteSelectedAsync(
        async task => _ = await _manager.MoveAsync(task.Id, offset, DownloadPositionMode.Current).ConfigureAwait(true),
        offset < 0 ? "任务已上移" : "任务已下移");

    private Task ApplyTaskOptionsAsync() => ExecuteSelectedAsync(
        task => _manager.ChangeOptionsAsync(task.Id, DownloadOptionText.Parse(TaskOptionsText)).AsTask(),
        "任务选项已应用");

    private Task ApplyGlobalOptionsAsync() => ExecuteAsync(async () =>
    {
        var options = DownloadOptionText.Parse(GlobalOptionsText);
        await _manager.ChangeGlobalOptionsAsync(options).ConfigureAwait(true);
        _globalOptionsTextDirty = false;
        StatusMessage = FormatGlobalOptionsAppliedMessage(options);
    });

    private Task RefreshConnectionsAsync() => ExecuteAsync(async () =>
    {
        var task = SelectedTask?.Snapshot;
        if (task is null)
        {
            return;
        }

        var details = await _manager.GetLiveDetailsAsync(task.Id).ConfigureAwait(true);
        Peers.Clear();
        foreach (var peer in details.Peers)
        {
            Peers.Add(peer);
        }

        Servers.Clear();
        foreach (var server in details.Servers)
        {
            Servers.Add(server);
        }

        StatusMessage = $"已刷新连接：{Peers.Count} 个 peer，{Servers.Count} 个服务器";
    });

    private Task ApplyFileSelectionAsync() => ExecuteSelectedAsync(
        task => _manager.ChangeOptionsAsync(
            task.Id,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["select-file"] = FileSelectionText.Trim() }).AsTask(),
        "文件选择已应用");

    private Task StageNewTaskOptionAsync()
    {
        var option = SelectedOptionDefinition;
        if (option is not null)
        {
            NewTaskOptionsText = UpsertOption(NewTaskOptionsText, option.Name, SelectedOptionValue);
            StatusMessage = $"已将 {option.Name} 写入新任务选项";
        }

        return Task.CompletedTask;
    }

    private Task ApplySelectedTaskOptionAsync()
    {
        var option = SelectedOptionDefinition;
        return option is null
            ? Task.CompletedTask
            : ExecuteSelectedAsync(
                task => _manager.ChangeOptionsAsync(
                    task.Id,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [option.Name] = SelectedOptionValue,
                    }).AsTask(),
                $"任务选项 {option.Name} 已应用");
    }

    private Task ApplySelectedGlobalOptionAsync()
    {
        var option = SelectedGlobalOptionDefinition;
        return option is null
            ? Task.CompletedTask
            : ExecuteAsync(async () =>
            {
                await _manager.ChangeGlobalOptionsAsync(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [option.Name] = SelectedGlobalOptionValue,
                    }).ConfigureAwait(true);
                _selectedGlobalOptionDirty = false;
                SetGlobalOptionsText(UpsertOption(GlobalOptionsText, option.Name, SelectedGlobalOptionValue));
                StatusMessage = DownloadOptionPolicy.RequiresEngineRestart(option.Name)
                    ? $"全局选项 {option.Name} 已保存，将在重启后生效"
                    : $"全局选项 {option.Name} 已应用";
            });
    }

    private Task StageCommonNewTaskOptionsAsync() => ExecuteAsync(() =>
    {
        var options = ReadCommonOptions(CommonOptions, static option => option.Definition.Initial);
        NewTaskOptionsText = UpsertOptions(NewTaskOptionsText, options);
        StatusMessage = $"已将 {options.Count} 个常用设置写入新任务选项";
        return Task.CompletedTask;
    });

    private Task ApplyCommonTaskOptionsAsync() => ExecuteAsync(async () =>
    {
        var task = SelectedTask?.Snapshot ?? throw new InvalidOperationException("请先选择下载任务。");
        var state = task.State;
        var options = ReadCommonOptions(CommonOptions, option =>
            state == DownloadState.Active && option.Definition.ChangeActive ||
            state is DownloadState.Waiting or DownloadState.Paused && option.Definition.ChangeReserved);
        await _manager.ChangeOptionsAsync(task.Id, options).ConfigureAwait(true);
        StatusMessage = $"已应用 {options.Count} 个常用任务设置";
    });

    private Task ApplyCommonGlobalOptionsAsync() => ExecuteAsync(async () =>
    {
        var options = ReadCommonOptions(GlobalCommonOptions, static _ => true);
        await _manager.ChangeGlobalOptionsAsync(options).ConfigureAwait(true);
        _pendingGlobalCommonOptions.ExceptWith(options.Keys);
        SetGlobalOptionsText(UpsertOptions(GlobalOptionsText, options));
        StatusMessage = FormatGlobalOptionsAppliedMessage(options, $"已应用 {options.Count} 个常用全局设置");
    });

    private Task ApplyCompletionPolicyAsync() => ExecuteSelectedAsync(
        task => _manager.ChangeCompletionPolicyAsync(
            task.Id,
            SelectedNotifyOnCompletion,
            SelectedCompletionAction).AsTask(),
        "完成通知与动作已更新");

    private Task ApplyScheduleAsync() => ExecuteSelectedAsync(
        task => _manager.ScheduleAsync(task.Id, ParseScheduledStart(SelectedScheduledStartText)).AsTask(),
        string.IsNullOrWhiteSpace(SelectedScheduledStartText) ? "计划时间已清除" : "计划时间已更新");

    private async Task ExecuteSelectedAsync(
        Func<DownloadTaskSnapshot, Task> action,
        string successMessage)
    {
        var snapshot = SelectedTask?.Snapshot;
        if (snapshot is null)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            await action(snapshot).ConfigureAwait(true);
            StatusMessage = successMessage;
        }).ConfigureAwait(true);
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _applicationLog.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                "A download operation failed.",
                exception);
            StatusMessage = exception.Message;
        }
    }

    private bool CanPause() => IsReady && SelectedTask?.Snapshot.State is DownloadState.Active or DownloadState.Waiting;

    private bool CanResume() => IsReady && SelectedTask?.Snapshot.State is DownloadState.Paused or DownloadState.Error;

    private bool CanRefreshConnections() =>
        IsReady && _manager.Capabilities.ExtendedApi && SelectedTask?.Snapshot.State == DownloadState.Active;

    private bool CanApplyFileSelection() =>
        IsReady && SelectedTask?.Snapshot.State is DownloadState.Waiting or DownloadState.Paused;

    private bool CanStageNewTaskOption() => IsReady && SelectedOptionDefinition?.Initial == true;

    private bool CanApplySelectedTaskOption()
    {
        var option = SelectedOptionDefinition;
        var state = SelectedTask?.Snapshot.State;
        return IsReady && option is not null && state is not null &&
            (state == DownloadState.Active && option.ChangeActive ||
             state is DownloadState.Waiting or DownloadState.Paused && option.ChangeReserved);
    }

    private bool CanApplySelectedGlobalOption() =>
        IsReady && SelectedGlobalOptionDefinition is { } option && IsGlobalOption(option);

    private bool CanStageCommonNewTaskOptions() =>
        IsReady && CommonOptions.Any(static option => option.Definition.Initial && !string.IsNullOrWhiteSpace(option.Value));

    private bool CanApplyCommonTaskOptions()
    {
        var state = SelectedTask?.Snapshot.State;
        return IsReady && state is not null && CommonOptions.Any(option =>
            !string.IsNullOrWhiteSpace(option.Value) &&
            (state == DownloadState.Active && option.Definition.ChangeActive ||
             state is DownloadState.Waiting or DownloadState.Paused && option.Definition.ChangeReserved));
    }

    private bool CanApplyCommonGlobalOptions() =>
        IsReady && GlobalCommonOptions.Any(static option => !string.IsNullOrWhiteSpace(option.Value));

    private static bool IsGlobalOption(DownloadOptionDefinition option) =>
        option.ChangeGlobal || DownloadOptionPolicy.RequiresEngineRestart(option.Name);

    private static string FormatGlobalOptionsAppliedMessage(
        IReadOnlyDictionary<string, string> options,
        string successMessage = "全局选项已应用") =>
        options.Keys.Any(DownloadOptionPolicy.RequiresEngineRestart)
            ? $"{successMessage}；DHT 设置将在重启后生效"
            : successMessage;

    private static List<string> ParseLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static DateTimeOffset? ParseScheduledStart(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.CurrentCulture, out var startAt))
        {
            throw new FormatException("计划时间格式无效，请输入带日期和时间的本地时间。");
        }

        if (startAt <= DateTimeOffset.Now)
        {
            throw new ArgumentOutOfRangeException(nameof(text), "计划时间必须晚于当前时间。");
        }

        return startAt.ToUniversalTime();
    }

    private static string UpsertOption(string text, string name, string value)
    {
        var options = DownloadOptionText.Parse(text);
        options[name] = value;
        return DownloadOptionText.Format(options);
    }

    private static string UpsertOptions(string text, IReadOnlyDictionary<string, string> values)
    {
        var options = DownloadOptionText.Parse(text);
        foreach (var value in values)
        {
            options[value.Key] = value.Value;
        }

        return DownloadOptionText.Format(options);
    }

    private static Dictionary<string, string> ReadCommonOptions(
        IEnumerable<DownloadCommonOptionViewModel> source,
        Func<DownloadCommonOptionViewModel, bool> predicate)
    {
        var options = source
            .Where(option => predicate(option) && !string.IsNullOrWhiteSpace(option.Value))
            .ToDictionary(static option => option.Name, static option => option.ToAria2Value(), StringComparer.Ordinal);
        if (options.Count == 0)
        {
            throw new InvalidOperationException("没有可用于当前作用域的常用设置。");
        }

        return options;
    }

    private void InitializeCommonOptions()
    {
        CommonOptions.Clear();
        TaskCommonOptions.Clear();
        GlobalCommonOptions.Clear();
        foreach (var item in CommonOptionSpecs)
        {
            var definition = _manager.Capabilities.Options.FirstOrDefault(option => option.Name == item.Name);
            if (definition is null || definition.Hidden)
            {
                continue;
            }

            var taskOption = new DownloadCommonOptionViewModel(item.Label, item.Placeholder, definition);
            taskOption.PropertyChanged += OnCommonOptionPropertyChanged;
            CommonOptions.Add(taskOption);
            if (definition.Initial || definition.ChangeActive || definition.ChangeReserved)
            {
                TaskCommonOptions.Add(taskOption);
            }

            if (IsGlobalOption(definition))
            {
                var globalOption = new DownloadCommonOptionViewModel(item.Label, item.Placeholder, definition);
                globalOption.PropertyChanged += OnCommonOptionPropertyChanged;
                GlobalCommonOptions.Add(globalOption);
            }
        }
    }

    private void OnCommonOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadCommonOptionViewModel.Value))
        {
            if (!_isSynchronizingGlobalOptions &&
                sender is DownloadCommonOptionViewModel option &&
                GlobalCommonOptions.Contains(option))
            {
                _pendingGlobalCommonOptions.Add(option.Name);
            }

            RaiseCommandStates();
        }
    }

    private void RefreshOptionDefinitions()
    {
        var selectedName = SelectedOptionDefinition?.Name;
        var selectedGlobalName = SelectedGlobalOptionDefinition?.Name;
        var query = OptionSearchText.Trim();
        OptionDefinitions.Clear();
        GlobalOptionDefinitions.Clear();
        TaskOptionDefinitions.Clear();
        foreach (var option in _allOptionDefinitions.Where(option =>
                     query.Length == 0 ||
                     option.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     option.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     option.PossibleValues.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            OptionDefinitions.Add(option);
            if (IsGlobalOption(option))
            {
                GlobalOptionDefinitions.Add(option);
            }

            if (option.ChangeActive || option.ChangeReserved)
            {
                TaskOptionDefinitions.Add(option);
            }
        }

        SelectedOptionDefinition = OptionDefinitions.FirstOrDefault(option => option.Name == selectedName);
        SelectedGlobalOptionDefinition = GlobalOptionDefinitions.FirstOrDefault(option => option.Name == selectedGlobalName);
    }

    private void UpdateSelectedOptionValue()
    {
        var option = SelectedOptionDefinition;
        if (option is null)
        {
            SelectedOptionValue = string.Empty;
            return;
        }

        if (SelectedTask?.Snapshot.Options.TryGetValue(option.Name, out var taskValue) == true)
        {
            SelectedOptionValue = taskValue;
        }
        else if (_manager.GlobalStatus.Options.TryGetValue(option.Name, out var globalValue))
        {
            SelectedOptionValue = globalValue;
        }
        else
        {
            SelectedOptionValue = option.DefaultValue;
        }
    }

    private void ApplyTasks(IReadOnlyList<DownloadTaskSnapshot> snapshots)
    {
        var selectedId = SelectedTask?.Id;
        var existing = _allTasks.ToDictionary(static task => task.Id);
        var desiredIds = snapshots.Select(static snapshot => snapshot.Id).ToHashSet();
        for (var index = _allTasks.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(_allTasks[index].Id))
            {
                _allTasks.RemoveAt(index);
            }
        }

        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (existing.TryGetValue(snapshot.Id, out var item))
            {
                item.Update(snapshot);
            }
            else
            {
                item = new DownloadTaskRowViewModel(snapshot);
            }

            var currentIndex = _allTasks.IndexOf(item);
            if (currentIndex < 0)
            {
                _allTasks.Insert(index, item);
            }
            else if (currentIndex != index)
            {
                _allTasks.RemoveAt(currentIndex);
                _allTasks.Insert(index, item);
            }
        }

        RefreshVisibleTasks(selectedId);

        OnPropertyChanged(nameof(SelectedDetails));
        OnPropertyChanged(nameof(SelectedDiagnosticSummary));
        OnPropertyChanged(nameof(SelectedErrorSummary));
        OnPropertyChanged(nameof(SelectedFiles));
        OnPropertyChanged(nameof(SelectedPlayableFile));
        OnPropertyChanged(nameof(CanPlaySelectedTask));
        OnPropertyChanged(nameof(TrackerSummary));
        RaiseCommandStates();
    }

    private static bool IsPlayableMediaFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".3gp" or ".aac" or ".ac3" or ".aiff" or ".ape" or ".asf" or ".avi" or
            ".flac" or ".flv" or ".m2ts" or ".m4a" or ".m4v" or ".mka" or ".mkv" or
            ".mov" or ".mp2" or ".mp3" or ".mp4" or ".mpeg" or ".mpg" or ".mts" or
            ".oga" or ".ogg" or ".ogm" or ".ogv" or ".opus" or ".ts" or ".wav" or
            ".webm" or ".wma" or ".wmv";

    private void SetTaskFilter(DownloadTaskFilter filter)
    {
        if (_taskFilter == filter)
        {
            return;
        }

        _taskFilter = filter;
        OnPropertyChanged(nameof(ShowDownloadingTasks));
        OnPropertyChanged(nameof(ShowCompletedTasks));
        RefreshVisibleTasks();
    }

    private void RefreshVisibleTasks(Guid? selectedId = null)
    {
        selectedId ??= SelectedTask?.Id;
        var visibleTasks = _allTasks.Where(IsTaskVisible).ToArray();
        var visibleIds = visibleTasks.Select(static task => task.Id).ToHashSet();
        for (var index = Tasks.Count - 1; index >= 0; index--)
        {
            if (!visibleIds.Contains(Tasks[index].Id))
            {
                Tasks.RemoveAt(index);
            }
        }

        for (var index = 0; index < visibleTasks.Length; index++)
        {
            var item = visibleTasks[index];
            var currentIndex = Tasks.IndexOf(item);
            if (currentIndex < 0)
            {
                Tasks.Insert(index, item);
            }
            else if (currentIndex != index)
            {
                Tasks.Move(currentIndex, index);
            }
        }

        SelectedTask = selectedId is null ? null : Tasks.FirstOrDefault(task => task.Id == selectedId);
    }

    private bool IsTaskVisible(DownloadTaskRowViewModel task) => _taskFilter switch
    {
        DownloadTaskFilter.Downloading => task.Snapshot.State is not DownloadState.Complete and not DownloadState.Removed,
        DownloadTaskFilter.Completed => task.Snapshot.State == DownloadState.Complete,
        _ => false,
    };

    private void ApplyGlobalStatus(DownloadGlobalSnapshot snapshot)
    {
        GlobalSummary =
            $"↓ {DownloadFormatting.Speed(snapshot.DownloadSpeed)}  ↑ {DownloadFormatting.Speed(snapshot.UploadSpeed)}" +
            $"  · 活动 {snapshot.ActiveCount}  等待 {snapshot.WaitingCount}  已停止 {snapshot.StoppedCount}";

        if (!GlobalOptionsEqual(snapshot.Options, _synchronizedGlobalOptions))
        {
            SynchronizeGlobalOptionEditors(snapshot.Options);
        }
    }

    private void UpdateSelectedGlobalOptionValue(IReadOnlyDictionary<string, string>? options = null)
    {
        var option = SelectedGlobalOptionDefinition;
        var wasSynchronizing = _isSynchronizingGlobalOptions;
        _isSynchronizingGlobalOptions = true;
        try
        {
            if (option is null)
            {
                SelectedGlobalOptionValue = string.Empty;
            }
            else if ((options ?? _manager.GlobalStatus.Options).TryGetValue(option.Name, out var globalValue))
            {
                SelectedGlobalOptionValue = globalValue;
            }
            else
            {
                SelectedGlobalOptionValue = option.DefaultValue;
            }
        }
        finally
        {
            _isSynchronizingGlobalOptions = wasSynchronizing;
        }

        _selectedGlobalOptionDirty = false;
    }

    private static string FormatOptionDetails(DownloadOptionDefinition? option)
    {
        if (option is null)
        {
            return "选择一个 option 查看说明、默认值和可修改范围。";
        }

        var scopes = new List<string>();
        if (option.Initial) scopes.Add("新建");
        if (option.ChangeActive) scopes.Add("活动任务");
        if (option.ChangeReserved) scopes.Add("等待任务");
        if (option.ChangeGlobal) scopes.Add("全局");
        if (DownloadOptionPolicy.RequiresEngineRestart(option.Name)) scopes.Add("全局（重启后生效）");
        if (scopes.Count == 0) scopes.Add("只读");
        return $"{option.Description}\n默认值：{option.DefaultValue}\n作用域：{string.Join(" / ", scopes)}";
    }

    private void SynchronizeGlobalOptionEditors(
        IReadOnlyDictionary<string, string> options,
        bool force = false)
    {
        if (!force && GlobalOptionsEqual(options, _synchronizedGlobalOptions))
        {
            return;
        }

        _isSynchronizingGlobalOptions = true;
        try
        {
            if (force || !_globalOptionsTextDirty)
            {
                GlobalOptionsText = DownloadOptionText.Format(options);
            }

            foreach (var option in GlobalCommonOptions)
            {
                if (force || !_pendingGlobalCommonOptions.Contains(option.Name))
                {
                    option.SetFromAria2Value(options.TryGetValue(option.Name, out var value) ? value : string.Empty);
                }
            }

            if (force || !_selectedGlobalOptionDirty)
            {
                UpdateSelectedGlobalOptionValue(options);
            }
        }
        finally
        {
            _isSynchronizingGlobalOptions = false;
        }

        if (force)
        {
            _globalOptionsTextDirty = false;
            _selectedGlobalOptionDirty = false;
            _pendingGlobalCommonOptions.Clear();
        }

        _synchronizedGlobalOptions = new Dictionary<string, string>(options, StringComparer.Ordinal);
        RaiseCommandStates();
    }

    private void SetGlobalOptionsText(string value)
    {
        var wasSynchronizing = _isSynchronizingGlobalOptions;
        _isSynchronizingGlobalOptions = true;
        try
        {
            GlobalOptionsText = value;
        }
        finally
        {
            _isSynchronizingGlobalOptions = wasSynchronizing;
        }
    }

    private static bool GlobalOptionsEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(option =>
            right.TryGetValue(option.Key, out var value) &&
            string.Equals(option.Value, value, StringComparison.Ordinal));

    private void Post(Action action)
    {
        if (_synchronizationContext is null || SynchronizationContext.Current == _synchronizationContext)
        {
            action();
            return;
        }

        _synchronizationContext.Post(static state => ((Action)state!).Invoke(), action);
    }

    private void OnDownloadListChanged(DownloadListChanged message)
    {
        DownloadTaskSnapshot[] snapshots;
        lock (_taskListInitializationGate)
        {
            // DownloadManager publishes its restored list before StartAsync
            // returns. The initial authoritative read above absorbs those events,
            // while this shared gate guarantees that an event racing that read is
            // observed after initialization instead of being dropped.
            if (!_taskListInitialized)
            {
                return;
            }

            snapshots = [.. message.Snapshots];
        }

        Post(() => ApplyTasks(snapshots));
    }

    private void RaiseCommandStates()
    {
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        ApplyTaskOptionsCommand.RaiseCanExecuteChanged();
        ApplyGlobalOptionsCommand.RaiseCanExecuteChanged();
        RefreshConnectionsCommand.RaiseCanExecuteChanged();
        ApplyFileSelectionCommand.RaiseCanExecuteChanged();
        StageNewTaskOptionCommand.RaiseCanExecuteChanged();
        ApplySelectedTaskOptionCommand.RaiseCanExecuteChanged();
        ApplySelectedGlobalOptionCommand.RaiseCanExecuteChanged();
        StageCommonNewTaskOptionsCommand.RaiseCanExecuteChanged();
        ApplyCommonTaskOptionsCommand.RaiseCanExecuteChanged();
        ApplyCommonGlobalOptionsCommand.RaiseCanExecuteChanged();
        ApplyCompletionPolicyCommand.RaiseCanExecuteChanged();
        ApplyScheduleCommand.RaiseCanExecuteChanged();
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

    private static readonly (string Name, string Label, string Placeholder)[] CommonOptionSpecs =
    [
        ("max-download-limit", "任务下载限速", "例如 4096；0 表示不限速"),
        ("max-upload-limit", "任务上传限速", "例如 1024；0 表示不限速"),
        ("max-overall-download-limit", "全局下载限速", "例如 20480；0 表示不限速"),
        ("max-overall-upload-limit", "全局上传限速", "例如 4096；0 表示不限速"),
        ("max-concurrent-downloads", "并发任务数", "例如 5"),
        ("max-connection-per-server", "单服务器连接数", "1–16"),
        ("split", "分片数", "例如 8"),
        ("min-split-size", "最小分片大小", "例如 20480"),
        ("checksum", "文件校验", "例如 sha-256=十六进制摘要"),
        ("all-proxy", "代理地址", "例如 http://127.0.0.1:7890，不含凭证"),
        ("seed-ratio", "做种分享率", "例如 1.0；0 表示不限"),
        ("seed-time", "做种时长（分钟）", "例如 60；0 表示不限"),
        ("enable-dht", "启用 IPv4 DHT（重启后生效）", "true / false"),
        ("enable-dht6", "启用 IPv6 DHT（重启后生效）", "true / false"),
        ("dht-listen-port", "DHT 监听端口（重启后生效）", "例如 6881-6999"),
        ("bt-tracker", "附加 Tracker", "每行一个 Tracker，也可以使用英文逗号分隔"),
        ("bt-exclude-tracker", "排除 Tracker", "每行一个 Tracker，也可以使用英文逗号分隔，或使用 *"),
        ("bt-tracker-connect-timeout", "Tracker 连接超时（秒）", "1–600"),
        ("bt-tracker-timeout", "Tracker 响应超时（秒）", "1–600"),
        ("bt-tracker-interval", "Tracker 请求间隔（秒）", "0 表示使用 Tracker 返回的间隔"),
        ("bt-max-peers", "单个 Torrent 最大 Peer 数", "例如 55；0 表示不限"),
        ("enable-peer-exchange", "启用 Peer Exchange", "true / false"),
        ("bt-enable-lpd", "启用本地节点发现", "true / false"),
        ("bt-save-metadata", "保存 Magnet 元数据", "true / false"),
        ("bt-metadata-only", "仅获取 Magnet 元数据", "true / false"),
        ("follow-torrent", "获取元数据后继续下载", "true / mem / false"),
    ];

    private enum DownloadTaskFilter
    {
        Downloading,
        Completed,
    }
}
