// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using CanX.Tooling;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class ToolSecurityTests
{
    [Fact]
    public async Task Registry_RejectsPatternPropertyCountAndExtremeNumbersWithoutInvokingTool()
    {
        var handler = new StrictTool();
        var registry = new ToolRegistry(new DefaultToolAuthorizer(), new InMemoryToolAuditSink());
        registry.ReplaceHandlers([handler]);
        var context = ToolCallContext.LocalUser;

        var invalidPattern = await registry.InvokeAsync(
            "test.strict",
            new JsonObject { ["id"] = "../../escape", ["value"] = 1 },
            context);
        var tooMany = await registry.InvokeAsync(
            "test.strict",
            new JsonObject { ["id"] = "safe-id", ["value"] = 1, ["extra"] = true },
            context);
        using var hugeDocument = System.Text.Json.JsonDocument.Parse("{\"id\":\"safe-id\",\"value\":1e300}");
        var huge = await registry.InvokeAsync(
            "test.strict",
            JsonNode.Parse(hugeDocument.RootElement.GetRawText()) as JsonObject,
            context);

        Assert.Equal("invalid_arguments", invalidPattern.ErrorCode);
        Assert.Equal("invalid_arguments", tooMany.ErrorCode);
        Assert.Equal("invalid_arguments", huge.ErrorCode);
        Assert.Equal(0, handler.InvocationCount);
    }

    [Fact]
    public void OutputSanitizer_RemovesCredentialFieldsAndInlineSecretsButKeepsOpaqueReviewToken()
    {
        var source = new JsonObject
        {
            ["password"] = "hunter2",
            ["api_key"] = "company-key",
            ["reviewToken"] = "opaque-review-reference",
            ["message"] = "Authorization: Bearer abc123 https://user:pass@example.test/a?api_key=query-secret&item=1",
            ["nested"] = new JsonArray(new JsonObject { ["clientSecret"] = "nested-secret" }),
        };

        var result = Assert.IsType<JsonObject>(ToolOutputSanitizer.Sanitize(source));
        var serialized = result.ToJsonString();

        Assert.DoesNotContain("hunter2", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("company-key", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-secret", serialized, StringComparison.Ordinal);
        Assert.Equal("opaque-review-reference", result["reviewToken"]?.GetValue<string>());

        var failure = ToolOutputSanitizer.Sanitize(ToolResult.Failure(
            "remote_error",
            "Authorization: Bearer failure-secret"));
        Assert.DoesNotContain("failure-secret", failure.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class StrictTool : IToolHandler
    {
        public int InvocationCount { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            "test.strict",
            "Strict test tool.",
            ToolRisk.LocalWrite,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("id", "value"),
                ["additionalProperties"] = true,
                ["maxProperties"] = 2,
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["pattern"] = "^[a-z-]+$",
                    },
                    ["value"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["minimum"] = 0,
                        ["maximum"] = 10,
                    },
                },
            },
            new JsonObject { ["type"] = "object" });

        public ValueTask<ToolResult> ExecuteAsync(
            JsonObject arguments,
            ToolCallContext context,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return ValueTask.FromResult(ToolResult.Success());
        }
    }
}
