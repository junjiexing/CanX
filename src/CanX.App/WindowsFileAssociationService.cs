// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CanX.App;

/// <summary>
/// Registers CanX as a per-user handler for the supported formats and reads the
/// current Explorer defaults. Windows protects the per-extension UserChoice with a
/// hash, so applications register themselves and the user picks CanX in the
/// system Default apps page instead of the default being written directly.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsFileAssociationService : IFileAssociationPlatform
{
    internal const string ApplicationName = "CanX";
    private const string MediaProgId = "CanX.Media";
    private const string PlaylistProgId = "CanX.Playlist";
    private const string DownloadProgId = "CanX.Download";
    private const string CapabilitiesPath = @"Software\CanX\Capabilities";
    private readonly RegistryKey _root;
    private readonly string _classesPath;
    private readonly string _softwarePath;
    private readonly string? _executablePath;
    private readonly bool _notifyShell;

    public WindowsFileAssociationService()
        : this(Registry.CurrentUser, string.Empty, Environment.ProcessPath, notifyShell: true)
    {
    }

    /// <param name="root">Registry hive that receives the per-user registration.</param>
    /// <param name="prefix">Key prefix under <paramref name="root"/>; tests use an isolated subtree.</param>
    internal WindowsFileAssociationService(RegistryKey root, string prefix, string? executablePath, bool notifyShell)
    {
        _root = root;
        _softwarePath = string.IsNullOrEmpty(prefix) ? "Software" : $@"{prefix}\Software";
        _classesPath = $@"{_softwarePath}\Classes";
        _executablePath = executablePath is not null &&
            string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(executablePath)
                ? Path.GetFullPath(executablePath)
                : null;
        _notifyShell = notifyShell;
    }

    public bool CanChange => _executablePath is not null;

    public string? UnavailableReason =>
        CanChange ? null : "请从发布的 CanX.App.exe 启动以更改文件关联。";

    public string SetButtonText => "设为 CanX…";

    public string SetAllButtonText => "注册并打开默认应用设置…";

    public string? RemoveButtonText => "移除注册";

    public string RemoveRegistration()
    {
        Unregister();
        return "已移除 CanX 的文件关联注册。已设为默认的格式会由 Windows 恢复为其他程序。";
    }

    private string ExecutableName => Path.GetFileName(_executablePath!);

    public IReadOnlyDictionary<string, FileAssociationState> ReadCurrentApplications(IEnumerable<string> extensions)
    {
        var results = new Dictionary<string, FileAssociationState>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in extensions)
        {
            var executable = QueryAssociation(extension, AssociationString.Executable);
            if (string.IsNullOrWhiteSpace(executable))
            {
                results[extension] = new FileAssociationState("未设置", false);
                continue;
            }

            var isCanX = _executablePath is not null &&
                string.Equals(Path.GetFullPath(executable), _executablePath, StringComparison.OrdinalIgnoreCase);
            var name = isCanX
                ? ApplicationName
                : QueryAssociation(extension, AssociationString.FriendlyApplicationName) ??
                    Path.GetFileNameWithoutExtension(executable);
            results[extension] = new FileAssociationState(name, isCanX);
        }
        return results;
    }

    public FileAssociationChangeResult SetDefaults(IReadOnlyList<string> extensions)
    {
        Register(CanXFileFormats.Groups.SelectMany(static group => group.Extensions));
        var message = extensions.Count == 1
            ? $"已将 CanX 注册为 {extensions[0]} 的可选程序。请在打开的“默认应用”设置中为 {extensions[0]} 选择 CanX，完成后点“刷新”。"
            : "已将 CanX 注册为所有支持格式的可选程序。请在打开的“默认应用”设置中选择 CanX，完成后点“刷新”。";
        OpenDefaultAppsSettings();
        return new FileAssociationChangeResult([], message);
    }

    /// <summary>
    /// Writes the per-user registration. Idempotent; every run rewrites the command
    /// line so moving CanX.App.exe keeps the association working after a re-register.
    /// </summary>
    public void Register(IEnumerable<string> extensions)
    {
        if (_executablePath is null)
        {
            throw new InvalidOperationException(UnavailableReason);
        }

        var command = $"\"{_executablePath}\" \"%1\"";
        var icon = $"\"{_executablePath}\",0";
        WriteProgId(MediaProgId, "CanX 媒体文件", command, icon);
        WriteProgId(PlaylistProgId, "CanX 播放列表", command, icon);
        WriteProgId(DownloadProgId, "CanX 下载描述文件", command, icon);

        using var application = _root.CreateSubKey($@"{_classesPath}\Applications\{ExecutableName}");
        application.SetValue("FriendlyAppName", ApplicationName);
        using (var applicationCommand = application.CreateSubKey(@"shell\open\command"))
        {
            applicationCommand.SetValue(string.Empty, command);
        }

        using var supportedTypes = application.CreateSubKey("SupportedTypes");
        using var capabilities = _root.CreateSubKey($@"{_softwarePath}\CanX\Capabilities");
        capabilities.SetValue("ApplicationName", ApplicationName);
        capabilities.SetValue("ApplicationDescription", "CanX 视频播放器与下载工具");
        capabilities.SetValue("ApplicationIcon", icon);
        using var fileAssociations = capabilities.CreateSubKey("FileAssociations");
        foreach (var extension in extensions.Select(NormalizeExtension).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var progId = ProgIdFor(extension);
            using (var openWith = _root.CreateSubKey($@"{_classesPath}\{extension}\OpenWithProgids"))
            {
                openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            supportedTypes.SetValue(extension, string.Empty);
            fileAssociations.SetValue(extension, progId);
        }

        using (var registered = _root.CreateSubKey($@"{_softwarePath}\RegisteredApplications"))
        {
            registered.SetValue(ApplicationName, CapabilitiesPath);
        }

        NotifyShell();
    }

    /// <summary>Removes every key written by <see cref="Register"/>.</summary>
    public void Unregister()
    {
        foreach (var progId in new[] { MediaProgId, PlaylistProgId, DownloadProgId })
        {
            _root.DeleteSubKeyTree($@"{_classesPath}\{progId}", throwOnMissingSubKey: false);
        }

        foreach (var extension in CanXFileFormats.Groups.SelectMany(static group => group.Extensions))
        {
            using var openWith = _root.OpenSubKey($@"{_classesPath}\{extension}\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgIdFor(extension), throwOnMissingValue: false);
        }

        if (_executablePath is not null)
        {
            _root.DeleteSubKeyTree($@"{_classesPath}\Applications\{ExecutableName}", throwOnMissingSubKey: false);
        }

        _root.DeleteSubKeyTree($@"{_softwarePath}\CanX\Capabilities", throwOnMissingSubKey: false);
        using (var registered = _root.OpenSubKey($@"{_softwarePath}\RegisteredApplications", writable: true))
        {
            registered?.DeleteValue(ApplicationName, throwOnMissingValue: false);
        }

        NotifyShell();
    }

    internal static string ProgIdFor(string extension)
    {
        var normalized = NormalizeExtension(extension);
        return CanXFileFormats.Playlists.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? PlaylistProgId
            : CanXFileFormats.DownloadMetadata.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? DownloadProgId
            : MediaProgId;
    }

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";

    private void WriteProgId(string progId, string description, string command, string icon)
    {
        using var key = _root.CreateSubKey($@"{_classesPath}\{progId}");
        key.SetValue(string.Empty, description);
        key.SetValue("FriendlyTypeName", description);
        using (var defaultIcon = key.CreateSubKey("DefaultIcon"))
        {
            defaultIcon.SetValue(string.Empty, icon);
        }

        using var open = key.CreateSubKey(@"shell\open\command");
        open.SetValue(string.Empty, command);
    }

    private void NotifyShell()
    {
        if (_notifyShell)
        {
            SHChangeNotify(ShellChangeAssociationChanged, ShellChangeFlagIdList, 0, 0);
        }
    }

    private static void OpenDefaultAppsSettings()
    {
        // registeredAppUser opens CanX's own page on Windows 11 22H2 and later;
        // older builds ignore the query and show the general Default apps page.
        Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={ApplicationName}")
        {
            UseShellExecute = true,
        })?.Dispose();
    }

    private static string? QueryAssociation(string extension, AssociationString kind)
    {
        uint length = 0;
        var result = AssocQueryString(AssociationFlagIgnoreUnknown, kind, extension, null, null, ref length);
        if (result != ShellResultFalse || length == 0)
        {
            return null;
        }

        var buffer = new char[length];
        result = AssocQueryString(AssociationFlagIgnoreUnknown, kind, extension, null, buffer, ref length);
        if (result != 0)
        {
            return null;
        }

        var value = new string(buffer, 0, (int)Math.Max(0, length - 1));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private enum AssociationString
    {
        Executable = 2,
        FriendlyApplicationName = 4,
    }

    private const uint AssociationFlagIgnoreUnknown = 0x400;
    private const int ShellResultFalse = 1;
    private const int ShellChangeAssociationChanged = 0x08000000;
    private const uint ShellChangeFlagIdList = 0;

    [LibraryImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AssocQueryString(
        uint flags,
        AssociationString kind,
        string association,
        string? extra,
        [Out] char[]? output,
        ref uint outputLength);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
