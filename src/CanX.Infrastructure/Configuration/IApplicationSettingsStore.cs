// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Configuration;

namespace CanX.Infrastructure.Configuration;

public interface IApplicationSettingsStore
{
    ValueTask<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default);
}
