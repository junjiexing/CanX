// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Streaming;

/// <summary>
/// Provides blocking, random-access media reads to a native playback consumer.
/// A temporary lack of data must block until data, cancellation, or a terminal
/// error is available; returning zero means end of stream.
/// </summary>
public interface IMediaByteStream : IDisposable
{
    bool CanSeek { get; }

    long? Length { get; }

    int Read(Span<byte> destination, CancellationToken cancellationToken);

    long Seek(long absoluteOffset, CancellationToken cancellationToken);
}
