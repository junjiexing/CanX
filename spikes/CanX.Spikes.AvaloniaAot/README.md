# Avalonia NativeAOT spike

This spike verifies that the pinned Avalonia release can be compiled as a .NET 10 NativeAOT desktop executable and initialize a real Linux window backend.

Run the repeatable smoke test from the repository root:

```sh
dotnet publish spikes/CanX.Spikes.AvaloniaAot -c Release -r linux-x64 -o artifacts/spikes/avalonia-aot
artifacts/spikes/avalonia-aot/CanX.Spikes.AvaloniaAot --smoke
```

Success is reported as `CANX_AVALONIA_AOT_OK`; `backend=Wayland` confirms the native Wayland path and `backend=XID` confirms the X11 fallback. Avalonia's Wayland backend intentionally does not expose a platform window handle, so the spike identifies that path by the missing handle on Linux. The command must be run inside a graphical session with a working Wayland or X11 display.
