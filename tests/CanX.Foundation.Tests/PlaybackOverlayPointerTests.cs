using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using CanX.App;
using Xunit;
using BindingFlags = System.Reflection.BindingFlags;

namespace CanX.Foundation.Tests;

public sealed class PlaybackOverlayPointerTests
{
    [Fact]
    public async Task ToolTipOpening_DisablesNativePopupHitTesting()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
        await session.Dispatch(() =>
        {
            var window = new PlaybackWindow();
            window.Show();
            var overlay = window.FindControl<Control>("PlaybackOverlay")!;
            ToolTip.SetTip(overlay, "Mute");
            overlay.RaiseEvent(new CancelRoutedEventArgs(ToolTip.ToolTipOpeningEvent));
            var toolTip = Assert.IsType<ToolTip>(ToolTip.GetTip(overlay));
            Assert.Equal("Mute", toolTip.Content);
            Assert.False(toolTip.IsHitTestVisible);

            var platform = System.Reflection.DispatchProxy.Create<IPopupImpl, PopupPlatformProxy>();
            var proxy = (PopupPlatformProxy)platform;
            proxy.Target = window.PlatformImpl!;
            using (var popup = new PopupRoot(window, platform))
            {
                Assert.True(popup.IsHitTestVisible);
                Assert.True(proxy.IsHitTestVisible);
                popup.Content = toolTip;
                popup.Template = new Avalonia.Controls.Templates.FuncControlTemplate<PopupRoot>(
                    (popupControl, nameScope) => toolTip);
                popup.ApplyTemplate();
                Assert.Same(popup, TopLevel.GetTopLevel(toolTip));
                Assert.False(popup.IsHitTestVisible);
                Assert.False(proxy.IsHitTestVisible);
            }
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PointerExit_InsideWindowKeepsOverlayVisible_OutsideWindowHidesIt()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
        await session.Dispatch(() =>
        {
            var window = new PlaybackWindow { Width = 960, Height = 540 };
            window.Show();
            window.UpdateLayout();
            var root = window.FindControl<Control>("PlaybackWindowRoot")!;
            var overlay = window.FindControl<Control>("PlaybackOverlay")!;
            var inside = overlay.TranslatePoint(new Point(10, 10), root)!.Value;
            var pointer = new Pointer(1, PointerType.Mouse, true);

            root.RaiseEvent(new PointerEventArgs(
                InputElement.PointerMovedEvent, root, pointer, root, inside,
                0, new PointerPointProperties(), KeyModifiers.None));
            Assert.Equal(1, overlay.Opacity);
            Assert.True(overlay.IsHitTestVisible);

            root.RaiseEvent(new PointerEventArgs(
                InputElement.PointerExitedEvent, root, pointer, root, inside,
                0, new PointerPointProperties(), KeyModifiers.None));
            Assert.Equal(1, overlay.Opacity);
            Assert.True(overlay.IsHitTestVisible);

            var tooltip = new ToolTip { Content = "Playback controls" };
            var tooltipWindow = new Window { Content = tooltip };
            tooltipWindow.Show();
            Assert.NotNull(TopLevel.GetTopLevel(tooltip));
            Assert.NotSame(window, TopLevel.GetTopLevel(tooltip));
            var tooltipExit = new PointerEventArgs(
                InputElement.PointerExitedEvent, tooltip, pointer, root, new Point(-1, -1),
                0, new PointerPointProperties(), KeyModifiers.None);
            var exitHandler = typeof(PlaybackWindow).GetMethod(
                "OnPlaybackPointerExited", BindingFlags.Instance | BindingFlags.NonPublic)!;
            exitHandler.Invoke(window, [root, tooltipExit]);
            Assert.Same(tooltip, tooltipExit.Source);
            Assert.Equal(1, overlay.Opacity);
            Assert.True(overlay.IsHitTestVisible);
            tooltipWindow.Close();

            root.RaiseEvent(new PointerEventArgs(
                InputElement.PointerExitedEvent, root, pointer, root, new Point(-1, -1),
                0, new PointerPointProperties(), KeyModifiers.None));
            Assert.Equal(0, overlay.Opacity);
            Assert.False(overlay.IsHitTestVisible);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }, CancellationToken.None);
    }

    public class PopupPlatformProxy : System.Reflection.DispatchProxy
    {
        public ITopLevelImpl Target { get; set; } = null!;
        public bool IsHitTestVisible { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == "SetHitTestVisible")
            {
                IsHitTestVisible = (bool)arguments![0]!;
                return null;
            }
            if (method.Name == "SetWindowManagerAddShadowHint")
                return null;
            return method.Invoke(Target, arguments);
        }
    }

    private sealed class TestApplication : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApplication>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}