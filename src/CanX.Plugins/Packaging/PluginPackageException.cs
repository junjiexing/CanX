// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Plugins.Packaging;

public sealed class PluginPackageException : Exception
{
    public PluginPackageException(string message)
        : base(message)
    {
    }

    public PluginPackageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
