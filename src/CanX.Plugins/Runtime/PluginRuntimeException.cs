// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Runtime;

public sealed class PluginRuntimeException : Exception
{
    public PluginRuntimeException(string message)
        : base(message)
    {
    }

    public PluginRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
