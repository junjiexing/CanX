// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Avalonia.Threading;
using CanX.Agent;

namespace CanX.App.ViewModels;

public sealed class AgentApprovalService : IAgentToolApprovalService, INotifyPropertyChanged
{
    private readonly Lock _gate = new();
    private PendingAgentApprovalViewModel? _pending;
    private TaskCompletionSource<bool>? _completion;
    private Func<AgentToolApprovalRequest, CancellationToken, ValueTask<TrustedAgentApprovalDetails?>>? _detailsProvider;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PendingAgentApprovalViewModel? Pending
    {
        get => _pending;
        private set
        {
            if (ReferenceEquals(_pending, value)) return;
            _pending = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pending)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPending)));
        }
    }

    public bool HasPending => Pending is not null;

    public void SetTrustedDetailsProvider(
        Func<AgentToolApprovalRequest, CancellationToken, ValueTask<TrustedAgentApprovalDetails?>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            if (_detailsProvider is not null) throw new InvalidOperationException("The Agent approval details provider is already configured.");
            _detailsProvider = provider;
        }
    }

    public async ValueTask<bool> RequestApprovalAsync(
        AgentToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Func<AgentToolApprovalRequest, CancellationToken, ValueTask<TrustedAgentApprovalDetails?>>? detailsProvider;
        lock (_gate) detailsProvider = _detailsProvider;
        var trustedDetails = detailsProvider is null
            ? null
            : await detailsProvider(request, cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_completion is not null) throw new InvalidOperationException("An Agent approval is already pending.");
            _completion = completion;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => Pending = new PendingAgentApprovalViewModel(request, trustedDetails));
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_completion, completion)) _completion = null;
            }
            await Dispatcher.UIThread.InvokeAsync(() => Pending = null);
        }
    }

    public void Approve() => Resolve(true);

    public void Deny() => Resolve(false);

    private void Resolve(bool approved)
    {
        TaskCompletionSource<bool>? completion;
        lock (_gate) completion = _completion;
        completion?.TrySetResult(approved);
    }
}

public sealed class PendingAgentApprovalViewModel
{
    public PendingAgentApprovalViewModel(
        AgentToolApprovalRequest request,
        TrustedAgentApprovalDetails? trustedDetails = null)
    {
        ToolName = request.ToolName;
        Description = request.Description;
        Risk = request.Risk switch
        {
            CanX.Tooling.ToolRisk.Network => "网络操作",
            CanX.Tooling.ToolRisk.LocalWrite => "本地写入",
            CanX.Tooling.ToolRisk.Destructive => "高风险操作",
            _ => "只读操作",
        };
        var arguments = request.Arguments.ToJsonString();
        Arguments = arguments.Length <= 4000 ? arguments : $"{arguments[..4000]}…";
        TrustedDetails = trustedDetails;
    }

    public string ToolName { get; }

    public string Description { get; }

    public string Risk { get; }

    public string Arguments { get; }

    public TrustedAgentApprovalDetails? TrustedDetails { get; }

    public bool HasTrustedDetails => TrustedDetails is not null;
}

public sealed record TrustedAgentApprovalDetails(
    string Title,
    string Permissions,
    string Checks,
    string Changes,
    string Integrity);
