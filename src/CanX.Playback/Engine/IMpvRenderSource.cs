// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Playback.Engine;

public interface IMpvRenderSource
{
    bool IsInitialized { get; }

    IMpvRenderContext CreateOpenGlRenderContext(nint getProcAddress, nint callbackContext);
}

public interface IMpvRenderContext : IDisposable
{
    void SetUpdateCallback(nint callback, nint callbackContext);

    bool Update();

    MpvRenderFrameInfo GetNextFrameInfo();

    void Render(int framebuffer, int width, int height, bool flipY);

    void SkipRender();

    void ReportSwap();
}

public readonly record struct MpvRenderFrameInfo(
    bool IsPresent,
    bool IsRedraw,
    bool IsRepeat,
    bool BlocksForVSync,
    long TargetTimeMicroseconds);
