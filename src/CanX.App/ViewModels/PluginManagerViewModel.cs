// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CanX.Core.Diagnostics;
using CanX.Plugins.Lifecycle;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;
using CanX.Plugins.Services;
using CanX.Plugins.Ui;

namespace CanX.App.ViewModels;

public sealed class PluginManagerViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly PluginLifecycleManager _manager;
    private readonly PluginHotReloadService _hotReload;
    private readonly string _developmentModePath;
    private readonly PluginServiceRegistry? _serviceRegistry;
    private readonly Lock _serviceSnapshotGate = new();
    private CancellationTokenSource? _serviceSnapshotDebounce;
    private PluginItemViewModel? _selectedPlugin;
    private string _statusMessage = string.Empty;
    private string _permissionChanges = string.Empty;
    private string _consoleText = string.Empty;
    private bool _isDeveloperMode;
    private bool _initialized;
    private bool _disposed;

    public PluginManagerViewModel(
        PluginLifecycleManager manager,
        string developmentModePath,
        string logDirectory,
        IApplicationLog? log = null,
        PluginUiRegistry? uiRegistry = null,
        PluginServiceRegistry? serviceRegistry = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _hotReload = new PluginHotReloadService(manager, log);
        _hotReload.Reloaded += OnPluginReloaded;
        _developmentModePath = Path.GetFullPath(developmentModePath);
        LogDirectory = Path.GetFullPath(logDirectory);
        UiRegistry = uiRegistry ?? new PluginUiRegistry();
        _serviceRegistry = serviceRegistry;
        if (_serviceRegistry is not null)
        {
            _serviceRegistry.Changed += OnServiceRegistryChanged;
        }
        EnableCommand = new AsyncCommand(EnableSelectedAsync, () => SelectedPlugin is { CanEnable: true });
        DisableCommand = new AsyncCommand(DisableSelectedAsync, () => SelectedPlugin is { IsEnabled: true });
        RollbackCommand = new AsyncCommand(RollbackSelectedAsync, () => SelectedPlugin is { CanRollback: true });
        ReloadCommand = new AsyncCommand(ReloadSelectedAsync, () => SelectedPlugin is { IsEnabled: true });
        RefreshConsoleCommand = new AsyncCommand(RefreshConsoleAsync);
        UninstallCommand = new AsyncCommand(UninstallSelectedAsync, () => SelectedPlugin is not null);
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    public AsyncCommand EnableCommand { get; }

    public AsyncCommand DisableCommand { get; }

    public AsyncCommand RollbackCommand { get; }

    public AsyncCommand ReloadCommand { get; }

    public AsyncCommand RefreshConsoleCommand { get; }

    public AsyncCommand UninstallCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public string LogDirectory { get; }

    public PluginUiRegistry UiRegistry { get; }

    public PluginItemViewModel? SelectedPlugin
    {
        get => _selectedPlugin;
        set
        {
            if (SetField(ref _selectedPlugin, value))
            {
                OnPropertyChanged(nameof(SelectedDetails));
                OnPropertyChanged(nameof(SelectedError));
                OnPropertyChanged(nameof(SelectedRollbackDetails));
                OnPropertyChanged(nameof(CanConfigureSelected));
                OnPropertyChanged(nameof(SelectedConfigurationTitle));
                OnPropertyChanged(nameof(HasSelectedPlugin));
                RaiseCommandStates();
            }
        }
    }

    public bool HasPlugins => Plugins.Count > 0;

    public bool HasSelectedPlugin => SelectedPlugin is not null;

    public bool IsDeveloperMode
    {
        get => _isDeveloperMode;
        set
        {
            if (SetField(ref _isDeveloperMode, value))
            {
                SaveDevelopmentMode();
                UpdateHotReloadState();
                StatusMessage = value
                    ? "开发模式已启用；可安装 .zip 开发包，修改已安装插件后会自动热重载。"
                    : "开发模式已关闭。";
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public string PermissionChanges
    {
        get => _permissionChanges;
        private set
        {
            if (SetField(ref _permissionChanges, value))
            {
                OnPropertyChanged(nameof(HasPermissionChanges));
            }
        }
    }

    public bool HasPermissionChanges => !string.IsNullOrWhiteSpace(PermissionChanges);

    public string ConsoleText
    {
        get => _consoleText;
        private set => SetField(ref _consoleText, value);
    }

    public string SelectedDetails => SelectedPlugin?.PermissionSummary ?? "选择一个插件以查看权限。";

    public string SelectedError => string.IsNullOrWhiteSpace(SelectedPlugin?.LastError)
        ? "无运行错误。"
        : SelectedPlugin.LastError!;

    public string SelectedRollbackDetails => SelectedPlugin?.RollbackVersion is { Length: > 0 } version
        ? $"可回滚到 {version}；当前版本会保留为新的回滚点。"
        : "没有可用的上一版本。";

    public bool CanConfigureSelected => SelectedPlugin?.CanConfigure == true;

    public string SelectedConfigurationTitle => SelectedPlugin?.ConfigurationTitle ?? "插件配置";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _isDeveloperMode = File.Exists(_developmentModePath) &&
                string.Equals(File.ReadAllText(_developmentModePath).Trim(), "enabled", StringComparison.Ordinal);
            OnPropertyChanged(nameof(IsDeveloperMode));
            await _manager.InitializeAsync(cancellationToken).ConfigureAwait(true);
            _initialized = true;
            UpdateHotReloadState();
            RefreshItems();
            await RefreshConsoleAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusMessage = $"插件系统初始化失败：{exception.Message}";
        }
    }

    public async Task InstallAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        try
        {
            var previous = _manager.Plugins.ToDictionary(status => status.Manifest.Id, StringComparer.Ordinal);
            await using var package = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var installed = await _manager.InstallAsync(package, allowUpgrade: true, cancellationToken)
                .ConfigureAwait(true);
            PermissionChanges = previous.TryGetValue(installed.Manifest.Id, out var oldStatus)
                ? FormatPermissionDiff(PluginPermissionDiff.Compare(oldStatus.Manifest, installed.Manifest))
                : FormatInitialPermissions(installed.Manifest);
            RefreshItems(installed.Manifest.Id);
            StatusMessage = installed.RequiresPermissionApproval
                ? $"已安装 {installed.Manifest.Name} {installed.Manifest.Version}；请检查权限后启用。"
                : $"已更新 {installed.Manifest.Name} {installed.Manifest.Version}。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"插件安装失败：{exception.Message}";
        }
    }

    public async Task EnableSelectedAsync()
    {
        if (SelectedPlugin is null)
        {
            return;
        }
        try
        {
            var status = await _manager.ApproveAndEnableAsync(SelectedPlugin.Id).ConfigureAwait(true);
            RefreshItems(status.Manifest.Id);
            StatusMessage = status.RuntimeState == CanX.Plugins.Runtime.PluginHostState.Running
                ? $"已启用 {status.Manifest.Name}。"
                : $"{status.Manifest.Name} 启动失败：{status.LastError}";
        }
        catch (Exception exception)
        {
            StatusMessage = $"插件启用失败：{exception.Message}";
        }
    }

    public async Task DisableSelectedAsync()
    {
        if (SelectedPlugin is null)
        {
            return;
        }
        try
        {
            var status = await _manager.DisableAsync(SelectedPlugin.Id).ConfigureAwait(true);
            RefreshItems(status.Manifest.Id);
            StatusMessage = $"已停用 {status.Manifest.Name}。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"插件停用失败：{exception.Message}";
        }
    }

    public async Task UninstallSelectedAsync()
    {
        if (SelectedPlugin is null)
        {
            return;
        }
        var plugin = SelectedPlugin;
        try
        {
            await _manager.UninstallAsync(plugin.Id).ConfigureAwait(true);
            RefreshItems();
            PermissionChanges = string.Empty;
            StatusMessage = $"已卸载 {plugin.Name}；插件数据和系统凭证未自动删除。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"插件卸载失败：{exception.Message}";
        }
    }

    public async Task RollbackSelectedAsync()
    {
        if (SelectedPlugin is not { CanRollback: true } plugin)
        {
            return;
        }
        try
        {
            var current = _manager.Plugins.Single(status => status.Manifest.Id == plugin.Id);
            var status = await _manager.RollbackAsync(plugin.Id).ConfigureAwait(true);
            PermissionChanges = FormatPermissionDiff(
                PluginPermissionDiff.Compare(current.Manifest, status.Manifest));
            RefreshItems(status.Manifest.Id);
            StatusMessage = status.RequiresPermissionApproval
                ? $"已回滚到 {status.Manifest.Name} {status.Manifest.Version}；请重新检查权限后启用。"
                : $"已回滚到 {status.Manifest.Name} {status.Manifest.Version}。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"插件回滚失败：{exception.Message}";
        }
    }

    public Task RefreshAsync()
    {
        RefreshItems(SelectedPlugin?.Id);
        StatusMessage = "插件状态已刷新。";
        return Task.CompletedTask;
    }

    public async Task ReloadSelectedAsync()
    {
        if (SelectedPlugin is not { IsEnabled: true } plugin)
        {
            return;
        }
        try
        {
            var status = await _manager.ReloadAsync(plugin.Id).ConfigureAwait(true);
            RefreshItems(status.Manifest.Id);
            StatusMessage = status.RuntimeState == CanX.Plugins.Runtime.PluginHostState.Running
                ? $"已重载 {status.Manifest.Name}。"
                : $"{status.Manifest.Name} 重载失败：{status.LastError}";
        }
        catch (Exception exception)
        {
            StatusMessage = $"插件重载失败：{exception.Message}";
        }
        await RefreshConsoleAsync().ConfigureAwait(true);
    }

    public async Task RefreshConsoleAsync()
    {
        try
        {
            ConsoleText = await Task.Run(() => PluginDiagnosticConsole.Read(LogDirectory)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ConsoleText = $"无法读取插件日志：{exception.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? pendingSnapshot;
        lock (_serviceSnapshotGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            pendingSnapshot = _serviceSnapshotDebounce;
            _serviceSnapshotDebounce = null;
        }
        if (_serviceRegistry is not null)
        {
            _serviceRegistry.Changed -= OnServiceRegistryChanged;
        }
        pendingSnapshot?.Cancel();
        _hotReload.Reloaded -= OnPluginReloaded;
        _hotReload.Dispose();
        foreach (var plugin in Plugins)
        {
            plugin.Dispose();
        }
        Plugins.Clear();
        await _manager.DisposeAsync().ConfigureAwait(false);
    }

    private void RefreshItems(string? selectedId = null)
    {
        selectedId ??= SelectedPlugin?.Id;
        foreach (var plugin in Plugins)
        {
            plugin.Dispose();
        }
        Plugins.Clear();
        foreach (var status in _manager.Plugins)
        {
            Plugins.Add(new PluginItemViewModel(status));
        }
        SelectedPlugin = Plugins.FirstOrDefault(plugin => plugin.Id == selectedId) ?? Plugins.FirstOrDefault();
        OnPropertyChanged(nameof(HasPlugins));
        _serviceRegistry?.WriteSchemaSnapshot("plugins-refreshed");
    }

    private void SaveDevelopmentMode()
    {
        var directory = Path.GetDirectoryName(_developmentModePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_developmentModePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, IsDeveloperMode ? "enabled" : "disabled");
            File.Move(temporaryPath, _developmentModePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private void UpdateHotReloadState()
    {
        if (!_initialized || _disposed)
        {
            return;
        }
        if (IsDeveloperMode)
        {
            _hotReload.Start();
        }
        else
        {
            _hotReload.Stop();
        }
    }

    private void OnPluginReloaded(string pluginId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }
            RefreshItems(pluginId);
            var plugin = Plugins.FirstOrDefault(item => item.Id == pluginId);
            StatusMessage = plugin is { LastError.Length: > 0 }
                ? $"插件 {plugin.Name} 热重载失败：{plugin.LastError}"
                : $"插件 {plugin?.Name ?? pluginId} 已热重载。";
            _ = RefreshConsoleAsync();
        });
    }

    private void OnServiceRegistryChanged(object? sender, EventArgs eventArgs)
    {
        CancellationTokenSource current;
        CancellationTokenSource? previous;
        lock (_serviceSnapshotGate)
        {
            if (_disposed)
            {
                return;
            }
            current = new CancellationTokenSource();
            previous = _serviceSnapshotDebounce;
            _serviceSnapshotDebounce = current;
        }
        previous?.Cancel();
        _ = WriteDebouncedServiceSchemaSnapshotAsync(current);
    }

    private async Task WriteDebouncedServiceSchemaSnapshotAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(250, source.Token).ConfigureAwait(false);
            _serviceRegistry?.WriteSchemaSnapshot("registry-stable");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_serviceSnapshotGate)
            {
                if (ReferenceEquals(_serviceSnapshotDebounce, source))
                {
                    _serviceSnapshotDebounce = null;
                }
            }
            source.Dispose();
        }
    }

    private static string FormatInitialPermissions(PluginManifest manifest) => manifest.Permissions.Count == 0
        ? "该插件未申请宿主权限。"
        : "新增权限：\n" + string.Join('\n', manifest.Permissions.Select(FormatPermission));

    private static string FormatPermissionDiff(PluginPermissionDiff diff)
    {
        if (!diff.HasChanges)
        {
            return "本次升级没有权限变化。";
        }

        var lines = new List<string>();
        lines.AddRange(diff.Added.Select(change => $"+ {FormatPermission(change.Capability, change.AddedScopes)}"));
        lines.AddRange(diff.Removed.Select(change => $"- {FormatPermission(change.Capability, change.RemovedScopes)}"));
        lines.AddRange(diff.Changed.Select(change =>
            $"~ {change.Capability}：新增 [{string.Join(", ", change.AddedScopes)}]；移除 [{string.Join(", ", change.RemovedScopes)}]"));
        return string.Join('\n', lines);
    }

    private static string FormatPermission(PluginPermission permission) =>
        FormatPermission(permission.Capability, permission.Scopes);

    private static string FormatPermission(string capability, IReadOnlyList<string> scopes) =>
        scopes.Count == 0 ? capability : $"{capability} ({string.Join(", ", scopes)})";

    private void RaiseCommandStates()
    {
        EnableCommand.RaiseCanExecuteChanged();
        DisableCommand.RaiseCanExecuteChanged();
        RollbackCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
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

public sealed class PluginItemViewModel : IDisposable
{
    private Bitmap? assetIcon;

    public PluginItemViewModel(PluginStatus status)
    {
        Id = status.Manifest.Id;
        Name = status.Manifest.Name;
        Description = string.IsNullOrWhiteSpace(status.Manifest.Description)
            ? "该插件没有提供详细描述。"
            : status.Manifest.Description;
        Version = status.Manifest.Version;
        IsEnabled = status.IsEnabled;
        RequiresApproval = status.RequiresPermissionApproval;
        RuntimeState = status.RuntimeState.ToString();
        LastError = status.LastError;
        RollbackVersion = status.RollbackVersion;
        ConfigurationTitle = status.Manifest.Configuration?.Title;
        var icon = status.Manifest.Ui?.Icon ?? new PluginUiIcon(PluginUiIconKind.BuiltIn, "tools");
        if (icon.Kind == PluginUiIconKind.Asset)
        {
            assetIcon = LoadAssetIcon(status.DirectoryPath, icon.Value);
        }
        IconGlyph = BuiltInIconGlyph(icon);
        SignatureStatus = status.Signature is null
            ? "未签名"
            : status.Signature.IsTrusted ? "签名可信" : "自签名 / 未信任";
        PermissionSummary = status.Manifest.Permissions.Count == 0
            ? "无宿主权限"
            : string.Join('\n', status.Manifest.Permissions.Select(permission =>
                permission.Scopes.Count == 0
                    ? permission.Capability
                    : $"{permission.Capability}: {string.Join(", ", permission.Scopes)}"));
        Status = status.RequiresPermissionApproval
            ? "等待权限批准"
            : status.IsEnabled
                ? status.RuntimeState == CanX.Plugins.Runtime.PluginHostState.Running ? "运行中" : $"已启用 / {status.RuntimeState}"
                : "已停用";
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public bool HasAssetIcon => assetIcon is not null;

    public bool HasBuiltinIcon => assetIcon is null;

    public Bitmap? AssetIcon => assetIcon;

    public string IconGlyph { get; }

    public string Version { get; }

    public bool IsEnabled { get; }

    public bool RequiresApproval { get; }

    public bool CanEnable => !IsEnabled || RequiresApproval;

    public bool CanRollback => !string.IsNullOrWhiteSpace(RollbackVersion);

    public bool CanConfigure => ConfigurationTitle is not null &&
        IsEnabled &&
        RuntimeState == CanX.Plugins.Runtime.PluginHostState.Running.ToString();

    public string RuntimeState { get; }

    public string Status { get; }

    public string SignatureStatus { get; }

    public string PermissionSummary { get; }

    public string? LastError { get; }

    public string? RollbackVersion { get; }

    public string? ConfigurationTitle { get; }

    public void Dispose()
    {
        assetIcon?.Dispose();
        assetIcon = null;
    }

    private static Bitmap? LoadAssetIcon(string pluginDirectory, string relativePath)
    {
        try
        {
            var root = Path.GetFullPath(pluginDirectory);
            var path = Path.GetFullPath(Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) ||
                relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return null;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Bitmap.DecodeToWidth(stream, 128, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }

    private static string BuiltInIconGlyph(PluginUiIcon icon) => icon.Kind == PluginUiIconKind.Asset
        ? "\u25c7"
        : icon.Value switch
        {
            "home" => "\u2302",
            "search" => "\u2315",
            "globe" => "\u25ce",
            "library" => "\u25a4",
            "folder" => "\u25b1",
            "file" => "\u25a1",
            "link" => "\u2197",
            "download" => "\u2193",
            "upload" => "\u2191",
            "play" => "\u25b6",
            "music" => "\u266a",
            "video" => "\u25b7",
            "subtitles" => "CC",
            "image" => "\u25a7",
            "list" => "\u2637",
            "grid" => "\u25a6",
            "history" => "\u21b6",
            "favorite" => "\u2606",
            "settings" => "\u2699",
            "tools" => "\u2318",
            "warning" => "!",
            "info" => "i",
            _ => "\u25c7",
        };
}
