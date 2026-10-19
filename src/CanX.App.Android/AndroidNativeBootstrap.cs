// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Java.Interop;

namespace CanX.App.Android;

internal static partial class AndroidNativeBootstrap
{
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        JniEnvironment.References.GetJavaVM(out var javaVm);
        if (javaVm == 0)
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw new InvalidOperationException("Android Java VM is unavailable.");
        }

        var result = SetJavaVm(javaVm, 0);
        if (result < 0)
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw new InvalidOperationException($"FFmpeg Java VM registration failed ({result}).");
        }
    }

    [LibraryImport("avcodec", EntryPoint = "av_jni_set_java_vm")]
    private static partial int SetJavaVm(nint javaVm, nint logContext);
}
