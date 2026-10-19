// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Tooling;

public sealed class InMemoryToolAuditSink : IToolAuditSink
{
    private readonly Lock _lock = new();
    private readonly List<ToolAuditEntry> _entries = [];

    public IReadOnlyList<ToolAuditEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public ValueTask WriteAsync(ToolAuditEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _entries.Add(entry);
        }

        return ValueTask.CompletedTask;
    }
}
