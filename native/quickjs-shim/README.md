# CanX QuickJS host shim

This C ABI keeps `JSRuntime`, `JSContext` and `JSValue` out of managed code. It establishes the minimum plugin-host boundary required by the stage-0 NativeAOT spike:

- one isolated runtime/context with explicit memory and stack limits;
- deadline-based interrupt handling;
- ES module evaluation through a host-populated in-memory module map;
- tracking of module-evaluation promises so top-level exceptions are reported;
- Promise-based Tool requests that are notified to the host and resolved later;
- explicit pending-job pumping and copied UTF-8 results.

QuickJS callbacks only copy/enqueue data and never synchronously re-enter the same runtime. The production managed host keeps each runtime on a dedicated thread, confines all JavaScript modules to the installed plugin root, preloads them into the native module map, validates JSON Tool messages, and enforces capability scopes. A richer structured-clone bridge remains follow-up work.

The default build remains `SHARED` for the stage-0 dynamic-link smoke test. Release
recipes set `-DCANX_QUICKJS_LIBRARY_TYPE=STATIC`; this embeds QuickJS and the C
ABI shim into `libcanx_quickjs.a` for NativeAOT direct linking. Static Windows
builds use Visual Studio ClangCL with the static CRT and produce
`canx_quickjs.lib`. Consumers compile the header with `CANX_QUICKJS_STATIC`,
which CMake exports automatically.

`scripts/verify-quickjs-host-windows-x64.ps1` exercises the statically linked
NativeAOT production host as well as the raw ABI. It verifies startup and
provider-handler exceptions, memory exhaustion, execution deadlines, caller
responsiveness, and successful use of the same runtime after an isolated
provider failure.
