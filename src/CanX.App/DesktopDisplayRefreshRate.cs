// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CanX.App;

/// <summary>
/// Supplies the display cadence that libmpv cannot discover when the host owns
/// the window and presents Render API framebuffers itself.
/// </summary>
internal static partial class DesktopDisplayRefreshRate
{
    private const int VerticalRefresh = 116;

    public static double? DetectPrimary()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return DetectWindows();
            }

            if (OperatingSystem.IsMacOS())
            {
                return DetectMacOS();
            }

            if (OperatingSystem.IsLinux())
            {
                return DetectLinux();
            }
        }
        catch
        {
            // The explicit display-fps-override mpv option remains available.
        }

        return null;
    }

    internal static double? ParseXrandr(string output)
    {
        double? firstCurrentRate = null;
        double? currentOutputRate = null;
        var currentOutputIsPrimary = false;

        foreach (var line in output.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                if (currentOutputIsPrimary && currentOutputRate is not null)
                {
                    return currentOutputRate;
                }

                currentOutputIsPrimary = line.Contains(" connected primary ", StringComparison.Ordinal);
                currentOutputRate = null;
                continue;
            }

            if (!line.Contains('*', StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var marker = token.IndexOf('*');
                if (marker <= 0 ||
                    !double.TryParse(token[..marker], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ||
                    rate is < 20 or > 1000)
                {
                    continue;
                }

                currentOutputRate = rate;
                firstCurrentRate ??= rate;
                if (currentOutputIsPrimary)
                {
                    return rate;
                }

                break;
            }
        }

        return currentOutputIsPrimary && currentOutputRate is not null
            ? currentOutputRate
            : firstCurrentRate;
    }

    private static double? DetectLinux()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "xrandr",
                Arguments = "--current",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.Environment["LC_ALL"] = "C";
        if (!process.Start())
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(2000))
        {
            process.Kill(entireProcessTree: true);
            return null;
        }

        _ = errorTask.GetAwaiter().GetResult();

        return process.ExitCode == 0
            ? ParseXrandr(outputTask.GetAwaiter().GetResult())
            : null;
    }

    private static double? DetectWindows()
    {
        var deviceContext = GetDC(0);
        if (deviceContext == 0)
        {
            return null;
        }

        try
        {
            var rate = GetDeviceCaps(deviceContext, VerticalRefresh);
            return rate is >= 20 and <= 1000 ? rate : null;
        }
        finally
        {
            _ = ReleaseDC(0, deviceContext);
        }
    }

    private static double? DetectMacOS()
    {
        var mode = CGDisplayCopyDisplayMode(CGMainDisplayID());
        if (mode == 0)
        {
            return null;
        }

        try
        {
            var rate = CGDisplayModeGetRefreshRate(mode);
            return rate is >= 20 and <= 1000 ? rate : null;
        }
        finally
        {
            CGDisplayModeRelease(mode);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDeviceCaps(nint deviceContext, int index);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial uint CGMainDisplayID();

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial nint CGDisplayCopyDisplayMode(uint display);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial double CGDisplayModeGetRefreshRate(nint mode);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial void CGDisplayModeRelease(nint mode);
}
