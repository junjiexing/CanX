# libaria2 NativeAOT interop spike

This console spike exercises the C ABI shim from a .NET 10 NativeAOT executable. It adds a URI, pauses it, observes the paused state, resumes it, reads progress, receives the native completion callback, shuts down cleanly and verifies the downloaded bytes.

Run from the repository root:

```sh
scripts/verify-aria2-shim.sh
```

Success is reported as `CANX_ARIA2_SHIM_OK`.
