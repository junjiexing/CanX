# CanX libaria2 C ABI shim

libaria2 exposes a C++ API, which is unsuitable as a stable cross-language ABI. This library catches C++ exceptions and exposes versioned, fixed-layout C data to NativeAOT.

ABI 6 covers lifecycle, event callbacks, URI/Torrent/Metalink creation, polling,
status, queue control, runtime/global options, capability metadata, peer/server
details, stopped-result removal, runtime piece-range priority/clear and shutdown. It
also accepts several mirror URIs as one aria2 task. STL types and C++ object
ownership never cross the ABI.

Rules established by the spike:

- one libaria2 session and one dedicated engine thread per process;
- all callbacks are notifications only and must be queued before invoking other aria2 functions;
- call `shutdown`, continue `run_once` until it returns zero, then destroy the session and deinitialize the library;
- add fields through versioned structs/functions; never change an existing field's size or meaning.

The default build remains `SHARED` for the distro-libaria2 compatibility smoke
test. That mode implements the ABI 6 baseline and returns `extendedApi=false`;
peer/server metadata is reported as unsupported and the option manifest is
empty; runtime piece priority is also unsupported. `scripts/build-dev-native-linux-x64.sh` also supports a full development
sidecar: it enables the extended API and folds the pinned static libaria2
dependency closure into one `libcanx_aria2.so`, allowing `dotnet run` without
shipping the library in release packages. Release recipes set
`CANX_ARIA2_EXTENDED_API=ON` and `CANX_ARIA2_LIBRARY_TYPE=STATIC`, then directly
link `libcanx_aria2.a` into the NativeAOT executable. Static Windows consumers
must compile the header with `CANX_ARIA2_STATIC`, which CMake exports
automatically.

Every string or list returned by the shim has a matching free function. JSON is
used only as a length-explicit serialization boundary; callers must validate
`canx_aria2_abi_version()` before creating a session.
