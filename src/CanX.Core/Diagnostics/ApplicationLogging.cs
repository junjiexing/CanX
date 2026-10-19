// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Core.Diagnostics;

public enum LogComponent
{
    App,
    Aria2,
    Mpv,
    QuickJs,
}

public enum ApplicationLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

public interface IApplicationLog : IDisposable
{
    bool IsEnabled { get; }

    string DirectoryPath { get; }

    event Action<bool>? EnabledChanged;

    string GetFilePath(LogComponent component);

    void SetEnabled(bool enabled);

    void Write(
        LogComponent component,
        ApplicationLogLevel level,
        string message,
        Exception? exception = null);
}

public sealed class NullApplicationLog : IApplicationLog
{
    public static NullApplicationLog Instance { get; } = new();

    private NullApplicationLog()
    {
    }

    public bool IsEnabled => false;

    public string DirectoryPath => string.Empty;

    public event Action<bool>? EnabledChanged
    {
        add { }
        remove { }
    }

    public string GetFilePath(LogComponent component) => string.Empty;

    public void SetEnabled(bool enabled)
    {
    }

    public void Write(
        LogComponent component,
        ApplicationLogLevel level,
        string message,
        Exception? exception = null)
    {
    }

    public void Dispose()
    {
    }
}
