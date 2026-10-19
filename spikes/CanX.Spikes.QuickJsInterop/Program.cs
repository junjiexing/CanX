using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CanX.Plugins.Manifest;
using CanX.Plugins.Packaging;
using CanX.Plugins.Runtime;
using CanX.Plugins.Sdk;
using CanX.Plugins.Services;

namespace CanX.Spikes.QuickJsInterop;

internal static class Program
{
    public static async Task<int> Main()
    {
        var result = RunNativeInterop();
        await RunProductionHost();
        var isolation = await RunProductionHostIsolation();
        Console.WriteLine(
            $"CANX_QUICKJS_OK jobs={result.ExecutedJobs} result=42 " +
            $"exception={Sanitize(result.ExceptionError)} " +
            $"timeout={Sanitize(result.TimeoutError)} memory={Sanitize(result.MemoryError)} " +
            $"recovery={result.RecoveryValue} host=ok isolation={isolation}");
        return 0;
    }

    private static unsafe NativeInteropResult RunNativeInterop()
    {
        nint runtime = 0;
        var callbackState = new CallbackState();
        var callbackHandle = GCHandle.Alloc(callbackState);

        try
        {
            Ensure(QuickJsNative.AbiVersion() == 2 ? 0 : -1, runtime, "ABI version");
            Ensure(
                QuickJsNative.Create(
                    16 * 1024 * 1024,
                    512 * 1024,
                    (nint)(delegate* unmanaged[Cdecl]<nint, ulong, nint, nint, void>)&OnToolRequest,
                    GCHandle.ToIntPtr(callbackHandle),
                    &runtime),
                runtime,
                "runtime creation");

            const string pluginModule = """
                import { add } from "./math.mjs";
                export const pluginName = "stage-zero";
                globalThis.pluginState = "waiting";
                canx.invokeTool("math.add", JSON.stringify({ left: add(10, 9), right: 23 }))
                  .then(result => {
                    globalThis.pluginState = String(JSON.parse(result).value);
                  });
                """;
            Ensure(
                QuickJsNative.AddModule(
                    runtime,
                    "math.mjs",
                    "export const add = (left, right) => left + right;"),
                runtime,
                "module registration");
            Ensure(QuickJsNative.Eval(runtime, pluginModule, "plugin.mjs", 1, 500), runtime, "module eval");

            if (!callbackState.Requests.TryDequeue(out var request))
            {
                throw new InvalidOperationException("tool callback was not delivered");
            }
            if (request.ToolName != "math.add" || !request.InputJson.Contains("19", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("tool callback payload was corrupted");
            }

            Ensure(QuickJsNative.ResolveTool(runtime, request.RequestId, "{\"value\":42}", 0), runtime, "tool resolve");
            var executedJobs = 0;
            Ensure(QuickJsNative.PumpJobs(runtime, 32, 500, &executedJobs), runtime, "job pump");
            if (executedJobs == 0 || GetGlobalString(runtime, "pluginState") != "42")
            {
                throw new InvalidOperationException("Promise continuation did not update plugin state");
            }

            var exceptionResult = QuickJsNative.Eval(
                runtime,
                "throw new Error('intentional-native-exception');",
                "exception-test.js",
                0,
                500);
            if (exceptionResult >= 0)
            {
                throw new InvalidOperationException("JavaScript exception escaped native error handling");
            }
            var exceptionError = QuickJsNative.GetLastError(runtime);
            if (!exceptionError.Contains("intentional-native-exception", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("JavaScript exception details were not preserved");
            }

            var timeoutResult = QuickJsNative.Eval(
                runtime,
                "while (true) {}",
                "timeout-test.js",
                0,
                25);
            if (timeoutResult >= 0)
            {
                throw new InvalidOperationException("execution deadline did not interrupt JavaScript");
            }
            var timeoutError = QuickJsNative.GetLastError(runtime);

            var memoryResult = QuickJsNative.Eval(
                runtime,
                "globalThis.tooLarge = new ArrayBuffer(64 * 1024 * 1024);",
                "memory-test.js",
                0,
                500);
            if (memoryResult >= 0)
            {
                throw new InvalidOperationException("memory limit did not reject oversized allocation");
            }
            var memoryError = QuickJsNative.GetLastError(runtime);

            Ensure(
                QuickJsNative.Eval(
                    runtime,
                    "globalThis.recoveryValue = 6 * 7;",
                    "recovery-test.js",
                    0,
                    500),
                runtime,
                "runtime recovery");
            var recoveryValue = GetGlobalString(runtime, "recoveryValue");
            if (recoveryValue != "42")
            {
                throw new InvalidOperationException("runtime did not recover after interrupted and rejected scripts");
            }

            return new NativeInteropResult(
                executedJobs,
                exceptionError,
                timeoutError,
                memoryError,
                recoveryValue);
        }
        finally
        {
            if (runtime != 0)
            {
                QuickJsNative.Destroy(runtime);
            }
            callbackHandle.Free();
        }
    }

    private static async Task RunProductionHost()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CanX.QuickJsSpike", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string source = """
                import { value } from "./lib/value.mjs";
                if (value !== 42) throw new Error("module import failed");
                canx.tools.invoke("math.add", { left: 19, right: 23 });
                canx.services.register({
                  id: "smoke",
                  contract: "org.canx.test.search",
                  contractVersion: "1.0",
                  title: "Smoke Search",
                  audiences: ["plugin", "ai"],
                  inputSchema: { type: "object", required: ["query"] },
                  outputSchema: { type: "object", required: ["items"] },
                  invoke: request => ({
                    items: [{ id: "result", title: `Found ${request.query}` }]
                  })
                });
                """;
            File.WriteAllText(Path.Combine(directory, "main.mjs"), source, new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(directory, "lib"));
            File.WriteAllText(
                Path.Combine(directory, "lib", "value.mjs"),
                "export const value = 42;",
                new UTF8Encoding(false));
            var manifest = new PluginManifest(
                1,
                "org.canx.quickjs-spike",
                "QuickJS Spike",
                null,
                "1.0.0",
                new PluginApiVersion(2, 0),
                "main.mjs",
                [
                    new PluginPermission(PluginCapabilities.ToolsInvoke, ["math.add"]),
                    new PluginPermission(PluginCapabilities.ServicesProvide, ["org.canx.test.search"]),
                ]);
            var installation = new PluginInstallation(
                manifest,
                directory,
                new string('0', 64),
                DateTimeOffset.UtcNow);
            var dispatcher = new SpikeToolDispatcher();
            var services = new PluginServiceRegistry();
            await using var host = new QuickJsPluginHost(
                installation,
                dispatcher,
                serviceRegistry: services);
            await host.StartAsync();
            await dispatcher.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (var attempt = 0; services.Services.Count == 0 && attempt < 100; attempt++)
            {
                await Task.Delay(10);
            }
            var service = AssertSingle(services.Services);
            using var input = JsonDocument.Parse("{\"query\":\"movie\"}");
            var search = await services.InvokeAsync(
                service.Id,
                input.RootElement,
                PluginServiceCallContext.ForAi("quickjs-smoke"));
            if (search.GetProperty("items")[0].GetProperty("title").GetString() != "Found movie")
            {
                throw new InvalidOperationException("production service result was corrupted");
            }
            if (host.State != PluginHostState.Running)
            {
                throw new InvalidOperationException($"production host entered state {host.State}");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> RunProductionHostIsolation()
    {
        await RunFaultedStartScenario(
            "exception",
            "throw new Error('intentional-host-exception');");
        await RunFaultedStartScenario(
            "memory",
            "globalThis.tooLarge = new ArrayBuffer(64 * 1024 * 1024);");
        var deadlineMilliseconds = await RunFaultedStartScenario(
            "deadline",
            "while (true) {}",
            verifyCallerRemainsResponsive: true);
        await RunServiceIsolationScenario();
        return $"ok,deadline_ms={deadlineMilliseconds}";
    }

    private static async Task<long> RunFaultedStartScenario(
        string name,
        string source,
        bool verifyCallerRemainsResponsive = false)
    {
        var directory = CreatePluginDirectory(name, source);
        try
        {
            var installation = CreateInstallation(directory, $"org.canx.quickjs-{name}", []);
            var options = new PluginRuntimeOptions
            {
                ExecutionTimeout = TimeSpan.FromMilliseconds(
                    verifyCallerRemainsResponsive ? 200 : 50),
            };
            await using var host = new QuickJsPluginHost(
                installation,
                new SpikeToolDispatcher(),
                options);
            var dispatchStopwatch = Stopwatch.StartNew();
            var startTask = host.StartAsync();
            dispatchStopwatch.Stop();
            if (verifyCallerRemainsResponsive)
            {
                if (dispatchStopwatch.Elapsed > TimeSpan.FromMilliseconds(100))
                {
                    throw new InvalidOperationException(
                        $"plugin startup blocked its caller for {dispatchStopwatch.ElapsedMilliseconds} ms");
                }
                await Task.Delay(20).WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await startTask.WaitAsync(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException($"{name} plugin unexpectedly started");
            }
            catch (PluginRuntimeException)
            {
            }
            stopwatch.Stop();
            if (host.State != PluginHostState.Faulted)
            {
                throw new InvalidOperationException($"{name} plugin entered state {host.State}");
            }
            return stopwatch.ElapsedMilliseconds;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunServiceIsolationScenario()
    {
        const string source = """
            const register = (id, invoke) => canx.services.register({
              id,
              contract: "org.canx.test.isolation",
              contractVersion: "1.0",
              title: id,
              audiences: ["plugin", "ai"],
              inputSchema: { type: "object", required: ["query"] },
              outputSchema: { type: "object", required: ["items"] },
              invoke
            });
            register("deadline", () => { while (true) {} });
            register("exception", () => { throw new Error("intentional-service-exception"); });
            register("memory", () => new ArrayBuffer(64 * 1024 * 1024));
            register("healthy", request => ({ items: [{ id: "ok", title: `Recovered ${request.query}` }] }));
            """;
        var directory = CreatePluginDirectory("service-isolation", source);
        try
        {
            var installation = CreateInstallation(
                directory,
                "org.canx.quickjs-service-isolation",
                [new PluginPermission(PluginCapabilities.ServicesProvide, ["org.canx.test.isolation"])]);
            var services = new PluginServiceRegistry();
            var options = new PluginRuntimeOptions
            {
                ExecutionTimeout = TimeSpan.FromMilliseconds(50),
                ToolTimeout = TimeSpan.FromSeconds(1),
            };
            await using var host = new QuickJsPluginHost(
                installation,
                new SpikeToolDispatcher(),
                options,
                serviceRegistry: services);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForServices(services, 4);

            await ExpectServiceFailure(services, "deadline");
            await VerifyHealthyService(services, "deadline");
            await ExpectServiceFailure(services, "exception");
            await VerifyHealthyService(services, "exception");
            await ExpectServiceFailure(services, "memory");
            await VerifyHealthyService(services, "memory");
            if (host.State != PluginHostState.Running)
            {
                throw new InvalidOperationException($"isolated service failures entered host state {host.State}");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ExpectServiceFailure(PluginServiceRegistry services, string localId)
    {
        try
        {
            using var input = JsonDocument.Parse("{\"query\":\"test\"}");
            await services.InvokeAsync(
                    $"org.canx.quickjs-service-isolation/{localId}",
                    input.RootElement,
                    PluginServiceCallContext.ForAi("quickjs-smoke"))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException($"{localId} service unexpectedly succeeded");
        }
        catch (Exception exception) when (exception is PluginRuntimeException or PluginServiceException)
        {
        }
    }

    private static async Task VerifyHealthyService(PluginServiceRegistry services, string after)
    {
        using var input = JsonDocument.Parse($"{{\"query\":\"{after}\"}}");
        var result = await services.InvokeAsync(
                "org.canx.quickjs-service-isolation/healthy",
                input.RootElement,
                PluginServiceCallContext.ForAi("quickjs-smoke"))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        if (result.GetProperty("items")[0].GetProperty("title").GetString() != $"Recovered {after}")
        {
            throw new InvalidOperationException($"service runtime did not recover after {after}");
        }
    }

    private static async Task WaitForServices(PluginServiceRegistry services, int count)
    {
        for (var attempt = 0; services.Services.Count < count && attempt < 100; attempt++)
        {
            await Task.Delay(10);
        }
        if (services.Services.Count != count)
        {
            throw new InvalidOperationException(
                $"Expected {count} services, got {services.Services.Count}.");
        }
    }

    private static string CreatePluginDirectory(string name, string source)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "CanX.QuickJsSpike",
            $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "main.mjs"), source, new UTF8Encoding(false));
        return directory;
    }

    private static PluginInstallation CreateInstallation(
        string directory,
        string id,
        IReadOnlyList<PluginPermission> permissions) =>
        new(
            new PluginManifest(
                1,
                id,
                id,
                null,
                "1.0.0",
                new PluginApiVersion(2, 0),
                "main.mjs",
                permissions),
            directory,
            new string('0', 64),
            DateTimeOffset.UtcNow);

    private static T AssertSingle<T>(IReadOnlyList<T> values) =>
        values.Count == 1
            ? values[0]
            : throw new InvalidOperationException($"Expected one item, got {values.Count}.");

    private static unsafe string GetGlobalString(nint runtime, string propertyName)
    {
        var buffer = stackalloc byte[256];
        nuint requiredSize = 0;
        Ensure(
            QuickJsNative.GetGlobalString(runtime, propertyName, buffer, 256, &requiredSize),
            runtime,
            "read global string");
        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buffer, checked((int)requiredSize - 1)));
    }

    private static string Sanitize(string value) => value.Replace(' ', '_');

    private static void Ensure(int result, nint runtime, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"{operation} failed ({result}): {QuickJsNative.GetLastError(runtime)}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnToolRequest(
        nint context,
        ulong requestId,
        nint toolName,
        nint inputJson)
    {
        try
        {
            var state = (CallbackState?)GCHandle.FromIntPtr(context).Target;
            var tool = Marshal.PtrToStringUTF8(toolName);
            var input = Marshal.PtrToStringUTF8(inputJson);
            if (state is not null && tool is not null && input is not null)
            {
                state.Requests.Enqueue(new ToolRequest(requestId, tool, input));
            }
        }
        catch
        {
            // Exceptions must never cross the C callback boundary.
        }
    }

    private sealed class CallbackState
    {
        public ConcurrentQueue<ToolRequest> Requests { get; } = new();
    }

    private sealed record ToolRequest(ulong RequestId, string ToolName, string InputJson);

    private sealed record NativeInteropResult(
        int ExecutedJobs,
        string ExceptionError,
        string TimeoutError,
        string MemoryError,
        string RecoveryValue);

    private sealed class SpikeToolDispatcher : IPluginToolDispatcher
    {
        public TaskCompletionSource Called { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<PluginToolDispatchResult> InvokeAsync(
            PluginToolRequest request,
            CancellationToken cancellationToken)
        {
            if (request.ToolName != "math.add" || !request.InputJson.Contains("19", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("production host Tool payload was corrupted");
            }

            Called.TrySetResult();
            return ValueTask.FromResult(PluginToolDispatchResult.Success("{\"value\":42}"));
        }
    }
}
