// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Application.Playback;

public interface IMpvOptionValidator
{
    void Validate(IReadOnlyDictionary<string, string> options);
}
