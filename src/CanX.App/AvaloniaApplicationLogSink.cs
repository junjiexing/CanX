// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Logging;
using CanX.Core.Diagnostics;

namespace CanX.App;

internal sealed class AvaloniaApplicationLogSink(bool writeToStandardError) : ILogSink
{
    public bool IsEnabled(LogEventLevel level, string area) =>
        writeToStandardError ||
        ApplicationLogHost.Current?.IsEnabled == true && level >= LogEventLevel.Information;

    public void Log(
        LogEventLevel level,
        string area,
        object? source,
        string messageTemplate) =>
        Write(level, area, source, messageTemplate, []);

    public void Log(
        LogEventLevel level,
        string area,
        object? source,
        string messageTemplate,
        params object?[] propertyValues) =>
        Write(level, area, source, messageTemplate, propertyValues);

    private void Write(
        LogEventLevel level,
        string area,
        object? source,
        string messageTemplate,
        IReadOnlyList<object?> propertyValues)
    {
        var sourceName = source?.GetType().Name ?? "unknown";
        var values = propertyValues.Count == 0
            ? string.Empty
            : $" | values={string.Join(", ", propertyValues.Select(FormatValue))}";
        var message = $"[Avalonia/{area}/{sourceName}] {messageTemplate}{values}";
        if (writeToStandardError)
        {
            Console.Error.WriteLine(message);
        }

        ApplicationLogHost.Current?.Write(LogComponent.App, MapLevel(level), message);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static ApplicationLogLevel MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => ApplicationLogLevel.Trace,
        LogEventLevel.Debug => ApplicationLogLevel.Debug,
        LogEventLevel.Information => ApplicationLogLevel.Information,
        LogEventLevel.Warning => ApplicationLogLevel.Warning,
        LogEventLevel.Error => ApplicationLogLevel.Error,
        LogEventLevel.Fatal => ApplicationLogLevel.Critical,
        _ => ApplicationLogLevel.Information,
    };
}
