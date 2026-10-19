# ADR-0006：原生依赖优先静态链接

- 状态：已接受；Linux x64 核心引擎已验证，其他 RID 待各平台 CI 验证
- 日期：2026-09-08

## 背景

CanX 使用 NativeAOT，并直接集成 libaria2、libmpv 与 QuickJS。发布包应尽量避免携带无法统一升级、审计和签名的第三方动态库，同时不能假装操作系统 ABI、GPU 驱动或 Apple Framework 可以可靠地静态化。

## 决策

1. libaria2、libmpv、QuickJS 及可控的传递依赖均从固定源码版本构建。默认通过 `.a`/`.lib` 和 NativeAOT `DirectPInvoke` 合入最终程序；若平台运行时存在无法安全合并的 native runtime，允许使用符号隐藏、依赖静态闭包的自有薄 sidecar。发行版预编译引擎 `.so`、`.dylib` 或 `.dll` 不进入正式发布包。
2. 每个 RID 独立构建，不跨操作系统复用二进制。允许 CI 缓存我们自己从锁定源码生成的 archive，但源码 manifest、编译器版本、参数、补丁和散列必须能复现它。
3. Linux archive 放入 linker group；静态 C++ runtime 使用 `libstdc++.a` 与 `libgcc_eh.a`。使用 `--exclude-libs,ALL` 隐藏 archive 符号，防止 HarfBuzz、FreeType 等符号被其他 native module 抢占。
4. 发布门禁同时检查最终可执行文件的 `DT_NEEDED`/平台等价物和发布目录中的 native sidecar。未在 allowlist 中出现的动态依赖令构建失败。
5. “静态优先”不等于完全静态 executable。系统 ABI、驱动 loader 和系统 Framework 保持动态链接；这是平台支持与安全更新边界。

## 每个平台的链接形态

| 平台 | libaria2/libmpv/QuickJS | 允许保留动态的边界 |
|---|---|---|
| Linux x64/ARM64 | PIC `.a` 合入 NativeAOT ELF | glibc、ELF loader；按功能显式 allowlist 的 Wayland/X11、EGL/OpenGL/Vulkan、ALSA/Pulse/PipeWire、VA-API 等系统/驱动 ABI |
| Windows x64/ARM64 | MSVC/clang-cl 静态 `.lib` 合入 `.exe`，C/C++ runtime 采用 `/MT` | Windows 系统 DLL，以及 DXGI/D3D11、Media Foundation、WASAPI 等系统组件 |
| macOS x64/ARM64 | universal 或分架构 `.a` 合入 Mach-O | AppKit、CoreFoundation、Metal、VideoToolbox、AudioToolbox 等 Apple Framework |
| Android x64/ARM64 | libmpv/FFmpeg/QuickJS 以 NDK PIC `.a` 合入 NativeAOT 应用 `.so`；aria2 及其依赖静态封装在仅导出 CanX C ABI 的 `libcanx_aria2.so` | Avalonia NuGet 的 `libSkiaSharp.so`、`libHarfBuzzSharp.so`；bionic、`libdl`、`liblog`、`libandroid`、EGL/GLES、Media NDK、OpenSL ES 等 NDK/系统 ABI |
| iOS ARM64/模拟器 | `.a` 合入应用 Mach-O | UIKit、Metal、VideoToolbox、AudioToolbox 等 Apple Framework；最终结果必须在 Xcode 主机验证 |

## Linux x64 已验证依赖闭包

CanX 使用以下已推送分支作为两项核心引擎的源码入口，并在本仓库锁文件中固定到不可变 commit：

- aria2：`junjiexing/aria2` 的 `canx/integration`（`0782d46235cf4fa0b9931fcebc2a5eb1ed56fe8c`，与 `native/sources-linux-x64.lock` 一致）；
- mpv：`junjiexing/mpv` 的 `canx/static-linking`（`0c9de6514f63788686750cb000fdd5a55af234d5`）。

- aria2：libaria2、C ABI shim、OpenSSL、zlib、SQLite、Expat、c-ares、libssh2，以及内置 wslay；保留 HTTP/FTP/SFTP、BitTorrent、Metalink、JSON/XML-RPC 和 WebSocket 能力。
- mpv：libmpv、FFmpeg、libass、libplacebo、HarfBuzz、FreeType、FriBidi、Fontconfig、Expat、libunibreak，以及静态 C++ runtime。
- Linux 通用 GPU 能力：oneVPL dispatcher、libva/libva-drm、libdrm、OpenCL ICD loader、glslang 与 SPIRV-Tools 静态链接；FFmpeg 同时编入 AMF、QSV/oneVPL、VAAPI、Vulkan Video、OpenCL 和 NVDEC/CUDA，libmpv 编入 VAAPI/DRM/DMABUF、Vulkan 和 CUDA interop。
- 插件：QuickJS 与 CanX C ABI shim。

上述 engine archive 已通过 NativeAOT 链接、实际运行和依赖审计。Linux x64 测试 ELF 对 libaria2/libmpv/FFmpeg/OpenSSL/oneVPL/libva/libdrm/libplacebo 等均无 `DT_NEEDED`；除 Linux 基础 ABI 外，只直接依赖 `libEGL.so.1` 与 `libvulkan.so.1`。Vulkan Loader 在 Linux 上不提供受支持的静态链接方案；GPU 厂商驱动、AMD AMF runtime、Intel oneVPL implementation、VAAPI driver 和 OpenCL ICD 也必须按目标机器动态发现，不能打进通用 App。

Linux x64 的最低运行基线是 Ubuntu 22.04 / glibc 2.35。正式产物使用 `native/linux-x64-ubuntu22.04.Dockerfile` 与 `scripts/run-ubuntu-22.04-linux-x64.sh` 构建，并由 `scripts/verify-linux-glibc-baseline.sh` 拒绝高于 GLIBC 2.35 的符号版本。构建时是否存在 Intel、AMD 或 NVIDIA GPU 不影响各后端是否编入；实际设备、驱动和 codec profile 在运行时探测，不可用时由 `hwdec=auto-safe` 回退。

## Avalonia 原生依赖

Avalonia 12.1.2 的 NuGet 依赖默认会输出 `libSkiaSharp.so`（SkiaSharp 3.119.4）和 `libHarfBuzzSharp.so`（HarfBuzzSharp 8.3.1.3），前者还动态依赖系统 `libfontconfig.so.1`。Linux x64 recipe 已改为从对应的 SkiaSharp tag 构建 `libSkiaSharp.a`、`libHarfBuzzSharp.a` 及 Skia 内置 codec archive，并为两个 P/Invoke 名启用 `DirectPInvoke`；发布后只删除明确列入 `CanXStaticNativeSidecars` 的 NuGet runtime asset。

SkiaSharp 与 mpv 都使用 FreeType、Expat 和 HarfBuzz。为了避免同名静态符号被不同版本实现抢占，最终 linker group 先使用 SkiaSharp 的 FreeType/Expat；Fontconfig 所缺少的 BDF 模块由 mpv 依赖闭包中的 FreeType 补齐；libass 使用与 HarfBuzzSharp 绑定匹配的 HarfBuzz 8.3.1 实现，不再同时链接 mpv 构建出的另一份 HarfBuzz。`--exclude-libs,ALL` 继续确保这些实现不会成为最终 ELF 的公共 ABI。

Linux x64 完整 Avalonia + libmpv 发布目录现已无第三方 `.so`。Android 的 Avalonia 渲染栈仍随包携带
`libSkiaSharp.so` 与 `libHarfBuzzSharp.so`；aria2 sidecar 只动态依赖 Android 系统 ABI，并通过隐藏
archive 符号避免静态 libc++/依赖与主 NativeAOT 模块发生符号抢占。该例外不改用系统或第三方预编译
aria2，完整引擎闭包仍由锁定源码生成。其他 RID 仍需逐平台构建和运行验证，不能由 Linux 结果推定。

## 工程入口

- `native/sources-linux-x64.lock` 固定源仓库、commit 与许可证；Skia 的 submodule 与 `DEPS` 再锁定其传递源码；
- `scripts/build-static-native-linux-x64.sh` 从源码构建 archive；
- `scripts/run-ubuntu-22.04-linux-x64.sh` 在固定 Ubuntu 22.04 容器内生成正式 Linux x64 产物；
- `Directory.Build.targets` 将 pipe 分隔的 archive 列表映射为 NativeAOT link items；
- `scripts/verify-static-native-aot.sh` 发布并运行 aria2、mpv、QuickJS 静态 smoke tests；
- `scripts/audit-native-dependencies-linux.sh` 执行动态依赖 allowlist 门禁。

## 后续平台顺序

1. Linux ARM64，验证同一 recipe 的交叉编译与真机运行。
2. Windows x64/ARM64，使用 vcpkg-compatible triplet 或仓库内 CMake toolchain，产物必须来自锁定源码。
3. macOS x64/ARM64，再组合 universal package（如需要）。
4. Android ARM64/x64，使用 NDK toolchain；libmpv 渲染后端走宿主提供的 EGL/GLES/Vulkan context。
5. iOS device/simulator，使用 Xcode toolchain 和系统 Framework，在法律门禁通过后决定分发。
