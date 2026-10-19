// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using CanX.Core.Playback;

namespace CanX.App.Controls;

internal sealed class MpvPresentationMetrics
{
    private readonly TimingWindow _renderTimings = new();
    private readonly TimingWindow _submitTimings = new();
    private long _renderOperations;
    private long _submitOperations;
    private long _presentedFrames;
    private long _gpuCopies;
    private long _blockingFinishCalls;

    public void Reset()
    {
        Interlocked.Exchange(ref _renderOperations, 0);
        Interlocked.Exchange(ref _submitOperations, 0);
        Interlocked.Exchange(ref _presentedFrames, 0);
        Interlocked.Exchange(ref _gpuCopies, 0);
        Interlocked.Exchange(ref _blockingFinishCalls, 0);
        _renderTimings.Reset();
        _submitTimings.Reset();
    }

    public void RecordRender(long startedTimestamp, bool usedBlockingFinish)
        => RecordRenderMilliseconds(
            Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            usedBlockingFinish);

    internal void RecordRenderMilliseconds(double milliseconds, bool usedBlockingFinish)
    {
        Interlocked.Increment(ref _renderOperations);
        if (usedBlockingFinish)
        {
            Interlocked.Increment(ref _blockingFinishCalls);
        }

        _renderTimings.Add(milliseconds);
    }

    public void RecordSubmit(long startedTimestamp, bool performedGpuCopy, bool usedBlockingFinish)
        => RecordSubmitMilliseconds(
            Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            performedGpuCopy,
            usedBlockingFinish);

    internal void RecordSubmitMilliseconds(
        double milliseconds,
        bool performedGpuCopy,
        bool usedBlockingFinish)
    {
        Interlocked.Increment(ref _submitOperations);
        if (performedGpuCopy)
        {
            Interlocked.Increment(ref _gpuCopies);
        }

        if (usedBlockingFinish)
        {
            Interlocked.Increment(ref _blockingFinishCalls);
        }

        _submitTimings.Add(milliseconds);
    }

    public void RecordPresentedFrame() => Interlocked.Increment(ref _presentedFrames);

    public VideoPresentationDiagnostics Snapshot(
        string backend,
        string synchronizationMode,
        bool usesCpuReadback,
        int gpuCopiesPerFrame,
        int compositorGpuCopiesPerFrame)
    {
        var render = _renderTimings.Snapshot();
        var submit = _submitTimings.Snapshot();
        return new VideoPresentationDiagnostics
        {
            Backend = backend,
            SynchronizationMode = synchronizationMode,
            UsesCpuReadback = usesCpuReadback,
            CanXGpuCopiesPerFrame = gpuCopiesPerFrame,
            CompositorGpuCopiesPerFrame = compositorGpuCopiesPerFrame,
            RenderOperations = Interlocked.Read(ref _renderOperations),
            SubmitOperations = Interlocked.Read(ref _submitOperations),
            PresentedFrames = Interlocked.Read(ref _presentedFrames),
            CanXGpuCopies = Interlocked.Read(ref _gpuCopies),
            BlockingFinishCalls = Interlocked.Read(ref _blockingFinishCalls),
            AverageRenderMilliseconds = render.AverageMilliseconds,
            P95RenderMilliseconds = render.P95Milliseconds,
            AverageSubmitMilliseconds = submit.AverageMilliseconds,
            P95SubmitMilliseconds = submit.P95Milliseconds,
        };
    }

    private sealed class TimingWindow
    {
        private const int Capacity = 240;
        private readonly object _gate = new();
        private readonly double[] _samples = new double[Capacity];
        private int _count;
        private int _next;
        private double _total;
        private long _totalCount;

        public void Add(double milliseconds)
        {
            lock (_gate)
            {
                _samples[_next] = milliseconds;
                _next = (_next + 1) % Capacity;
                _count = Math.Min(_count + 1, Capacity);
                _total += milliseconds;
                _totalCount++;
            }
        }

        public TimingSnapshot Snapshot()
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    return default;
                }

                var ordered = new double[_count];
                Array.Copy(_samples, ordered, _count);
                Array.Sort(ordered);
                var p95Index = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
                return new TimingSnapshot(
                    _totalCount == 0 ? 0 : _total / _totalCount,
                    ordered[p95Index]);
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                Array.Clear(_samples);
                _count = 0;
                _next = 0;
                _total = 0;
                _totalCount = 0;
            }
        }
    }

    private readonly record struct TimingSnapshot(
        double AverageMilliseconds,
        double P95Milliseconds);
}
