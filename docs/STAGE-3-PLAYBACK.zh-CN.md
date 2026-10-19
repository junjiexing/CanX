# 阶段 3：播放器实现状态

更新时间：2026-09-20

## 当前结论

阶段 3 的基础播放、窗口、设置、输入、轨道/字幕、播放列表/历史记录、章节/书签、高级播放控制、硬解/色彩/性能诊断、versioned mpv capability manifest，以及 Linux x64 自动稳定性子阶段已经完成，但阶段 3 尚未关闭。Linux x64 上的正式 `CanX.Playback` 模块已替代阶段 0 Spike：Avalonia 主窗口可通过 libmpv Render API 播放本地文件与 HTTP/HTTPS 媒体，开发态 DLL 和静态 NativeAOT App 均已真实渲染视频。播放窗口的完整输入、高级控制、诊断面板、能力发现和 120 秒稳定性流程已于 2026-09-10 通过 Wayland/X11 真实播放验收；实际多显示器、系统休眠唤醒和其他目标平台仍待对应环境验收。

2026-09-20 开始 Direct Composition 呈现优化子阶段。现有 Linux/Android 硬解正常路径没有 CPU readback，但 `shared-worker` 在 mpv Render API 之后仍有一次 CanX 自有全帧 GPU blit 和两个阻塞式 `glFinish()`。目标是在保留 mpv Shader、字幕、OSD、色彩处理和 Avalonia 浮层的前提下，用 Avalonia composition 可导入共享纹理移除这一次 blit，并把同步改为异步所有权交接；完整计划见下方“Direct Composition 优化计划”。

## Direct Composition 优化计划

当前链路：

```text
硬解 surface
  → mpv GPU render
  → CanX worker 中间纹理
  → glFinish
  → glBlitFramebuffer 到 OpenGlControlBase framebuffer
  → glFinish
  → Avalonia compositor GPU snapshot
  → 屏幕
```

目标链路：

```text
硬解 surface
  → mpv 直接渲染到 Avalonia 可导入共享纹理
  → GPU fence 或 glFlush + 异步所有权交接
  → CompositionDrawingSurface / CompositionSurfaceVisual
  → Avalonia compositor
  → 屏幕
```

实施按可独立回归和提交的里程碑推进：

1. **已完成—诊断与基线**：增加呈现后端、同步模式、CPU readback、CanX GPU copy 次数、render/submit/present 计数和平均/P95 时间；mpv 日志、桌面/Android 播放诊断面板与 smoke 输出使用同一快照。保留现有 smoke/stability 数据作为优化前基线。
2. **已完成—`composition-shared` 后端**：通过 `CreateSharedTextureForComposition()` 创建共享纹理，由 mpv worker 直接渲染；用 `CompositionDrawingSurface.UpdateAsync()` 发布到 `CompositionSurfaceVisual`，提交完成前不复用对应 slot。桌面使用三缓冲，Android 根据真机 compositor 延迟使用双缓冲。窗口 resize 时先由 visual 流畅缩放上一帧，停止连续 resize 80 ms 后再异步重建精确尺寸纹理，避免在每个 configure 事件中分配 GPU 资源。
3. **已完成—同步与回退**：主路径按 Avalonia OpenGL shared-texture 契约采用 `glFlush + UpdateAsync` 自动同步；旧 shared-worker 探测 `glFenceSync/glWaitSync`，可用时用跨共享上下文 GPU fence 去掉 producer 侧 `glFinish`，不可用时保留完整等待。固定回退顺序为 `composition-shared` → `shared-worker-fence` → `shared-worker-finish` → `inline`，初始化失败和 context loss 均自动降级并记录原因。
4. **已完成—Linux 验收**：Wayland/X11 使用 4K HEVC 10-bit 对比旧基线，覆盖播放、seek、连续缩放、全屏、Render Context 重建和稳定性；新后端的 CanX 自有 GPU copy 与逐帧 `glFinish()` 均为 0。
5. **已完成—Android 验收**：Android 16 / Adreno 750 真机覆盖 MediaCodec/AHardwareBuffer、方向切换、Home 后从桌面图标和媒体通知恢复、seek 和浮层；解码保持 `mediacodec + aimagereader`，没有退化到 `mediacodec-copy` 或呈现回退后端。
6. **已完成—二次决策**：保留 Avalonia OpenGL compositor 内部的一次 GPU snapshot。Linux 的 render/submit 已远低于帧预算，Android 双缓冲也稳定贴合实际 60 Hz；现有数据不能证明 snapshot 是主要瓶颈，因此暂不维护 Avalonia fork，也不改用会牺牲统一 Shader、字幕、OSD、tone mapping 和 Avalonia 浮层的 `mediacodec_embed`。

验收数据必须区分“解码 surface → mpv”的 CPU/GPU 传输和“mpv → compositor”的呈现 copy，不能再用单一的 `hardware-zero-copy-interop` 概括端到端路径。功能开关允许强制选择各后端，便于驱动兼容性回归；默认仍自动选择当前设备上最高级且已成功初始化的后端。

第一里程碑的 Linux x64 NativeAOT 基线已通过 Wayland/X11 实播。两者均报告 `frameTransfer=hardware-zero-copy-interop`、`presentationBackend=shared-worker-finish`、`cpuReadback=false`、`copiesPerFrame=1`；本次 640×360/30 fps fixture 在 Wayland 得到 render 平均 32.646 ms、P95 33.855 ms、submit 平均 0.096 ms、P95 0.105 ms，在 X11 得到 render 平均 32.605 ms、P95 33.824 ms、submit 平均 0.095 ms、P95 0.107 ms。render 时间包含 worker 侧等待 GPU 完成和 mpv 的显示同步，不能直接解释为纯 shader 执行耗时；这些数据的用途是给同机、同媒体、同设置的新后端作前后对比。

本次代码已通过 265 项 Foundation、226 项 Plugins、19 项 Agent 测试，以及静态 NativeAOT 依赖审计（`needed=5 sidecars=0`）。Android 共享 UI 代码已纳入编译设计，但当前 .NET SDK 10.0.400 未安装 Android workload，因此本里程碑没有声称完成 Android APK 编译或真机验收；该项保留到 Android 优化步骤。

第二里程碑已在 Linux x64 的 Wayland/X11 上通过完整播放 smoke、15 秒稳定性流程和用户指定的 4K HEVC Main 10 文件实播。新路径报告 `presentationBackend=composition-shared`、`sync=automatic-gl-flush`、`copiesPerFrame=0`、`gpuCopies=0`、`blockingFinishes=0`；Avalonia 12.1.2 的 `GlSkiaExternalObjectsFeature` 仍通过 `CopyTexSubImage2D` 生成 compositor snapshot，因此另行诚实报告 `compositorCopiesPerFrame=1`。4K 流程覆盖三次尺寸变化、两次 seek、隐藏/恢复及 Render Context 重建，得到 `decoderDrops=0`、`outputDrops=3`，render 平均 0.194 ms、P95 0.324 ms，submit 平均 0.128 ms、P95 0.151 ms。

第三里程碑的 `shared-worker-fence` 已用同一 4K 文件在 X11 强制启用并通过：`copiesPerFrame=1`，但 `blockingFinishes` 从旧后端每轮 2 次降为 1 次，producer render 平均 0.214 ms、P95 0.346 ms。`CANX_MPV_PRESENTATION_BACKEND` 可设为 `composition-shared`、`shared-worker-fence`、`shared-worker-finish` 或 `inline`；对应 smoke 通过 `CANX_EXPECTED_PRESENTATION_BACKEND` 校验实际选择，便于驱动回归。主路径的 `automatic-gl-flush` 不是 CPU 忙等：Avalonia 在 compositor render thread 中按队列顺序 snapshot，`UpdateAsync` 完成后才归还三缓冲 slot。

Android ARM64 首轮真机数据确认 4K HEVC Main 10 使用 `mediacodec + aimagereader`，呈现后端为
`composition-shared`，CPU 回读、CanX GPU copy 和 `glFinish` 均为 0。该设备的
`CompositionDrawingSurface.UpdateAsync()` 平均约 31 ms 完成；若沿用桌面三缓冲，会形成约 90 次/秒的
并发重复提交。Android 因此采用双缓冲，桌面仍保留三缓冲，以便让一张纹理在 compositor 中更新时另一张
继续由 mpv 渲染，同时把 60 Hz 屏幕上的无效提交和排队延迟限制在合理范围。双缓冲后每 10 秒的
render/submit/present 均约增加 607，render 平均约 1.57 ms、P95 约 4.3 ms，submit 平均约 30.8 ms、
P95 约 39 ms；提交频率从三缓冲的约 90 次/秒收敛为实际显示速率约 60 次/秒。

最终 Android 流程还验证了关键帧 seek、横竖屏切换、Home 后从桌面图标恢复，以及从前台媒体通知恢复。
主 Activity 不再使用会清除栈顶播放器的 `singleTask`；所有恢复 Intent 也不再使用 `ClearTop`，而是将
已有 Activity `ReorderToFront`。两条恢复路径均保持同一个 `MainActivity → PlaybackActivity` task，
不会重复创建播放器，恢复后画面继续更新且后端仍为 `composition-shared`。方向切换时 AImageReader
可能出现约 3 次、总计不到 1 秒的等待超时，随后自动取得新尺寸帧；未发生 CPU copy、后端回退、持续
黑屏或播放状态丢失，因此作为 Android Surface 重配置期间的瞬态记录，而不是常态播放故障。

当前已实现：

- libmpv C API 的 source-generated P/Invoke，以及与 UI 无关的播放引擎、控制器和状态快照；
- `MpvVideoSurfaceHost` 默认使用 `CompositionSurfaceVisual` 视频 surface；mpv worker 直接渲染到 Avalonia 可导入的共享纹理（桌面三缓冲、Android 双缓冲），以 `glFlush` 和 `CompositionDrawingSurface.UpdateAsync()` 异步交接，不再经过 CanX 中间 framebuffer blit 或逐帧 `glFinish()`。初始化失败时自动切换到原 `OpenGlControlBase` 的 shared-worker，后者不支持时再使用 inline 兼容路径；
- worker 完成帧只在 Avalonia Dispatcher 中保留一个待处理通知，既持续唤醒 surface 呈现，又不会以视频帧率堆积 UI 工作项；Render Context 丢失或后台 worker 异常后会重建渲染管线，最多连续重试 3 次，并恢复丢失前选中的视频轨道，避免 libmpv 在 context 被销毁后停留于 `vid=no`；
- 打开本地音视频、HTTP/HTTPS 地址、文件选择和向播放器页面拖放文件/地址；
- 主窗口保留媒体浏览页；阶段 4 完成后，该页统一列出已添加的本地文件夹与网络播放源，并通过单个应用内浮层添加来源。桌面端只将视频画面、进度和播放控制放入单实例独立窗口；Android 将这些内容放入独立的沉浸式全屏 `PlaybackActivity`，iOS 使用等价的独立全屏播放器 View；
- 播放/暂停、停止、绝对/相对 seek、音量、静音和 0.5×/1×/1.5×/2× 倍速；
- 播放操作区以半透明浮层叠加在画面底部，鼠标进入或移动时显示，静止 3 秒或移出播放窗口后隐藏；浮层控件保持当前设计尺寸为上限，窗口宽度不足时整体等比缩小，避免窄窗口裁切操作项；
- 视频画面无外边距填满播放窗口客户区；Wayland 下先在透明窗口中初始化 libmpv，取得显示尺寸后以最终尺寸重新映射并交给 compositor 居中；普通窗口在 Wayland configure 进入 Avalonia 布局前约束宽高比，并像 mpv 一样在一次拖拽内锁定宽或高作为约束基准，避免客户端事后修正及约束轴反复切换造成的抖动、回弹和卡顿；
- 时长、当前位置、媒体标题、视频分辨率、可 seek 状态和基础轨道元数据；
- F11/按钮进入全屏，F11/Esc 退出并恢复进入全屏前的窗口状态；空格播放/暂停，左右方向键前后跳转 5 秒；
- 快速连续的控制命令串行执行，停止或播放结束后再次播放会重新打开当前媒体；
- 视频 surface 先于 libmpv 控制器初始化时可延迟建立 render context，关闭窗口时先拆除 GL surface 再销毁 mpv；
- `playback.*` 基础 Tool，状态查询会脱敏 URL 中的用户名、密码、query 和 fragment。
- Linux x64 通用硬件加速构建：FFmpeg 同时编入 AMF、QSV/oneVPL、VAAPI、Vulkan Video、OpenCL 与 NVDEC/CUDA，libmpv 同时编入 VAAPI/DRM/DMABUF、Vulkan 与 CUDA interop；构建机的 GPU 厂商不决定发布能力。
- 设置页可即时启停日志；mpv 客户端事件写入独立 `mpv.log`，libaria2 原生日志写入独立 `aria2.log`，Avalonia 与应用生命周期写入 `app.log`，并为后续 QuickJS runtime 预留 `quickjs.log` 通道。
- 播放设置已按官方 mpv 0.41 选项文档分为播放/解码、画面处理、色彩/HDR、音频、字幕、网络/缓存六组常用控件；硬解、同步、缩放、目标色域/传递函数、tone mapping、语言、字幕自动加载和缓存等选项使用下拉框、复选框或有单位的紧凑文本框。
- 长尾 mpv 选项通过 `mpv.conf` 风格的高级文本配置，每行一个 `option=value`；支持省略布尔项的 `=yes`、空行、行内 `#` 注释、引号、固定长度字符串和可选 `--` 前缀。保存前使用 `mpv_set_option_string` 对当前构建实际支持的选项名、值与范围逐项校验；常用控件覆盖高级文本中的同名项。profile 段具有条件恢复等运行期语义，暂不在这个启动选项编辑器中展开。
- 播放设置随应用设置持久化，在 libmpv 初始化前一次性注入。运行中保存会明确提示下次启动生效；`config`、`vo`、`wid`、`gpu-api`、`gpu-context` 等宿主契约选项不可覆盖，并在 native 初始化末尾再次强制设置必要的 Render API 选项。
- 播放窗口将 Avalonia 的按键按下/释放、组合键、鼠标坐标、鼠标按钮和高精度滚轮转换为 mpv input 事件，启用当前 mpv 0.41 构建内置的完整默认绑定；按住可重复按键时由 mpv 自己控制重复节奏，窗口失去焦点会释放全部按键。
- `f`/F11 与左键双击由宿主切换真实 Avalonia 全屏，`Esc` 退出全屏；右键在整个视频内容区（包括浮层）按 CanX 约定切换暂停/继续。`q`/`Q`、置顶和窗口倍率等需要操作宿主窗口的绑定也由 Avalonia 等价实现，其余绑定原样交给 libmpv。
- CanX 固定使用自己的底部播放控制浮层，因此宿主关闭 libmpv 的 `osd-bar`，快进、快退和调节参数时不会再叠加显示 mpv 进度条；字幕与文字 OSD 不受影响。
- 桌面和移动端进度条在拖动期间保存 `ValueChanged` 给出的最新目标值，并在 Slider 完成内部 pointer 更新后的 bubble 阶段只提交一次 seek；提交后继续保留目标值，直到 mpv 回报已离开旧位置或超时，避免 200 ms 状态轮询把滑块弹回原处。进度条和移动端水平拖动手势使用 libmpv 的关键帧快速跳转；按钮、快捷键、章节、书签和 `playback.seek` Tool 仍使用精确跳转。桌面播放器还按事件的视觉树来源识别浮层控件，避免视频拖动手势抢占 Slider 的指针。
- 视频画面支持带方向锁定和 12 DIP 防误触阈值的鼠标/触控板拖动手势：无修饰左键横向拖动预览目标时间并在松手时跳转；从左半区开始纵向拖动调节 mpv 画面亮度，从右半区开始纵向拖动调节音量。输入由视频父容器统一捕获，不依赖 OpenGL surface 参与命中测试；调整以 30 Hz 合并提交并显示居中反馈。带修饰键的左键操作继续原样交给 mpv，保留 `Ctrl+左键` 画面平移等内置行为；未达到拖动阈值的普通左键点击也会转发，供 discnav 和交互脚本使用。
- 播放窗口内提供可缩放的“轨道与字幕”面板，可选择或禁用视频、音频和主字幕轨道；轨道标签展示 ID、语言、标题、codec、外部/默认/强制标记，列表定期与 mpv 同步，因此 mpv 快捷键造成的轨道变化也会反映到界面。
- 支持通过文件选择器一次加载一个或多个本地字幕，也可直接将受支持的字幕文件拖入播放窗口；第一份字幕自动选中，其余字幕加入可选列表。远程字幕将在阶段 4 通过 Gateway 加载，当前不把带凭证的 URL 直接交给 mpv。
- 字幕面板可实时控制字幕显隐、延迟（-10 至 +10 秒的常用 UI 范围，底层 Tool 支持 -3600 至 +3600 秒）、字号（8–96 px 的常用 UI 范围）和垂直位置（0%–150%）。
- 主窗口支持一次选择多个媒体文件，或选择一个文件夹并按文件名排序构建播放列表；文件夹 UI 默认只读取当前层，`playback.folder.open` Tool 可显式启用递归扫描，单次列表上限为 10,000 项。
- 播放列表可播放、追加、重排、移除及清空待播项，libmpv 自动连播、默认快捷键切项和 CanX 上一项/下一项按钮都会同步回界面；播放窗口内另有自适应缩放的列表浮层。
- 最近播放保留 100 项，显示上次播放时间和进度；启用“自动恢复播放位置”后，重新打开未完成媒体会恢复到上次位置，达到 95%（或至少 30 秒媒体仅剩 10 秒）视为已播完并从头开始。
- 队列和最近播放写入独立的 `playback-state.json`，采用临时文件加原子替换；读取损坏文件时记录警告并以空状态启动。为避免凭证泄漏，含用户名/密码、query 或 fragment 的远程 URL 不会持久化。
- 通过 libmpv `chapter-list` 读取章节索引、标题和起始时间，并在播放器的“章节与书签”浮层中标记当前章节、支持双击或按钮精确跳转。
- 书签按媒体来源保存，支持自定义名称、当前或指定时间创建、重命名、删除、清空和跨媒体打开后精确定位。单媒体上限为 500 个，全局上限为 5,000 个；含凭证、query 或 fragment 的远程媒体书签不写入磁盘。播放状态 schema 当前为 2；开发阶段直接更新本机状态文件，不保留旧 schema 迁移代码。
- 播放窗口新增自适应缩放的“高级播放”浮层：可前进/后退一帧，在当前位置设置或清除 A-B 循环点，并以纯视频、含字幕或完整 mpv 输出三种模式截图。自动截图保存到系统图片目录的 `CanX` 子目录，显式路径和自动命名均拒绝覆盖已有文件。
- 通过 `MPV_FORMAT_NODE` 读取 `audio-device-list` 和 `glsl-shaders`，不依赖平台路径分隔符或字符串解析。音频输出设备可在运行时切换；用户 Shader 支持本地 `.glsl`/`.hook` 文件的添加、移除和清空。
- 视频和音频滤镜分别以 mpv 原生 `vf`/`af` 表达式编辑、应用和清空，不对 libmpv 的完整滤镜语法做有损转换；无效表达式沿用统一的播放操作错误提示和独立 mpv 日志诊断。
- 播放窗口新增互斥且可自适应缩放的“播放诊断与运行时控制”面板。诊断每秒采样一次，展示实际 codec、解码像素格式、`hwdec-current`、`hwdec-interop`、VO/GPU context、输入和目标色彩参数、容器/解码/显示 FPS、A/V 同步误差、丢帧/错时/延迟帧、音视频码率及 demuxer 缓存状态，不增加 200 ms 基础状态刷新的属性读取压力。
- 诊断根据 `hwdec-current`、`hwdec-interop` 和 hardware pixel format 明确区分软件解码后上传、硬解后复制回 CPU、GPU interop 零拷贝以及无法判断四种路径。`hardware-zero-copy-interop` 只表示 libmpv 解码 surface 到 Render API 的路径无需读回 CPU；独立的呈现诊断继续报告 Surface backend、同步方式、CanX GPU copy、Avalonia snapshot copy、render/submit/present 计数及平均/P95 耗时。
- 设置页和播放窗口均提供目标色彩空间提示、提示来源、目标色域/传递函数/峰值、tone mapping、gamut mapping 与动态 HDR 峰值分析；设置页持久化到下次启动，播放窗口可立即应用。输入与输出 `video-params` 会同时显示，以便确认 HDR 到 SDR、HDR 到 HDR 或色域转换实际结果。
- 当前 Avalonia OpenGL surface 由宿主/compositor 拥有最终 swapchain。mpv 内部 tone mapping、gamut mapping 和高精度渲染有效，但 `target-colorspace-hint` 设置成功不等于 Wayland/X11/Windows/macOS 已收到端到端 HDR metadata，也不保证最终 swapchain 为 10-bit/HDR。平台专用 10-bit framebuffer、颜色空间协商与 HDR metadata 传递仍属于阶段 7 的 `IVideoSurface` 验收项。
- 高级面板可以读取非敏感 mpv property、写入非宿主管理 property，并以“每行一个参数”执行命令。属性名、值、参数数量和总长度均有限制；进程执行、媒体/脚本加载、文件写入、截图、宿主退出、按键注入、受保护属性覆盖及凭证相关属性读取会在 Controller 边界拒绝，因此 UI、插件和 Agent 使用同一安全规则。
- libmpv 初始化后直接读取 `property-list` 和 `command-list`（包括命令参数类型、可选参数、默认值和 choices），生成 schema v1 的运行时 capability manifest；同时记录实际 mpv、client API、FFmpeg、平台和编译配置，避免手写列表与不同平台构建漂移。每项能力被归入专用 Tool、受限高级入口或明确禁用，覆盖测试会拒绝空名、重名、未分类或没有原因的禁用项。当前 Linux x64 构建为 968 项 property、87 项 command，未覆盖 0 项；详细报告见 [mpv Property / Command 能力覆盖](MPV-CAPABILITY-COVERAGE.zh-CN.md)。

字段名、可选值、默认值与范围以当前固定 mpv 源码中的 `DOCS/man/options.rst` 和实际 option definition 为准；面向用户的在线参考是 [mpv 官方手册](https://mpv.io/manual/master/)。不同平台或构建未包含的后端仍可能在打开媒体时失败并回退，配置校验只保证当前 libmpv 接受该选项和值。

当前 Tool：

```text
playback.get
playback.capabilities
playback.chapter.list
playback.chapter.open
playback.bookmark.list
playback.bookmark.add
playback.bookmark.open
playback.bookmark.rename
playback.bookmark.remove
playback.bookmark.clear
playback.open
playback.play
playback.pause
playback.togglePause
playback.stop
playback.seek
playback.frame.next
playback.frame.previous
playback.abLoop.set
playback.abLoop.clear
playback.screenshot
playback.volume.set
playback.brightness.set
playback.mute.set
playback.speed.set
playback.audioDevice.list
playback.audioDevice.set
playback.filter.video.set
playback.filter.audio.set
playback.filter.clear
playback.shader.list
playback.shader.add
playback.shader.remove
playback.shader.clear
playback.diagnostics.get
playback.color.set
playback.property.get
playback.property.set
playback.command.execute
playback.track.select
playback.subtitle.add
playback.subtitle.visibility.set
playback.subtitle.delay.set
playback.subtitle.style.set
playback.playlist.open
playback.playlist.add
playback.folder.open
playback.playlist.play
playback.playlist.next
playback.playlist.previous
playback.playlist.remove
playback.playlist.move
playback.playlist.clear
playback.recent.open
playback.recent.remove
playback.recent.clear
```

## 原生链接

开发构建通过 `scripts/build-dev-mpv-linux-x64.sh` 将固定源码构建的 `libmpv.a`、FFmpeg、libass、libplacebo、HarfBuzz 等 archive 聚合为 `artifacts/native/mpv-dev-linux-x64/libmpv.so`，供以下方式快速启动和调试：

```bash
dotnet src/CanX.App/bin/Release/net10.0/linux-x64/CanX.App.dll
```

正式发布由 `scripts/publish-static-app-linux-x64.sh` 将同一组 archive 通过 NativeAOT DirectPInvoke 合入 `CanX.App`。发布目录不携带 `libmpv.so`。FFmpeg、libmpv、libplacebo、oneVPL dispatcher、libva/libdrm、OpenCL ICD loader、glslang/SPIRV-Tools 仍为静态链接；最终 ELF 额外保留系统 `libEGL.so.1` 和 `libvulkan.so.1`，各厂商的 VAAPI、Vulkan、OpenCL、AMF、oneVPL implementation 与 NVIDIA 驱动由这些接口在运行时发现。

Linux x64 正式发布基线为 Ubuntu 22.04（glibc 2.35）或更新版本。发布产物必须在固定的 Ubuntu 22.04 容器中构建，入口为：

```bash
scripts/run-ubuntu-22.04-linux-x64.sh
```

播放器默认使用 `hwdec=auto-safe`。它会在打开媒体时尝试当前设备可用的安全零拷贝后端，在后端、驱动或 codec profile 不可用时继续尝试并最终回退软件解码。Android 对该默认值使用 `mediacodec,mediacodec-copy` 的平台顺序，优先 `AImageReader/EGLImage` 零拷贝 interop，再回退到 CPU copy；用户显式选择的硬解模式不受影响。Linux 上 Intel/AMD 的默认主路径是 VAAPI，NVIDIA 是 NVDEC/CUDA；QSV、AMF 与 Vulkan Video 已在 FFmpeg 中编译，但是否参与默认选择由 mpv 的安全列表和设备驱动决定。设置页可显式选择 NVDEC、VAAPI、Vulkan、D3D11VA、VideoToolbox、MediaCodec、自动复制回内存或禁用硬解；选项是否能在目标机器成功启用仍由对应构建和驱动决定。完整构建矩阵见 [FFmpeg / libmpv 功能矩阵](MPV-FFMPEG-FEATURES-linux-x64.md)。

Render API 使用 `MPV_RENDER_PARAM_ADVANCED_CONTROL`：native update callback 只唤醒持有共享 GL context 的渲染线程，线程调用 `mpv_render_context_update()` 后仅在 `MPV_RENDER_UPDATE_FRAME` 置位或 framebuffer 尺寸变化时渲染；Avalonia 将新纹理复制到实际目标后，再由同一渲染线程调用 `mpv_render_context_report_swap()`。seek 重置 renderer 时，宿主通过 `MPV_RENDER_PARAM_NEXT_FRAME_INFO` 区分临时 `REDRAW` 与真正的新视频帧：前者以 `MPV_RENDER_PARAM_SKIP_RENDERING` 消费但不替换已发布纹理，直到目标位置的首个有效帧到达，避免短暂发布空 framebuffer 造成黑闪。由于 Avalonia 离屏 FBO 的 redraw 不保证阻塞到显示器 vblank，宿主还按探测到的刷新率限制 swap 回报节奏，避免 `display-resample` 失控空转。桌面端会自动探测主显示器刷新率（Linux 使用 XRandR、Windows 使用 GDI、macOS 使用 CoreGraphics），用户仍可用高级配置中的 `display-fps-override` 显式覆盖。新配置默认采用 `video-sync=display-resample` 和 `interpolation=yes`，避免 23.976 fps 素材在约 60 Hz 屏幕上的 3:2 cadence 抖动。

## 已通过验证

```bash
dotnet test CanX.slnx -c Release --no-restore
CANX_ALLOW_NEWER_GLIBC=1 \
CANX_STATIC_ROOT="$PWD/.cache/static-native-test2/linux-x64" \
  scripts/publish-static-app-linux-x64.sh --skip-build
CANX_STATIC_ROOT="$PWD/.cache/static-native-test2/linux-x64" \
  scripts/verify-static-native-aot.sh --skip-build
scripts/verify-playback-linux-x64.sh --skip-publish
CANX_PLAYBACK_STABILITY_SECONDS=15 scripts/verify-playback-stability-linux-x64.sh --skip-publish
CANX_MPV_PRESENTATION_BACKEND=shared-worker-fence \
CANX_EXPECTED_PRESENTATION_BACKEND=shared-worker-fence \
  scripts/verify-playback-linux-x64.sh --skip-publish
scripts/verify-download-ui-linux-x64.sh --skip-publish
scripts/verify-download-fixtures-linux-x64.sh --skip-publish
```

关键结果：

```text
Passed: 111, Failed: 0
CANX_NATIVE_DEPENDENCY_AUDIT_OK ... needed=5 sidecars=0
CANX_STATIC_NATIVE_AOT_OK platform=linux-x64
CANX_PLAYBACK_BACKEND_OK backend=Wayland
CANX_PLAYBACK_BACKEND_OK backend=X11
CANX_APP_PLAYBACK_OK ... contextRecovery=true contextGeneration=2 ... capabilities=true propertyCapabilities=968 commandCapabilities=87 capabilityUncovered=0 ...
CANX_PLAYBACK_OK platform=linux-x64
CANX_PLAYBACK_STABILITY_OK seconds=15 frames=876 seeks=2 resizeCycles=1 contextRecreations=1 presentationResumes=1 contextGeneration=2 decoderDrops=0 renderer=composition-shared presentationBackend=composition-shared copiesPerFrame=0 compositorCopiesPerFrame=1 gpuCopies=0 blockingFinishes=0
CANX_PLAYBACK_STABILITY_OK seconds=15 frames=853 seeks=2 resizeCycles=1 contextRecreations=1 presentationResumes=1 contextGeneration=2 decoderDrops=0 renderer=composition-shared presentationBackend=composition-shared copiesPerFrame=0 compositorCopiesPerFrame=1 gpuCopies=0 blockingFinishes=0
CANX_PLAYBACK_STABILITY_ALL_OK platform=linux-x64 seconds=15
CANX_DOWNLOAD_UI_OK backend=X11 scales=100,200,300 accessibility=at-spi keyboard=true playback-overlay=true playback-gestures=true playback-tracks=true playback-playlist=true playback-chapters=true playback-advanced=true playback-diagnostics=true screenshots=17
CANX_DOWNLOAD_FIXTURES_OK torrent=5 metalink=1
```

`verify-playback-linux-x64.sh` 会即时生成带两条音轨、两个章节的 H.264/YUV420P 测试视频、一份独立 SRT、第二个播放列表媒体和一个无效果测试 Shader，等待 Avalonia composition surface 和 mpv render context 就绪，确认至少 48 次实际视频帧更新、轨道/字幕、章节/书签、播放列表/历史、高级播放控制、解码/色彩/性能诊断、HDR 属性往返、受限高级 property/command，以及实际 libmpv capability 清单完整覆盖，而不是只检查进程能否启动。默认 smoke 要求 `renderer=composition-shared`、CanX copy/finish 为 0；也可用环境变量强制并验证三个回退后端。另使用 HEVC Main 10/YUV420P10 真实文件在 Wayland 和 X11 下执行相同检查。

`verify-playback-stability-linux-x64.sh` 默认即时生成 150 秒媒体并在 Wayland、X11 各连续播放 120 秒；期间每 10 秒 seek，多次主动重建 Render Context，并隐藏/重新显示播放窗口模拟 presentation suspend/resume。测试在 6 秒无新渲染帧、托管内存增长达到 128 MiB 或 working set 增长达到 768 MiB 时失败。持续时间可用 `CANX_PLAYBACK_STABILITY_SECONDS=15..7200` 调整；X11 检测到多个屏幕时还会逐屏移动并等待新帧。本次机器仅检测到一个屏幕（`screensDetected=1`），因此不能声称已完成真实多屏验收；隐藏/恢复也不等同于操作系统 suspend/resume。

2026-09-18 追加了 seek 瞬间的画面保持回归：使用持续变化的 1280×720 测试图在 X11 对首次绝对 seek 前后逐帧抓取播放器窗口，seek 两侧画面均保持为有效视频帧；测试流程中唯一的黑帧对应主动销毁并重建 Render Context，而不是 seek。真实 4K HEVC 文件的 20 秒流程完成 2 次 seek、2 次 Render Context 重建，统计为 `decoderDrops=0`、`outputDrops=1`、`delayed=0`、`avsync=0.003`。

另以 HEVC Main 10、3840×1600、23.976 fps 的 4K 文件验证帧同步修复：NVDEC/CUDA P010 保持零解码丢帧；15 秒 NativeAOT 稳定性流程在 59.98 Hz 显示器上得到 863 次 Render API 帧更新、864 次实际呈现、0 个延迟帧、4 个错时帧和约 0.010 秒 A/V 偏差。切回 `video-sync=audio` 的回归测试得到 352 次帧更新、0 个输出丢帧和 0 A/V 偏差，未引入失控渲染。

以上 NativeAOT 播放验证使用本机工具链，并显式绕过发行版 GLIBC 门禁；本机产物要求 GLIBC 2.43，不能作为 Ubuntu 22.04 发布包。正式产物仍必须由上述固定容器重建并通过 GLIBC 2.35 检查。

## 下一步

按以下顺序继续阶段 3：

1. 在实际多显示器环境完成跨屏移动验收，并在可控测试机执行真实系统休眠/唤醒；Linux x64 的自动化连续播放、seek、窗口呈现恢复和 Render Context 重建已完成。
2. 进入阶段 4，先实现统一 Stream Gateway 和 HTTP Range 基线，再接 aria2 边下边播及 SMB/FTP/WebDAV。

SMB/FTP/WebDAV、字幕自动发现以及与 aria2 协调的边下边播仍属于阶段 4 的 Stream Gateway，不在当前基础播放器中直接暴露带凭证的远程 URL。
