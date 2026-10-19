// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CanX.Core.Diagnostics;
using CanX.Plugins.Manifest;
using CanX.Plugins.Security;

namespace CanX.Plugins.Services;

public sealed partial class PluginServiceRegistry
{
    private const int MaximumServicesPerPlugin = 32;
    private const int MaximumSchemaBytes = 64 * 1024;
    private const int MaximumPayloadBytes = 1024 * 1024;
    private const int MaximumCallDepth = 8;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Registration> registrations = new(StringComparer.Ordinal);
    private readonly IApplicationLog log;

    public PluginServiceRegistry(IApplicationLog? log = null)
    {
        this.log = log ?? NullApplicationLog.Instance;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<PluginServiceDescriptor> Services
    {
        get
        {
            lock (gate)
            {
                return registrations.Values
                    .Select(static registration => registration.Descriptor)
                    .OrderBy(static descriptor => descriptor.Contract, StringComparer.Ordinal)
                    .ThenBy(static descriptor => descriptor.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<PluginServiceDescriptor> Find(
        string? contract = null,
        int? majorVersion = null,
        PluginServiceAudience? audience = null)
    {
        if (majorVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(majorVersion));
        }
        return Services.Where(service =>
                (contract is null || string.Equals(service.Contract, contract, StringComparison.Ordinal)) &&
                (majorVersion is null || ParseMajor(service.ContractVersion) == majorVersion) &&
                (audience is null || service.Audiences.Contains(audience.Value)))
            .ToArray();
    }

    public PluginServiceDescriptor GetDescriptor(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        lock (gate)
        {
            return registrations.TryGetValue(serviceId, out var registration)
                ? registration.Descriptor
                : throw new PluginServiceException("service_not_found", $"Service '{serviceId}' is not registered.");
        }
    }

    public async ValueTask<JsonElement> InvokeAsync(
        string serviceId,
        JsonElement input,
        PluginServiceCallContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(context);
        var started = Stopwatch.GetTimestamp();
        PluginServiceDescriptor? descriptor = null;
        try
        {
            Registration registration;
            lock (gate)
            {
                registration = registrations.GetValueOrDefault(serviceId)
                    ?? throw new PluginServiceException("service_not_found", $"Service '{serviceId}' is not registered.");
            }
            descriptor = registration.Descriptor;
            if (context.PluginChain.Count >= MaximumCallDepth)
            {
                throw new PluginServiceException("service_call_depth_exceeded", "Service call depth exceeded the configured limit.");
            }
            if (context.PluginChain.Contains(descriptor.PluginId, StringComparer.Ordinal))
            {
                throw new PluginServiceException("service_call_cycle", $"Service call cycle detected at '{serviceId}'.");
            }

            var nextContext = context with
            {
                PluginChain = context.PluginChain.Append(descriptor.PluginId).ToArray(),
            };
            PluginJsonSchema.ValidateInstance(registration.Descriptor.InputSchema, input, "Service input");
            var resultJson = await registration.Invoke(input.GetRawText(), nextContext, cancellationToken)
                .ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(resultJson) > MaximumPayloadBytes)
            {
                throw new PluginServiceException("service_result_too_large", "Service result exceeds the configured limit.");
            }
            try
            {
                using var result = JsonDocument.Parse(resultJson);
                PluginJsonSchema.ValidateInstance(registration.Descriptor.OutputSchema, result.RootElement, "Service output");
                LogInvocation(serviceId, descriptor, context, started, success: true, errorCode: null, exception: null);
                return result.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new PluginServiceException("invalid_service_result", $"Service returned invalid JSON: {exception.Message}");
            }
        }
        catch (Exception exception)
        {
            var errorCode = exception switch
            {
                PluginServiceException serviceException => serviceException.Code,
                OperationCanceledException => "cancelled",
                _ => "service_failure",
            };
            LogInvocation(serviceId, descriptor, context, started, success: false, errorCode, exception);
            if (exception is PluginServiceException or OperationCanceledException)
            {
                throw;
            }
            throw new PluginServiceException("service_failure", "Service invocation failed.");
        }
    }

    public void WriteSchemaSnapshot(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var services = Services.Select(DescriptorJson).ToArray();
        var snapshot = new JsonObject
        {
            ["event"] = "plugin.service.registry",
            ["reason"] = reason,
            ["services"] = new JsonArray(services),
        };
        log.Write(LogComponent.QuickJs, ApplicationLogLevel.Information, snapshot.ToJsonString());
    }

    internal IDisposable Register(
        string pluginId,
        string localId,
        string contract,
        string contractVersion,
        string title,
        string? description,
        PluginServiceRisk risk,
        IReadOnlyList<PluginServiceAudience> audiences,
        JsonElement inputSchema,
        JsonElement outputSchema,
        PluginPermissionSet permissions,
        Func<string, PluginServiceCallContext, CancellationToken, ValueTask<string>> invoke)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(audiences);
        ArgumentNullException.ThrowIfNull(invoke);
        if (!LocalIdPattern().IsMatch(localId))
        {
            throw Invalid("Service id must be a lowercase identifier of at most 64 characters.");
        }
        if (!ContractPattern().IsMatch(contract))
        {
            throw Invalid("Service contract must be a lowercase dotted identifier.");
        }
        if (!VersionPattern().IsMatch(contractVersion))
        {
            throw Invalid("Service contractVersion must use major.minor or major.minor.patch format.");
        }
        if (string.IsNullOrWhiteSpace(title) || title.Length > 100)
        {
            throw Invalid("Service title must be between 1 and 100 characters.");
        }
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (description?.Length > 1000)
        {
            throw Invalid("Service description cannot exceed 1000 characters.");
        }
        if (audiences.Count == 0 || audiences.Count != audiences.Distinct().Count())
        {
            throw Invalid("Service audiences must be a non-empty unique list.");
        }
        if (Encoding.UTF8.GetByteCount(inputSchema.GetRawText()) > MaximumSchemaBytes ||
            Encoding.UTF8.GetByteCount(outputSchema.GetRawText()) > MaximumSchemaBytes)
        {
            throw Invalid("Service schema exceeds the configured limit.");
        }

        permissions.DemandServiceContract(PluginCapabilities.ServicesProvide, contract);
        PluginJsonSchema.ValidateDefinition(inputSchema);
        PluginJsonSchema.ValidateDefinition(outputSchema);
        var id = $"{pluginId}/{localId}";
        var descriptor = new PluginServiceDescriptor(
            id,
            pluginId,
            localId,
            contract,
            contractVersion,
            title.Trim(),
            description,
            risk,
            audiences.ToArray(),
            inputSchema.Clone(),
            outputSchema.Clone());
        var registration = new Registration(descriptor, invoke);
        lock (gate)
        {
            if (registrations.Values.Count(item => item.Descriptor.PluginId == pluginId) >= MaximumServicesPerPlugin)
            {
                throw Invalid($"Plugin cannot register more than {MaximumServicesPerPlugin} services.");
            }
            if (!registrations.TryAdd(id, registration))
            {
                throw new PluginServiceException("service_exists", $"Service '{localId}' is already registered.");
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return new RegistrationLease(this, id, registration);
    }

    private void Unregister(string id, Registration registration)
    {
        var removed = false;
        lock (gate)
        {
            if (ReferenceEquals(registrations.GetValueOrDefault(id), registration))
            {
                registrations.Remove(id);
                removed = true;
            }
        }
        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void LogInvocation(
        string serviceId,
        PluginServiceDescriptor? descriptor,
        PluginServiceCallContext context,
        long started,
        bool success,
        string? errorCode,
        Exception? exception)
    {
        var entry = new JsonObject
        {
            ["event"] = "plugin.service.call",
            ["caller"] = context.Caller,
            ["target"] = serviceId,
            ["contract"] = descriptor?.Contract,
            ["durationMs"] = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3),
            ["success"] = success,
            ["errorCode"] = errorCode,
        };
        log.Write(
            LogComponent.QuickJs,
            success ? ApplicationLogLevel.Information : ApplicationLogLevel.Warning,
            entry.ToJsonString(),
            exception);
    }

    private static JsonObject DescriptorJson(PluginServiceDescriptor descriptor) => new()
    {
        ["id"] = descriptor.Id,
        ["pluginId"] = descriptor.PluginId,
        ["localId"] = descriptor.LocalId,
        ["contract"] = descriptor.Contract,
        ["contractVersion"] = descriptor.ContractVersion,
        ["title"] = descriptor.Title,
        ["description"] = descriptor.Description,
        ["risk"] = descriptor.Risk.ToString(),
        ["audiences"] = new JsonArray(descriptor.Audiences.Select(audience =>
            (JsonNode?)JsonValue.Create(audience.ToString())).ToArray()),
        ["inputSchema"] = JsonNode.Parse(descriptor.InputSchema.GetRawText()),
        ["outputSchema"] = JsonNode.Parse(descriptor.OutputSchema.GetRawText()),
    };

    private static int ParseMajor(string version) => int.Parse(
        version.AsSpan(0, version.IndexOf('.')),
        System.Globalization.CultureInfo.InvariantCulture);

    private static PluginServiceException Invalid(string message) => new("invalid_service", message);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalIdPattern();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62})(?:\\.[a-z0-9](?:[a-z0-9-]{0,62})){1,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContractPattern();

    [GeneratedRegex("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:\\.(0|[1-9]\\d*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private sealed record Registration(
        PluginServiceDescriptor Descriptor,
        Func<string, PluginServiceCallContext, CancellationToken, ValueTask<string>> Invoke);

    private sealed class RegistrationLease(
        PluginServiceRegistry owner,
        string id,
        Registration registration) : IDisposable
    {
        private PluginServiceRegistry? currentOwner = owner;

        public void Dispose() => Interlocked.Exchange(ref currentOwner, null)?.Unregister(id, registration);
    }
}
