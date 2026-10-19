// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace CanX.App;

internal static partial class OwnedStoragePicker
{
    public static Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(
        TopLevel owner,
        FilePickerOpenOptions options) =>
        RunAsync(owner, storage => storage.OpenFilePickerAsync(options));

    public static Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(
        TopLevel owner,
        FolderPickerOpenOptions options) =>
        RunAsync(owner, storage => storage.OpenFolderPickerAsync(options));

    private static async Task<T> RunAsync<T>(TopLevel owner, Func<IStorageProvider, Task<T>> show)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(show);

        using var activationGuard = WindowsDialogActivationGuard.Start(owner);
        try
        {
            return await show(owner.StorageProvider).ConfigureAwait(true);
        }
        finally
        {
            if (owner is Window { IsVisible: true } window)
            {
                window.Activate();
            }
        }
    }

    private sealed partial class WindowsDialogActivationGuard : IDisposable
    {
        private const uint GetWindowOwner = 4;
        private const int WindowLongOwner = -8;
        private const uint SetWindowPositionNoSize = 0x0001;
        private const uint SetWindowPositionNoMove = 0x0002;
        private const uint SetWindowPositionNoActivate = 0x0010;
        private static readonly nint TopMost = new(-1);
        private static readonly nint NotTopMost = new(-2);

        private readonly nint ownerHandle;
        private readonly bool keepTopMost;
        private readonly uint processId;
        private readonly Lock callbackLock = new();
        private readonly Timer timer;
        private nint promotedDialog;
        private bool disposed;

        private WindowsDialogActivationGuard(nint ownerHandle, bool keepTopMost)
        {
            this.ownerHandle = ownerHandle;
            this.keepTopMost = keepTopMost;
            processId = checked((uint)Environment.ProcessId);
            timer = new Timer(ActivateDialog, null, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(150));
        }

        public static IDisposable? Start(TopLevel owner)
        {
            if (!OperatingSystem.IsWindows() ||
                owner.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } platformHandle)
            {
                return null;
            }

            if (owner is Window window)
            {
                window.Activate();
            }
            BringWindowToTop(platformHandle.Handle);
            SetForegroundWindow(platformHandle.Handle);
            return new WindowsDialogActivationGuard(
                platformHandle.Handle,
                owner is Window { Topmost: true });
        }

        public void Dispose()
        {
            lock (callbackLock)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                timer.Dispose();
                RestorePromotedDialog();
            }
        }

        private void ActivateDialog(object? state)
        {
            lock (callbackLock)
            {
                if (disposed)
                {
                    return;
                }

                var dialog = FindOwnedDialog();
                if (dialog == nint.Zero)
                {
                    RestorePromotedDialog();
                    return;
                }

                // Some Shell versions create IFileDialog with an intermediate or
                // missing owner. Repairing GWLP_HWNDPARENT gives Windows a durable
                // modal relationship instead of relying only on foreground calls.
                if (!IsOwnedBy(dialog, ownerHandle))
                {
                    _ = SetWindowLongPtr(dialog, WindowLongOwner, ownerHandle);
                }

                var foreground = GetForegroundWindow();
                var applicationIsForeground = foreground == ownerHandle ||
                    foreground == dialog ||
                    IsOwnedBy(foreground, ownerHandle);
                if (!keepTopMost && !applicationIsForeground)
                {
                    RestorePromotedDialog();
                    return;
                }

                const uint flags = SetWindowPositionNoSize |
                    SetWindowPositionNoMove |
                    SetWindowPositionNoActivate;
                _ = SetWindowPos(dialog, TopMost, 0, 0, 0, 0, flags);
                promotedDialog = dialog;

                // Only take focus back when the disabled owner itself is in front.
                // Do not steal focus after the user deliberately switches apps.
                if (foreground == ownerHandle)
                {
                    _ = BringWindowToTop(dialog);
                    _ = SetForegroundWindow(dialog);
                }
            }
        }

        private nint FindOwnedDialog()
        {
            var dialog = nint.Zero;
            while ((dialog = FindWindowEx(nint.Zero, dialog, "#32770", null)) != nint.Zero)
            {
                _ = GetWindowThreadProcessId(dialog, out var dialogProcessId);
                if (!IsWindowVisible(dialog))
                {
                    continue;
                }

                if (IsOwnedBy(dialog, ownerHandle) ||
                    dialogProcessId == processId && !IsWindowEnabled(ownerHandle))
                {
                    return dialog;
                }
            }

            return nint.Zero;
        }

        private void RestorePromotedDialog()
        {
            if (keepTopMost || promotedDialog == nint.Zero)
            {
                return;
            }

            const uint flags = SetWindowPositionNoSize |
                SetWindowPositionNoMove |
                SetWindowPositionNoActivate;
            _ = SetWindowPos(promotedDialog, NotTopMost, 0, 0, 0, 0, flags);
            promotedDialog = nint.Zero;
        }

        private static bool IsOwnedBy(nint window, nint expectedOwner)
        {
            for (var owner = GetWindow(window, GetWindowOwner);
                 owner != nint.Zero;
                 owner = GetWindow(owner, GetWindowOwner))
            {
                if (owner == expectedOwner)
                {
                    return true;
                }
            }

            return false;
        }

        [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

        [LibraryImport("user32.dll")]
        private static partial nint GetWindow(nint window, uint command);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static partial nint SetWindowLongPtr(nint window, int index, nint newValue);

        [LibraryImport("user32.dll")]
        private static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowEnabled(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool BringWindowToTop(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(nint window);
    }
}
