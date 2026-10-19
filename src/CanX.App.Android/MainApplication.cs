// SPDX-License-Identifier: GPL-3.0-or-later

using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace CanX.App.Android;

[Application]
public sealed class MainApplication : AvaloniaAndroidApplication<App>
{
    private string? _runtimeFailureLogPath;

    public MainApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        _runtimeFailureLogPath = Path.Combine(
            FilesDir?.AbsolutePath ?? CacheDir?.AbsolutePath ?? "/data/local/tmp",
            "canx",
            "logs",
            "android-runtime.log");
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AndroidEnvironment.UnhandledExceptionRaiser += OnAndroidUnhandledException;
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder);

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs) =>
        WriteRuntimeFailure("AppDomain", eventArgs.ExceptionObject);

    private void OnAndroidUnhandledException(object? sender, RaiseThrowableEventArgs eventArgs) =>
        WriteRuntimeFailure("Android", eventArgs.Exception);

    private void WriteRuntimeFailure(string source, object? failure)
    {
        try
        {
            var text = $"{DateTimeOffset.UtcNow:O} [{source}] {failure}{Environment.NewLine}";
            global::Android.Util.Log.Error("CanX", text);
            var path = _runtimeFailureLogPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, text);
        }
        catch
        {
        }
    }
}
