// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.RegularExpressions;
using CanX.Core.Diagnostics;
using CanX.Core.Security;

namespace CanX.Infrastructure.Diagnostics;

public sealed partial class FileApplicationLog : IApplicationLog
{
    public const long DefaultMaximumFileSize = 10 * 1024 * 1024;

    private readonly Lock _sync = new();
    private readonly Dictionary<LogComponent, StreamWriter> _writers = [];
    private readonly long _maximumFileSize;
    private bool _enabled;
    private bool _disposed;

    public FileApplicationLog(string directoryPath, long maximumFileSize = DefaultMaximumFileSize)
    {
        DirectoryPath = Path.GetFullPath(
            directoryPath ?? throw new ArgumentNullException(nameof(directoryPath)));
        if (maximumFileSize < 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFileSize),
                "The maximum log file size must be at least 1024 bytes.");
        }

        _maximumFileSize = maximumFileSize;
    }

    public bool IsEnabled
    {
        get
        {
            lock (_sync)
            {
                return _enabled && !_disposed;
            }
        }
    }

    public string DirectoryPath { get; }

    public event Action<bool>? EnabledChanged;

    public string GetFilePath(LogComponent component) =>
        Path.Combine(DirectoryPath, component switch
        {
            LogComponent.App => "app.log",
            LogComponent.Aria2 => "aria2.log",
            LogComponent.Mpv => "mpv.log",
            LogComponent.QuickJs => "quickjs.log",
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        });

    public void SetEnabled(bool enabled)
    {
        var changed = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled == enabled)
            {
                return;
            }

            if (enabled)
            {
                PrivateFileSystem.EnsurePrivateDirectory(DirectoryPath);
                _enabled = true;
                WriteCore(
                    LogComponent.App,
                    ApplicationLogLevel.Information,
                    "Logging enabled.",
                    exception: null);
            }
            else
            {
                WriteCore(
                    LogComponent.App,
                    ApplicationLogLevel.Information,
                    "Logging disabled.",
                    exception: null);
                _enabled = false;
                CloseWriters();
            }

            changed = true;
        }

        if (changed)
        {
            EnabledChanged?.Invoke(enabled);
        }
    }

    public void Write(
        LogComponent component,
        ApplicationLogLevel level,
        string message,
        Exception? exception = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_sync)
        {
            if (!_enabled || _disposed)
            {
                return;
            }

            WriteCore(component, level, message, exception);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_enabled)
            {
                WriteCore(
                    LogComponent.App,
                    ApplicationLogLevel.Information,
                    "Logging stopped because the application is exiting.",
                    exception: null);
            }

            _enabled = false;
            _disposed = true;
            CloseWriters();
        }
    }

    private void WriteCore(
        LogComponent component,
        ApplicationLogLevel level,
        string message,
        Exception? exception)
    {
        try
        {
            var sanitizedMessage = Sanitize(message);
            var sanitizedException = exception is null ? null : Sanitize(exception.ToString());
            var line =
                $"[{DateTimeOffset.UtcNow:O}] [{level.ToString().ToUpperInvariant()}] {sanitizedMessage}" +
                (sanitizedException is null ? string.Empty : $" | {sanitizedException}");
            var writer = GetWriter(component, Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
            writer.WriteLine(line);
            if (OperatingSystem.IsWindows())
            {
                // A reader opened through File.ReadAllText/File.ReadLines does not
                // share write access on Windows, even when our writer shares read
                // access. Release the append handle after each complete line so
                // logs remain readable while logging is enabled.
                CloseWriter(component);
            }
        }
        catch (Exception)
        {
            CloseWriter(component);
        }
    }

    private StreamWriter GetWriter(LogComponent component, int incomingByteCount)
    {
        if (_writers.TryGetValue(component, out var writer))
        {
            if (writer.BaseStream.Length + incomingByteCount < _maximumFileSize)
            {
                return writer;
            }

            CloseWriter(component);
        }

        var path = GetFilePath(component);
        RotateIfNecessary(path, incomingByteCount);
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        PrivateFileSystem.HardenFile(path);
        writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        _writers.Add(component, writer);
        return writer;
    }

    private void RotateIfNecessary(string path, int incomingByteCount)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingByteCount < _maximumFileSize)
        {
            return;
        }

        var backupPath = path + ".1";
        File.Move(path, backupPath, overwrite: true);
    }

    private void CloseWriter(LogComponent component)
    {
        if (_writers.Remove(component, out var writer))
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception)
            {
                // Logging failures must not break the application path being diagnosed.
            }
        }
    }

    private void CloseWriters()
    {
        foreach (var writer in _writers.Values)
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception)
            {
                // Continue closing the other component files.
            }
        }

        _writers.Clear();
    }

    internal static string Sanitize(string value)
    {
        var sanitized = UriUserInfoRegex().Replace(value, "${scheme}<redacted>@");
        sanitized = AuthorizationRegex().Replace(sanitized, "${name}${separator}<redacted>");
        sanitized = SensitiveValueRegex().Replace(sanitized, "${name}${separator}<redacted>");
        sanitized = UrlQueryRegex().Replace(sanitized, "${prefix}?<redacted>${fragment}");
        return sanitized
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    [GeneratedRegex(
        @"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^\s/@:]+(?::[^\s/@]*)?@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfoRegex();

    [GeneratedRegex(
        @"(?<name>\bAuthorization)(?<separator>\s*[:=]\s*)[^\s,;]+(?:\s+[^\s,;]+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(
        @"(?<name>\b(?:password|passwd|token|access_token|refresh_token|secret|cookie|set-cookie|api[-_]?key))(?<separator>\s*[:=]\s*)[^\s,;&]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveValueRegex();

    [GeneratedRegex(
        @"(?<prefix>\b[a-z][a-z0-9+.-]*://[^\s?#]*)\?[^\s#]*(?<fragment>#[^\s]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlQueryRegex();
}
