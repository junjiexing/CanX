# ADR-0002：libmpv 与 Avalonia 的渲染集成

- 状态：已接受
- 日期：2026-09-08
- 修订：2026-09-20

## 决策

CanX 采用 libmpv Render API 作为主渲染路径，通过 Avalonia `OpenGlControlBase` 提供的 OpenGL/OpenGL ES framebuffer 合成视频。平台原生子窗口嵌入不作为统一实现，只保留为将来的诊断降级方案。

生产代码必须把渲染表面、mpv core 控制和平台图形资源分成三个接口；普通 mpv 命令不得在 render callback 内同步执行。

生产 Render Context 开启 `MPV_RENDER_PARAM_ADVANCED_CONTROL`。update callback 仅发信号，所有 `mpv_render_context_update/render/report_swap/free` 调用必须在持有创建时 GL context 的同一渲染线程上串行执行。只有 update 返回 `MPV_RENDER_UPDATE_FRAME`（或输出尺寸确实变化）时才调用 render；Avalonia 呈现新纹理后再把 swap 通知排回渲染线程。Avalonia 的离屏 redraw 不能视为阻塞式 vblank，因此宿主根据显示器刷新率为 swap 回报限速；libmpv Render API 无法自行获知该值时，由平台适配层探测并设置 `display-fps-override`。

2026-09-20 补充决策：首选呈现后端改为 Direct Composition shared texture。mpv worker 直接渲染到 Avalonia composition 可导入的三缓冲纹理，以异步提交完成表示纹理所有权归还；优先显式 GPU fence/semaphore，不可用时用 `glFlush` 加 slot 生命周期隔离。固定回退为 `composition-shared`、`shared-worker-fence`、`shared-worker-finish`、`inline`。Android 不采用 `mediacodec_embed` 作为主路径，以保留统一的 Shader、字幕、OSD、色彩处理和 Avalonia 浮层。

“零拷贝”诊断必须分段：硬解码 surface 到 mpv renderer 可为零 CPU 回读，但这不代表 mpv 输出到 Avalonia compositor 没有 GPU copy。运行时必须分别报告解码传输、呈现后端、GPU copy 数和同步策略。

## 阶段 0 证据

`spikes/CanX.Spikes.MpvAvalonia` 已验证：

- .NET 10 NativeAOT executable 能以 source-generated P/Invoke 创建、初始化和销毁 mpv 0.41；
- unmanaged GL proc-address callback 和 render-update callback 在 AOT 下工作；
- mpv 能把动态测试视频连续渲染到 Avalonia 提供的 FBO；
- 同一代码在原生 Wayland backend 和 X11 fallback 上均通过；
- 普通 Avalonia 控件可位于视频 surface 上方，不引入 native child window 的 airspace；
- 本机使用 OpenGL 4.0，测试窗口的 HiDPI framebuffer 尺寸与逻辑尺寸不同，像素尺寸由 Avalonia scaling 正确换算。
- X11/NVIDIA 下直接关闭仍挂载着 OpenGL surface 的窗口会在 Skia texture teardown 中崩溃；先从 visual tree 移除 surface、完成 `OnOpenGlDeinit`，再关闭窗口可以稳定退出。生产生命周期必须保留这个顺序并做回归测试。

重复验证入口是 `scripts/verify-mpv-avalonia.sh`。成功标志为两条分别包含 `backend=Wayland` 和 `backend=XID` 的 `CANX_MPV_RENDER_OK`。

## 限制与后续验证

- Ubuntu package 只用于 Spike；正式产物必须使用仓库管理的、逐平台可复现的 mpv/FFmpeg build recipe。
- Spike 未向 mpv 传递 `wl_display`/X11 Display，因此只证明 GPU render API 可合成，不证明 VA-API zero-copy。直接硬解互操作需要单独的平台资源桥接。
- macOS、Windows、Android 和 iOS 仍需各自验证图形上下文、生命周期和硬件解码。
- 全屏、多屏、HDR、context loss、后台恢复和长时间播放属于播放器实现阶段的验收矩阵，不由这个最小可行性 Spike 代替。
