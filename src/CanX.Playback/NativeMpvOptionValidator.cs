// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Application.Playback;
using CanX.Playback.Interop;

namespace CanX.Playback;

public sealed class NativeMpvOptionValidator : IMpvOptionValidator
{
    public void Validate(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handle = MpvNative.Create();
        if (handle == 0)
        {
            throw new InvalidOperationException("无法创建 libmpv 配置校验实例。");
        }

        try
        {
            ValidateOption(handle, "config", "no");
            foreach (var option in options)
            {
                ValidateOption(handle, option.Key, option.Value);
            }
        }
        finally
        {
            MpvNative.Destroy(handle);
        }
    }

    private static void ValidateOption(nint handle, string name, string value)
    {
        var result = MpvNative.SetOptionString(handle, name, value);
        if (result < 0)
        {
            throw new InvalidDataException(
                $"mpv 选项 {name}={value} 无效：{MpvNative.GetError(result)}。");
        }
    }
}
