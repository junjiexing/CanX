// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CanX.Plugins.Manifest;
using CanX.Plugins.Ui;

namespace CanX.App.ViewModels;

public sealed class PluginPagesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PluginUiRegistry registry;
    private PluginPageItemViewModel? selectedPage;
    private string? activePluginId;
    private string? statusMessage;
    private bool disposed;

    public PluginPagesViewModel(PluginUiRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        registry.Changed += OnRegistryChanged;
        RefreshPages();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ActivePageRemoved;

    public ObservableCollection<PluginPageItemViewModel> Pages { get; } = [];

    public PluginUiRegistry Registry => registry;

    public PluginPageItemViewModel? SelectedPage
    {
        get => selectedPage;
        private set
        {
            if (ReferenceEquals(selectedPage, value))
            {
                return;
            }
            selectedPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedPage));
        }
    }

    public bool HasSelectedPage => SelectedPage is not null;

    public string? StatusMessage
    {
        get => statusMessage;
        private set
        {
            if (statusMessage == value)
            {
                return;
            }
            statusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public async Task<bool> ActivateAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var target = Pages.FirstOrDefault(page => page.PluginId == pluginId);
        if (target is null)
        {
            return false;
        }

        var previousId = activePluginId;
        activePluginId = target.PluginId;
        SelectedPage = target;
        UpdateSelection();
        StatusMessage = null;
        if (previousId is not null && previousId != target.PluginId)
        {
            await InvokeLifecycleAsync(previousId, "deactivate", cancellationToken).ConfigureAwait(true);
        }
        if (previousId != target.PluginId)
        {
            await InvokeLifecycleAsync(target.PluginId, "activate", cancellationToken).ConfigureAwait(true);
        }
        return true;
    }

    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        var previousId = activePluginId;
        activePluginId = null;
        UpdateSelection();
        if (previousId is not null)
        {
            await InvokeLifecycleAsync(previousId, "deactivate", cancellationToken).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        registry.Changed -= OnRegistryChanged;
        foreach (var page in Pages)
        {
            page.Dispose();
        }
        Pages.Clear();
    }

    private void OnRegistryChanged(object? sender, EventArgs eventArgs) =>
        Dispatcher.UIThread.Post(RefreshPages);

    private void RefreshPages()
    {
        if (disposed)
        {
            return;
        }
        var snapshots = registry.Pages;
        var current = Pages.ToDictionary(page => page.PluginId, StringComparer.Ordinal);
        var ordered = new List<PluginPageItemViewModel>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            if (current.Remove(snapshot.PluginId, out var item))
            {
                item.Update(snapshot);
            }
            else
            {
                item = new PluginPageItemViewModel(snapshot);
            }
            ordered.Add(item);
        }
        foreach (var removed in current.Values)
        {
            Pages.Remove(removed);
            removed.Dispose();
        }
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            var currentIndex = Pages.IndexOf(item);
            if (currentIndex < 0)
            {
                Pages.Insert(index, item);
            }
            else if (currentIndex != index)
            {
                Pages.Move(currentIndex, index);
            }
        }

        if (SelectedPage is not null)
        {
            SelectedPage = Pages.FirstOrDefault(page => page.PluginId == SelectedPage.PluginId);
        }
        if (activePluginId is not null && Pages.All(page => page.PluginId != activePluginId))
        {
            activePluginId = null;
            SelectedPage = null;
            ActivePageRemoved?.Invoke(this, EventArgs.Empty);
        }
        UpdateSelection();
    }

    private async Task InvokeLifecycleAsync(
        string pluginId,
        string eventType,
        CancellationToken cancellationToken)
    {
        var page = registry.Pages.FirstOrDefault(candidate => candidate.PluginId == pluginId);
        if (page is not { IsMounted: true, IsInteractive: true })
        {
            return;
        }
        try
        {
            using var document = JsonDocument.Parse($"{{\"type\":\"{eventType}\"}}");
            await registry.InvokePageAsync(pluginId, document.RootElement, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusMessage = exception.Message;
        }
    }

    private void UpdateSelection()
    {
        foreach (var page in Pages)
        {
            page.IsSelected = page.PluginId == activePluginId;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PluginPageItemViewModel : INotifyPropertyChanged, IDisposable
{
    private PluginUiPage snapshot;
    private bool isSelected;
    private Bitmap? assetIcon;

    internal PluginPageItemViewModel(PluginUiPage snapshot)
    {
        this.snapshot = snapshot;
        if (snapshot.Icon.Kind == PluginUiIconKind.Asset)
        {
            assetIcon = LoadAssetIcon(snapshot);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PluginId => snapshot.PluginId;

    public string Title => snapshot.Title;

    public string? BadgeText => snapshot.BadgeText;

    public string BadgeTone => snapshot.BadgeTone;

    public bool HasBadge => !string.IsNullOrWhiteSpace(BadgeText);

    public bool HasAssetIcon => assetIcon is not null;

    public bool HasBuiltinIcon => assetIcon is null;

    public Bitmap? AssetIcon => assetIcon;

    public string IconGlyph => BuiltInIconGlyph(snapshot.Icon);

    public bool IsSelected
    {
        get => isSelected;
        internal set
        {
            if (isSelected == value)
            {
                return;
            }
            isSelected = value;
            OnPropertyChanged();
        }
    }

    internal void Update(PluginUiPage value)
    {
        var previous = snapshot;
        snapshot = value;
        if (previous.Title != value.Title)
        {
            OnPropertyChanged(nameof(Title));
        }
        if (previous.BadgeText != value.BadgeText || previous.BadgeTone != value.BadgeTone)
        {
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(BadgeTone));
            OnPropertyChanged(nameof(HasBadge));
        }
    }

    public void Dispose()
    {
        assetIcon?.Dispose();
        assetIcon = null;
    }

    private static Bitmap? LoadAssetIcon(PluginUiPage page)
    {
        try
        {
            var relativePath = page.Icon.Value.Replace('/', Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(page.PluginDirectory, relativePath));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Bitmap.DecodeToWidth(stream, 32, BitmapInterpolationMode.HighQuality);
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
