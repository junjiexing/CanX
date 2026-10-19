// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Runtime;

internal interface IQuickJsRuntimeFactory
{
    IQuickJsRuntime Create(PluginRuntimeOptions options, Action<ulong, string, string> toolCallback);
}

internal interface IQuickJsRuntime : IDisposable
{
    void AddModule(string moduleName, string source);

    void EvaluateScript(string source, string filename, TimeSpan timeout);

    void EvaluateModule(string source, string filename, TimeSpan timeout);

    void ResolveTool(ulong requestId, string resultJson, bool isError);

    void InvokeCallback(ulong requestId, string registrationId, string inputJson, TimeSpan timeout);

    int PumpJobs(int maximumJobs, TimeSpan timeout);
}
