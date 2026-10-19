# 播放期间的防息屏策略

视频处于 `Playing` 状态时申请防息屏；暂停、停止、播放结束、错误或销毁会话时释放。
缓冲期间只要用户没有暂停，仍按播放中处理。窗口失去焦点、最小化、隐藏、Android Activity
切到后台或重建都不释放。每个会话独立持有系统请求，关闭其中一个不会影响其他播放会话。
纯音频和音频封面不申请屏幕常亮。

请求直接订阅播放事件，不依赖 UI Dispatcher，也不依赖 mpv 的 `stop-screensaver`
选项。异步申请在暂停或关闭后才完成时会立即释放；系统接口失败写入日志并限速重试，
不会中断播放。不修改用户的系统电源设置，不主动点亮已经手动关闭的屏幕。

| 平台 | 实现 |
| --- | --- |
| Windows | 每会话使用 `PowerCreateRequest`，设置 `DisplayRequired` 和 `SystemRequired`，释放后关闭句柄；没有线程亲和要求。 |
| macOS | 每会话持有 `PreventUserIdleDisplaySleep` 和 `PreventUserIdleSystemSleep` 的 IOPM assertion。 |
| Linux X11 / Wayland | 依次尝试 GNOME SessionManager、freedesktop ScreenSaver / PowerManagement、XDG Inhibit portal。每会话独立保留 D-Bus 连接，断开时服务清理请求；后台播放不依赖可见的 Wayland surface。需要桌面提供其中一种服务。 |
| Android | 应用级 `SCREEN_BRIGHT_WAKE_LOCK`，配合现有后台播放前台服务。窗口级 `KEEP_SCREEN_ON` 在后台无效，因此不再使用它。需要普通 `WAKE_LOCK` 权限，无运行时授权弹窗。 |
| iOS | 原型工程提供 `IosDisplaySleepInhibitor.AcquireAsync`，在主线程以引用计数管理 `UIApplication.IdleTimerDisabled`，最后一个请求释放后恢复原值。 |

iOS 目前没有正式播放宿主，适配器尚无真实播放入口；接入播放器时将其传给
`PlaybackViewModel.ConfigureDisplaySleepInhibition`。它不会因退到后台主动释放请求，
但 iOS 不允许普通应用保证其他应用在前台时仍禁止自动锁屏，也不允许通过后台音频模式
绕过这一限制。原型没有为此虚报后台音频能力。

回归测试覆盖状态切换、后台 UI 队列停滞、多会话、取消与释放竞态、错误恢复，以及
Windows 原生请求的申请与跨线程释放。实际平台验收应将系统自动息屏时间设为较短值，
分别验证前台、最小化/后台、暂停、恢复、结束与退出，并确认手动锁屏仍可使用。
