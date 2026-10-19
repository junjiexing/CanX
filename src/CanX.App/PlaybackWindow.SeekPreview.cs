// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CanX.App.Controls;
using CanX.Core.Configuration;
using CanX.Playback;
using CanX.Streaming;

namespace CanX.App;

public sealed partial class PlaybackWindow
{
    private const int PreviewCacheLimit = 24;
    private const double PreviewIntervalSeconds = 2;
    private readonly DispatcherTimer _seekPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly Dictionary<long, Bitmap> _seekPreviewCache = [];
    private readonly Queue<long> _seekPreviewCacheOrder = [];
    private MpvThumbnailExtractor? _seekPreviewExtractor;
    private CancellationTokenSource? _seekPreviewCancellation;
    private string? _seekPreviewSource;
    private long? _seekPreviewBucket;
    private long? _seekPreviewFailedBucket;
    private Point _seekPreviewPointer;
    private bool _seekPreviewEnabled = true;
    private int _seekPreviewDelayMilliseconds = 180;
    private bool _seekPreviewClosed;

    private void InitializeSeekPreview(MediaStreamBroker? streamBroker, MpvSettings settings)
    {
        _seekPreviewExtractor = new MpvThumbnailExtractor(streamBroker);
        _seekPreviewSource = ViewModel.Source;
        ApplySeekPreviewSettings(settings);
        _seekPreviewTimer.Tick += OnSeekPreviewTimerTick;
        PlaybackSeekArea.AddHandler(
            PointerMovedEvent,
            OnSeekPreviewPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackSeekArea.AddHandler(
            PointerExitedEvent,
            OnSeekPreviewPointerExited,
            RoutingStrategies.Direct,
            handledEventsToo: true);
    }

    internal void ApplySeekPreviewSettings(MpvSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_seekPreviewClosed) return;

        var delay = Math.Clamp(settings.SeekPreviewDelayMilliseconds, 0, 10000);
        if (_seekPreviewEnabled == settings.SeekPreviewEnabled && _seekPreviewDelayMilliseconds == delay)
            return;

        _seekPreviewEnabled = settings.SeekPreviewEnabled;
        _seekPreviewDelayMilliseconds = delay;
        _seekPreviewTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay));
        HideSeekPreview();
        _seekPreviewFailedBucket = null;
        if (!_seekPreviewEnabled) ClearSeekPreviewCache();
    }

    private void OnSeekPreviewPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_seekPreviewClosed || eventArgs.Pointer.Type != PointerType.Mouse ||
            !ViewModel.CanSeek || ViewModel.VideoWidth <= 0 || ViewModel.DurationSeconds <= 0 ||
            !PlaybackChapterMarkerOverlay.TryGetSeekRange(out var left, out var right))
        {
            HideSeekPreview();
            return;
        }

        var source = ViewModel.Source;
        if (string.IsNullOrWhiteSpace(source))
        {
            HideSeekPreview();
            return;
        }

        if (!string.Equals(_seekPreviewSource, source, StringComparison.Ordinal))
        {
            ResetSeekPreviewSource(source);
        }

        var position = eventArgs.GetPosition(PlaybackChapterMarkerOverlay).X;
        var seconds = PlaybackChapterMarkers.SecondsFromPosition(
            position, ViewModel.DurationSeconds, left, right);
        var bucket = (long)Math.Round(seconds / PreviewIntervalSeconds, MidpointRounding.AwayFromZero);
        _seekPreviewPointer = eventArgs.GetPosition(PlaybackView);
        var needsCapture = false;

        if (_seekPreviewBucket != bucket)
        {
            _seekPreviewBucket = bucket;
            _seekPreviewFailedBucket = null;
            _seekPreviewTimer.Stop();
            _seekPreviewCancellation?.Cancel();
            SeekPreviewImage.Source = null;
            if (_seekPreviewEnabled && _seekPreviewCache.TryGetValue(bucket, out var cached))
            {
                SeekPreviewImage.Source = cached;
            }
            else
            {
                needsCapture = _seekPreviewEnabled;
            }
        }

        SeekPreviewTimeText.Text = FormatPreviewTime(seconds) +
            (_seekPreviewFailedBucket == bucket ? " · 预览不可用" : string.Empty);
        SeekPreviewBubble.IsVisible = true;
        PositionSeekPreview();
        if (needsCapture)
        {
            if (_seekPreviewDelayMilliseconds == 0) _ = CaptureSeekPreviewAsync();
            else _seekPreviewTimer.Start();
        }
    }

    private void OnSeekPreviewPointerExited(object? sender, PointerEventArgs eventArgs) => HideSeekPreview();

    private void OnSeekPreviewTimerTick(object? sender, EventArgs eventArgs)
    {
        _seekPreviewTimer.Stop();
        _ = CaptureSeekPreviewAsync();
    }

    private async Task CaptureSeekPreviewAsync()
    {
        if (_seekPreviewClosed || !_seekPreviewEnabled || !SeekPreviewBubble.IsVisible ||
            _seekPreviewBucket is not { } bucket ||
            _seekPreviewSource is not { Length: > 0 } source ||
            _seekPreviewExtractor is null)
        {
            return;
        }

        var target = Math.Clamp(bucket * PreviewIntervalSeconds, 0,
            Math.Max(0, ViewModel.DurationSeconds - 0.05));
        var cancellation = new CancellationTokenSource();
        _seekPreviewCancellation = cancellation;
        try
        {
            var bytes = await _seekPreviewExtractor.CaptureAsync(source, target, cancellation.Token);
            if (cancellation.IsCancellationRequested || _seekPreviewClosed || !_seekPreviewEnabled ||
                !SeekPreviewBubble.IsVisible || _seekPreviewBucket != bucket ||
                !string.Equals(_seekPreviewSource, source, StringComparison.Ordinal))
            {
                return;
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            _seekPreviewCache[bucket] = bitmap;
            _seekPreviewCacheOrder.Enqueue(bucket);
            SeekPreviewImage.Source = bitmap;
            while (_seekPreviewCacheOrder.Count > PreviewCacheLimit)
            {
                var oldest = _seekPreviewCacheOrder.Dequeue();
                if (_seekPreviewCache.Remove(oldest, out var oldBitmap)) oldBitmap.Dispose();
            }

            PositionSeekPreview();
        }
        catch (OperationCanceledException)
        {
            // A newer hover position superseded this frame.
        }
        catch
        {
            if (!_seekPreviewClosed && SeekPreviewBubble.IsVisible && _seekPreviewBucket == bucket)
            {
                _seekPreviewFailedBucket = bucket;
                SeekPreviewTimeText.Text += " · 预览不可用";
            }
        }
        finally
        {
            if (ReferenceEquals(_seekPreviewCancellation, cancellation)) _seekPreviewCancellation = null;
            cancellation.Dispose();
        }
    }

    private void PositionSeekPreview()
    {
        if (PlaybackTimeline.TranslatePoint(new Point(0, 0), PlaybackView) is not { } timeline) return;
        var imageHeight = Math.Min(120, timeline.Y - 48);
        var showImage = SeekPreviewImage.Source is not null && imageHeight >= 68;
        SeekPreviewFrame.IsVisible = showImage;
        if (showImage) SeekPreviewFrame.Height = imageHeight;
        var cardHeight = showImage ? imageHeight + 36 : 32;
        var maxLeft = Math.Max(8, PlaybackView.Bounds.Width - SeekPreviewBubble.Width - 8);
        var cardLeft = Math.Clamp(_seekPreviewPointer.X - SeekPreviewBubble.Width / 2, 8, maxLeft);
        var cardTop = Math.Max(8, timeline.Y - cardHeight - 8);
        SeekPreviewBubble.Margin = new Thickness(cardLeft, cardTop, 0, 0);
    }

    private static string FormatPreviewTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void HideSeekPreview()
    {
        _seekPreviewTimer.Stop();
        _seekPreviewCancellation?.Cancel();
        _seekPreviewBucket = null;
        SeekPreviewBubble.IsVisible = false;
        SeekPreviewImage.Source = null;
    }

    private void ResetSeekPreviewSource(string? source)
    {
        HideSeekPreview();
        _seekPreviewSource = source;
        _seekPreviewFailedBucket = null;
        ClearSeekPreviewCache();
    }

    private void ClearSeekPreviewCache()
    {
        foreach (var bitmap in _seekPreviewCache.Values) bitmap.Dispose();
        _seekPreviewCache.Clear();
        _seekPreviewCacheOrder.Clear();
    }

    private void ReleaseSeekPreview()
    {
        if (_seekPreviewClosed) return;
        _seekPreviewClosed = true;
        ResetSeekPreviewSource(null);
        if (_seekPreviewExtractor is { } extractor)
        {
            _seekPreviewExtractor = null;
            _ = DisposeSeekPreviewExtractorAsync(extractor);
        }
    }

    private static async Task DisposeSeekPreviewExtractorAsync(MpvThumbnailExtractor extractor)
    {
        try { await extractor.DisposeAsync(); }
        catch { /* Window shutdown must continue if the preview instance fails to close. */ }
    }
}
