// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;

namespace CanX.Plugins.Ui;

public sealed partial class PluginUiRegistry
{
    private const int MaximumPageUpdatesPerSecond = 30;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Registration> registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageRegistration> pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageRegistration> configurations = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public event EventHandler<PluginUiPageChangedEventArgs>? PageChanged;

    public event EventHandler<PluginUiPageChangedEventArgs>? ConfigurationChanged;

    public event EventHandler<PluginConfigurationCloseRequestedEventArgs>? ConfigurationCloseRequested;

    public ValueTask<ReadOnlyMemory<byte>> LoadRemoteImageAsync(
        string pluginId,
        string source,
        bool isConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? loader;
        lock (gate)
        {
            var registration = isConfiguration
                ? GetConfiguration(pluginId)
                : GetPage(pluginId);
            loader = registration.RemoteImageLoader;
        }
        if (loader is null)
        {
            throw new PluginUiException(
                "remote_images_not_available",
                "Plugin remote images are not available for this page.");
        }
        return loader(source, cancellationToken);
    }

    public IReadOnlyList<PluginUiContribution> Contributions
    {
        get
        {
            lock (gate)
            {
                return registrations.Values
                    .Select(registration => registration.Contribution)
                    .OrderBy(contribution => contribution.Slot, StringComparer.Ordinal)
                    .ThenBy(contribution => contribution.Title, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<PluginUiPage> Pages
    {
        get
        {
            lock (gate)
            {
                return pages.Values
                    .Select(registration => registration.Page)
                    .OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(page => page.PluginId, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<PluginUiPage> Configurations
    {
        get
        {
            lock (gate)
            {
                return configurations.Values
                    .Select(registration => registration.Page)
                    .OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(page => page.PluginId, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public async ValueTask<JsonElement?> InvokeConfigurationAsync(
        string pluginId,
        JsonElement input,
        CancellationToken cancellationToken = default)
    {
        Func<string, CancellationToken, ValueTask<string>>? invoke;
        lock (gate)
        {
            if (!configurations.TryGetValue(pluginId, out var registration))
            {
                throw new KeyNotFoundException($"Plugin configuration '{pluginId}' is not registered.");
            }
            invoke = registration.Invoke;
        }
        if (invoke is null)
        {
            throw new PluginUiException("configuration_not_mounted", "Plugin configuration has not registered an event handler.");
        }
        var result = await invoke(input.GetRawText(), cancellationToken).ConfigureAwait(false);
        if (result == "null")
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(result);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new PluginUiException("invalid_ui_result", "Plugin configuration handler returned invalid JSON.");
        }
    }

    internal IDisposable DeclareConfiguration(
        string pluginId,
        PluginConfigurationManifest declaration,
        string pluginDirectory,
        PluginPermissionSet permissions,
        Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? remoteImageLoader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(declaration);
        permissions.Demand(PluginCapabilities.UiConfiguration);
        var page = new PluginUiPage(
            pluginId,
            declaration.Title,
            new PluginUiIcon(PluginUiIconKind.BuiltIn, "settings"),
            Path.GetFullPath(pluginDirectory),
            null,
            false,
            false,
            null,
            "default",
            0);
        var registration = new PageRegistration(page, remoteImageLoader);
        lock (gate)
        {
            if (!configurations.TryAdd(pluginId, registration))
            {
                throw new PluginUiException("configuration_exists", $"Plugin configuration '{pluginId}' is already registered.");
            }
        }
        ConfigurationChanged?.Invoke(
            this,
            new PluginUiPageChangedEventArgs(pluginId, PluginUiPageChangeKind.Declared, page));
        return new ConfigurationRegistrationLease(this, pluginId, registration);
    }

    internal PluginUiPage MountConfiguration(
        string pluginId,
        string contentJson,
        PluginPermissionSet permissions,
        Func<string, CancellationToken, ValueTask<string>> invoke)
    {
        permissions.Demand(PluginCapabilities.UiConfiguration);
        var content = PluginUiSchema.ParsePageContent(contentJson, permissions);
        PluginUiPage page;
        lock (gate)
        {
            var registration = GetConfiguration(pluginId);
            if (registration.Page.IsMounted)
            {
                throw new PluginUiException("configuration_mounted", "Plugin configuration is already mounted.");
            }
            page = registration.Page with
            {
                Content = content,
                IsMounted = true,
                IsInteractive = true,
                Revision = registration.Page.Revision + 1,
            };
            registration.Page = page;
            registration.Invoke = invoke;
        }
        ConfigurationChanged?.Invoke(
            this,
            new PluginUiPageChangedEventArgs(pluginId, PluginUiPageChangeKind.Rendered, page));
        return page;
    }

    internal PluginUiPage RenderConfiguration(
        string pluginId,
        string contentJson,
        PluginPermissionSet permissions)
    {
        permissions.Demand(PluginCapabilities.UiConfiguration);
        var content = PluginUiSchema.ParsePageContent(contentJson, permissions);
        PluginUiPage page;
        lock (gate)
        {
            var registration = GetConfiguration(pluginId);
            if (!registration.Page.IsMounted)
            {
                throw new PluginUiException("configuration_not_mounted", "Plugin configuration must be mounted before rendering.");
            }
            page = registration.Page with
            {
                Content = content,
                Revision = registration.Page.Revision + 1,
            };
            registration.Page = page;
        }
        ConfigurationChanged?.Invoke(
            this,
            new PluginUiPageChangedEventArgs(pluginId, PluginUiPageChangeKind.Rendered, page));
        return page;
    }

    internal PluginUiPage UpdateConfiguration(
        string pluginId,
        string patchesJson,
        PluginPermissionSet permissions)
    {
        permissions.Demand(PluginCapabilities.UiConfiguration);
        PluginUiPage page;
        IReadOnlyList<string> updatedComponentIds;
        lock (gate)
        {
            var registration = GetConfiguration(pluginId);
            if (!registration.Page.IsMounted || registration.Page.Content is not { } content)
            {
                throw new PluginUiException("configuration_not_mounted", "Plugin configuration must be mounted before updating.");
            }
            var now = Environment.TickCount64;
            while (registration.UpdateTimestamps.TryPeek(out var timestamp) && timestamp <= now - 1000)
            {
                registration.UpdateTimestamps.Dequeue();
            }
            if (registration.UpdateTimestamps.Count >= MaximumPageUpdatesPerSecond)
            {
                throw new PluginUiException(
                    "ui_rate_limited",
                    $"Plugin configuration cannot update more than {MaximumPageUpdatesPerSecond} times per second.");
            }
            registration.UpdateTimestamps.Enqueue(now);
            var updated = PluginUiSchema.ApplyPageUpdates(
                content,
                patchesJson,
                permissions,
                out updatedComponentIds);
            page = registration.Page with
            {
                Content = updated,
                Revision = registration.Page.Revision + 1,
            };
            registration.Page = page;
        }
        ConfigurationChanged?.Invoke(
            this,
            new PluginUiPageChangedEventArgs(
                pluginId,
                PluginUiPageChangeKind.Updated,
                page,
                updatedComponentIds));
        return page;
    }

    internal void RequestConfigurationClose(string pluginId, PluginPermissionSet permissions)
    {
        permissions.Demand(PluginCapabilities.UiConfiguration);
        lock (gate)
        {
            _ = GetConfiguration(pluginId);
        }
        ConfigurationCloseRequested?.Invoke(this, new PluginConfigurationCloseRequestedEventArgs(pluginId));
    }

    public async ValueTask<JsonElement?> InvokePageAsync(
        string pluginId,
        JsonElement input,
        CancellationToken cancellationToken = default)
    {
        Func<string, CancellationToken, ValueTask<string>>? invoke;
        lock (gate)
        {
            if (!pages.TryGetValue(pluginId, out var registration))
            {
                throw new KeyNotFoundException($"Plugin page '{pluginId}' is not registered.");
            }
            invoke = registration.Invoke;
        }
        if (invoke is null)
        {
            throw new PluginUiException("page_not_mounted", "Plugin page has not registered an event handler.");
        }

        var result = await invoke(input.GetRawText(), cancellationToken).ConfigureAwait(false);
        if (result == "null")
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(result);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new PluginUiException("invalid_ui_result", "Plugin page handler returned invalid JSON.");
        }
    }

    internal IDisposable DeclarePage(
        string pluginId,
        PluginUiManifest declaration,
        string pluginDirectory,
        PluginPermissionSet permissions,
        Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? remoteImageLoader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(declaration);
        permissions.Demand(PluginCapabilities.UiPage);
        var page = new PluginUiPage(
            pluginId,
            declaration.Title,
            declaration.Icon,
            Path.GetFullPath(pluginDirectory),
            null,
            false,
            false,
            null,
            "default",
            0);
        var registration = new PageRegistration(page, remoteImageLoader);
        lock (gate)
        {
            if (!pages.TryAdd(pluginId, registration))
            {
                throw new PluginUiException("page_exists", $"Plugin page '{pluginId}' is already registered.");
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        PageChanged?.Invoke(
            this,
            new PluginUiPageChangedEventArgs(pluginId, PluginUiPageChangeKind.Declared, page));
        return new PageRegistrationLease(this, pluginId, registration);
    }

    internal PluginUiPage MountPage(
        string pluginId,
        string contentJson,
        PluginPermissionSet permissions,
        Func<string, CancellationToken, ValueTask<string>>? invoke)
    {
        permissions.Demand(PluginCapabilities.UiPage);
        var content = PluginUiSchema.ParsePageContent(contentJson, permissions);
        PluginUiPage page;
        lock (gate)
        {
            var registration = GetPage(pluginId);
            if (registration.Page.IsMounted)
            {
                throw new PluginUiException("page_mounted", "Plugin page is already mounted.");
            }
            page = registration.Page with
            {
                Content = content,
                IsMounted = true,
                IsInteractive = invoke is not null,
                Revision = registration.Page.Revision + 1,
            };
            registration.Page = page;
            registration.Invoke = invoke;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        PageChanged?.Invoke(
            this,
            new PluginUiPageChangedEventArgs(pluginId, PluginUiPageChangeKind.Rendered, page));
        return page;
    }

    internal PluginUiPage RenderPage(
        string pluginId,
        string contentJson,
        PluginPermissionSet permissions)
    {
        permissions.Demand(PluginCapabilities.UiPage);
        var content = PluginUiSchema.ParsePageContent(contentJson, permissions);
        PluginUiPage page;
        lock (gate)
        {
            var registration = GetPage(pluginId);
            if (!registration.Page.IsMounted)
            {
                throw new PluginUiException("page_not_mounted", "Plugin page must be mounted before rendering.");
            }
            page = registration.Page with
            {
                Content = content,
                Revision = registration.Page.Revision + 1,
            };
            registration.Page = page;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        PageChanged?.Invoke(
            this,
            new PluginUiPageChangedEventArgs(pluginId, PluginUiPageChangeKind.Rendered, page));
        return page;
    }

    internal PluginUiPage UpdatePage(
        string pluginId,
        string patchesJson,
        PluginPermissionSet permissions)
    {
        permissions.Demand(PluginCapabilities.UiPage);
        PluginUiPage page;
        IReadOnlyList<string> updatedComponentIds;
        lock (gate)
        {
            var registration = GetPage(pluginId);
            if (!registration.Page.IsMounted || registration.Page.Content is not { } content)
            {
                throw new PluginUiException("page_not_mounted", "Plugin page must be mounted before updating.");
            }
            var now = Environment.TickCount64;
            while (registration.UpdateTimestamps.TryPeek(out var timestamp) && timestamp <= now - 1000)
            {
                registration.UpdateTimestamps.Dequeue();
            }
            if (registration.UpdateTimestamps.Count >= MaximumPageUpdatesPerSecond)
            {
                throw new PluginUiException(
                    "ui_rate_limited",
                    $"Plugin page cannot update more than {MaximumPageUpdatesPerSecond} times per second.");
            }
            registration.UpdateTimestamps.Enqueue(now);
            var updated = PluginUiSchema.ApplyPageUpdates(
                content,
                patchesJson,
                permissions,
                out updatedComponentIds);
            page = registration.Page with
            {
                Content = updated,
                Revision = registration.Page.Revision + 1,
            };
            registration.Page = page;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        PageChanged?.Invoke(
            this,
            new PluginUiPageChangedEventArgs(
                pluginId,
                PluginUiPageChangeKind.Updated,
                page,
                updatedComponentIds));
        return page;
    }

    internal PluginUiPage SetPageBadge(
        string pluginId,
        string? text,
        string tone,
        PluginPermissionSet permissions)
    {
        permissions.Demand(PluginCapabilities.UiPage);
        if (text?.Length > 8)
        {
            throw new PluginUiException("invalid_ui_page", "Plugin page badge cannot exceed 8 characters.");
        }
        if (tone is not ("default" or "accent" or "success" or "warning" or "error"))
        {
            throw new PluginUiException("invalid_ui_page", "Plugin page badge tone is invalid.");
        }
        PluginUiPage page;
        lock (gate)
        {
            var registration = GetPage(pluginId);
            page = registration.Page with
            {
                BadgeText = string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
                BadgeTone = tone,
                Revision = registration.Page.Revision + 1,
            };
            registration.Page = page;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        PageChanged?.Invoke(
            this,
            new PluginUiPageChangedEventArgs(pluginId, PluginUiPageChangeKind.Badge, page));
        return page;
    }

    public async ValueTask<JsonElement?> InvokeAsync(
        string contributionId,
        JsonElement? input = null,
        CancellationToken cancellationToken = default)
    {
        Registration registration;
        lock (gate)
        {
            if (!registrations.TryGetValue(contributionId, out registration!))
            {
                throw new KeyNotFoundException($"Plugin UI contribution '{contributionId}' is not registered.");
            }
        }

        if (registration.Invoke is null)
        {
            throw new PluginUiException("not_interactive", "Plugin UI contribution is not interactive.");
        }

        var inputJson = input?.GetRawText() ?? "null";
        var result = await registration.Invoke(inputJson, cancellationToken).ConfigureAwait(false);
        if (result == "null")
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(result);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new PluginUiException("invalid_ui_result", "Plugin UI handler returned invalid JSON.");
        }
    }

    internal (IDisposable Lease, PluginUiContribution Contribution) Register(
        string pluginId,
        string json,
        PluginPermissionSet permissions,
        Func<string, CancellationToken, ValueTask<string>>? invoke)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(permissions);
        var contribution = PluginUiSchema.Parse(pluginId, json, permissions, invoke is not null);
        var registration = new Registration(contribution, invoke);
        lock (gate)
        {
            if (!registrations.TryAdd(contribution.Id, registration))
            {
                throw new PluginUiException(
                    "contribution_exists",
                    $"UI contribution '{contribution.LocalId}' is already registered.");
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return (new RegistrationLease(this, contribution.Id, registration), contribution);
    }

    private void Unregister(string id, Registration registration)
    {
        var removed = false;
        lock (gate)
        {
            if (ReferenceEquals(registrations.GetValueOrDefault(id), registration))
            {
                registrations.Remove(id);
                removed = true;
            }
        }
        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private PageRegistration GetPage(string pluginId) =>
        pages.TryGetValue(pluginId, out var registration)
            ? registration
            : throw new PluginUiException("page_not_declared", "Plugin manifest does not declare a page.");

    private PageRegistration GetConfiguration(string pluginId) =>
        configurations.TryGetValue(pluginId, out var registration)
            ? registration
            : throw new PluginUiException(
                "configuration_not_declared",
                "Plugin manifest does not declare a configuration dialog.");

    private void UnregisterPage(string pluginId, PageRegistration registration)
    {
        var removed = false;
        lock (gate)
        {
            if (ReferenceEquals(pages.GetValueOrDefault(pluginId), registration))
            {
                pages.Remove(pluginId);
                removed = true;
            }
        }
        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            PageChanged?.Invoke(
                this,
                new PluginUiPageChangedEventArgs(pluginId, PluginUiPageChangeKind.Removed));
        }
    }

    private void UnregisterConfiguration(string pluginId, PageRegistration registration)
    {
        var removed = false;
        lock (gate)
        {
            if (ReferenceEquals(configurations.GetValueOrDefault(pluginId), registration))
            {
                configurations.Remove(pluginId);
                removed = true;
            }
        }
        if (removed)
        {
            ConfigurationChanged?.Invoke(
                this,
                new PluginUiPageChangedEventArgs(pluginId, PluginUiPageChangeKind.Removed));
        }
    }

    internal static bool IsValidLocalId(string id) => UiIdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex UiIdPattern();

    private sealed class Registration(
        PluginUiContribution contribution,
        Func<string, CancellationToken, ValueTask<string>>? invoke)
    {
        public PluginUiContribution Contribution { get; } = contribution;

        public Func<string, CancellationToken, ValueTask<string>>? Invoke { get; } = invoke;
    }

    private sealed class PageRegistration(
        PluginUiPage page,
        Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? remoteImageLoader)
    {
        public PluginUiPage Page { get; set; } = page;

        public Func<string, CancellationToken, ValueTask<string>>? Invoke { get; set; }

        public Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? RemoteImageLoader { get; } =
            remoteImageLoader;

        public Queue<long> UpdateTimestamps { get; } = [];
    }

    private sealed class RegistrationLease(
        PluginUiRegistry owner,
        string id,
        Registration registration) : IDisposable
    {
        private PluginUiRegistry? currentOwner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref currentOwner, null)?.Unregister(id, registration);
    }

    private sealed class PageRegistrationLease(
        PluginUiRegistry owner,
        string pluginId,
        PageRegistration registration) : IDisposable
    {
        private PluginUiRegistry? currentOwner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref currentOwner, null)?.UnregisterPage(pluginId, registration);
    }

    private sealed class ConfigurationRegistrationLease(
        PluginUiRegistry owner,
        string pluginId,
        PageRegistration registration) : IDisposable
    {
        private PluginUiRegistry? currentOwner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref currentOwner, null)?.UnregisterConfiguration(pluginId, registration);
    }
}
