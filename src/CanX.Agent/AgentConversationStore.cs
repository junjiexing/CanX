// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using CanX.Core.Security;

namespace CanX.Agent;

public enum AgentConversationDisplayKind
{
    User,
    Assistant,
    Tool,
}

public sealed record AgentConversationDisplayItem
{
    public required AgentConversationDisplayKind Kind { get; init; }

    public required string Content { get; init; }
}

public sealed record AgentConversation
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Id { get; init; }

    public required string Title { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<AgentMessage> Messages { get; init; } = [];

    public IReadOnlyList<AgentConversationDisplayItem> DisplayItems { get; init; } = [];

    public string? ContextSummary { get; init; }

    public int SummarizedMessageCount { get; init; }

    public int ContextTokensUsed { get; init; }

    public bool ContextUsageEstimated { get; init; }

    public string? ContextModel { get; init; }
}

public interface IAgentConversationStore
{
    ValueTask<IReadOnlyList<AgentConversation>> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(IReadOnlyList<AgentConversation> conversations, CancellationToken cancellationToken = default);
}

public sealed class JsonAgentConversationStore(string path) : IAgentConversationStore
{
    private const long MaximumStoreBytes = 128 * 1024 * 1024;
    private readonly string _path = PrivateFileSystem.PreparePrivateFilePath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<IReadOnlyList<AgentConversation>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return [];
            if (new FileInfo(_path).Length > MaximumStoreBytes)
            {
                throw new InvalidDataException("Agent conversation store exceeds the size limit.");
            }
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync(
                stream,
                AgentJsonContext.Default.ListAgentConversation,
                cancellationToken).ConfigureAwait(false) ?? [];
            if (document.Any(static item => item.SchemaVersion != AgentConversation.CurrentSchemaVersion))
            {
                throw new InvalidDataException("Unsupported Agent conversation schema version.");
            }
            Validate(document);
            return document
                .OrderByDescending(static item => item.UpdatedAt)
                .Take(100)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        IReadOnlyList<AgentConversation> conversations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        Validate(conversations);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PrivateFileSystem.EnsurePrivateParentDirectory(_path);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    PrivateFileSystem.HardenFile(temporaryPath);
                    await JsonSerializer.SerializeAsync(
                        stream,
                        conversations.OrderByDescending(static item => item.UpdatedAt).Take(100).ToList(),
                        AgentJsonContext.Default.ListAgentConversation,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporaryPath, _path, overwrite: true);
                PrivateFileSystem.HardenFile(_path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Validate(IReadOnlyList<AgentConversation> conversations)
    {
        if (conversations.Count > 100) throw new ArgumentException("At most 100 Agent conversations may be persisted.");
        foreach (var conversation in conversations)
        {
            var displayItems = conversation.DisplayItems ?? [];
            if (string.IsNullOrWhiteSpace(conversation.Id) || conversation.Id.Length > 128 ||
                string.IsNullOrWhiteSpace(conversation.Title) || conversation.Title.Length > 200 ||
                conversation.Messages.Count > 10_000 ||
                displayItems.Count > 30_000 ||
                conversation.SummarizedMessageCount < 0 ||
                conversation.SummarizedMessageCount > conversation.Messages.Count ||
                conversation.ContextTokensUsed < 0 ||
                conversation.ContextSummary?.Length > 64_000 ||
                conversation.ContextModel?.Length > 200 ||
                displayItems.Any(static item =>
                    item is null || !Enum.IsDefined(item.Kind) || item.Content is null || item.Content.Length > 4_000_000) ||
                conversation.Messages.Any(static message =>
                    message.Role is not (AgentMessageRole.User or AgentMessageRole.Assistant) ||
                    message.ToolCalls.Count != 0 || message.ToolCallId is not null))
            {
                throw new ArgumentException("Agent conversation exceeds persistence limits.");
            }
            var size = conversation.Messages.Sum(static message =>
                (long)(message.Content?.Length ?? 0) + message.ToolCalls.Sum(static call => call.ArgumentsJson.Length));
            size += displayItems.Sum(static item => (long)item.Content.Length);
            if (size > 32_000_000) throw new ArgumentException("Agent conversation exceeds the size limit.");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<AgentConversation>))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
