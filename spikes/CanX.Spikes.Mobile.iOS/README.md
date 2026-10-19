# Avalonia iOS NativeAOT build spike

This project is the Apple-hosted counterpart to the Android stage-0 spike. It targets an ARM64 iOS device plus ARM64 and x64 simulators with .NET 10 NativeAOT and Avalonia 12.1.2. It does not yet link iOS builds of libmpv, libaria2 or QuickJS.

`IosDisplaySleepInhibitor.AcquireAsync` is the native display-sleep adapter for a
future playback host. Wire it to `PlaybackViewModel.ConfigureDisplaySleepInhibition`
when playback is added. Its leases survive background transitions, but iOS does
not let an ordinary background application keep other applications' screens awake.
See [the playback power policy](../../docs/PLAYBACK-POWER.zh-CN.md).

Run on a supported macOS/Xcode host:

```sh
scripts/verify-ios-aot.sh
```

The script installs the repository-local .NET SDK and iOS workload, builds unsigned `.app` bundles, and verifies each Mach-O architecture and the absence of packaged managed assemblies. A signing identity and provisioning profile are intentionally not required for this link-only spike; installing the device bundle is a separate gate.

Any host can validate the checked-in scaffold without claiming an Apple build:

```sh
scripts/verify-ios-aot.sh --static
```

The iOS project is intentionally not part of `CanX.slnx`, so Linux solution operations do not demand the Apple-only workload. Running the verification script on a non-macOS host exits with code 2 and reports the external Xcode gate. The current Linux development host can validate these source files, but cannot honestly mark the native link/build as passed.
