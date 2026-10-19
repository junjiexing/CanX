// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Diagnostics;
using CanX.Core.Security;
using CanX.Plugins.Manifest;
using CanX.Plugins.Packaging;
using CanX.Plugins.Runtime;
using CanX.Plugins.Security;

namespace CanX.Plugins.Lifecycle;

public sealed class PluginLifecycleManager : IAsyncDisposable
{
    private const string ManifestFileName = "canx-plugin.json";
    private readonly string rootDirectory;
    private readonly PluginPackageInstaller installer;
    private readonly IPluginRuntimeHostFactory hostFactory;
    private readonly IApplicationLog log;
    private readonly PluginStateStore stateStore;
    private readonly IPluginAuditSink audit;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ManagedPlugin> plugins = new(StringComparer.Ordinal);
    private bool initialized;
    private bool disposed;

    public PluginLifecycleManager(
        string rootDirectory,
        IPluginRuntimeHostFactory hostFactory,
        IApplicationLog? log = null,
        PluginPackageLimits? packageLimits = null,
        PluginSignaturePolicy? signaturePolicy = null,
        IPluginAuditSink? audit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        this.hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        this.log = log ?? NullApplicationLog.Instance;
        this.audit = audit ?? NullPluginAuditSink.Instance;
        installer = new PluginPackageInstaller(this.rootDirectory, packageLimits, signaturePolicy);
        stateStore = new PluginStateStore(this.rootDirectory);
    }

    public string RootDirectory => rootDirectory;

    public IReadOnlyList<PluginStatus> Plugins
    {
        get
        {
            gate.Wait();
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return plugins.Values
                    .Select(plugin => plugin.Status)
                    .OrderBy(plugin => plugin.Manifest.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal)
                    .ToArray();
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized)
            {
                return;
            }

            PrivateFileSystem.EnsurePrivateDirectory(rootDirectory);
            var savedStates = stateStore.Load();
            foreach (var installation in DiscoverInstallations(savedStates))
            {
                var savedState = savedStates.GetValueOrDefault(installation.Manifest.Id);
                var rollback = RestoreRollback(installation.Manifest.Id, savedState?.Rollback);
                var fingerprint = PluginPermissionFingerprint.Compute(installation.Manifest);
                var approved = savedState is not null && string.Equals(
                    savedState.ApprovedPermissionFingerprint,
                    fingerprint,
                    StringComparison.Ordinal);
                var enabled = savedState?.IsEnabled == true && approved;
                var status = new PluginStatus(
                    installation.Manifest,
                    installation.DirectoryPath,
                    installation.PackageSha256,
                    installation.InstalledAt,
                    enabled,
                    installation.Manifest.Permissions.Count > 0 && !approved,
                    PluginHostState.Stopped,
                    null,
                    installation.Signature,
                    rollback?.Installation.Manifest.Version);
                plugins.Add(
                    installation.Manifest.Id,
                    new ManagedPlugin(
                        installation,
                        status,
                        savedState?.ApprovedPermissionFingerprint ?? string.Empty,
                        rollback));
            }

            initialized = true;
            SaveState();
            foreach (var plugin in plugins.Values.Where(plugin => plugin.Status.IsEnabled))
            {
                await StartPluginAsync(plugin, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PluginStatus> InstallAsync(
        Stream package,
        bool allowUpgrade = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            cancellationToken.ThrowIfCancellationRequested();
            var installation = installer.Install(package, allowUpgrade);
            plugins.TryGetValue(installation.Manifest.Id, out var previous);
            var rollback = previous is null
                ? null
                : new ManagedRollback(
                    previous.Installation with
                    {
                        DirectoryPath = installer.GetRollbackDirectory(installation.Manifest.Id),
                    },
                    previous.Status.IsEnabled,
                    previous.ApprovedPermissionFingerprint);
            var fingerprint = PluginPermissionFingerprint.Compute(installation.Manifest);
            var permissionApproved = false;
            var shouldRemainEnabled = false;
            var approvedFingerprint = string.Empty;
            if (previous is not null)
            {
                var previousFingerprint = PluginPermissionFingerprint.Compute(previous.Installation.Manifest);
                var previouslyApproved = string.Equals(
                    previous.ApprovedPermissionFingerprint,
                    previousFingerprint,
                    StringComparison.Ordinal);
                var permissionDiff = PluginPermissionDiff.Compare(
                    previous.Installation.Manifest,
                    installation.Manifest);
                // An approval belongs to the publisher that was reviewed. A package signed by another key,
                // or an unsigned package replacing a signed one, is treated like a new plugin: it installs
                // stopped and its permissions must be approved again.
                permissionApproved = previouslyApproved &&
                    !permissionDiff.HasPrivilegeIncrease &&
                    IsSameSigner(previous.Installation.Signature, installation.Signature);
                shouldRemainEnabled = previous.Status.IsEnabled && permissionApproved;
                if (permissionApproved)
                {
                    approvedFingerprint = fingerprint;
                }
            }

            var requiresApproval = installation.Manifest.Permissions.Count > 0 && !permissionApproved;
            var status = new PluginStatus(
                installation.Manifest,
                installation.DirectoryPath,
                installation.PackageSha256,
                installation.InstalledAt,
                shouldRemainEnabled,
                requiresApproval,
                PluginHostState.Stopped,
                null,
                installation.Signature,
                rollback?.Installation.Manifest.Version);
            var managed = new ManagedPlugin(installation, status, approvedFingerprint, rollback);
            try
            {
                await WriteRequiredAuditAsync(
                    previous is null ? "install" : "upgrade",
                    installation,
                    previous?.Installation,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (previous?.Host is not null)
                {
                    await previous.Host.DisposeAsync().ConfigureAwait(false);
                    previous.Host = null;
                }

                plugins[installation.Manifest.Id] = managed;
                SaveState();
                if (shouldRemainEnabled)
                {
                    await StartPluginAsync(managed, CancellationToken.None).ConfigureAwait(false);
                }

                await WriteCompletionAuditAsync(
                    previous is null ? "install" : "upgrade",
                    installation,
                    previous?.Installation).ConfigureAwait(false);

                return managed.Status;
            }
            catch
            {
                if (managed.Host is not null)
                {
                    await managed.Host.DisposeAsync().ConfigureAwait(false);
                    managed.Host = null;
                }
                await RestoreFailedInstallationAsync(installation.Manifest.Id, previous).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PluginStatus> ApproveAndEnableAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var plugin = GetPlugin(pluginId);
            await WriteRequiredAuditAsync(
                "approve-enable",
                plugin.Installation,
                null,
                cancellationToken).ConfigureAwait(false);
            var previousStatus = plugin.Status;
            var previousFingerprint = plugin.ApprovedPermissionFingerprint;
            var hostWasRunning = plugin.Host?.State == PluginHostState.Running;
            plugin.ApprovedPermissionFingerprint = PluginPermissionFingerprint.Compute(plugin.Installation.Manifest);
            plugin.Status = plugin.Status with
            {
                IsEnabled = true,
                RequiresPermissionApproval = false,
                LastError = null,
            };
            try
            {
                SaveState();
                if (plugin.Host is null || plugin.Host.State is PluginHostState.Faulted or PluginHostState.Stopped)
                {
                    if (plugin.Host is not null)
                    {
                        await plugin.Host.DisposeAsync().ConfigureAwait(false);
                        plugin.Host = null;
                    }

                    await StartPluginAsync(plugin, CancellationToken.None).ConfigureAwait(false);
                }

                await WriteCompletionAuditAsync("approve-enable", plugin.Installation, null).ConfigureAwait(false);
                return plugin.Status;
            }
            catch
            {
                if (!hostWasRunning && plugin.Host is not null)
                {
                    await plugin.Host.DisposeAsync().ConfigureAwait(false);
                    plugin.Host = null;
                }
                plugin.Status = previousStatus;
                plugin.ApprovedPermissionFingerprint = previousFingerprint;
                TrySaveRestoredState("approve-enable");
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PluginStatus> DisableAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var plugin = GetPlugin(pluginId);
            await WriteRequiredAuditAsync("disable", plugin.Installation, null, cancellationToken).ConfigureAwait(false);
            var previousStatus = plugin.Status;
            var wasRunning = plugin.Host is not null;
            if (plugin.Host is not null)
            {
                await plugin.Host.DisposeAsync().ConfigureAwait(false);
                plugin.Host = null;
            }
            plugin.Status = plugin.Status with
            {
                IsEnabled = false,
                RuntimeState = PluginHostState.Stopped,
                LastError = null,
            };
            try
            {
                SaveState();
            }
            catch
            {
                plugin.Status = previousStatus;
                if (wasRunning)
                {
                    await StartPluginAsync(plugin, CancellationToken.None).ConfigureAwait(false);
                }
                TrySaveRestoredState("disable");
                throw;
            }
            await WriteCompletionAuditAsync("disable", plugin.Installation, null).ConfigureAwait(false);
            return plugin.Status;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UninstallAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var plugin = GetPlugin(pluginId);
            await WriteRequiredAuditAsync("uninstall", plugin.Installation, null, cancellationToken).ConfigureAwait(false);
            var wasRunning = plugin.Host is not null;
            if (plugin.Host is not null)
            {
                await plugin.Host.DisposeAsync().ConfigureAwait(false);
                plugin.Host = null;
            }

            var removedRoot = Path.Combine(rootDirectory, ".removed");
            Directory.CreateDirectory(removedRoot);
            var removedPath = Path.Combine(removedRoot, $"{pluginId}-{Guid.NewGuid():N}");
            Directory.Move(plugin.Installation.DirectoryPath, removedPath);
            plugins.Remove(pluginId);
            try
            {
                SaveState();
            }
            catch
            {
                Directory.Move(removedPath, plugin.Installation.DirectoryPath);
                plugins.Add(pluginId, plugin);
                if (wasRunning)
                {
                    await StartPluginAsync(plugin, CancellationToken.None).ConfigureAwait(false);
                }
                TrySaveRestoredState("uninstall");
                throw;
            }

            TryDeleteDirectory(removedPath);
            installer.DeleteRollback(pluginId);
            await WriteCompletionAuditAsync("uninstall", plugin.Installation, null).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PluginStatus> RollbackAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var current = GetPlugin(pluginId);
            var rollback = current.Rollback ?? throw new InvalidOperationException(
                $"Plugin '{pluginId}' does not have a rollback version.");
            await WriteRequiredAuditAsync(
                "rollback",
                rollback.Installation,
                current.Installation,
                cancellationToken).ConfigureAwait(false);
            if (current.Host is not null)
            {
                await current.Host.DisposeAsync().ConfigureAwait(false);
                current.Host = null;
            }

            try
            {
                installer.SwapWithRollback(pluginId);
            }
            catch
            {
                if (current.Status.IsEnabled)
                {
                    await StartPluginAsync(current, CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            var restoredInstallation = rollback.Installation with
            {
                DirectoryPath = current.Installation.DirectoryPath,
            };
            var displaced = new ManagedRollback(
                current.Installation with
                {
                    DirectoryPath = installer.GetRollbackDirectory(pluginId),
                },
                current.Status.IsEnabled,
                current.ApprovedPermissionFingerprint);
            var restoredFingerprint = PluginPermissionFingerprint.Compute(restoredInstallation.Manifest);
            var permissionApproved = string.Equals(
                rollback.ApprovedPermissionFingerprint,
                restoredFingerprint,
                StringComparison.Ordinal);
            var enabled = rollback.WasEnabled && permissionApproved;
            var status = new PluginStatus(
                restoredInstallation.Manifest,
                restoredInstallation.DirectoryPath,
                restoredInstallation.PackageSha256,
                restoredInstallation.InstalledAt,
                enabled,
                restoredInstallation.Manifest.Permissions.Count > 0 && !permissionApproved,
                PluginHostState.Stopped,
                null,
                restoredInstallation.Signature,
                displaced.Installation.Manifest.Version);
            var restored = new ManagedPlugin(
                restoredInstallation,
                status,
                permissionApproved ? rollback.ApprovedPermissionFingerprint : string.Empty,
                displaced);
            plugins[pluginId] = restored;
            try
            {
                SaveState();
            }
            catch
            {
                installer.SwapWithRollback(pluginId);
                plugins[pluginId] = current;
                if (current.Status.IsEnabled)
                {
                    await StartPluginAsync(current, CancellationToken.None).ConfigureAwait(false);
                }
                TrySaveRestoredState("rollback");
                throw;
            }
            if (enabled)
            {
                await StartPluginAsync(restored, CancellationToken.None).ConfigureAwait(false);
            }

            await WriteCompletionAuditAsync(
                "rollback",
                restoredInstallation,
                current.Installation).ConfigureAwait(false);
            return restored.Status;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PluginStatus> ReloadAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var plugin = GetPlugin(pluginId);
            if (!plugin.Status.IsEnabled)
            {
                return plugin.Status;
            }
            await WriteRequiredAuditAsync("reload", plugin.Installation, null, cancellationToken).ConfigureAwait(false);
            if (plugin.Host is not null)
            {
                await plugin.Host.DisposeAsync().ConfigureAwait(false);
                plugin.Host = null;
            }
            plugin.Status = plugin.Status with { RuntimeState = PluginHostState.Stopped, LastError = null };
            await StartPluginAsync(plugin, CancellationToken.None).ConfigureAwait(false);
            await WriteCompletionAuditAsync("reload", plugin.Installation, null).ConfigureAwait(false);
            return plugin.Status;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var plugin in plugins.Values)
            {
                if (plugin.Host is not null)
                {
                    await plugin.Host.DisposeAsync().ConfigureAwait(false);
                    plugin.Host = null;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<PluginInstallation> DiscoverInstallations(
        IReadOnlyDictionary<string, PluginStateEntry> savedStates)
    {
        var installations = new List<PluginInstallation>();
        foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
        {
            var directoryName = Path.GetFileName(directory);
            if (directoryName.StartsWith('.'))
            {
                continue;
            }

            var manifestPath = Path.Combine(directory, ManifestFileName);
            try
            {
                var file = new FileInfo(manifestPath);
                if (!file.Exists || file.Length > PluginManifestReader.MaximumManifestBytes)
                {
                    continue;
                }

                var manifest = PluginManifestReader.Parse(File.ReadAllBytes(manifestPath));
                if (!string.Equals(directoryName, manifest.Id, StringComparison.Ordinal))
                {
                    log.Write(
                        LogComponent.QuickJs,
                        ApplicationLogLevel.Warning,
                        $"Ignored plugin directory whose name does not match its manifest ID: {directoryName}");
                    continue;
                }

                var saved = savedStates.GetValueOrDefault(manifest.Id);
                installations.Add(new PluginInstallation(
                    manifest,
                    directory,
                    saved?.PackageSha256 ?? string.Empty,
                    saved?.InstalledAt ?? new DateTimeOffset(file.CreationTimeUtc),
                    saved?.SignatureKeyId is { Length: > 0 } keyId
                        ? new PluginSignatureInfo(
                            keyId,
                            PluginSignatureVerifier.Algorithm,
                            saved.SignatureTrusted)
                        : null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PluginManifestException)
            {
                log.Write(
                    LogComponent.QuickJs,
                    ApplicationLogLevel.Warning,
                    $"Ignored invalid plugin directory: {directoryName}",
                    exception);
            }
        }

        return installations;
    }

    private async Task StartPluginAsync(ManagedPlugin plugin, CancellationToken cancellationToken)
    {
        var host = hostFactory.Create(plugin.Installation);
        plugin.Host = host;
        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            plugin.Status = plugin.Status with
            {
                RuntimeState = host.State,
                LastError = null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            plugin.Host = null;
            throw;
        }
        catch (Exception exception)
        {
            plugin.Status = plugin.Status with
            {
                RuntimeState = PluginHostState.Faulted,
                LastError = exception.Message,
            };
            await host.DisposeAsync().ConfigureAwait(false);
            plugin.Host = null;
        }
    }

    private ManagedPlugin GetPlugin(string pluginId) =>
        plugins.TryGetValue(pluginId, out var plugin)
            ? plugin
            : throw new KeyNotFoundException($"Plugin '{pluginId}' is not installed.");

    private void SaveState() => stateStore.Save(plugins.Values.Select(plugin => new PluginStateEntry
    {
        Id = plugin.Installation.Manifest.Id,
        IsEnabled = plugin.Status.IsEnabled,
        ApprovedPermissionFingerprint = plugin.ApprovedPermissionFingerprint,
        PackageSha256 = plugin.Installation.PackageSha256,
        InstalledAt = plugin.Installation.InstalledAt,
        SignatureKeyId = plugin.Installation.Signature?.KeyId,
        SignatureTrusted = plugin.Installation.Signature?.IsTrusted == true,
        Rollback = plugin.Rollback is null
            ? null
            : new PluginRollbackState
            {
                PackageSha256 = plugin.Rollback.Installation.PackageSha256,
                InstalledAt = plugin.Rollback.Installation.InstalledAt,
                WasEnabled = plugin.Rollback.WasEnabled,
                ApprovedPermissionFingerprint = plugin.Rollback.ApprovedPermissionFingerprint,
                SignatureKeyId = plugin.Rollback.Installation.Signature?.KeyId,
                SignatureTrusted = plugin.Rollback.Installation.Signature?.IsTrusted == true,
            },
    }));

    private ValueTask WriteRequiredAuditAsync(
        string operation,
        PluginInstallation installation,
        PluginInstallation? previous,
        CancellationToken cancellationToken) =>
        audit.WriteAsync(CreateAuditEntry(operation, installation, previous, PluginAuditPhase.Started), cancellationToken);

    private async ValueTask WriteCompletionAuditAsync(
        string operation,
        PluginInstallation installation,
        PluginInstallation? previous)
    {
        try
        {
            await audit.WriteAsync(
                CreateAuditEntry(operation, installation, previous, PluginAuditPhase.Completed),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Write(
                LogComponent.QuickJs,
                ApplicationLogLevel.Error,
                $"Plugin lifecycle completion audit failed: {operation} {installation.Manifest.Id}",
                exception);
        }
    }

    private static PluginAuditEntry CreateAuditEntry(
        string operation,
        PluginInstallation installation,
        PluginInstallation? previous,
        PluginAuditPhase phase) =>
            new(
                DateTimeOffset.UtcNow,
                operation,
                installation.Manifest.Id,
                installation.Manifest.Version,
                installation.PackageSha256,
                PluginPermissionFingerprint.Compute(installation.Manifest),
                previous?.Manifest.Version,
                previous?.PackageSha256,
                DescribePermissionChanges(previous?.Manifest, installation.Manifest),
                installation.Signature?.KeyId,
                installation.Signature?.IsTrusted == true,
                phase);

    private async ValueTask RestoreFailedInstallationAsync(string pluginId, ManagedPlugin? previous)
    {
        if (previous is null)
        {
            plugins.Remove(pluginId);
            TryDeleteDirectory(Path.Combine(rootDirectory, pluginId));
            installer.DeleteRollback(pluginId);
            TrySaveRestoredState("install");
            return;
        }

        plugins[pluginId] = previous;
        installer.SwapWithRollback(pluginId);
        installer.DeleteRollback(pluginId);
        previous.Rollback = null;
        previous.Status = previous.Status with { RollbackVersion = null };
        if (previous.Status.IsEnabled && previous.Host?.State != PluginHostState.Running)
        {
            await StartPluginAsync(previous, CancellationToken.None).ConfigureAwait(false);
        }
        TrySaveRestoredState("install");
    }

    private void TrySaveRestoredState(string operation)
    {
        try
        {
            SaveState();
        }
        catch (Exception exception)
        {
            log.Write(
                LogComponent.QuickJs,
                ApplicationLogLevel.Error,
                $"Plugin lifecycle could not persist restored state after failed {operation}.",
                exception);
        }
    }

    private ManagedRollback? RestoreRollback(string pluginId, PluginRollbackState? saved)
    {
        if (saved is null)
        {
            return null;
        }
        try
        {
            var manifest = installer.ReadRollbackManifest(pluginId);
            if (manifest is null || !string.Equals(manifest.Id, pluginId, StringComparison.Ordinal))
            {
                return null;
            }
            var signature = saved.SignatureKeyId is { Length: > 0 } keyId
                ? new PluginSignatureInfo(keyId, PluginSignatureVerifier.Algorithm, saved.SignatureTrusted)
                : null;
            return new ManagedRollback(
                new PluginInstallation(
                    manifest,
                    installer.GetRollbackDirectory(pluginId),
                    saved.PackageSha256,
                    saved.InstalledAt,
                    signature),
                saved.WasEnabled,
                saved.ApprovedPermissionFingerprint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PluginManifestException or PluginPackageException)
        {
            log.Write(
                LogComponent.QuickJs,
                ApplicationLogLevel.Warning,
                $"Ignored invalid rollback version for plugin: {pluginId}",
                exception);
            return null;
        }
    }

    private static IReadOnlyList<string> DescribePermissionChanges(
        PluginManifest? previous,
        PluginManifest current)
    {
        if (previous is null)
        {
            return current.Permissions
                .OrderBy(permission => permission.Capability, StringComparer.Ordinal)
                .Select(permission => $"+ {DescribePermission(permission.Capability, permission.Scopes)}")
                .ToArray();
        }

        var diff = PluginPermissionDiff.Compare(previous, current);
        return diff.Added
            .Select(change => $"+ {DescribePermission(change.Capability, change.AddedScopes)}")
            .Concat(diff.Removed.Select(change => $"- {DescribePermission(change.Capability, change.RemovedScopes)}"))
            .Concat(diff.Changed.Select(change =>
                $"~ {change.Capability} +[{string.Join(',', change.AddedScopes)}] -[{string.Join(',', change.RemovedScopes)}]"))
            .ToArray();
    }

    private static string DescribePermission(string capability, IReadOnlyList<string> scopes) =>
        scopes.Count == 0 ? capability : $"{capability}[{string.Join(',', scopes)}]";

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!initialized)
        {
            throw new InvalidOperationException("Plugin lifecycle manager has not been initialized.");
        }
    }

    private static bool IsSameSigner(PluginSignatureInfo? previous, PluginSignatureInfo? current) =>
        previous is null || string.Equals(previous.KeyId, current?.KeyId, StringComparison.Ordinal);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ManagedPlugin(
        PluginInstallation installation,
        PluginStatus status,
        string approvedPermissionFingerprint,
        ManagedRollback? rollback)
    {
        public PluginInstallation Installation { get; } = installation;

        public PluginStatus Status { get; set; } = status;

        public string ApprovedPermissionFingerprint { get; set; } = approvedPermissionFingerprint;

        public ManagedRollback? Rollback { get; set; } = rollback;

        public IPluginRuntimeHost? Host { get; set; }
    }

    private sealed record ManagedRollback(
        PluginInstallation Installation,
        bool WasEnabled,
        string ApprovedPermissionFingerprint);
}
