# Avalonia Android NativeAOT build spike

This project validates the managed/Avalonia mobile shell with .NET 10 Android NativeAOT for both supported CanX ABIs. It does not yet contain Android builds of libmpv, libaria2 or QuickJS, and this Linux-hosted spike does not claim a device launch test.

The repository-local workload, Android SDK, NDK and JDK are prepared under `.cache`; nothing is installed into the system .NET SDK. Run:

```sh
scripts/verify-android-aot.sh
```

The script installs a repository-local .NET SDK, Android workload, SDK, NDK and JDK, then performs clean builds of separate `android-arm64` and `android-x64` APKs. It verifies the NativeAOT runtime bootstrap class, application ELF architecture, expected APK ABI layout, and absence of packaged managed assemblies.

.NET 10 currently reports Android NativeAOT as experimental (`XA1040`). `Mono.Android` also emits the assembly-level `IL3053` warning, which this spike permits without disabling other warnings-as-errors. The preparation script repairs NDK symbolic-link stubs produced as plain files by the current `InstallAndroidDependencies` task on Linux. `android-clang-wrapper.sh` works around the matching NativeAOT LLD version probe, which NDK Clang rejects because the probe contains no input file.

The stock `AvaloniaMainActivity` creates its `AvaloniaView` before calling Android's `Activity.OnCreate`. That ordering deadlocks before window attachment on the tested Android 16 device. The spike uses a minimal activity that initializes the Android lifecycle first and then attaches the Avalonia view. A cold-start device check completed in 408 ms with the activity resumed and its first frame drawn.
