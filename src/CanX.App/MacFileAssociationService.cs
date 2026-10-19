// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace CanX.App;

// Launch Services owns the effective Finder defaults. Reading Info.plist or the
// com.apple.LaunchServices preference file does not give the current answer.
internal sealed record MacFileAssociationState(string? BundleId, string ApplicationName)
{
    public bool IsCanX => BundleId == "org.canx.app";
}

internal sealed class MacFileAssociationService : IFileAssociationPlatform
{
    private const string BundleIdentifier = "org.canx.app";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const uint Utf8Encoding = 0x08000100;
    private const uint AllRoles = uint.MaxValue;
    private readonly string? _appBundlePath;

    public MacFileAssociationService()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        var contentsDirectory = executableDirectory is null ? null : Path.GetDirectoryName(executableDirectory);
        var bundleDirectory = contentsDirectory is null ? null : Path.GetDirectoryName(contentsDirectory);
        if (Path.GetFileName(executableDirectory) == "MacOS" &&
            Path.GetFileName(contentsDirectory) == "Contents" &&
            bundleDirectory?.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true &&
            File.Exists(Path.Combine(contentsDirectory!, "Info.plist")))
        {
            _appBundlePath = bundleDirectory;
        }
    }

    public bool CanSetDefaults => _appBundlePath is not null;

    bool IFileAssociationPlatform.CanChange => CanSetDefaults;

    string? IFileAssociationPlatform.UnavailableReason =>
        CanSetDefaults ? null : "请从 CanX.app 启动以更改文件关联。";

    string IFileAssociationPlatform.SetButtonText => "设为 CanX";

    string IFileAssociationPlatform.SetAllButtonText => "全部设为 CanX";

    IReadOnlyDictionary<string, FileAssociationState> IFileAssociationPlatform.ReadCurrentApplications(
        IEnumerable<string> extensions) =>
        ReadCurrentApplications(extensions).ToDictionary(
            static entry => entry.Key,
            static entry => new FileAssociationState(entry.Value.ApplicationName, entry.Value.IsCanX),
            StringComparer.OrdinalIgnoreCase);

    FileAssociationChangeResult IFileAssociationPlatform.SetDefaults(IReadOnlyList<string> extensions) =>
        new(SetDefaults(extensions));

    public IReadOnlyDictionary<string, MacFileAssociationState> ReadCurrentApplications(IEnumerable<string> extensions)
    {
        EnsureMacOS();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var results = new Dictionary<string, MacFileAssociationState>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in extensions)
        {
            var contentType = GetContentType(extension);
            if (contentType is null)
            {
                results[extension] = new(null, "系统未识别");
                continue;
            }

            using var type = new CfString(contentType);
            var handler = LSCopyDefaultRoleHandlerForContentType(type.Handle, AllRoles);
            if (handler == 0)
            {
                results[extension] = new(null, "未设置");
                continue;
            }

            try
            {
                var bundleId = ReadString(handler);
                if (!names.TryGetValue(bundleId, out var name))
                {
                    name = bundleId == BundleIdentifier ? "CanX" : GetApplicationName(bundleId);
                    names.Add(bundleId, name);
                }
                results[extension] = new(bundleId, name);
            }
            finally
            {
                CFRelease(handler);
            }
        }
        return results;
    }

    public IReadOnlyList<string> SetDefaults(IEnumerable<string> extensions)
    {
        EnsureMacOS();
        if (_appBundlePath is null)
            throw new InvalidOperationException("请从打包后的 CanX.app 启动，再修改文件关联。");

        using var path = new CfString(_appBundlePath);
        var appUrl = CFURLCreateWithFileSystemPath(0, path.Handle, 0, true);
        if (appUrl == 0) throw new InvalidOperationException("无法注册 CanX.app。");
        try
        {
            var status = LSRegisterURL(appUrl, true);
            if (status != 0) throw new InvalidOperationException($"注册 CanX.app 失败（{status}）。");
        }
        finally
        {
            CFRelease(appUrl);
        }

        using var bundleId = new CfString(BundleIdentifier);
        var attemptedTypes = new HashSet<string>(StringComparer.Ordinal);
        var failed = new List<string>();
        foreach (var extension in extensions)
        {
            var contentType = GetContentType(extension);
            if (contentType is null)
            {
                failed.Add(extension);
                continue;
            }
            if (!attemptedTypes.Add(contentType)) continue;

            using var type = new CfString(contentType);
            var status = LSSetDefaultRoleHandlerForContentType(type.Handle, AllRoles, bundleId.Handle);
            if (status != 0) failed.Add($"{extension}（{status}）");
        }
        return failed;
    }

    private static string? GetContentType(string extension)
    {
        using var tagClass = new CfString("public.filename-extension");
        using var tag = new CfString(extension.TrimStart('.'));
        var result = UTTypeCreatePreferredIdentifierForTag(tagClass.Handle, tag.Handle, 0);
        if (result == 0) return null;
        try
        {
            var identifier = ReadString(result);
            // The bundle imports a stable type for each supported extension.
            // A dynamic identifier cannot be a reliable persistent default.
            return identifier.StartsWith("dyn.", StringComparison.Ordinal)
                ? $"org.canx.format.{extension.TrimStart('.').ToLowerInvariant()}"
                : identifier;
        }
        finally
        {
            CFRelease(result);
        }
    }

    private static string GetApplicationName(string bundleId)
    {
        using var identifier = new CfString(bundleId);
        var urls = LSCopyApplicationURLsForBundleIdentifier(identifier.Handle, 0);
        if (urls == 0) return bundleId;
        try
        {
            if (CFArrayGetCount(urls) == 0) return bundleId;
            var url = CFArrayGetValueAtIndex(urls, 0);
            var path = CFURLCopyFileSystemPath(url, 0);
            if (path == 0) return bundleId;
            try
            {
                var name = Path.GetFileNameWithoutExtension(ReadString(path));
                return string.IsNullOrWhiteSpace(name) ? bundleId : name;
            }
            finally
            {
                CFRelease(path);
            }
        }
        finally
        {
            CFRelease(urls);
        }
    }

    private static string ReadString(nint handle)
    {
        var buffer = new byte[checked((int)CFStringGetLength(handle) * 4 + 1)];
        if (!CFStringGetCString(handle, buffer, buffer.Length, Utf8Encoding))
            throw new InvalidOperationException("无法读取 macOS 文件关联信息。");
        var length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("文件关联管理仅支持 macOS。");
    }

    private sealed class CfString : IDisposable
    {
        public CfString(string value)
        {
            Handle = CFStringCreateWithCString(0, value, Utf8Encoding);
            if (Handle == 0) throw new OutOfMemoryException();
        }

        public nint Handle { get; }
        public void Dispose() => CFRelease(Handle);
    }

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(nint value);
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(nint value, [Out] byte[] buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);
    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CoreFoundation)]
    private static extern nint CFURLCreateWithFileSystemPath(nint allocator, nint filePath, int pathStyle, [MarshalAs(UnmanagedType.I1)] bool isDirectory);
    [DllImport(CoreFoundation)]
    private static extern nint CFURLCopyFileSystemPath(nint url, int pathStyle);
    [DllImport(CoreServices)]
    private static extern nint UTTypeCreatePreferredIdentifierForTag(nint tagClass, nint tag, nint conformingTo);
    [DllImport(CoreServices)]
    private static extern nint LSCopyDefaultRoleHandlerForContentType(nint contentType, uint roles);
    [DllImport(CoreServices)]
    private static extern nint LSCopyApplicationURLsForBundleIdentifier(nint bundleIdentifier, nint error);
    [DllImport(CoreServices)]
    private static extern int LSRegisterURL(nint url, [MarshalAs(UnmanagedType.I1)] bool update);
    [DllImport(CoreServices)]
    private static extern int LSSetDefaultRoleHandlerForContentType(nint contentType, uint roles, nint handlerBundleIdentifier);
}
