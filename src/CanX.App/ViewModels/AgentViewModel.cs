// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using CanX.Agent;

namespace CanX.App.ViewModels;

public sealed class AgentViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly AgentRunner _runner;
    private readonly IAgentConversationStore _store;
    private readonly Func<AgentProviderConfiguration> _configuration;
    private readonly Func<AgentRunOptions> _runOptions;
    private readonly Func<int> _contextWindowTokens;
    private readonly Func<bool> _showToolCalls;
    private readonly AgentApprovalService _approval;
    private readonly List<AgentConversation> _saved = [];
    private AgentConversationItemViewModel? _selectedConversation;
    private CancellationTokenSource? _runCancellation;
    private string _inputText = string.Empty;
    private string _statusMessage = "在设置中配置 LLM 网关和模型后即可开始。";
    private bool _isRunning;
    private int _inputTokens;
    private int _outputTokens;
    private int _contextTokensUsed;
    private int _activeContextWindowTokens;
    private bool _contextUsageEstimated;

    public AgentViewModel(
        AgentRunner runner,
        IAgentConversationStore store,
        Func<AgentProviderConfiguration> configuration,
        Func<AgentRunOptions> runOptions,
        AgentApprovalService approval,
        Func<int>? contextWindowTokens = null,
        Func<bool>? showToolCalls = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _runOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
        _contextWindowTokens = contextWindowTokens ?? (() => 0);
        _showToolCalls = showToolCalls ?? (() => false);
        Messages.CollectionChanged += OnMessagesChanged;
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
        _approval.PropertyChanged += OnApprovalChanged;
        SendCommand = new AsyncCommand(SendAsync, () => !IsRunning && !string.IsNullOrWhiteSpace(InputText));
        StopCommand = new AsyncCommand(StopAsync, () => IsRunning);
        NewConversationCommand = new AsyncCommand(NewConversationAsync, () => !IsRunning);
        DeleteConversationCommand = new AsyncCommand(DeleteConversationAsync, () => !IsRunning && SelectedConversation is not null);
        ApproveCommand = new AsyncCommand(() => ResolveApprovalAsync(true), () => HasPendingApproval);
        DenyCommand = new AsyncCommand(() => ResolveApprovalAsync(false), () => HasPendingApproval);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AgentConversationItemViewModel> Conversations { get; } = [];

    public ObservableCollection<AgentChatMessageViewModel> Messages { get; } = [];

    public ObservableCollection<AgentChatMessageViewModel> VisibleMessages { get; } = [];

    public void RefreshMessageVisibility()
    {
        VisibleMessages.Clear();
        foreach (var message in Messages)
        {
            if (!message.IsToolStatus || _showToolCalls()) VisibleMessages.Add(message);
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
        {
            foreach (AgentChatMessageViewModel message in args.NewItems)
            {
                if (!message.IsToolStatus || _showToolCalls()) VisibleMessages.Add(message);
            }
        }
        else
        {
            RefreshMessageVisibility();
        }
    }

    public AgentConversationItemViewModel? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (IsRunning && !ReferenceEquals(_selectedConversation, value)) return;
            if (ReferenceEquals(_selectedConversation, value)) return;
            _selectedConversation = value;
            OnPropertyChanged();
            LoadSelectedMessages();
            RefreshContextDisplay();
            DeleteConversationCommand.RaiseCanExecuteChanged();
        }
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetField(ref _inputText, value ?? string.Empty)) SendCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            SendCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            NewConversationCommand.RaiseCanExecuteChanged();
            DeleteConversationCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(UsageText));
        }
    }

    public int InputTokens { get => _inputTokens; private set => SetField(ref _inputTokens, value); }

    public int OutputTokens { get => _outputTokens; private set => SetField(ref _outputTokens, value); }

    public int ContextTokensUsed
    {
        get => _contextTokensUsed;
        private set
        {
            if (SetField(ref _contextTokensUsed, value)) OnPropertyChanged(nameof(UsageText));
        }
    }

    public string UsageText
    {
        get
        {
            var context = IsRunning ? _activeContextWindowTokens : _contextWindowTokens();
            var used = _contextUsageEstimated ? $"约 {ContextTokensUsed:N0}" : $"{ContextTokensUsed:N0}";
            return $"上下文：{used} / {(context > 0 ? context.ToString("N0") : "未知")} tokens";
        }
    }

    public void RefreshContextDisplay()
    {
        if (IsRunning) return;
        var conversation = _saved.FirstOrDefault(item => item.Id == SelectedConversation?.Id);
        var model = TryGetModel();
        if (conversation is not null && conversation.ContextModel == model && conversation.ContextTokensUsed > 0)
        {
            ContextTokensUsed = conversation.ContextTokensUsed;
            SetContextUsageEstimated(conversation.ContextUsageEstimated);
        }
        else if (conversation is not null)
        {
            var active = conversation.Messages.Skip(conversation.SummarizedMessageCount);
            var messages = string.IsNullOrWhiteSpace(conversation.ContextSummary)
                ? active : Enumerable.Repeat(AgentMessage.Assistant(conversation.ContextSummary), 1).Concat(active);
            ContextTokensUsed = AgentContextEstimator.EstimateMessages(messages);
            SetContextUsageEstimated(true);
        }
        else
        {
            ContextTokensUsed = 0;
            SetContextUsageEstimated(false);
        }
        OnPropertyChanged(nameof(UsageText));
    }

    public PendingAgentApprovalViewModel? PendingApproval => _approval.Pending;

    public bool HasPendingApproval => _approval.HasPending;

    public AsyncCommand SendCommand { get; }

    public AsyncCommand StopCommand { get; }

    public AsyncCommand NewConversationCommand { get; }

    public AsyncCommand DeleteConversationCommand { get; }

    public AsyncCommand ApproveCommand { get; }

    public AsyncCommand DenyCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var conversations = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        _saved.Clear();
        _saved.AddRange(conversations);
        Conversations.Clear();
        foreach (var conversation in _saved) Conversations.Add(new AgentConversationItemViewModel(conversation));
        SelectedConversation = Conversations.FirstOrDefault();
        if (SelectedConversation is null) await NewConversationAsync().ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        _approval.PropertyChanged -= OnApprovalChanged;
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = null;
        await SaveAsync().ConfigureAwait(false);
    }

    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0 || IsRunning) return;
        var conversation = EnsureConversation();
        AgentProviderConfiguration configuration;
        AgentRunOptions options;
        try
        {
            configuration = _configuration();
            options = _runOptions();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            return;
        }

        InputText = string.Empty;
        _activeContextWindowTokens = configuration.ContextWindowTokens;
        IsRunning = true;
        InputTokens = 0;
        OutputTokens = 0;
        StatusMessage = "Agent 正在处理…";
        using var runCancellation = new CancellationTokenSource();
        _runCancellation = runCancellation;
        var historicalMessages = conversation.Messages.ToArray();
        var context = new AgentConversationContext(
            conversation.ContextSummary,
            conversation.SummarizedMessageCount,
            conversation.ContextModel == configuration.Model ? conversation.ContextTokensUsed : 0);
        var userMessage = AgentMessage.User(text);
        conversation.Messages.Add(userMessage);
        Messages.Add(AgentChatMessageViewModel.From(userMessage));
        var assistantText = new StringBuilder();
        AgentChatMessageViewModel? activeAssistant = null;
        var activeTools = new Dictionary<string, AgentChatMessageViewModel>(StringComparer.Ordinal);
        var runCompleted = false;

        void AppendAssistantText(string? fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return;
            if (activeAssistant is null)
            {
                activeAssistant = new AgentChatMessageViewModel("助手", string.Empty, false);
                Messages.Add(activeAssistant);
            }
            activeAssistant.Content += fragment;
            assistantText.Append(fragment);
        }

        try
        {
            await foreach (var item in _runner.RunAsync(
                conversation.Id,
                historicalMessages,
                text,
                configuration,
                options,
                runCancellation.Token,
                context).ConfigureAwait(true))
            {
                switch (item)
                {
                    case AgentTextDelta delta:
                        AppendAssistantText(delta.Text);
                        break;
                    case AgentToolStarted started:
                        activeAssistant = null;
                        var toolStatus = new AgentChatMessageViewModel(
                            "工具",
                            $"正在调用 {started.ToolName}（{RiskLabel(started.Risk)}）",
                            true);
                        Messages.Add(toolStatus);
                        activeTools[started.CallId] = toolStatus;
                        break;
                    case AgentToolFinished finished:
                        activeAssistant = null;
                        var resultText = finished.Result.IsSuccess
                            ? $"{finished.ToolName} 已完成"
                            : $"{finished.ToolName} 失败：{finished.Result.ErrorCode} · {finished.Result.ErrorMessage}";
                        if (activeTools.Remove(finished.CallId, out var runningTool))
                        {
                            runningTool.Content = resultText;
                        }
                        else
                        {
                            Messages.Add(new AgentChatMessageViewModel("工具", resultText, true));
                        }
                        break;
                    case AgentContextCompressionStarted:
                        StatusMessage = "正在压缩上下文…";
                        break;
                    case AgentContextCompressed compressed:
                        conversation.ContextSummary = compressed.Summary;
                        conversation.SummarizedMessageCount = compressed.SummarizedMessages;
                        StatusMessage = "上下文已压缩，继续处理…";
                        break;
                    case AgentContextUsageUpdated current:
                        conversation.ContextTokensUsed = current.UsedTokens;
                        conversation.ContextUsageEstimated = current.IsEstimated;
                        conversation.ContextModel = configuration.Model;
                        ContextTokensUsed = current.UsedTokens;
                        SetContextUsageEstimated(current.IsEstimated);
                        break;
                    case AgentRunCompleted completed:
                        runCompleted = true;
                        if (activeAssistant is null) AppendAssistantText(completed.Content);
                        InputTokens = completed.Usage.InputTokens;
                        OutputTokens = completed.Usage.OutputTokens;
                        OnPropertyChanged(nameof(UsageText));
                        StatusMessage = $"已完成 · {completed.Rounds} 轮 · {completed.ToolCalls} 次 Tool 调用";
                        break;
                    case AgentRunFailed failed:
                        if (assistantText.Length == 0) AppendAssistantText($"执行失败：{failed.Message}");
                        StatusMessage = $"{failed.Code}：{failed.Message}";
                        break;
                }
            }

        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            if (assistantText.Length == 0) AppendAssistantText("已取消。");
            StatusMessage = "已取消";
        }
        catch (Exception exception)
        {
            if (assistantText.Length == 0) AppendAssistantText($"执行失败：{exception.Message}");
            StatusMessage = $"执行失败：{exception.Message}";
        }
        finally
        {
            foreach (var runningTool in activeTools.Values) runningTool.Content += " · 已中止";
            conversation.Messages.Add(AgentMessage.Assistant(assistantText.ToString()));
            conversation.DisplayItems.Clear();
            conversation.DisplayItems.AddRange(Messages.Select(static item => item.ToDisplayItem()));
            if (!runCompleted || conversation.ContextModel != configuration.Model)
            {
                var active = conversation.Messages.Skip(conversation.SummarizedMessageCount);
                var savedContext = string.IsNullOrWhiteSpace(conversation.ContextSummary)
                    ? active : Enumerable.Repeat(AgentMessage.Assistant(conversation.ContextSummary), 1).Concat(active);
                conversation.ContextTokensUsed = AgentContextEstimator.EstimateMessages(savedContext);
                conversation.ContextUsageEstimated = true;
                conversation.ContextModel = configuration.Model;
                ContextTokensUsed = conversation.ContextTokensUsed;
                SetContextUsageEstimated(true);
            }
            conversation.Title = CreateTitle(conversation);
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            var index = _saved.FindIndex(item => item.Id == conversation.Id);
            if (index >= 0) _saved[index] = conversation.ToRecord();
            SelectedConversation?.Refresh(conversation.Title, conversation.UpdatedAt);
            try
            {
                await _store.SaveAsync(_saved).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                StatusMessage = $"会话保存失败：{exception.Message}";
            }
            if (ReferenceEquals(_runCancellation, runCancellation)) _runCancellation = null;
            IsRunning = false;
            OnPropertyChanged(nameof(UsageText));
        }
    }

    private Task StopAsync()
    {
        _runCancellation?.Cancel();
        return Task.CompletedTask;
    }

    private async Task NewConversationAsync()
    {
        var conversation = new MutableConversation(
            Guid.NewGuid().ToString("N"),
            "新会话",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            []);
        _saved.Insert(0, conversation.ToRecord());
        var item = new AgentConversationItemViewModel(conversation.ToRecord());
        Conversations.Insert(0, item);
        SelectedConversation = item;
        await SaveAsync().ConfigureAwait(true);
    }

    private async Task DeleteConversationAsync()
    {
        if (SelectedConversation is null) return;
        var id = SelectedConversation.Id;
        var index = Conversations.IndexOf(SelectedConversation);
        Conversations.Remove(SelectedConversation);
        _saved.RemoveAll(item => item.Id == id);
        SelectedConversation = Conversations.Count == 0 ? null : Conversations[Math.Min(index, Conversations.Count - 1)];
        if (SelectedConversation is null)
        {
            await NewConversationAsync().ConfigureAwait(true);
            return;
        }
        await SaveAsync().ConfigureAwait(true);
    }

    private Task ResolveApprovalAsync(bool approved)
    {
        if (approved) _approval.Approve();
        else _approval.Deny();
        return Task.CompletedTask;
    }

    private MutableConversation EnsureConversation()
    {
        if (SelectedConversation is null) throw new InvalidOperationException("No Agent conversation is selected.");
        var persisted = _saved.Single(item => item.Id == SelectedConversation.Id);
        return new MutableConversation(
            persisted.Id,
            persisted.Title,
            persisted.CreatedAt,
            persisted.UpdatedAt,
            persisted.Messages.ToList(),
            persisted.ContextSummary,
            persisted.SummarizedMessageCount,
            persisted.ContextTokensUsed,
            persisted.ContextUsageEstimated,
            persisted.ContextModel,
            persisted.DisplayItems?.ToList() ?? []);
    }

    private async Task SaveAsync()
    {
        await _store.SaveAsync(_saved).ConfigureAwait(false);
    }

    private void LoadSelectedMessages()
    {
        Messages.Clear();
        if (SelectedConversation is null) return;
        var conversation = _saved.FirstOrDefault(item => item.Id == SelectedConversation.Id);
        if (conversation is null) return;
        if (conversation.DisplayItems is { Count: > 0 } displayItems)
        {
            foreach (var item in displayItems) Messages.Add(AgentChatMessageViewModel.From(item));
            return;
        }
        foreach (var message in conversation.Messages.Where(static item =>
                     item.Role is AgentMessageRole.User or AgentMessageRole.Assistant))
        {
            Messages.Add(AgentChatMessageViewModel.From(message));
        }
    }

    private void SetContextUsageEstimated(bool value)
    {
        if (_contextUsageEstimated == value) return;
        _contextUsageEstimated = value;
        OnPropertyChanged(nameof(UsageText));
    }

    private string? TryGetModel()
    {
        try { return _configuration().Model; }
        catch { return null; }
    }

    private void OnApprovalChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(AgentApprovalService.Pending) or nameof(AgentApprovalService.HasPending))
        {
            OnPropertyChanged(nameof(PendingApproval));
            OnPropertyChanged(nameof(HasPendingApproval));
            ApproveCommand.RaiseCanExecuteChanged();
            DenyCommand.RaiseCanExecuteChanged();
        }
    }

    private static string CreateTitle(MutableConversation conversation)
    {
        var first = conversation.Messages.FirstOrDefault(static message => message.Role == AgentMessageRole.User)?.Content?.Trim();
        if (string.IsNullOrEmpty(first)) return "新会话";
        return first.Length <= 40 ? first : $"{first[..40]}…";
    }

    private static string RiskLabel(CanX.Tooling.ToolRisk risk) => risk switch
    {
        CanX.Tooling.ToolRisk.ReadOnly => "只读",
        CanX.Tooling.ToolRisk.LocalWrite => "本地写入",
        CanX.Tooling.ToolRisk.Network => "网络",
        CanX.Tooling.ToolRisk.Destructive => "高风险",
        _ => "未知",
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class MutableConversation(
        string id,
        string title,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        List<AgentMessage> messages,
        string? contextSummary = null,
        int summarizedMessageCount = 0,
        int contextTokensUsed = 0,
        bool contextUsageEstimated = false,
        string? contextModel = null,
        List<AgentConversationDisplayItem>? displayItems = null)
    {
        public string Id { get; } = id;
        public string Title { get; set; } = title;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset UpdatedAt { get; set; } = updatedAt;
        public List<AgentMessage> Messages { get; } = messages;
        public List<AgentConversationDisplayItem> DisplayItems { get; } = displayItems ?? [];
        public string? ContextSummary { get; set; } = contextSummary;
        public int SummarizedMessageCount { get; set; } = summarizedMessageCount;
        public int ContextTokensUsed { get; set; } = contextTokensUsed;
        public bool ContextUsageEstimated { get; set; } = contextUsageEstimated;
        public string? ContextModel { get; set; } = contextModel;

        public AgentConversation ToRecord() => new()
        {
            Id = Id,
            Title = Title,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Messages = Messages.ToArray(),
            DisplayItems = DisplayItems.ToArray(),
            ContextSummary = ContextSummary,
            SummarizedMessageCount = SummarizedMessageCount,
            ContextTokensUsed = ContextTokensUsed,
            ContextUsageEstimated = ContextUsageEstimated,
            ContextModel = ContextModel,
        };
    }
}

public sealed class AgentConversationItemViewModel : INotifyPropertyChanged
{
    private string _title;
    private DateTimeOffset _updatedAt;

    public AgentConversationItemViewModel(AgentConversation conversation)
    {
        Id = conversation.Id;
        _title = conversation.Title;
        _updatedAt = conversation.UpdatedAt;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Title { get => _title; private set => SetField(ref _title, value); }

    public DateTimeOffset UpdatedAt { get => _updatedAt; private set => SetField(ref _updatedAt, value); }

    public void Refresh(string title, DateTimeOffset updatedAt)
    {
        Title = title;
        UpdatedAt = updatedAt;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class AgentChatMessageViewModel : INotifyPropertyChanged
{
    private string _content;

    public AgentChatMessageViewModel(string role, string content, bool isToolStatus)
    {
        Role = role;
        _content = content;
        IsToolStatus = isToolStatus;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Role { get; }

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
        }
    }

    public bool IsToolStatus { get; }

    public bool IsMarkdown => Role == "助手";

    public bool IsPlainText => !IsMarkdown;

    public AgentConversationDisplayItem ToDisplayItem() => new()
    {
        Kind = IsToolStatus
            ? AgentConversationDisplayKind.Tool
            : Role == "用户" ? AgentConversationDisplayKind.User : AgentConversationDisplayKind.Assistant,
        Content = Content,
    };

    public static AgentChatMessageViewModel From(AgentConversationDisplayItem item) => item.Kind switch
    {
        AgentConversationDisplayKind.User => new("用户", item.Content, false),
        AgentConversationDisplayKind.Assistant => new("助手", item.Content, false),
        AgentConversationDisplayKind.Tool => new("工具", item.Content, true),
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

    public static AgentChatMessageViewModel From(AgentMessage message) => new(
        message.Role == AgentMessageRole.User ? "用户" : "助手",
        message.Content ?? string.Empty,
        false);
}
