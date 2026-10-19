// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Diagnostics;

namespace CanX.App;

internal static class ApplicationLogHost
{
    private static IApplicationLog? s_current;

    static ApplicationLogHost()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            var exception = eventArgs.ExceptionObject as Exception;
            Current?.Write(
                LogComponent.App,
                ApplicationLogLevel.Critical,
                "An unhandled application exception occurred.",
                exception);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Current?.Write(
                LogComponent.App,
                ApplicationLogLevel.Error,
                "An unobserved task exception occurred.",
                eventArgs.Exception);
        };
    }

    public static IApplicationLog? Current
    {
        get => Volatile.Read(ref s_current);
        set => Volatile.Write(ref s_current, value);
    }
}
