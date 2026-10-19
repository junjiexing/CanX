// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Headless;
using CanX.App.ViewModels;
using SkiaSharp;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class ImageViewerDecodeTests
{
    static ImageViewerDecodeTests() => SkiaPlatform.EnsureInitialized();

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, 1920, 1080)]
    [InlineData(SKEncodedImageFormat.Jpeg, 1200, 800)]
    [InlineData(SKEncodedImageFormat.Webp, 4096, 100)]
    public void DecodeBounded_KeepsImagesWithinTheLimitAtNativeSize(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = ImageViewerViewModel.DecodeBounded(Encode(format, width, height), 4096);

        Assert.Equal(new PixelSize(width, height), bitmap.PixelSize);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, 8000, 6000)]
    [InlineData(SKEncodedImageFormat.Png, 5000, 1000)]
    public void DecodeBounded_ShrinksWideImagesToTheLimitKeepingAspectRatio(
        SKEncodedImageFormat format,
        int width,
        int height)
    {
        using var bitmap = ImageViewerViewModel.DecodeBounded(Encode(format, width, height), 4096);

        // JPEG uses the nearest libjpeg-turbo DCT scale (8000 px -> 4/8 = 4000 px),
        // which already fits; it is not resampled back up to exactly 4096 px.
        Assert.InRange(bitmap.PixelSize.Width, 4096 / 2, 4096);
        var expectedHeight = (double)height * bitmap.PixelSize.Width / width;
        Assert.InRange(bitmap.PixelSize.Height, expectedHeight - 1, expectedHeight + 1);
    }

    [Fact]
    public void DecodeBounded_RejectsUnknownData()
    {
        Assert.Throws<InvalidDataException>(() => ImageViewerViewModel.DecodeBounded([1, 2, 3, 4, 5, 6, 7, 8], 4096));
    }

    private static byte[] Encode(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x33, 0x66, 0xCC));
        }

        using var encoded = bitmap.Encode(format, 90);
        return encoded.ToArray();
    }

    private static class SkiaPlatform
    {
        private static readonly Lock Gate = new();
        private static bool _initialized;

        // Avalonia Bitmap needs a platform render interface; headless + Skia
        // provides the same Skia backend the desktop app renders with.
        public static void EnsureInitialized()
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    return;
                }

                AppBuilder.Configure<Avalonia.Application>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .SetupWithoutStarting();
                _initialized = true;
            }
        }
    }
}
