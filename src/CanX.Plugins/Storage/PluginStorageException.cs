// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Storage;

public sealed class PluginStorageException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
