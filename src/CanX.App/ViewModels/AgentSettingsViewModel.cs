// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CanX.Agent;
using CanX.Core.Configuration;

namespace CanX.App.ViewModels;

public sealed class AgentSettingsViewModel : INotifyPropertyChanged
{
    private readonly CredentialAgentApiKeyProvider? _apiKeyProvider;
    private readonly IAgentModelCatalog? _modelCatalog;
    private readonly IAgentChatConnectionTester? _chatTester;
    private readonly Dictionary<string, AgentModelInfo> _knownModels = new(StringComparer.Ordinal);
    private string _baseUrl;
    private string _model;
    private string? _selectedModel;
    private double _temperature;
    private int _contextWindowTokens;
    private int _maximumDurationSeconds;
    private int _toolTimeoutSeconds;
    private bool _showToolCalls;
    private bool _autoApproveToolCalls;
    private string _apiKeyInput = string.Empty;
    private bool _hasStoredApiKey;
    private string _credentialStatus = string.Empty;
    private string _connectionStatus = string.Empty;
    private bool _isTestingConnection;
    private int _modelSelectionVersion;
    private int _connectionVersion;

    public AgentSettingsViewModel(
        AgentSettings settings,
        CredentialAgentApiKeyProvider? apiKeyProvider = null,
        IAgentModelCatalog? modelCatalog = null,
        IAgentChatConnectionTester? chatTester = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _apiKeyProvider = apiKeyProvider;
        _modelCatalog = modelCatalog;
        _chatTester = chatTester;
        _baseUrl = settings.BaseUrl;
        _model = settings.Model;
        _selectedModel = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model;
        _temperature = settings.Temperature;
        _contextWindowTokens = settings.ContextWindowTokens;
        _maximumDurationSeconds = settings.MaximumDurationSeconds;
        _toolTimeoutSeconds = settings.ToolTimeoutSeconds;
        _showToolCalls = settings.ShowToolCalls;
        _autoApproveToolCalls = settings.AutoApproveToolCalls;
        if (_selectedModel is not null) ModelIds.Add(_selectedModel);
        DeleteApiKeyCommand = new AsyncCommand(DeleteApiKeyAsync, () =>
            _apiKeyProvider is not null && !IsTestingConnection);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync, () =>
            _modelCatalog is not null && !IsTestingConnection && !string.IsNullOrWhiteSpace(BaseUrl));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> ModelIds { get; } = [];

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (!SetField(ref _baseUrl, value ?? string.Empty)) return;
            _connectionVersion++;
            _modelSelectionVersion++;
            _knownModels.Clear();
            ModelIds.Clear();
            if (!string.IsNullOrWhiteSpace(Model)) ModelIds.Add(Model);
            ContextWindowTokens = 0;
            ConnectionStatus = string.Empty;
            TestConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    public string Model
    {
        get => _model;
        set
        {
            if (!SetField(ref _model, value ?? string.Empty)) return;
            _modelSelectionVersion++;
            ContextWindowTokens = _knownModels.TryGetValue(_model, out var info)
                ? info.ContextWindowTokens ?? 0 : 0;
            if (info?.DefaultTemperature is { } temperature) Temperature = temperature;
        }
    }

    public string? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetField(ref _selectedModel, value) || string.IsNullOrWhiteSpace(value)) return;
            Model = value;
            if (!IsTestingConnection && _knownModels.TryGetValue(value, out var info) &&
                info.ContextWindowTokens is null)
                _ = LoadSelectedModelDetailsAsync(value, ++_modelSelectionVersion);
        }
    }

    public double Temperature { get => _temperature; set => SetField(ref _temperature, value); }

    public int ContextWindowTokens { get => _contextWindowTokens; set => SetField(ref _contextWindowTokens, value); }

    public int MaximumDurationSeconds { get => _maximumDurationSeconds; set => SetField(ref _maximumDurationSeconds, value); }

    public int ToolTimeoutSeconds { get => _toolTimeoutSeconds; set => SetField(ref _toolTimeoutSeconds, value); }

    public bool ShowToolCalls { get => _showToolCalls; set => SetField(ref _showToolCalls, value); }

    public bool AutoApproveToolCalls { get => _autoApproveToolCalls; set => SetField(ref _autoApproveToolCalls, value); }

    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set
        {
            if (SetField(ref _apiKeyInput, value ?? string.Empty))
            {
                _connectionVersion++;
                _modelSelectionVersion++;
                ConnectionStatus = string.Empty;
            }
        }
    }

    public bool HasStoredApiKey
    {
        get => _hasStoredApiKey;
        private set => SetField(ref _hasStoredApiKey, value);
    }

    public string CredentialStatus { get => _credentialStatus; private set => SetField(ref _credentialStatus, value); }

    public string ConnectionStatus { get => _connectionStatus; private set => SetField(ref _connectionStatus, value); }

    public bool IsTestingConnection
    {
        get => _isTestingConnection;
        private set
        {
            if (!SetField(ref _isTestingConnection, value)) return;
            TestConnectionCommand.RaiseCanExecuteChanged();
            DeleteApiKeyCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncCommand DeleteApiKeyCommand { get; }
    public AsyncCommand TestConnectionCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_apiKeyProvider is null) return;
        try
        {
            HasStoredApiKey = !string.IsNullOrEmpty(await _apiKeyProvider.GetApiKeyAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CredentialStatus = $"无法读取系统凭据：{exception.Message}";
        }
    }

    public async Task TestConnectionAsync()
    {
        if (_modelCatalog is null || IsTestingConnection) return;
        IsTestingConnection = true;
        ConnectionStatus = "正在连接模型接口…";
        try
        {
            var endpoint = ParseBaseUri();
            var requestedUrl = BaseUrl.Trim();
            var requestedKey = ApiKeyInput;
            var requestedModel = Model.Trim();
            var requestedContext = ContextWindowTokens;
            var connectionVersion = _connectionVersion;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var models = await _modelCatalog.GetModelsAsync(endpoint, requestedKey, timeout.Token).ConfigureAwait(true);
            if (connectionVersion != _connectionVersion || BaseUrl.Trim() != requestedUrl) return;
            _knownModels.Clear();
            ModelIds.Clear();
            foreach (var model in models)
            {
                _knownModels[model.Id] = model;
                ModelIds.Add(model.Id);
            }
            var selected = string.IsNullOrWhiteSpace(requestedModel)
                ? models.FirstOrDefault()?.Id : requestedModel;
            if (!string.IsNullOrWhiteSpace(selected) && !ModelIds.Contains(selected)) ModelIds.Insert(0, selected);
            SelectedModel = selected;
            if (selected is not null) Model = selected;
            if (selected is not null && _knownModels.TryGetValue(selected, out var info))
            {
                ApplyModelInfo(info);
                if (info.ContextWindowTokens is null)
                {
                    if (selected == requestedModel) ContextWindowTokens = requestedContext;
                    await LoadSelectedModelDetailsAsync(selected, ++_modelSelectionVersion, endpoint, requestedKey, timeout.Token)
                        .ConfigureAwait(true);
                }
            }
            else if (selected == requestedModel) ContextWindowTokens = requestedContext;
            if (connectionVersion != _connectionVersion || !string.Equals(Model, selected ?? string.Empty, StringComparison.Ordinal)) return;
            var chatVerified = !string.IsNullOrWhiteSpace(selected) && _chatTester is not null;
            if (chatVerified)
            {
                ConnectionStatus = "正在验证聊天接口…";
                await _chatTester!.TestAsync(new AgentProviderConfiguration
                {
                    BaseUri = endpoint,
                    Model = selected!,
                    Temperature = Temperature,
                    ContextWindowTokens = ContextWindowTokens,
                }, requestedKey, timeout.Token).ConfigureAwait(true);
                if (connectionVersion != _connectionVersion || Model != selected) return;
            }
            ConnectionStatus = models.Count == 0
                ? chatVerified
                    ? "聊天接口已验证；模型列表为空。"
                    : "模型列表为空；可手动输入模型 ID 后重新测试。"
                : chatVerified
                    ? $"连接成功，已获取 {models.Count} 个模型，聊天接口已验证。"
                    : $"连接成功，已获取 {models.Count} 个模型。";
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "连接测试超时，请检查接口地址或网络。";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConnectionStatus = $"连接失败：{exception.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    public AgentSettings ToSettings()
    {
        _ = CreateRunOptions();
        if (string.IsNullOrWhiteSpace(BaseUrl) != string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("LLM Base URL 和模型必须同时填写或同时留空。");
        if (!string.IsNullOrWhiteSpace(BaseUrl)) _ = CreateConfiguration();
        return new AgentSettings
        {
            BaseUrl = BaseUrl.Trim(),
            Model = Model.Trim(),
            Temperature = Temperature,
            ContextWindowTokens = ContextWindowTokens,
            MaximumDurationSeconds = MaximumDurationSeconds,
            ToolTimeoutSeconds = ToolTimeoutSeconds,
            ShowToolCalls = ShowToolCalls,
            AutoApproveToolCalls = AutoApproveToolCalls,
        };
    }

    public AgentProviderConfiguration CreateConfiguration()
    {
        var result = new AgentProviderConfiguration
        {
            BaseUri = ParseBaseUri(),
            Model = Model.Trim(),
            Temperature = Temperature,
            ContextWindowTokens = ContextWindowTokens,
        };
        result.Validate();
        return result;
    }

    public AgentRunOptions CreateRunOptions()
    {
        var result = new AgentRunOptions
        {
            MaximumDuration = TimeSpan.FromSeconds(MaximumDurationSeconds),
            ToolTimeout = TimeSpan.FromSeconds(ToolTimeoutSeconds),
            AutoApproveToolCalls = AutoApproveToolCalls,
        };
        result.Validate();
        return result;
    }

    private async Task LoadSelectedModelDetailsAsync(
        string modelId, int version, Uri? endpoint = null, string? enteredKey = null,
        CancellationToken cancellationToken = default)
    {
        if (_modelCatalog is null) return;
        try
        {
            endpoint ??= ParseBaseUri();
            enteredKey ??= ApiKeyInput;
            var connectionVersion = _connectionVersion;
            var details = await _modelCatalog.GetModelAsync(endpoint, modelId, enteredKey, cancellationToken)
                .ConfigureAwait(true);
            if (version != _modelSelectionVersion || connectionVersion != _connectionVersion ||
                details is null || Model != modelId) return;
            _knownModels[modelId] = details;
            ApplyModelInfo(details);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // /models/{id} is optional in OpenAI-compatible APIs. The model
            // list remains usable and the context field can be entered manually.
            if (version == _modelSelectionVersion)
                ConnectionStatus = $"模型详情不可用，可手动填写上下文大小：{exception.Message}";
        }
    }

    private void ApplyModelInfo(AgentModelInfo info)
    {
        if (info.Id != Model) return;
        if (info.ContextWindowTokens is { } context) ContextWindowTokens = context;
        if (info.DefaultTemperature is { } temperature) Temperature = temperature;
    }

    private Uri ParseBaseUri()
    {
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("请填写有效的 LLM Base URL。");
        AgentProviderConfiguration.ValidateBaseUri(endpoint);
        return endpoint;
    }

    public async Task SavePendingApiKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKeyInput)) return;
        if (_apiKeyProvider is null) throw new InvalidOperationException("系统凭据存储不可用，无法保存 API Key。");
        var pendingKey = ApiKeyInput;
        try
        {
            var endpoint = ParseBaseUri();
            await _apiKeyProvider.SetApiKeyAsync(pendingKey.Trim(), endpoint).ConfigureAwait(true);
            if (ApiKeyInput == pendingKey) ApiKeyInput = string.Empty;
            HasStoredApiKey = true;
            CredentialStatus = "API Key 已保存到系统凭据存储。";
        }
        catch (Exception exception)
        {
            CredentialStatus = $"API Key 保存失败：{exception.Message}";
            throw;
        }
    }

    private async Task DeleteApiKeyAsync()
    {
        if (_apiKeyProvider is null) return;
        try
        {
            await _apiKeyProvider.DeleteApiKeyAsync().ConfigureAwait(true);
            HasStoredApiKey = false;
            ApiKeyInput = string.Empty;
            CredentialStatus = "已删除保存的 API Key。";
        }
        catch (Exception exception)
        {
            CredentialStatus = $"API Key 删除失败：{exception.Message}";
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
