// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.App;

internal sealed record FileAssociationState(string ApplicationName, bool IsCanX);

internal sealed record FileAssociationChangeResult(IReadOnlyList<string> Failed, string? Message = null);

/// <summary>
/// Platform backend for the settings page's file association panel.
/// </summary>
internal interface IFileAssociationPlatform
{
    /// <summary>Whether this process can change associations at all (for example, a packaged app).</summary>
    bool CanChange { get; }

    /// <summary>Why <see cref="CanChange"/> is false, shown to the user.</summary>
    string? UnavailableReason { get; }

    /// <summary>Label of the per-format action button.</summary>
    string SetButtonText { get; }

    /// <summary>Label of the toolbar action applied to every supported format.</summary>
    string SetAllButtonText { get; }

    IReadOnlyDictionary<string, FileAssociationState> ReadCurrentApplications(IEnumerable<string> extensions);

    /// <summary>
    /// Associates the extensions with CanX, or starts the platform flow that lets the user do so.
    /// Called on a worker thread.
    /// </summary>
    FileAssociationChangeResult SetDefaults(IReadOnlyList<string> extensions);

    /// <summary>Label of the action that removes CanX's registration, or null when the platform has none.</summary>
    string? RemoveButtonText => null;

    /// <summary>Removes CanX's registration. Called on a worker thread.</summary>
    string RemoveRegistration() => throw new NotSupportedException();

    static IFileAssociationPlatform? CreateForCurrentPlatform() =>
        OperatingSystem.IsMacOS() ? new MacFileAssociationService()
        : OperatingSystem.IsWindows() ? new WindowsFileAssociationService()
        : null;
}
