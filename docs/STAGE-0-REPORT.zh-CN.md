# CanX 阶段 0 验证报告

- 日期：2026-09-08
- 本机基线：Ubuntu 26.04.1 LTS、Linux x64、.NET SDK 10.0.400
- 总状态：**本机验证通过，阶段 0 未关闭**
- 阻断项：iOS native link/build 需要 macOS + Xcode

## 结论

核心技术路线可以继续：Avalonia 12.1.2 能以 .NET 10 NativeAOT 在 Linux/Wayland/X11 运行；libmpv Render API 能嵌入 Avalonia；libaria2 可经版本化 C ABI 驱动；aria2 与 libmpv 可通过 Range Gateway 边下边播；QuickJS 可在受限 runtime 中异步调用宿主 Tool；Android ARM64/x64 均能生成 NativeAOT APK。Linux x64 上的 libaria2、libmpv、QuickJS 及其已启用传递依赖也已由固定源码构建并静态合入 NativeAOT 可执行文件。

这不是阶段 1 的无条件放行。根据计划的退出条件，iOS 仍须在 Apple 主机完成 native link/build。iOS 默认采用用户自行签名和侧载路线，App Store 法律结论已不再是阶段 0 或阶段 1 的门槛。技术验证未完成前，状态保持为“阶段 0 执行中”。

## 能力与证据矩阵

| 项目 | 状态 | 已验证证据 | 尚未覆盖 |
|---|---|---|---|
| 许可证与分发 | 工程决策通过 | 采用 GPL-3.0-or-later；iOS 默认用户自行签名和侧载；native recipe、对应源码、SBOM、notices 为发布要求 | 正式发布依赖审计；地区 codec/专利复核；仅在决定上架时复核 App Store/GPL 兼容性 |
| Avalonia NativeAOT | 通过 | Linux x64 单文件 native executable；Wayland、X11 均启动和退出 | 其他桌面平台运行测试 |
| libmpv + Avalonia | 通过（Linux Spike） | mpv 0.41 Render API；Wayland/X11；FBO 渲染、浮层、HiDPI、正确销毁顺序 | 硬解 zero-copy、HDR、多屏、休眠恢复及非 Linux 图形后端 |
| libaria2 C ABI | 通过（Linux Spike） | aria2 1.37.0；AOT P/Invoke、回调、暂停/恢复、状态、完整下载、干净退出 | Torrent/Metalink 与完整 option/capability manifest 属于阶段 2 |
| Stream Gateway | 通过（Linux Spike） | fast-start MP4、尾部索引 MP4、MKV、随机中部/尾部 Range、无 Range 503、断线续传、逐字节校验 | bitfield、多连接、BT、鉴权 token/缓存属于正式实现 |
| QuickJS bridge | 通过（Linux Spike） | QuickJS 2025-09-13；ES module、异步 Promise Tool bridge、16 MiB 内存限制、25 ms 中断 | 模块 loader、签名、进程隔离、完整插件 SDK |
| 原生依赖静态化 | 完整通过（Linux x64） | libaria2/libmpv/QuickJS/SkiaSharp/HarfBuzzSharp 及其启用依赖均为源码构建 archive；最终 ELF 及发布目录无第三方动态库；Wayland/X11 渲染及 aria2 下载回归通过 | 其他 RID；最终 codec/硬解 feature manifest |
| Android NativeAOT | ARM64 真机通过，x64 构建通过 | Avalonia 12.1.2；完整 `android-arm64` 与 `android-x64` APK；ARM64 Android 16 启动、输入、模块和 Activity 重建 smoke | x64 模拟器；广泛设备/厂商兼容性；上游仍标记 `XA1040` experimental |
| iOS NativeAOT | 外部门禁 | iOS 工程和可重复脚本已完成；Linux 静态校验通过 | macOS/Xcode 上 `ios-arm64`、`iossimulator-arm64`、`iossimulator-x64` native link/build |

## 可重复验证入口

```sh
scripts/verify-avalonia-aot.sh
scripts/verify-mpv-avalonia.sh
scripts/verify-aria2-shim.sh
scripts/verify-stream-gateway.sh
scripts/verify-quickjs-shim.sh
scripts/verify-android-aot.sh
scripts/verify-ios-aot.sh --static
scripts/build-static-native-linux-x64.sh all
scripts/verify-static-native-aot.sh --skip-build
```

本轮实际通过标记：

- `CANX_AVALONIA_AOT_OK`：Wayland、XID；
- `CANX_MPV_RENDER_OK`：Wayland、XID；
- `CANX_ARIA2_SHIM_OK`；
- `CANX_STREAM_GATEWAY_MATRIX_OK`，五个场景均有 `CANX_STREAM_CASE_OK`；
- `CANX_QUICKJS_OK`；
- `CANX_ANDROID_AOT_OK`：android-arm64、android-x64；
- `CANX_IOS_SCAFFOLD_OK`；
- `CANX_STATIC_NATIVE_BUILD_OK`、`CANX_NATIVE_DEPENDENCY_AUDIT_OK`、`CANX_STATIC_NATIVE_AOT_OK`。

在当前 Linux 主机直接运行 `scripts/verify-ios-aot.sh` 会以退出码 2 返回：

```text
CANX_IOS_AOT_BLOCKED reason=requires-macos-and-xcode host=Linux
```

这表示外部测试门禁，而不是构建通过。

## 已确认的工程决策

1. libmpv 使用 Render API；平台差异封装在 `IVideoSurface` 后端。
2. libaria2 只通过版本化 C ABI shim 暴露给 .NET。
3. 阶段 0 以 loopback HTTP Range Gateway 验证边下边播可行性；阶段 4 复验后将正式主路径改为进程内 Stream Broker + mpv stream callback，loopback 只保留为特定场景的可选方案，见 ADR-0004。
4. QuickJS 每插件独立 runtime，插件与宿主通过异步 Tool 消息边界交互。
5. 插件只贡献声明式 UI，不获得 Avalonia 对象、native 指针或任意反射能力。
6. 所有用户级能力 Tool-first，UI、插件和 Agent 共用 Policy、审计和执行实现。
7. 源码和全部平台组合产物以 GPL-3.0-or-later 为基线；不把本机发行版 native 包当成发布产物。iOS 默认由用户自行签名和侧载，App Store 留到最终发布阶段决定。
8. 原生库采用“可控依赖从固定源码静态链接、系统 ABI 动态链接”的策略，详见 ADR-0006。

## 静态链接现状

Linux x64 已完成并实际运行验证的 archive 包括 aria2、mpv、QuickJS、SkiaSharp、HarfBuzzSharp、Skia 内置 codecs、FFmpeg、libass、libplacebo、OpenSSL、zlib、SQLite、c-ares、libssh2、Expat、HarfBuzz、FreeType、FriBidi、Fontconfig 和 libunibreak。所有顶层源码 commit 位于 `native/sources-linux-x64.lock`，SkiaSharp 的 submodule/`DEPS` 固定其传递源码；发布不再把发行版或 NuGet `.so` 当作输入。

完整 Avalonia + libmpv 输出已经不再包含 `libSkiaSharp.so`、`libHarfBuzzSharp.so`，最终 ELF 也不依赖 `libfontconfig.so.1`。操作系统 ABI、GPU/音频驱动和 Apple Framework 按 ADR-0006 保持动态链接。

## 进入阶段 1 前的门槛

1. 在 macOS/Xcode 主机执行 `scripts/verify-ios-aot.sh`，保存三个 RID 的成功日志；若某个 RID 失败，记录根因和经批准的降级方案。
2. Android ARM64 真机 smoke 已完成；仍需补做 x64 模拟器和更多厂商设备矩阵。

若需要在上述 Apple 技术门槛完成前启动阶段 1，应由用户明确批准风险豁免；阶段状态仍不得改写为“完成”。
