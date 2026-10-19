// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Tooling;

public sealed class DefaultToolAuthorizer : IToolAuthorizer
{
    public ValueTask<bool> IsAllowedAsync(
        ToolDescriptor descriptor,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var grants = context.Grants;
        var explicitlyGranted = grants?.Contains(descriptor.Name) == true ||
            grants?.Contains($"{descriptor.Name}:*") == true ||
            grants?.Contains($"risk:{descriptor.Risk.ToString().ToLowerInvariant()}") == true;
        return ValueTask.FromResult(
            descriptor.Risk is ToolRisk.ReadOnly || context.HasUserApproval || explicitlyGranted);
    }
}
