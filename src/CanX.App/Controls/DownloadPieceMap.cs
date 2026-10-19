// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Numerics;

namespace CanX.App.Controls;

public sealed class DownloadPieceMap : Control
{
    public static readonly StyledProperty<int> PieceCountProperty =
        AvaloniaProperty.Register<DownloadPieceMap, int>(nameof(PieceCount));

    public static readonly StyledProperty<string> BitfieldProperty =
        AvaloniaProperty.Register<DownloadPieceMap, string>(nameof(Bitfield), string.Empty);

    public static readonly StyledProperty<IBrush?> CompletedBrushProperty =
        AvaloniaProperty.Register<DownloadPieceMap, IBrush?>(nameof(CompletedBrush));

    public static readonly StyledProperty<IBrush?> PartialBrushProperty =
        AvaloniaProperty.Register<DownloadPieceMap, IBrush?>(nameof(PartialBrush));

    public static readonly StyledProperty<IBrush?> MissingBrushProperty =
        AvaloniaProperty.Register<DownloadPieceMap, IBrush?>(nameof(MissingBrush));

    static DownloadPieceMap()
    {
        AffectsRender<DownloadPieceMap>(
            PieceCountProperty,
            BitfieldProperty,
            CompletedBrushProperty,
            PartialBrushProperty,
            MissingBrushProperty);
    }

    public int PieceCount
    {
        get => GetValue(PieceCountProperty);
        set => SetValue(PieceCountProperty, value);
    }

    public string Bitfield
    {
        get => GetValue(BitfieldProperty);
        set => SetValue(BitfieldProperty, value);
    }

    public IBrush? CompletedBrush
    {
        get => GetValue(CompletedBrushProperty);
        set => SetValue(CompletedBrushProperty, value);
    }

    public IBrush? PartialBrush
    {
        get => GetValue(PartialBrushProperty);
        set => SetValue(PartialBrushProperty, value);
    }

    public IBrush? MissingBrush
    {
        get => GetValue(MissingBrushProperty);
        set => SetValue(MissingBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (PieceCount <= 0 || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        const double cellSize = 5;
        const double gap = 1;
        var columns = Math.Max(1, (int)Math.Floor((Bounds.Width + gap) / (cellSize + gap)));
        var rows = Math.Max(1, (int)Math.Floor((Bounds.Height + gap) / (cellSize + gap)));
        var bucketCount = Math.Min(PieceCount, columns * rows);

        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var firstPiece = bucket * PieceCount / bucketCount;
            var lastPiece = Math.Max(firstPiece + 1, (bucket + 1) * PieceCount / bucketCount);
            var completed = 0;
            for (var piece = firstPiece; piece < lastPiece; piece++)
            {
                if (IsPieceComplete(Bitfield, piece))
                {
                    completed++;
                }
            }

            var brush = completed == 0
                ? MissingBrush
                : completed == lastPiece - firstPiece ? CompletedBrush : PartialBrush;
            if (brush is null)
            {
                continue;
            }

            var column = bucket % columns;
            var row = bucket / columns;
            context.FillRectangle(brush, new Rect(
                column * (cellSize + gap),
                row * (cellSize + gap),
                cellSize,
                cellSize));
        }
    }

    public static bool IsPieceComplete(string bitfield, int pieceIndex)
    {
        if (pieceIndex < 0)
        {
            return false;
        }

        var nibbleIndex = pieceIndex / 4;
        if (nibbleIndex >= bitfield.Length)
        {
            return false;
        }

        var value = ParseNibble(bitfield[nibbleIndex]);
        return value >= 0 && (value & (1 << (3 - pieceIndex % 4))) != 0;
    }

    public static int CountCompletedPieces(string bitfield, int pieceCount)
    {
        if (pieceCount <= 0 || string.IsNullOrEmpty(bitfield))
        {
            return 0;
        }

        var completed = 0;
        var nibbleCount = Math.Min(bitfield.Length, (pieceCount + 3) / 4);
        for (var index = 0; index < nibbleCount; index++)
        {
            var value = ParseNibble(bitfield[index]);
            if (value < 0)
            {
                continue;
            }

            var validBits = Math.Min(4, pieceCount - index * 4);
            var mask = 0xf << (4 - validBits);
            completed += BitOperations.PopCount((uint)(value & mask));
        }

        return completed;
    }

    private static int ParseNibble(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };
}
