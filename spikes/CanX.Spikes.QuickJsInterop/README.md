# QuickJS NativeAOT plugin-host spike

This spike embeds QuickJS behind a small C ABI and verifies from a .NET 10 NativeAOT executable:

- ES module evaluation;
- a Promise-returning `canx.invokeTool()` API;
- unmanaged notification into .NET followed by later Promise resolution;
- explicit pending-job pumping;
- memory, stack and execution-time limits;
- safe runtime teardown.

Run `scripts/verify-quickjs-shim.sh`. Success is reported as `CANX_QUICKJS_OK`.
