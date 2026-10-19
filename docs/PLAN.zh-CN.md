# CanX 产品与实施计划

- 文档版本：1.1
- 日期：2026-09-20
- 状态：阶段 2 已完成 Linux x64 验收；阶段 3、4 已完成 Linux x64 实现，但跨设备/真实服务端矩阵仍待验收；阶段 5、6 的当前目标和自动验收已完成；阶段 7 的 Linux x64 发布工程和 Android 完整宿主已完成，Ubuntu 22.04 正式 CI 首跑、Android x64 模拟器及其余平台验收未完成
- 首要开发环境：Ubuntu 26.04 / Linux x64 / .NET SDK 10.0.400
- Linux x64 最低发行基线：Ubuntu 22.04 / glibc 2.35

## 1. 产品目标

CanX 是一个跨平台、可扩展、Agent-first 的视频播放器与下载工具。它同时覆盖四类用途：

1. 完整的日常下载器：以 aria2 为下载引擎，支持 HTTP(S)、FTP、SFTP、BitTorrent、Metalink、队列、限速、做种和高级任务配置。
2. 完整的日常播放器：以 libmpv 为播放引擎，覆盖播放列表、音视频/字幕轨道、滤镜、着色器、硬件解码、同步、缓存和高级属性。
3. 网络媒体中心：浏览并播放 SMB、FTP 和 WebDAV 资源，发现、选择和加载远程字幕。
4. 插件和 AI 工作台：插件可扩展搜索源、字幕源、下载、播放处理和界面；用户可用自然语言调用所有面向用户的能力，并让 AI 在受控环境中创建和调试插件。

本计划中的“完整”定义为：当前内置 native engine 支持且适合 GUI 暴露的稳定命令、属性和选项均可从专用界面或高级控制台访问；平台或构建未提供的能力必须明确显示为不可用。它不表示伪造引擎本身不支持的能力。

## 2. 固定约束与建议调整

### 2.1 技术约束

- UI：Avalonia UI。
- Runtime：.NET 10；发布版本以 NativeAOT 为目标。
- 下载：libaria2。
- 播放：libmpv。
- 插件：QuickJS，插件主体使用 JavaScript ES modules；可额外提供 TypeScript SDK 和打包工具。
- 架构：应用核心不直接依赖具体 UI，所有面向用户的动作统一注册为带 JSON Schema 的 Tool。
- 开发顺序：先 Linux x64 本机，再扩展桌面端，最后完成移动端适配。

NativeAOT 是每个平台的发布验收目标，但不是未经验证的先验保证。Avalonia、第三方托管库、原生回调、Android/iOS 工具链都要在阶段 0 做裁剪和 AOT 验证；不兼容的依赖不得进入核心路径。

### 2.2 平台和 CPU 的现实矩阵

| 平台 | x64 | ARM64 | 备注 |
|---|---:|---:|---|
| Windows | 支持 | 支持 | 两套 RID 和原生依赖 |
| Linux | 支持 | 支持 | 先确定 glibc 基线；后续考虑 Flatpak/AppImage |
| macOS | 支持 | 支持 | Universal 2 可作为组合产物 |
| Android | 模拟器/部分设备 | 支持 | ARM64 为正式设备主目标，x64 主要用于模拟器 |
| iOS | 不作为设备目标 | 支持 | iPhone/iPad 设备只有 ARM64；模拟器按 Apple 工具链另行产出 ARM64/x64 |

因此“所有平台均同时提供 x64 和 ARM64 设备包”在 iOS 上不成立。验收应按上表的有效平台组合执行。

### 2.3 统一 UI 设计系统与窗口策略

界面采用接近 Windows 10 的直角、紧凑、信息密度较高的生产力工具风格，但不直接复制 Windows 控件，也不根据运行平台切换整套主题。所有平台共享同一套 `CanX.DesignSystem` 资源、控件模板、图标、颜色和交互状态；只允许窗口管理、系统菜单、安全区域、输入法和无障碍等平台能力通过适配层变化。

视觉约束：

- 普通按钮、输入框、列表项、卡片、菜单、浮层、对话框和面板的默认 `CornerRadius` 为 `0`；圆形进度、旋钮等语义上必须为圆形的控件除外。业务 `ContextMenu`、子菜单以及 TextBox 等控件内置的 `MenuFlyout` 必须统一使用设计系统的直角容器、1 px 边框、紧凑间距和亮/暗主题交互色，禁止回退到平台原生菜单或 Fluent 默认圆角浮层。
- 图标按钮仍以直角为默认；播放器主播放键、移动端沉浸式播放控制等天然适合拇指操作且不属于表单布局的纯图标按钮可以使用圆形。只有图标而没有可见文字时，必须同时提供无障碍名称，桌面端还要提供 ToolTip。
- 使用实色背景、1 px 分隔线和克制阴影，不使用 Mica、Acrylic 或跟随平台变化的材质作为必要视觉信息。
- 提供统一的亮色、暗色和高对比度 token；以 Metro 蓝作为主强调色，并提供青色、绿色、黄色、红色等语义色用于媒体、成功、提醒和危险状态。允许跟随系统选择亮暗模式，但不加载平台专属 ControlTheme，也不以颜色作为唯一状态提示。
- 桌面默认使用紧凑密度、左侧图标导航和高信息密度工具栏；移动端使用同一视觉语言的触控密度、底部五项主导航和响应式内容区，而不是复制桌面布局。移动端普通触控目标最小高度为 42 px，播放器主要触控目标为 46–52 px，并处理 safe area 和软键盘。
- 图标使用 `CanX.DesignSystem` 随包发布的 `StreamGeometry` 矢量资源，避免依赖 Windows 字体图标；主操作采用“图标 + 短文字”，空间受限的播放器操作采用纯图标。字体使用统一 fallback 列表，若截图一致性不足，再引入许可清晰的内置 UI 字体子集。
- 播放器保持黑色沉浸式画布，控制区使用半透明深色 overlay；播放/暂停为高辨识度强调色圆形按钮，上一项、seek、停止、方向与更多操作使用统一矢量图标。

窗口装饰策略：

| 平台 | 窗口策略 |
|---|---|
| Windows | 移除系统边框，自绘直角标题栏、图标、标题和最小化/最大化/关闭按钮；保留拖动、双击最大化、边缘缩放、系统菜单、Snap、键盘与屏幕阅读器语义。必要时仅用平台适配代码关闭 Windows 11 自动圆角。 |
| Linux X11/Wayland | 移除客户端系统边框并使用同一自绘标题栏；移动和缩放必须调用 compositor/window-manager 支持的交互 API，不能假设应用可直接设置窗口坐标。 |
| macOS | 保留原生窗口框架、traffic-light 按钮和系统窗口行为；内容区、工具栏内容及全部业务控件仍使用 CanX 统一主题。可扩展内容到标题栏，但不得伪造 macOS 窗口按钮。 |
| Android/iOS | 不存在桌面窗口边框；使用统一的应用顶栏和响应式导航，同时保留系统状态栏、手势区和安全区域。这里的“自绘标题栏”指应用顶栏，不覆盖系统安全 UI。 |

组件栈采用小而可审计的组合，并全部固定版本、执行 NativeAOT 与裁剪测试：

| 组件 | 定位 | 决策 |
|---|---|---|
| `Avalonia`、`Avalonia.Themes.Fluent` 12.1.2 | 基础控件、样式系统、窗口与无障碍 | 必选。只把 FluentTheme 当作行为与模板基线，由 `CanX.DesignSystem` 覆盖视觉 token 和必要模板。 |
| `Avalonia.Controls.DataGrid` 12.1.2 | 下载任务、文件、peer/tracker、属性表 | 必选。官方包且与当前 Avalonia 版本一致；大数据量必须启用虚拟化。 |
| `FluentFTP` 54.2.1 | FTP/FTPS 浏览、认证和随机读取 | 阶段 4 使用。MIT、纯托管且无传递依赖，可合入 NativeAOT 单文件；真实地址与凭证只保留在宿主媒体访问层，不交给 libmpv。 |
| `SMBLibrary` 1.5.7.1 | SMB 2/3 浏览、认证和随机读取 | 阶段 4 使用。LGPL-3.0-or-later，与 CanX 的 GPL-3.0-or-later 兼容；采用纯托管客户端并启用 SMB 3.1.1，不引入 `libsmbclient` 或 native sidecar。 |
| `FluentAvaloniaUI` 3.1.0 | `NavigationView`、`CommandBar`、`InfoBar`、对话框等补充控件 | 推荐但按控件引入。其 NuGet 依赖 Avalonia 12.1，MIT；不直接采用默认主题，每个使用到的控件都要完成直角模板、移动端和 NativeAOT 验证。 |
| `Avalonia.AvaloniaEdit` 12.0.0 | 插件源码、日志和配置编辑器 | 阶段 5 引入。MIT、Avalonia 12 版本线；TextMate 语法包按需加载，不能依赖运行时反射发现。 |
| `Dock.Avalonia` 12.1.x | 可停靠的开发/诊断面板 | 可选，不作为播放器和下载器主导航依赖；仅在确认需要 IDE 式插件工作台后引入，并关闭移动端浮动窗口能力。 |

不把 `TreeDataGrid`、整套 Material/Suki 等第三方主题或 WebView UI 作为基础依赖：前者需单独确认当前授权与版本策略，后两者会与直角统一主题冲突，WebView 也会破坏 NativeAOT、插件隔离和无障碍边界。缺少的简单控件优先在 `CanX.DesignSystem` 内基于 Avalonia `TemplatedControl` 实现，不复制大型控件库。

UI 验收必须包含：Windows/Linux 自绘标题栏的移动、缩放、最大化、全屏和多屏；macOS 原生窗口控件；键盘导航、焦点可见性、屏幕阅读器和 100%–300% 缩放；桌面紧凑与移动触控两种密度；亮色、暗色、高对比度截图回归。插件声明式 UI 只能引用设计系统公开的组件 ID 和语义 token，不能提交任意平台样式或绕过全局直角规则。

## 3. 两个关键技术问题的初步结论

### 3.1 aria2 和 libmpv 能否边下边播

可以实现，但不能简单理解为“把 aria2 的未完成文件路径交给 mpv 就一定工作”。直接打开未完成文件只在以下条件同时较好时可靠：

- 容器适合渐进播放，例如 MP4 的 `moov` 位于头部，或容器索引已可用；
- aria2 已下载播放器当前需要的字节区间；
- 未下载区间没有因预分配而被播放器误当成有效数据；
- 拖动进度条后，下载器能够及时优先取得新的目标区间；
- 服务端支持 Range，且资源在下载期间保持不变。

采用统一的进程内 `Stream Broker`，不要让播放器直接感知 aria2、插件凭证或远程协议：

```text
插件 / HTTP / SMB / FTP / WebDAV
               |
               v
  Stream Broker（Range、缓存、凭证、限流）
               |
               v
 libmpv stream callback <---- canx://<opaque-session-id>/<resource-name>
               |
               v
      Download Coordinator <----> libaria2 <----> 持久化文件
```

Stream Broker 通过 `mpv_stream_cb_add_ro` 注册固定的 `canx` scheme，并负责：

- 已完成区间直接从文件读取；
- 尚未完成区间等待或提升对应下载区间优先级；
- 用户 seek 时切换优先区间；必要时直接向源站补取小范围数据；
- 隐藏真实 URL、Cookie、Header 和密码，只给播放器不可预测的 session token；
- 同时支持“仅流式播放”“播放并缓存”“正式下载后播放”三种策略；
- 为远程字幕提供同样的临时访问入口。

主路径不监听本地端口。每次 mpv `open` 获得独立的可 seek、可取消 byte stream；`read` 遵循阻塞语义，暂时缺少数据时等待而不能返回伪 EOF；`cancel` 只发出非阻塞取消信号；回调不反向调用同一个 mpv 实例。注册句柄必须存活到 mpv core 完全销毁，所有异常在 native 边界转换为 mpv 错误码。由于 stream callback API 仍标记为不稳定，项目固定自有 mpv fork，并以结构布局、Client API 版本、生命周期、seek、cancel 和 EOF 测试守住 ABI 边界。

仅在 HLS/DASH 嵌套或相对 URL、外部播放器、未来跨进程消费者等 callback 不合适的场景启用可选 loopback HTTP Gateway。二进制媒体数据不应逐块穿过 QuickJS；插件只负责解析受控的 `MediaSource`，或把数据交给宿主管理的 Range/缓冲数据源，实际 I/O 和背压由宿主执行。aria2 边下边播仍以稀疏临时文件作为默认缓存层，不为追求“纯内存”而重写其完整 piece 存储模型。

阶段 0 必须实测 MP4 fast-start、MP4 尾部索引、MKV、大文件随机 seek、断网恢复和无 Range 服务端。验证前不承诺任意文件均可无等待随机拖动。

### 3.2 libmpv 能否与 Avalonia UI 完美协作

能够集成，但不存在一个对五个平台都“零适配”的控件。推荐主路径是 libmpv Render API，而不是仅依赖 `wid` 把外部原生窗口嵌入 Avalonia：

- Render API 可在 Avalonia 自定义渲染控件中合成视频，较容易处理遮罩、浮层、HiDPI 和播放器控件。
- Linux Wayland 不适合传统子窗口嵌入；Render API 更符合跨平台目标。
- 桌面端先验证 OpenGL 渲染上下文；移动端验证 OpenGL ES 和 Avalonia 原生视图生命周期。
- 原生窗口嵌入可保留为 Windows/X11/macOS 的诊断或兼容后端，但不是统一架构。
- 若某个平台无法共享图形上下文，提供软件渲染或独立播放窗口作为降级能力。

必须通过一个 `IVideoSurface` 平台抽象隔离 Avalonia、图形 API 和 libmpv。阶段 0 的渲染 Spike 要验证：创建/销毁、窗口缩放、HiDPI、多屏、全屏、浮层、截图、硬件解码、休眠恢复、后台/前台切换，以及 X11/Wayland 差异。通过这些测试后才能称为可用于正式 UI。

硬解码 surface 到 libmpv Render API 的零 CPU 回读与最终 UI 合成是两段独立链路，诊断和优化不得把二者混为“零拷贝”。Linux 的 NVDEC/CUDA、VAAPI/DMABUF 和 Android 的 MediaCodec/AHardwareBuffer 正常 interop 路径不经过 CPU，但当前 `shared-worker` 后端仍将 mpv 输出先渲染到 CanX 中间纹理，再 `glBlitFramebuffer` 到 Avalonia framebuffer，并以两次阻塞式 `glFinish()` 交接所有权。阶段 3 增加 Direct Composition 优化子阶段：

1. 扩展运行时诊断，分别报告解码传输路径、CanX 呈现后端、GPU copy 次数、同步策略、渲染/提交/呈现计数与耗时；不能仅凭 `hwdec-current` 声称端到端零拷贝。
2. 新增 `composition-shared` 后端。mpv 在共享 GL context 中直接渲染到 Avalonia `CreateSharedTextureForComposition()` 创建并可由 compositor 导入的纹理；使用 `CompositionSurfaceVisual` 和 `CompositionDrawingSurface.UpdateAsync()` 提交，不再经过 `OpenGlControlBase` 的 CanX 中间纹理 blit。
3. 使用显式所有权状态和平台适配的缓冲数；桌面三缓冲，Android 根据 compositor 真机延迟使用双缓冲。在 compositor 完成异步提交前禁止复用纹理。优先使用平台可用的 GPU fence/semaphore，不能导入 fence 时用 `glFlush` 加提交生命周期隔离，不能安全交接时才降级到 `glFinish()`。
4. 固定回退顺序为 `composition-shared` → `shared-worker-fence` → `shared-worker-finish` → `inline`；任何后端初始化或 context-loss 失败都必须自动降级并记录原因，不能造成黑屏或破坏播放状态。
5. 第一轮不使用 Android `SurfaceView + vo=mediacodec_embed`，因为该路径会绕过统一的 mpv Shader、字幕、OSD、tone mapping 与 Avalonia 浮层组合；也不先 fork Avalonia 删除其内部 GPU snapshot。完成首轮实测后，再根据仍存在的 copy/同步成本决定是否修改 Avalonia fork。

优化验收使用相同的 4K HEVC 10-bit 媒体和固定播放设置对比旧 `shared-worker-finish` 基线：Linux Wayland/X11 与 Android 真机均需确认无 CPU readback、无新增解码/输出丢帧、seek/context-loss/前后台恢复稳定；`composition-shared` 生效时 CanX 自有全帧 blit 必须为 0，UI 线程不得出现逐帧 `glFinish()`，并记录平均与 P95 render/submit/present 时间。保留 feature flag，以便单独选择各回退后端做回归和驱动兼容性定位。

### 3.3 Linux 硬件加速与发行基线

Linux x64 不能根据开发机当前是 NVIDIA 显卡就裁剪其他厂商后端。正式构建固定同时启用：

- NVIDIA NVDEC/CUVID/NVENC 与 libmpv CUDA OpenGL/Vulkan interop；
- Intel QSV 与 oneVPL dispatcher；
- Intel/AMD VAAPI、DRM 和 DMABUF interop；
- AMD AMF；
- Vulkan Video、libplacebo、glslang/SPIRV-Tools；
- OpenCL filters。

FFmpeg、libmpv、libplacebo、oneVPL dispatcher、libva/libdrm、OpenCL ICD loader 和 shader 编译链优先静态链接。`libEGL.so.1`、`libvulkan.so.1` 以及各 GPU 厂商驱动/runtime 属于操作系统和硬件 ABI，保留动态加载；不能把某台构建机的驱动打进通用 App。

应用默认使用 `hwdec=auto-safe`，在运行时按媒体格式、GPU 和驱动能力尝试安全后端，失败后回退软件解码。Linux 上 Intel/AMD 的默认播放路径优先 VAAPI，NVIDIA 优先 NVDEC/CUDA；QSV、AMF 和 Vulkan Video 即使已编译，也不强制覆盖 mpv 的安全自动选择，后续由播放设置提供显式选择和诊断。

Linux x64 正式产物必须在 Ubuntu 22.04 容器中构建并通过 GLIBC 2.35 上限检查，保证可在 Ubuntu 22.04 或更新版本运行。开发机仍可使用更新发行版做增量构建，但其产物不能直接作为正式发布包。

## 4. GPL 许可证与分发策略

已确定 CanX 自有源码和组合发布产物以 `GPL-3.0-or-later` 为许可证基线，不为闭源分发或 App Store 改用弱 copyleft 路线：

- libaria2 为 GPL-2.0-or-later；libmpv、FFmpeg 及其依赖按实际构建配置逐项审计。只能纳入与最终 GPL-3.0-or-later 组合产物兼容的依赖，发现 GPL-2.0-only 或其他不兼容条款时必须更换、禁用或另行解决。
- 每个正式版本同时提供准确的对应源码：CanX 源码、fork 的确切 commit、修改补丁、构建脚本、配置参数和必要安装信息，并生成 SBOM、third-party notices 与 license bundle。
- iOS 首选分发不是 App Store。计划提供源码、可重复构建说明，以及工具链允许时可供重签名的未签名/临时签名 IPA；用户使用自己的 Apple ID、开发者证书和 provisioning profile 签名安装。
- 项目不收集或代管用户签名凭证，不承诺第三方侧载工具、免费证书期限或特定系统版本始终可用。用户自行承担签名、侧载、证书续期、设备政策和系统升级带来的安装风险。
- 上一条不转移项目自身的 GPL 合规责任。项目仍必须满足源码提供、许可证告知、修改说明和适用的 Installation Information 等义务，不能用“用户自行承担”免责声明替代。
- 是否进入 iOS App Store 留到最终发布阶段决定；只有实际启动上架时，才将 GPL 与 Apple 条款、审核政策和 QuickJS 下载代码限制作为该渠道的发布门槛。它不再阻塞桌面端、Android、iOS 技术适配或阶段 1 开发。

如果以后把 App Store 设为硬性目标，必须新建 ADR 并完成专业法律与技术复核；任何替换 libaria2、连接远程 aria2 或缩减插件能力的商店变体都必须显式记录，不能静默偏离主版本。

## 5. 总体架构

采用 Tool-first 的模块化单体。先保持单进程和清晰边界，在桌面端可按安全需要把插件或下载引擎移到辅助进程；iOS 不依赖创建子进程。

```text
Avalonia Shell / CLI / Plugin / LLM Agent
                  |
          Tool Registry + Policy
                  |
     Application Services / Event Bus
        /          |          \
 Downloads      Playback    Media/Search
    |               |          |
libaria2 shim     libmpv    Protocol adapters
        \          |          /
          In-process Stream Broker
                  |
       State / Secrets / Audit / Cache
```

建议的解决方案结构：

```text
src/
  CanX.App/                  Avalonia 桌面入口和组合根
  CanX.App.Mobile/           Android/iOS 入口（需要时拆分）
  CanX.DesignSystem/         统一主题、窗口 chrome、控件模板和图标
  CanX.Core/                 领域模型、结果类型、事件和接口
  CanX.Application/          用例、命令、查询和 Tool 实现
  CanX.Tooling/              Tool 注册、Schema、权限、审计
  CanX.Downloads/            aria2 编排、任务模型、选项模型
  CanX.Playback/             mpv 状态机、命令和渲染抽象
  CanX.Streaming/            Stream Broker、callback adapter、Range、缓存和凭证令牌
  CanX.MediaAccess/          SMB/FTP/WebDAV 和资源浏览
  CanX.Plugins/              QuickJS runtime、SDK bridge、权限
  CanX.Agent/                LLM provider、planner、tool loop
  CanX.Infrastructure/       数据库、密钥、日志和设置
  CanX.Platforms/            平台能力实现
native/
  aria2-shim/                稳定 C ABI 的 C++ wrapper
  build/                     可复现 native 构建脚本和补丁
plugins/
  sdk/                       类型、CLI、模板和测试工具
  builtin/                   经审计的内置插件
tests/
  unit/ integration/ e2e/ native/ fixtures/
docs/
  adr/                       架构决策记录
  specs/                     Tool、插件和协议规范
```

关键工程规则：

- libaria2 的公开接口是 C++ API；增加很薄的 C++/C ABI shim，再用 source-generated P/Invoke 调用，避免直接绑定不稳定的 C++ ABI。
- libmpv 通过其 C API 绑定；回调、字符串和所有权规则必须有专门 native interop 测试。
- 托管层使用 compiled bindings、source-generated JSON、显式注册和 AOT-safe API；禁止把反射扫描作为核心发现机制。
- UI 只订阅状态并调用 Tool，不直接调用 libaria2/libmpv。
- 下载、播放、插件和 Agent 均使用结构化事件，避免用日志文本驱动状态。
- native 二进制按 RID 固定版本、校验哈希并记录构建参数，不从未知站点临时下载。

## 6. 核心领域模型

建议尽早稳定以下跨模块契约：

- `MediaResource`：逻辑资源、显示名、大小、MIME、来源和可用操作。
- `MediaSource`：可播放 URL/宿主请求描述、有效期、所需 Header 的 secret reference、清晰度和音轨变体。
- `SubtitleSource`：语言、格式、名称、来源、匹配度、延迟和编码。
- `DownloadTask`：统一状态；保留 engine-specific 的 aria2 GID 和原始选项。
- `PlaybackSession`：媒体、播放列表、轨道、位置、滤镜、渲染能力和错误。
- `CredentialRef`：只指向系统密钥存储；密码、Cookie 和 token 不进入普通配置、日志、Tool 返回值或 LLM 上下文。
- `ToolDescriptor`：名称、版本、输入/输出 Schema、权限、风险级别、可取消性和进度事件。
- `PluginManifest`：插件 ID、版本、入口、API 版本、权限、UI contributions 和来源签名。

## 7. 功能设计

### 7.1 aria2 下载中心

专用界面覆盖：

- URI、Torrent、Metalink、新建批量任务、剪贴板捕获和拖放；交互采用主窗口内“输入来源 → 解析并确认”的两步遮罩流程，多文件 Torrent 在确认页选择文件和逐文件优先级，确认前元数据只驻留内存；Magnet 可在 metadata 未完成时直接提交；
- 等待/进行/暂停/完成/错误/移除，队列排序和计划任务；
- 全局与单任务限速、连接数、分片、重试、超时、代理、证书、校验和文件分配；
- Torrent 文件选择、优先级、tracker、peer、DHT、端口、做种比率/时长；
- 实时速度、ETA、连接、分片图、文件进度、错误和原始 engine 状态；
- 会话保存/恢复、失败重试、完成后动作和通知；
- 根据 BTIH、Torrent info-hash 或规范化 URL 检测重复任务；默认提醒并允许用户显式确认重复添加，Tool/插件调用使用同一判重规则；Magnet metadata 阶段使用 `[METADATA] <BTIH hex>` 区分任务；
- 版本化的高级选项编辑器和原始状态查看器，用来覆盖没有专用控件的长尾选项。

“完整”的验收方式不是肉眼检查页面，而是为固定 aria2 版本维护 capability/option manifest：每个可变选项必须映射到专用控件、高级编辑器或明确的只读/不适用原因。

### 7.2 mpv 播放器

- 主窗口的“播放器”页只展示已添加的播放源，本地文件夹与 WebDAV、FTP/FTPS、SMB 使用同一列表；页面只保留“添加播放源”入口，连接参数集中在主窗口内浮层。双击任一来源都进入统一目录浏览器并列出全部文件，只有用户双击或选中受支持的媒体后才创建播放器；Android 保留播放源与目录浏览在主 Activity，视频画面和播放控制进入独立的沉浸式全屏 `PlaybackActivity`；iOS 使用等价的独立全屏播放器 View。
- `PlaybackWindow` 的画面无外边距填满客户区，播放控件以自动隐藏浮层覆盖在底部；初始窗口尺寸取视频显示分辨率并受当前屏幕工作区约束，普通窗口缩放保持视频宽高比。

专用界面覆盖：

- 文件、URL、文件夹、播放列表和最近播放；
- 视频/音频/字幕轨道、外部轨道、字幕搜索、样式、延迟和同步；
- 章节、书签、A-B 循环、逐帧、倍速、截图和恢复位置；
- 硬件解码、去隔行、旋转、裁剪、宽高比、色彩管理和 HDR 能力；
- 音量、均衡、声道、音频设备、音视频滤镜和用户 shader；
- 缓存、网络、demux、同步、丢帧、性能统计和日志；
- 播放列表编辑、按键/手势映射、profiles；
- 高级命令面板、属性检查器和事件查看器。

使用 versioned mpv property/command manifest 做覆盖率测试。危险命令、任意路径写入和外部进程命令必须受策略限制；平台构建不支持的功能显示原因。

### 7.3 SMB / FTP / WebDAV

每种协议都实现统一的浏览、stat、Range 读取、凭证、取消和重试接口：

- 浏览器支持收藏、历史、排序、筛选和连接测试；
- 同一协议允许保存多个具名播放源；添加网络源时先读取根目录验证连接，添加本地源时验证目录存在，验证成功后才持久化；
- 凭证存入 Windows Credential Manager、macOS/iOS Keychain、Android Keystore 或 Linux Secret Service；
- 播放前同目录扫描常见字幕格式，按文件名、语言标签和用户偏好排序；
- 字幕通过 Stream Broker 和 `mpv_stream_cb` 主路径加载，不把带密码的 URL 直接交给 mpv；
- WebDAV/FTP/SMB 服务端能力差异通过 capability probing 处理；Range 不可用时降级为顺序缓存；
- FFmpeg/libmpv 自带协议只作为可选快速路径，目录浏览和凭证仍由宿主管理。

首期字幕格式以 mpv/FFmpeg 实际支持为准，至少验证 SRT、ASS/SSA、WebVTT 和常见文本编码。压缩包内字幕列为后续能力。

### 7.4 QuickJS 插件系统

插件不直接获得 Avalonia 对象、libmpv 指针、libaria2 session 或任意 .NET 反射能力。宿主提供稳定、异步、版本化的 SDK：

- `tools.invoke()`：按插件权限调用统一 Tool；
- `services.register/list/describe/invoke`：通过 contract、版本与 JSON Schema 发布、发现和调用通用插件接口；
- 搜索、字幕、媒体解析和下载发现不再拥有宿主特判的专用 API，而是使用统一 Service Registry；
- `ui.contribute()`：向固定 slot 添加声明式页面、面板、命令、菜单、设置项和状态卡片；
- `playback`：通过 Tool 调整属性、滤镜、shader、轨道和播放列表；
- `downloads`：观察和管理被授权的任务；新增任务只能由直接用户手势打开宿主标准确认页，插件不能直接调用 `downloads.add`、`downloads.addBatch` 或 `search.download`；
- `storage`、`http`、`secrets`：按插件隔离的存储和受策略控制的网络访问。

界面修改采用声明式组件 schema 和稳定 extension slots。首期不允许插件执行任意 Avalonia/C# 代码；这使移动端、NativeAOT、主题一致性、崩溃隔离和权限审计可控。

每个插件使用独立 QuickJS runtime/context，设置内存上限、执行 deadline、中断回调、并发/网络限额和域名 allowlist。插件 HTTP 在实际连接前解析全部地址，拒绝 loopback、私网、链路本地和保留地址，并把 TCP 连接固定到已验证地址以阻断 DNS rebinding。Manifest 使用 capability 权限；安装、升级、权限增加和发布必须向用户展示 diff。桌面端后续可增加进程隔离，移动端保持消息边界一致。

插件开发闭环包括：模板、TypeScript 类型、打包、lint、单元测试、模拟宿主、日志、调试控制台、热重载（开发模式）和签名包。QuickJS 解释代码在 iOS 商店的允许范围也必须列入阶段 0 商店合规检查。

### 7.5 Agent 与统一 Tool 系统

原则是“所有面向用户、可安全组合的动作都成为 Tool”，而不是把渲染回调、内存访问等内部实现暴露给模型。Tool 同时服务于 UI、键盘命令、插件、自动化和 LLM，避免出现多套行为不一致的 API。

首期 Tool namespace：

- `playback.*`：打开、播放控制、seek、轨道、字幕、滤镜、属性、截图、播放列表；
- `downloads.*`：添加、暂停、恢复、删除、查询、限速、文件选择和选项；
- `media.*`：浏览、连接、resolve、缓存和最近记录；
- `plugins.services.*`：按 JSON Schema 发现并调用插件服务，供普通插件和 Agent 共用；
- `plugins.*`：列出、启停、开发、验证和受控安装；
- `ui.*`：导航、通知、打开面板和读取有限的 UI context；
- `settings.*`：读取和修改允许自动化的设置；
- `diagnostics.*`：获取脱敏状态、日志和测试结果。

每次调用经过同一个 Policy Engine：

- 调用主体为用户、插件、Agent 或系统，各自有独立 grants；
- 只读、可逆写入、高风险写入分级；删除文件、覆盖、凭证、任意网络域、插件安装/发布等需要显式确认；
- 支持 dry-run、取消、超时、进度、幂等键、调用链和审计日志；
- 网络和破坏性 Tool 必须在执行副作用前持久化 `Started` 审计，写入失败时关闭失败且不执行；执行后的 `Completed` 审计失败只记录诊断，不能把已发生的副作用伪装成调用失败；
- Tool 输出默认脱敏并限制大小，媒体网页和插件内容一律视作不可信输入，防止 prompt injection；
- Agent 不直接获得密钥；需要认证的动作由宿主使用 `CredentialRef` 代办。

LLM 层使用 provider-neutral 接口，支持云端 OpenAI-compatible API 和本地模型。远程 Base URL 强制 HTTPS；明文 HTTP 只允许 `localhost`、`127.0.0.1` 和 `::1` 等 loopback 地址。首期采用单 Agent tool loop，加明确的最大轮次、费用/Token 限额、取消按钮和执行记录；在可靠性数据充分前不做无边界自主循环。

“AI 自动搜索视频和字幕”的推荐流程：理解条件 -> 按 contract 发现并行调用已授权 Service -> 规范化/去重/评分 -> 展示来源、清晰度和字幕匹配 -> 用户策略允许时播放或下载。内置功能只面向用户有权访问的来源，不绕过 DRM、付费墙或访问控制；第三方插件仍受权限、地区法律和来源规则约束。

### 7.6 AI 创建、修改和调试插件

AI 使用专门的 Plugin Workspace，而不是直接修改已安装插件：

1. 从版本化模板创建工作区；
2. 只在该目录内编辑；
3. 静态检查 manifest、权限和 API 版本；
4. 在模拟宿主中运行单元测试，并用网络 mock 复现解析；
5. 在有资源/时间上限的 QuickJS runtime 中试运行；
6. 展示源码、权限和测试结果的 diff；
7. 用户批准后原子安装新版本，旧版本可回滚。

Agent 可读取脱敏日志、异常栈和测试结果并继续修复，但不得自行扩大权限、读取其他插件数据、写入仓库外路径或自动发布到公共市场。

## 8. 数据、安全与可观测性

- 结构化配置与状态数据库分开；数据库做版本迁移和崩溃恢复。
- 凭证使用平台安全存储，配置仅保存引用；日志中央脱敏。
- 日志默认关闭，可在设置的“通用 / 诊断”中即时启停；`app.log`、`aria2.log`、`mpv.log` 和 `quickjs.log` 按组件隔离，托管日志单文件达到 10 MiB 后保留一份滚动备份。QuickJS 文件在插件运行时接入后按需创建。
- Unix 上包含凭证引用、会话、设置、插件状态/存储及审计/日志的目录和文件分别收紧为 `0700` / `0600`，已有文件在访问时迁移；Windows 使用平台 ACL。
- 下载目标使用 canonical path 校验；Linux 删除本地文件时使用目录描述符、`openat(O_NOFOLLOW)` 和 `unlinkat` 固定每一级目录，阻止目录穿越、符号链接逃逸和检查/删除竞态，其他平台拒绝路径中的 reparse point。
- Stream Broker token 使用密码学安全随机值、绑定 session，不包含路径或凭证；主路径不监听端口。可选 HTTP Gateway 的 token 短期有效并且仅监听 loopback。
- 所有引擎事件带 correlation ID，可从一次 Agent 请求追踪到 Tool、插件、下载和播放事件。
- 提供用户可导出的脱敏诊断包；默认不上传遥测。
- 插件包、native 依赖和更新清单校验哈希/签名；产物生成 SBOM。

## 9. 实施阶段与验收门槛

阶段是依赖顺序；本计划已批准实施，各阶段仍以对应退出条件为准。

### 阶段 0：决策与高风险 Spike

1. 固化 GPL-3.0-or-later、对应源码交付、iOS 自签名分发和编解码器审计 ADR。
2. Linux x64 上构建最小 NativeAOT Avalonia 应用。
3. libmpv Render API 控件 Spike，验证 X11/Wayland、浮层和生命周期。
4. libaria2 C ABI shim Spike，验证 NativeAOT 回调、任务控制和退出恢复。
5. Stream callback / Stream Broker Spike，验证 ABI、生命周期、阻塞读取、取消、EOF、边下边播和随机 seek；loopback HTTP 仅验证兼容回退场景。
6. QuickJS NativeAOT Spike，验证模块加载、异步 bridge、限制和中断。
7. 为 Android/iOS 各做一次最小 native link/build，尽早发现静态链接和 AOT 问题。

退出条件：上述路径均有可重复测试和 ADR；GPL 基线及自签名分发路线已记录；发布依赖审计无未解释冲突；失败项有被批准的降级方案。App Store 法律结论不是本阶段退出条件。未通过时不进入大规模 UI 开发。

### 阶段 1：仓库与应用骨架

- 建立项目结构、版本锁定、格式化、测试、CI 和 native artifact manifest。
- 实现 Core、Application、Tool Registry、Policy、事件总线和本地设置。
- 建立 `CanX.DesignSystem`、Avalonia shell、响应式导航、直角主题、通知、日志和诊断页面。
- Windows/Linux 实现无系统边框的自绘标题栏；macOS 保留原生窗口框架；移动端实现同风格应用顶栏和 safe-area 布局。
- 锁定并验证 DataGrid 与选定的 FluentAvalonia 补充控件，不允许第三方主题绕过全局设计 token。
- NativeAOT 发布和启动 smoke test 纳入每次合并检查。

退出条件：Linux x64 可产生可启动的 AOT 包；示例 Tool 可被 UI/CLI 调用并留下审计记录；自绘标题栏和直角主题通过 X11/Wayland、缩放、键盘及截图回归。

### 阶段 2：下载器 MVP -> 完整下载中心

状态：已于 2026-09-08 完成 Linux x64 首要平台验收；其他目标平台留待阶段 7 逐平台验证。

- 先实现 HTTP 单任务、状态、暂停/恢复/删除、会话恢复。
- 再实现队列、全局/单任务选项、限速、校验和批量任务。
- 增加 FTP/SFTP、Torrent、Metalink、文件选择、peer/tracker 和做种。
- 完成 capability manifest、高级选项编辑器和覆盖率测试。

退出条件：本机可替代日常 aria2 GUI；重启不丢任务；选项覆盖报告无未解释缺口。

### 阶段 3：播放器 MVP -> 完整播放器

状态：Linux x64 的实现范围已完成，但跨设备发布验收未完成。播放器基础链路、窗口交互、持久化播放设置、轨道与字幕管理、播放列表/历史记录、章节/书签、高级播放控制，以及硬解/色彩/性能诊断均已实现。正式 `CanX.Playback` 模块、独立播放窗口、Render API 视频 surface、mpv 默认键鼠绑定和 `playback.*` Tool 已通过本机 Wayland/X11 真实播放与静态 NativeAOT 验证。播放窗口内提供互斥的列表、轨道、章节/书签、高级播放和诊断浮层；诊断覆盖实际硬解与帧传输路径、输入/目标色彩元数据、性能统计、运行时 HDR 控制，以及受限的高级 property/command。Render API 已按 advanced-control 契约串行执行 update/render/present/report-swap，并由宿主提供显示刷新率与 vblank 节拍；seek 期间的临时 `REDRAW` 只推进 mpv renderer 状态而不覆盖最后一张已发布纹理，目标帧到达后再交换，避免 Linux 瞬时黑闪。23.976 fps 的 4K HEVC 素材已在 59.98 Hz 显示器上通过 NVDEC、插值和 NativeAOT 实播验证。Linux x64 的自动稳定性基线还覆盖 120 秒连续播放、周期 seek、窗口隐藏/恢复、Render Context 多次重建、无帧超时与内存增长检查；本机单屏环境无法替代真实多显示器和系统休眠唤醒验收。上述能力均已纳入 Tool；当前实现和剩余范围见 [阶段 3 播放器实现状态](STAGE-3-PLAYBACK.zh-CN.md)。

Linux x64 原生构建已改为与构建机 GPU 无关的通用 profile，同时编入 AMF、QSV/oneVPL、VAAPI、Vulkan Video、OpenCL 与 NVDEC/CUDA；详细启用状态、静态/动态边界和未启用原因见 [FFmpeg / libmpv 功能矩阵](MPV-FFMPEG-FEATURES-linux-x64.md)。

- 已完成本地文件/HTTP(S) 播放、Render API 渲染、独立播放窗口、基础控制、默认快捷键与鼠标/触控板手势。
- Android 已使用独立 `PlaybackActivity` 承载视频 Surface 与顶部/底部浮层，隐藏状态栏、导航栏和系统标题栏；主 Activity 只保留播放源浏览。播放器 Activity 使用独立 `AvaloniaView`，返回后释放 Render Context 且不破坏主界面的选择与滚动状态，再次进入可重新创建 Surface。
- 已完成音频/视频/字幕轨道选择、禁用轨道、本地外部字幕加载，以及字幕可见性、字号、垂直位置和延迟的运行时控制；对应 `playback.*` Tool 与多音轨/外部字幕真实播放 smoke test 已补齐。带凭证或远程来源的字幕仍统一留给阶段 4 Stream Broker。
- 已完成播放列表、多文件选择、文件夹打开、队列重排/移除、上一项/下一项、最近播放和断点续播。队列与最近 100 项以原子 JSON 文件独立持久化；带认证信息或 query/fragment 的远程地址只保留在本次运行内，不以明文写入状态文件。
- 已完成章节发现与跳转，以及按媒体保存的书签创建、重命名、删除、清空和跨媒体打开。每个媒体最多 500 个、全局最多 5,000 个书签；敏感远程 URL 的书签同样不落盘。
- 已完成逐帧前进/后退、A-B 循环点设置/清除、纯视频/含字幕/完整输出截图、音频设备发现与切换、运行时音视频滤镜，以及本地 `.glsl`/`.hook` Shader 的添加、移除与清空。截图默认保存到系统图片目录下的 `CanX` 文件夹，且不会覆盖已有文件；设备和 Shader 列表通过 libmpv 原生 node 属性读取。
- 已完成硬件解码与 CPU/GPU 帧传输路径诊断、输入/目标色彩与 HDR 元数据、性能统计、常用 HDR 运行时控制，以及具有统一安全边界的高级命令/属性面板。
- 已完成 Direct Composition 呈现优化。分段诊断、共享纹理 `composition-shared` 后端、GL fence 回退及 Linux Wayland/X11、Android 16 真机验收均于 2026-09-20 完成：桌面使用三缓冲；Android 根据真机 `UpdateAsync` 延迟使用双缓冲，将提交频率从约 90 次/秒收敛至实际 60 Hz。主路径的 CPU 回读、CanX 自有全帧 blit与逐帧 `glFinish()` 均为 0，Avalonia 12.1.2 内部仍保留一次 GPU snapshot。Android 已覆盖 seek、方向切换、Home 后经桌面图标/媒体通知恢复并保持同一播放器实例；`shared-worker-fence`、`shared-worker-finish` 与 `inline` 继续作为分级回退。现有测量不支持维护 Avalonia fork，除非后续设备数据证明 compositor snapshot 成为主要瓶颈，否则保持当前统一渲染架构。
- Avalonia 负责在播放父容器采集并转发键鼠输入，libmpv 执行其内置默认绑定；全屏、退出、置顶、窗口倍率和右键暂停等窗口级行为由宿主等价实现。
- 播放画面提供无修饰左键拖动手势：横向 seek、左半区纵向亮度、右半区纵向音量；方向一旦识别即锁定，带修饰键操作保留给 mpv 原生绑定。该交互已于 2026-09-10 通过用户实际播放验收。
- 播放配置在 libmpv 创建前注入，常用项提供类型合适的专用控件，长尾项使用高级文本；CanX Render API 宿主所需的输出和窗口选项禁止覆盖。
- 已完成从实际 libmpv `property-list` / `command-list` 发现的 versioned capability manifest、统一安全策略、`playback.capabilities` Tool 与属性/命令零缺口覆盖测试；当前 Linux x64 构建发现 968 项 property、87 项 command。

退出条件：连续播放、seek、全屏、多屏和休眠恢复稳定；无 UI 线程阻塞；高级能力可发现且可审计。

### 阶段 4：Stream Broker 与网络媒体

状态：Linux x64 桌面实现已于 2026-09-10 完成，真实服务端人工互操作矩阵延期统一验收。已完成独立 `CanX.Streaming` 模块、opaque `canx://` session 路由、独立 stream lease、注销/销毁取消、`.NET Stream` 适配，以及 `mpv_stream_cb_add_ro` 的 NativeAOT 回调桥。Linux x64 已在 Wayland、X11 和无 native sidecar 的静态 NativeAOT 产物上完成真实解码渲染、seek、size、未知长度 EOF、阻塞 read cancel、open/close 配对验证。`canx://` 来源只在当前会话有效，不写入播放列表、最近记录或书签的持久化文件。完整实现状态见 [阶段 4 Stream Broker 与网络媒体](STAGE-4-STREAMING.zh-CN.md)。

aria2 边下边播链路也已完成首轮闭环：fork 的统一 `canx/file-priority` 分支提供运行时 piece 区间提权；seek 改变优先区间时，HTTP/FTP/WebSeed 会保存已接收的数据并立即重发 Range/offset 请求，BitTorrent peer 会取消旧块后选择新分片，不再等待旧区间自然结束。`Aria2MediaByteStream` 只读取 bitfield 已确认完成的稀疏文件区间，在缺失时等待、提权和响应取消，并以稳定的 16 MiB 窗口避免逐分片重复调度。下载主列表使用清晰的普通进度条；任务选项提供独立的大尺寸分片进度页，任务行悬停提示同时显示传输详情和分片分布。Windows x64 的渐进播放 smoke 会记录 seek 到恢复出帧的耗时；Linux x64 已用 524 MiB HEVC 10-bit MP4 在 2 MiB/s 限速下验证文件未下载完成即可开始渲染、seek 到 70% 后继续出帧，期间只使用进程内 callback，未监听本地端口。Windows 的 MSYS2/MSVC 交叉工具链无法让 Automake 识别 clang 头文件依赖模式，因此 aria2 构建会先清理旧对象，并在静态 archive 变化时强制重新链接开发 sidecar，避免增量构建产生 C++ ABI 混用。

网络媒体已完成 WebDAV、FTP/FTPS 与 SMB 的桌面纵向闭环：新增 `CanX.MediaAccess` 边界；WebDAV 客户端可安全解析 `PROPFIND`、约束浏览范围并按一次单字节 GET 探测服务端能力，支持 Range 时使用带窗口缓存的随机读取，未提供资源长度时从 `Content-Range` 补全，不支持 Range 时复用同一响应降级为不落盘的顺序播放；幂等的 WebDAV 目录查询、能力探测以及包含响应体传输的完整 Range 读取会对超时、传输异常、HTTP 408/429/5xx 做最多三次且可取消的短退避重试。FTP 使用纯托管 FluentFTP 处理不同服务端的列表与被动连接差异，以独立数据连接提供顺序读取，并在服务端支持 `REST` 时提供随机 seek；SMB 使用纯托管 SMBLibrary 与 SMB 2/3 共享通信，通过 offset read 提供随机读取，不依赖 `libsmbclient`。FTP 与 SMB 的目录操作及播放读取也会对瞬时传输故障做最多三次短退避重试；可 seek 流从最后已交付的字节位置重连，不支持 REST 的 FTP 仅在尚未交付数据时自动重试，避免重复字节破坏解码。播放器主页现统一列出具名的本地文件夹、WebDAV、FTP/FTPS 和 SMB 播放源，只保留一个应用内“添加播放源”浮层入口；同一协议可保存多个来源，网络源在落库前先验证根目录，双击任一本地或网络来源后都进入统一目录浏览器。浏览器列出文件夹及全部文件，支持后退/前进、收藏目录、按名称/类型筛选并按名称/大小/修改时间排序；不受支持的文件仍可见但播放操作禁用，只有用户选择受支持的媒体后才打开播放器。本地媒体以原始文件路径交给 libmpv；网络媒体可自动匹配或手选同目录字幕，并通过 opaque `canx://` 注册把媒体与字幕直接交给 libmpv。不可 seek 的来源会禁用播放进度条和快进/快退按钮。每个来源以稳定 ID、名称、协议、无敏感参数的 endpoint、用户名及 `CredentialRef` 原子写入 `media-sources.json`，密码只进入 Windows Credential Manager、macOS Keychain 或 Linux Secret Service；收藏只保存协议、来源根和根内目录，不复制凭证。安全存储不可用时认证源添加失败且绝不明文降级。日志不记录端点、凭证和原始网络异常。Linux x64 已用本地 FTP 与进程隔离 SMB fixture 验证认证、目录浏览、媒体读取与 seek、远程字幕轨选择和字幕文本解码，整个播放过程不落地媒体或字幕文件。真实 WebDAV 实现、FTP/FTPS 服务端、Samba/Windows SMB 共享以及断线和网络切换测试按用户安排延期到统一人工验收。

2026-09-11 补充：FTP 的 `FEAT` 只作为能力提示；未声明 `REST STREAM` 时，CanX 会用 `REST 0` 主动探测并接受 2xx/3xx 响应，避免把 rclone 一类实际支持随机读取的服务端误判为不可 seek。mpv 不会把自定义 stream callback 自动识别为网络流，因此全局缓存模式为 `auto` 时，宿主会给 `canx://` 媒体附加文件级 `cache=yes`；用户显式选择 `cache=no/yes` 时不覆盖。设置页已暴露预读时长、向前/向后内存上限、缓存耗尽自动暂停、首次缓冲和恢复阈值。缓存由 mpv demuxer 执行，不增加 loopback HTTP 服务或第二套应用缓存。

1. 已完成：定义可阻塞读取、绝对 seek、长度查询和取消的 `IMediaByteStream`，实现进程内 Stream Broker、opaque session 路由、独立 stream lease 与凭证隔离。
2. 已完成：使用 `mpv_stream_cb_add_ro` 把固定 `canx://` scheme 接入 libmpv；完成 NativeAOT callback、异常映射、ABI/布局、注册与 cookie 生命周期、seek/cancel/EOF 测试和真实播放 smoke。
3. 已完成：实现 aria2 稀疏文件数据源、已完成区间读取、缺失 piece 等待/提权和 seek 协调，并提供下载任务“边下边播”入口。当前稳定策略是网络媒体直接流式播放、aria2 任务正式下载并可边下边播；是否增加“播放并缓存”的独立产品模式留到缓存策略设计时决定。
4. 已完成桌面实现：WebDAV、FTP/FTPS 与 SMB 已完成浏览、stat、连接测试、目录历史与收藏、筛选与排序、随机或降级顺序读取、播放、同目录字幕选择、安全凭证存储、有限瞬时故障重试和可 seek 流断点重连；`canx://` 默认启用 mpv 网络缓存，并向设置页暴露前后缓存上限及缓冲阈值。真实服务端矩阵作为外部人工验收项保留，不伪装为已经通过。
5. 已落实阶段 5 边界：插件只能传递经过校验的媒体描述符或请求宿主创建数据流，二进制数据不经过 QuickJS 消息循环；manifest、权限和 SDK 已按该边界实现。
6. 可选 loopback HTTP Range Gateway 不属于当前 callback 主路径；只有 HLS/DASH 嵌套相对 URL、外部播放器或跨进程消费者提出明确需求时才实现。
7. 已自动覆盖 Range/无 Range、已知/未知长度、正常 EOF/可 seek 流伪 EOF、响应体中断恢复、资源永久变化、取消阻塞读取、并发 lease 与 seek；真实协议断线、网络切换和服务端差异留待人工矩阵。

实现退出条件已经满足：默认播放链路不打开本地端口，URI 和日志不包含凭证；stream callback 的 open/read/seek/size/cancel/close、EOF 与销毁顺序均有自动化验证；边下边播与不可 seek 限制会在 UI 中准确呈现。发布验收仍要求 WebDAV、FTP、FTPS、Samba 和 Windows SMB 的真实服务端矩阵以及网络故障测试，通过前不宣称阶段 4 的跨服务端兼容性验收完成。

### 阶段 5：插件平台

- QuickJS host、manifest、权限、SDK、宿主 HTTP 和 storage。
- 统一 Service Registry、JSON Schema 接口、播放与下载 Tool。
- 声明式 UI contributions、开发工具、测试和原子安装/回滚。
- 至少四个示例插件：视频搜索、字幕搜索、播放器 UI/滤镜扩展、HTTP Magnet 搜索。

退出条件：插件不能越权读取文件/密钥/网络；异常或死循环不冻结 UI；API 有兼容性测试。

2026-09-13 完成：Manifest/权限、签名包、原子安装与持久化单级回滚、独立 QuickJS
runtime/context/线程、版本化 TypeScript SDK、宿主 HTTP/storage/secrets、统一 Service Registry、
有限播放/下载动作、声明式 UI 全 slot 与独立插件页、桌面管理、热重载、只读调试控制台和
十四个示例均已实现。权限增加会重新审批，签名与生命周期变更写入持久化审计。自动测试覆盖
文件/密钥/网络越权、异常/OOM/死循环与 runtime 恢复，以及仅接受 API 2.0 的完整契约；Windows x64 完整
应用已通过 NativeAOT 静态单文件发布、PE 依赖审计和启动 smoke。验收细节见
[阶段 5 插件平台验收报告](STAGE-5-PLUGINS.zh-CN.md)。

2026-09-15 安全复核完成：插件新增下载统一收口到带直接用户手势的确认页；HTTP 增加非公网地址
拒绝和 DNS pinning；生命周期操作采用 `Started` / `Completed` 双阶段审计并在状态写入失败时补偿恢复；
插件状态、存储、审计和日志在 Unix 上使用私有权限。

2026-09-18 补充：完成 `cocofhu/anime-find` 的 CanX 原生移植，不引入其 DSH React ToolView。
插件以桌面/Android 共用的声明式页面聚合 Mikan、AniBT、AnimeGarden 与 Bangumi，下载进入宿主确认页；
静态规则播放支持受限 CSS/XPath、MacCMS 地址解码、按域 Header 和 HLS 子资源重写，并通过
`canx.media.stream` 直接进入 libmpv Stream Callback。番剧详情 Service 可直接供 AI 查询，标准媒体和
BitTorrent Service 则必须经过宿主 `search.*` Tool 的 opaque 缓存，真实 URL、Magnet 和 Header 不进入
模型上下文。SDK Mock Host 同步执行宿主级声明式 UI 属性、长度、布局和资源限制校验。

2026-09-18 补充：声明式页面新增独立 `ui.remoteImages` 权限，scope 支持精确域名、`*.domain`
与用户显式批准的 `*`。远程图片不经 QuickJS/Base64 搬运，也不由 Avalonia 控件直接联网；宿主统一
执行逐跳权限、公网 DNS、无凭据 GET、2 MiB 限制与 PNG/JPEG/WebP 签名校验，并提供 runtime 生命周期
内的有界内存缓存。Anime Find 与 Bangumi Anime 已迁移为直接提交封面 URL。

### 阶段 6：Agent

- Provider-neutral LLM 配置、会话、tool loop、预算和取消。
- 将已有用户级动作逐项纳入 Tool coverage report。
- 搜索视频/字幕、选择、播放或下载的端到端流程。
- Plugin Workspace 创建、修改、测试、调试、diff、安装和回滚。
- prompt injection、恶意 Tool 参数、秘密泄漏和循环调用红队测试。

退出条件：所有写操作有策略和审计；高风险操作无法绕过确认；自动化结果可解释、可取消、可复现。

2026-09-14 完成：已实现 provider-neutral、有轮次/Token/Tool/响应大小/时间上限的 Agent loop，
OpenAI-compatible 公司网关或本地服务配置、系统凭据中的 API Key、桌面会话和逐次确认 UI；109 个
Tool 覆盖下载、播放、网络媒体、插件 Service、聚合搜索、设置、诊断、UI 和隔离 Plugin Workspace。
视频/字幕/Torrent 搜索使用宿主缓存的 opaque ID 完成受控播放/下载，不向模型暴露解析参数或真实
媒体地址。Plugin Workspace 提供受限编辑、固定 lint/build/test/pack、断网 QuickJS 试运行、源码/
权限/测试结果审查、digest 绑定安装与生命周期回滚。红队测试覆盖 prompt injection、伪造历史、恶意
参数、秘密脱敏、调用/Token/时间/响应预算、取消、路径穿越、符号链接、陈旧审核和包篡改。详细证据见
[阶段 6 Agent 与 Tool 平台验收报告](STAGE-6-AGENT.zh-CN.md)。

2026-09-15 安全复核完成：远程 LLM endpoint 强制 HTTPS；高风险 Tool 执行前审计不可用时关闭失败；
批量下载改为整批提交/失败回滚；调用者取消可传播到正在执行的下载命令。

### 阶段 7：跨平台发布

状态：Linux x64 发布工程已于 2026-09-14 完成，包括 Ubuntu 22.04 固定构建入口、静态 NativeAOT
依赖审计、GLIBC 2.35 上限检查、二进制/符号/完整对应源码归档、SPDX 2.3 SBOM、第三方声明、
许可证 bundle、校验和及 GitHub Actions release-candidate workflow。本机已验证生成器和归档结构；
因当前主机没有 Docker/Podman，Ubuntu 22.04 正式 CI 首跑仍是 Linux x64 发布阻塞项。详情见
[阶段 7 Linux x64 发布工程](STAGE-7-RELEASE.zh-CN.md)。

Android 完整宿主已于 2026-09-15 完成 ARM64/x64 NativeAOT 构建。下载、播放、网络媒体、QuickJS
插件、Agent、设置、通知、前台服务、MediaSession 和 SAF 均进入移动端组合根；ARM64 已在 Android 16
真机完成 109 Tool 初始化、主要页面与内嵌对话框、长期 aria2 轮询、Activity 原进程重建，以及
独立沉浸式全屏播放 Activity、4K HEVC Main 10 的 MediaCodec/AImageReader 零拷贝播放验证。
播放器未手动切换方向时遵循系统方向锁定与自动旋转；用户点击 Overlay 的横/竖屏按钮后，改由 App
锁定纵向或横向类别，其中横屏仍允许传感器在左右两个横屏方向间自动翻转。
x64 APK 已完成 ABI 和动态依赖审计，尚待 x64 模拟器运行。详见
[阶段 7 Android 实现与验证](STAGE-7-ANDROID.zh-CN.md)。

按 Linux x64 -> Windows x64 -> macOS ARM64/x64 -> Linux ARM64/Windows ARM64 -> Android ARM64/x64 -> iOS ARM64 的顺序推进。每个平台完成：

- native 依赖构建、AOT、渲染、硬解和协议能力验证；
- 文件选择器、通知、后台、休眠、权限和安全存储适配；
- 安装包、更新、签名、公证/商店材料、许可证、对应源码和 SBOM；iOS 默认验证可重签名 IPA 与用户自签流程，不默认准备 App Store 材料；
- 平台能力矩阵及已知限制。

移动端不是简单重新编译：触控 UI、后台下载/播放、系统媒体会话、存储沙箱和电量策略需要独立验收。

## 10. 测试策略

- 单元测试：领域状态机、Tool schema、权限、URL/path、字幕匹配和选项映射。
- Native ABI 测试：所有权、回调线程、异常、编码、销毁顺序和版本不匹配。
- 集成测试：真实 aria2/mpv/QuickJS，各网络协议使用容器化测试服务。
- 媒体 fixtures：小型、可再分发的 MP4/MKV/HLS、不同索引位置、音轨和字幕编码。
- UI 测试：下载/播放关键流程、键盘、触控、无障碍、亮暗/高对比度主题、自绘窗口 chrome 和 100%–300% 缩放；关键控件必须做跨平台截图回归。
- AOT 测试：每个支持 RID 至少做发布构建和启动 smoke test，正式平台做播放/下载真机测试。
- 故障测试：断网、磁盘满、权限撤销、源变化、插件死循环、native crash 和恢复。
- 安全测试：SSRF、目录穿越、恶意播放列表/字幕、zip bomb、日志泄密、prompt injection 和依赖扫描。

## 11. Git 与交付约定

- 默认分支：`main`。
- 一项可独立验收的功能对应一个提交；使用 Conventional Commits，例如 `feat(playback): add subtitle track selection`。
- 功能完成后自动执行相关格式化、测试和 AOT smoke test；通过才提交，不提交已知失败状态。
- 提交只暂存本功能涉及的文件，保留用户的并行修改；不自动 push、不改写已公开历史。
- 架构或产品决策用 `docs/adr/` 单独提交；生成物、密钥和签名材料不入库。
- “完成”的统一定义：验收条件满足、测试通过、Tool/权限/文档同步、无明文秘密、变更已提交且工作区无本功能遗留修改。
- 全程采用 AI Vibe Coding：AI 根据已确认的目标与验收条件完成检索、实现、测试、差异复核和提交；失败测试必须先诊断并修复，不能靠删除断言或虚报通过绕过。
- 人类保留产品范围、许可证法律意见、凭证、外部发布、高风险或不可逆操作的决策权；AI 不自动 push、不签名发布、不扩大插件或 Agent 权限。
- 每个阶段维护可重复执行的验证脚本与状态报告，让后续 AI 会话能从仓库证据继续，而不是依赖聊天上下文。

计划文档与仓库初始化作为首个文档提交；计划已由用户确认，后续按阶段和退出门槛推进。

## 12. 计划评审时需要确认的决策

按阻塞程度排序：

1. “完整 aria2/mpv”是否接受“专用 UI + 高级选项/属性控制台 + capability report”的定义。
2. 是否接受 Render API 为主、进程内 Stream Broker + mpv stream callback 为统一数据通路。
3. 插件 UI 是否接受声明式 slots；是否确实需要不受限制地改写任意界面。
4. 插件来源是仅本地开发、受信仓库，还是开放社区市场；不同选择决定签名和隔离强度。
5. LLM 首期需要支持哪些 provider，本地模型是否是首发硬要求。
6. 搜索源由用户/插件配置是否可接受；内置源的合法范围和 DRM 边界如何确定。
7. 本机 Linux 会话主要是 Wayland 还是 X11，以及首个可用版本最需要下载器、播放器还是插件/Agent 能力。

## 13. 当前推荐的默认决策

若没有相反意见，实施时建议采用以下默认值：

- CanX 使用 GPL-3.0-or-later；发布对应源码、固定 native build recipes、许可证材料和 SBOM。
- iOS 默认提供可重签名 IPA/构建说明，由用户自行签名和侧载；App Store 是否支持留到最终发布阶段另行决策。
- Linux x64 + Wayland 作为第一开发基线，同时跑 X11 smoke test。
- 全平台使用统一的 Win10 式直角 `CanX.DesignSystem`；Windows/Linux 自绘标题栏，macOS 保留原生窗口框架，移动端使用同风格应用顶栏。
- UI 以 Avalonia 官方控件和 DataGrid 为基线，FluentAvaloniaUI 只补充已验证控件；插件编辑器延后使用 AvaloniaEdit，Dock 保持可选。
- libmpv Render API + OpenGL/GLES 为主渲染路径。
- 进程内 Stream Broker + `mpv_stream_cb` 为统一媒体入口；loopback HTTP Gateway 只作为明确场景下的可选回退。
- 插件只做声明式 UI contribution，权限默认拒绝，社区插件不自动信任。
- 所有用户级命令 Tool-first；UI、插件和 Agent 共用实现。
- 首发先完成可靠的下载器和播放器核心，再开放插件和 Agent 自修改能力。
