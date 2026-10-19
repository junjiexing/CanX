# 阶段 7：Android 实现与验证

- 日期：2026-09-20
- 状态：ARM64 真机 smoke 通过；x64 构建与依赖审计通过、模拟器运行待验收
- 目标：Android 6.0（API 23）或更新版本，ARM64 设备与 x64 模拟器
- Runtime：.NET 10 NativeAOT（Android workload 仍标记为 experimental）

## 已完成范围

Android 宿主已接入下载、播放、本地/SMB/FTP/FTPS/WebDAV 媒体源、QuickJS 插件、Agent、设置、
109 个 Tool、SAF 文件选择、通知、下载前台服务和 MediaSession。移动端使用同一套直角视觉语言，
播放源浏览保留在主 Activity；视频 Surface、进度与播放控制在独立的沉浸式全屏
`PlaybackActivity` 中呈现，不把画面内嵌在主界面，也不模拟桌面窗口。

Android task 使用普通的 launcher 恢复语义：`MainActivity` 为 `singleTop`，不再使用会在重复点击桌面
图标时清除其上 Activity 的 `singleTask`。后台媒体通知按播放状态直接指向 `PlaybackActivity`，下载
完成通知指向主界面，两者均使用 `ReorderToFront` 而不是 `ClearTop`，因此恢复 App 不会销毁现有
播放器或重复创建 Render Context。

播放器 Activity 默认采用 Android `FullUser` 方向策略：用户尚未点击播放器内的“横/竖屏”按钮时，
遵循系统方向锁定；系统允许自动旋转时则接受传感器的四向旋转。首次点击按钮后切换为 App 手动策略，
在 `SensorLandscape` 与 `SensorPortrait` 之间切换并忽略系统方向锁定。横屏始终使用
`SensorLandscape` 而不是固定的 `Landscape`/`ReverseLandscape`，因此手机在左右横屏间掉转时仍会
自动选择对应方向。手动策略随 Activity 状态保存，可跨系统重建恢复，关闭本次播放 Activity 后重置。

aria2 初始化恢复任务时会在 `StartAsync` 返回前发布任务列表。移动 ViewModel 会忽略这份启动期事件，
再直接应用引擎的权威快照；视觉树挂载回调返回后才执行模块初始化和创建首个任务列表。后台事件显式
投递到 Avalonia UI 上下文，常规 UI 点击则在当前 UI 线程直接执行，避免 Android 输入事件因二次
Dispatcher 投递而无法完成。轮询只发布有意义的任务或全局状态变化。

外部本地文件夹采用按需授权：Android 11 及以上在用户第一次选择播放源或下载目录时打开系统
“所有文件访问权限”页面，Android 6–10 使用运行时读写权限；授权成功后再显示系统目录选择器。
应用会将 `com.android.externalstorage.documents` 返回的目录 URI 严格解析到对应存储卷内的规范路径。
这是 aria2 写入目录以及 libmpv 直接读取媒体、字幕所必需的；单文件打开仍使用 SAF 和进程内
Stream Callback，不要求全盘访问。

## 原生链接形态

| APK 内模块 | 链接方式 | 动态依赖边界 |
|---|---|---|
| `libCanX.App.Android.so` | NativeAOT 主模块；静态合入 libmpv、FFmpeg、libplacebo、字幕栈、QuickJS、OpenSSL、zlib 等可控依赖 | Android `libandroid`、`liblog`、`libEGL`、`libOpenSLES`、`libmediandk`、`libdl`、`libm`、`libc` |
| `libcanx_aria2.so` | 自有 C ABI sidecar；内部静态包含 fork 的 libaria2、OpenSSL、zlib、SQLite、Expat、c-ares、libssh2 与 libc++，archive 符号全部隐藏 | Android `liblog`、`libandroid`、`libm`、`libdl`、`libc` |
| `libSkiaSharp.so` | Avalonia/SkiaSharp NuGet runtime asset | Android EGL/GLES、log、dl、m、c |
| `libHarfBuzzSharp.so` | Avalonia/HarfBuzzSharp NuGet runtime asset | Android dl、m、c |

aria2 独立 sidecar 是有意的 ABI 隔离：它不是系统预装或下载的预编译 aria2，仍完全来自锁定源码；
隔离后不会让静态 C++ runtime 与 NativeAOT/Avalonia native 模块互相抢占符号。

NativeAOT 主模块还必须公开 .NET Android TLS 信任管理器调用的
`Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate` JNI 入口。该实现来自随
.NET runtime pack 提供并静态合入的 `libSystem.Security.Cryptography.Native.Android.a`，不是新增
sidecar。发布脚本会检查 APK 中主模块的动态符号表；若入口被链接器裁剪或隐藏，构建直接失败，避免
所有 HTTPS 请求直到真机运行时才暴露为证书握手错误。

## 构建与产物

```bash
scripts/publish-static-app-android.sh arm64-v8a
scripts/publish-static-app-android.sh x86_64
```

已有 native 输出时可以使用 `--skip-native`。本次生成：

正式 APK 内的 NativeAOT 主模块已剥离调试段。对应的 `CanX.App.Android.so.dbg`
在 `artifacts/symbols/android-arm64-full/` 或 `artifacts/symbols/android-x64-full/`，不放进 APK。

需要通过 ADB `run-as` 读取应用私有日志或做其他真机诊断时，可另外生成可调试的 NativeAOT 验证包；
该开关不会改变默认正式产物，输出目录也与正式包隔离：

```bash
CANX_ANDROID_DEBUGGABLE=true scripts/publish-static-app-android.sh arm64-v8a --skip-native
```

| ABI | APK | SHA-256 |
|---|---|---|
| ARM64 | `artifacts/android/android-arm64-full/org.canx.app-Signed.apk` | `b3b80b5396add7a93672b342de3055d8dd228219883b426fe741172cbe4aaade` |
| x64 | `artifacts/android/android-x64-full/org.canx.app-Signed.apk` | `ca8d4313dfba3b29040c4e9039ce049fb3f07bf7d939d3df99efd4f994f7c597` |

Android 播放器在用户选择默认的 `hwdec=auto-safe` 时，会将实际启动顺序细化为
`mediacodec,mediacodec-copy`：优先通过 `AImageReader/EGLImage` 把 MediaCodec 解码 surface
直接交给 GPU 渲染，不支持该 interop 的设备或编码格式再回退到复制路径。用户显式选择的其他
硬解模式不受此策略影响。默认的 `audio-channels=auto-safe` 在 Android 上细化为 `stereo`，由 mpv
在进入 AudioTrack 前完成多声道下混；用户为 HDMI 等输出显式选择的声道布局仍会保留。这样可绕过
部分厂商 AudioTrack 虽接受 5.1 float channel mask、实际输出路径却持续欠载的问题。

Android 合成器负责 VSync，Render API 不再叠加桌面端的软件 swap 节拍。Avalonia 当前也不暴露
其实际 `eglSwapBuffers` 完成信号，因此 Android 宿主不会调用可选的
`mpv_render_context_report_swap()`；一旦用晚到的近似时刻调用该 API，libmpv 会把宿主视为可靠的
swap reporter 并逐帧等待，实测会形成约 200 ms 的帧供应延迟。mpv 的帧通知使用可及时唤醒
Android Looper 的调度优先级，避免视频提交退化到状态轮询频率。
CanX 的 mpv fork 同步了上游 AImageReader 队列修复：每次提交 MediaCodec buffer 前清除旧的
image-available 信号，等待该 buffer 对应的新回调，并为 `acquireLatestImage` 保留 Android 文档要求的
两个空闲槽。这样 seek flush 后不会把旧回调误认为新帧并连续等待超时。

两个 APK 都只包含各自目标 ABI，没有托管 DLL。链接 map 已确认 libmpv、QuickJS、FFmpeg 和
libplacebo archive 进入主 NativeAOT 模块；`readelf` 已确认 aria2 sidecar 不依赖第三方动态库。

## 真机验证

验证设备为 nubia `NX779J`、Android 16、ARM64。最终 ARM64 APK 使用覆盖安装保留测试数据，完成：

- 启动并显示“全部模块已就绪 · 109 个 Tool”；
- 带恢复任务启动，持续跨越多个 10 秒 aria2 轮询周期，下载状态仍实时更新且无输入 ANR；
- 连续 8 次切换“下载中/已完成”，覆盖 Avalonia `ListBox` 虚拟化容器回收和空数据项模板路径；
- 下载、媒体、插件、Agent、设置五个页面连续切换；
- 下载任务与播放源应用内浮层打开、取消；
- 首次选择本地文件夹会显示系统“所有文件访问权限”页，授权后自动继续目录选择；Movies 被正确
  解析为 `/storage/emulated/0/Movies`，可添加、读取和删除播放源；
- 删除播放源时的 Avalonia 虚拟化空项路径无异常，所有 Android 列表共用空项安全模板；
- 下载列表启动恢复采用原子快照握手，Activity 每次返回前台都会以进程内 aria2 管理器的完整任务快照重新对账，避免后台下载仍存在但重建后的界面漏项；
- 本地播放从主界面进入独立 `PlaybackActivity`；播放器隐藏系统标题栏、状态栏与导航栏，视频 Surface
  填满整个 Activity，控制层在触摸时显示并于 3 秒后自动隐藏；
- 播放中按 Home 后，点击桌面图标或 CanX 前台媒体通知均恢复同一个 `PlaybackActivity`；ADB task
  记录始终保持两层且实例 ID 不变，视频恢复后继续前进，没有退回主界面、重复播放器或清栈；
- 使用 3840×1600、HEVC Main 10 测试片连续播放，实际路径为
  `mediacodec + aimagereader`；解码丢帧为 0，约 72 秒连续采样输出丢帧为 0，Render Surface
  使用双缓冲以约 60 fps 提交且渲染/呈现帧严格对应；render 平均约 1.57 ms、P95 约 4.3 ms，
  submit 平均约 30.8 ms、P95 约 39 ms。更长时间采样偶有显示 deadline miss，仍需扩充设备矩阵；
- 在同一测试片中将进度从约 22 分钟拖到 1:47:45，释放后 0.3 秒内已显示目标画面，1 秒时画面与
  时间继续前进到 1:47:46；未再出现 seek flush 后约 3 秒的画面停顿；
- 在小米 M2011K2G（Adreno 660）使用同一 4K HEVC Main 10 / AAC 5.1 文件复测：MediaCodec 仍走
  `mediacodec + aimagereader` 零拷贝路径；自动声道由 mpv 下混为 48 kHz stereo 后，原先约每
  1–2 秒一次的 AudioTrack 欠载消失，seek 后仅记录一次预期的设备重启欠载，随后 8 秒屏幕录制
  保持连续变化（193 帧，未出现约 3 秒重复帧区间）。主 Activity 与播放器 Activity 的内部切换
  采用 500 ms 可取消的后台判定，并在服务 `OnCreate` 最前面发布前台通知，未再触发
  `ForegroundServiceDidNotStartInTimeException`；
- 返回主 Activity 后播放源选择和操作按钮保持原状态；再次进入会创建新的播放器 Activity 与 Render
  Context。快速重复点击入口只产生一个播放器 Activity，未重复初始化 native 引擎；播放器内 SAF
  字幕选择器的 result 会返回当前播放器 Activity；
- 未出现 `mpv startup options cannot be changed after playback has started`、崩溃或新增 ANR。
- 插件宿主通过受限 HTTP 客户端分别访问 `mikanani.me` 和 `api.bgm.tv` 成功；TLS 证书回调可由
  Android Conscrypt 正常解析，未再出现 `DotnetProxyTrustManager.verifyRemoteCertificate` 的
  `UnsatisfiedLinkError`。

Foundation、Plugins、Agent 测试分别为 267/267、226/226、19/19 通过，其中包括启动期下载列表
事件回归测试。x64 APK 已完成构建、ABI 和依赖审计；本机没有可用的 x64 模拟器，因此不能宣称
x64 运行通过。

## 剩余验收

- 在 Android x64 模拟器执行同一启动、导航和生命周期 smoke；
- 使用 Android SAF 实测更多本地媒体、字幕和大文件，并覆盖 MediaCodec 设备矩阵；
- 在 Android 上补齐真实 SMB、FTP/FTPS、WebDAV、BT/磁力下载和边下边播的长时间/断网矩阵；
- 验证厂商后台限制、电量优化、通知权限拒绝和进程回收后的任务恢复；
- 若以后通过 Google Play 分发，需在商店审核前确认 `MANAGE_EXTERNAL_STORAGE` 政策资格，或为
  aria2 增加基于 SAF 文档树的存储后端；侧载 APK 不受商店审核限制；
- 发行前配置正式签名、版本号、GPL 对应源码与 Android 包级 SBOM。
