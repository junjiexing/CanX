// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CanX.Streaming;
using SkiaSharp;

namespace CanX.App.ViewModels;

public sealed class ImageViewerViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaximumImageBytes = 64 * 1024 * 1024;
    private readonly MediaImageSequence _sequence;
    private readonly int _decodeWidth;
    private CancellationTokenSource? _loadCancellation;
    private Bitmap? _image;
    private string? _errorMessage;
    private bool _isLoading;
    private bool _disposed;
    private int _index;

    public ImageViewerViewModel(MediaImageSequence sequence, int decodeWidth = 4096)
    {
        _sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
        if (sequence.Items.Count == 0) throw new ArgumentException("图片列表不能为空。", nameof(sequence));
        ArgumentOutOfRangeException.ThrowIfLessThan(decodeWidth, 1);
        _decodeWidth = decodeWidth;
        _index = sequence.InitialIndex;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Bitmap? Image => _image;

    public string Title => _sequence.Items[_index].Name;

    public string PositionText => $"{_index + 1} / {_sequence.Items.Count}";

    public bool CanPrevious => _index > 0;

    public bool CanNext => _index < _sequence.Items.Count - 1;

    public bool IsLoading => _isLoading;

    public string? ErrorMessage => _errorMessage;

    public Task MoveAsync(int direction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = _index + Math.Sign(direction);
        if (direction == 0 || next < 0 || next >= _sequence.Items.Count) return Task.CompletedTask;
        _index = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PositionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPrevious)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanNext)));
        return LoadAsync();
    }

    public async Task LoadAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _isLoading = true;
        _errorMessage = null;
        SetImage(null);
        NotifyLoadState();
        try
        {
            using var stream = await _sequence.OpenReadAsync(_index, cancellation.Token);
            var bitmap = await Task.Run(
                () => Decode(stream, _decodeWidth, cancellation.Token), cancellation.Token);
            if (_disposed || !ReferenceEquals(_loadCancellation, cancellation)) bitmap.Dispose();
            else SetImage(bitmap);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed && ReferenceEquals(_loadCancellation, cancellation))
            {
                _errorMessage = $"无法打开图片：{exception.Message}";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorMessage)));
            }
        }
        finally
        {
            if (!_disposed && ReferenceEquals(_loadCancellation, cancellation))
            {
                _isLoading = false;
                _loadCancellation = null;
                NotifyLoadState();
            }
            cancellation.Dispose();
        }
    }

    private static Bitmap Decode(IMediaByteStream source, int width, CancellationToken cancellationToken)
    {
        if (source.Length > MaximumImageBytes)
        {
            throw new InvalidDataException("图片超过 64 MiB，无法预览。");
        }
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = source.Read(buffer, cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > MaximumImageBytes)
            {
                throw new InvalidDataException("图片超过 64 MiB，无法预览。");
            }
            memory.Write(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return DecodeBounded(memory.GetBuffer().AsSpan(0, (int)memory.Length), width);
    }

    /// <summary>
    /// Decodes an image no wider than <paramref name="maximumWidth"/>.
    /// </summary>
    /// <remarks>
    /// Avalonia's DecodeToWidth resamples to exactly the requested width, so a
    /// 1920 px photo was upscaled to a 4096 px, ~36 MiB bitmap. For a large JPEG
    /// it also decodes the full-resolution image before shrinking it, and the
    /// statically linked Skia on Windows uses libjpeg-turbo without its SIMD
    /// paths, so an 8000 px photo took several seconds. SKCodec decodes directly
    /// at a supported scale instead (libjpeg-turbo's DCT scaling skips most of
    /// the work), and images within the limit are decoded at native size.
    /// </remarks>
    internal static Bitmap DecodeBounded(ReadOnlySpan<byte> encoded, int maximumWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumWidth, 1);
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("无法识别的图片格式。");
        var native = codec.Info;
        var size = native.Width <= maximumWidth
            ? new SKSizeI(native.Width, native.Height)
            : codec.GetScaledDimensions((float)maximumWidth / native.Width);
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidDataException($"无法解码图片：{result}。");
        }

        // Codec scales are coarse (n/8 for JPEG, none for most other formats),
        // so finish any remaining reduction with one downscale; never upscale.
        using var resized = decoded.Width > maximumWidth
            ? decoded.Resize(
                new SKImageInfo(
                    maximumWidth,
                    Math.Max(1, (int)Math.Round((double)decoded.Height * maximumWidth / decoded.Width)),
                    SKColorType.Bgra8888,
                    SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                ?? throw new InvalidDataException("无法缩放图片。")
            : null;
        var bitmap = resized ?? decoded;
        return new Bitmap(
            PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            bitmap.GetPixels(),
            new PixelSize(bitmap.Width, bitmap.Height),
            new Vector(96, 96),
            bitmap.RowBytes);
    }

    private void SetImage(Bitmap? image)
    {
        var previous = _image;
        _image = image;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        previous?.Dispose();
    }

    private void NotifyLoadState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorMessage)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadCancellation?.Cancel();
        _sequence.Dispose();
        SetImage(null);
    }
}
