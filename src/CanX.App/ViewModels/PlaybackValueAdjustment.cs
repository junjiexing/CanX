// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.App.ViewModels;

/// <summary>Keeps the latest user value visible while asynchronous player updates catch up.</summary>
internal sealed class PlaybackValueAdjustment(
    Func<double> readValue,
    Func<double, CancellationToken, Task> writeValue,
    Action valueChanged)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private double? _pendingValue;
    private long _revision;
    private long _completedRevision;

    public double Value => _pendingValue ?? readValue();

    public void ObserveSourceValue()
    {
        if (_completedRevision == _revision && _pendingValue is { } pending &&
            Math.Abs(readValue() - pending) < 0.0001)
            _pendingValue = null;
    }

    public async Task SetAsync(double value, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        cancellationToken.ThrowIfCancellationRequested();
        var revision = ++_revision;
        _pendingValue = value;
        valueChanged();
        var acquired = false;
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            acquired = true;
            // A slow player needs only the most recent value from this drag.
            if (revision != _revision) return;
            await writeValue(value, cancellationToken).ConfigureAwait(true);
            if (revision == _revision)
            {
                _completedRevision = revision;
                ObserveSourceValue();
            }
        }
        catch
        {
            if (revision == _revision)
            {
                _pendingValue = null;
                valueChanged();
            }
            throw;
        }
        finally
        {
            if (acquired) _writeGate.Release();
        }
    }
}
