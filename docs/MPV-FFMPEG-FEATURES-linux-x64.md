# FFmpeg / libmpv 功能矩阵（Linux x64）

> 本文由 `scripts/generate-media-feature-report-linux-x64.sh` 从实际构建生成；不是按命令行参数推测。

- FFmpeg revision: `894da5ca7d742e4429ffb2af534fcda0103ef593`
- mpv revision: `0c9de6514f63788686750cb000fdd5a55af234d5`
- FFmpeg `CONFIG_*`: 2202 enabled / 422 disabled
- libmpv `HAVE_*`: 41 enabled / 92 disabled

## 平台基线与后端策略

- 发布目标是 Ubuntu 22.04 x64 或更新版本（glibc 2.35 基线）；发布产物必须通过 `scripts/verify-linux-glibc-baseline.sh`。
- 构建时固定编入各厂商后端，启动和打开媒体时由 libmpv/FFmpeg 按驱动能力探测；构建机使用哪家 GPU 不影响其他后端是否编译。
- 默认 `hwdec=auto-safe`：后端不可用时继续尝试其他安全后端，最后回退软件解码。

## 静态链接与运行时边界

| 项目 | 状态 | 说明 |
|---|---|---|
| FFmpeg / libmpv / libass / libplacebo | 静态 | 最终链接进 NativeAOT 主程序；开发 `libmpv.so` 也内含这些静态归档 |
| oneVPL dispatcher / libva / libva-drm / libdrm | 静态 | Intel QSV 调度与 Intel/AMD VAAPI/DMABUF 用户态入口 |
| OpenCL ICD loader | 静态 | 运行时再发现厂商 OpenCL ICD |
| glslang / SPIRV-Tools | 静态 | Vulkan shader 编译链；固定为 glslang `known_good.json` 对应版本 |
| dav1d / libvpx / Opus / Vorbis / OpenJPEG | 静态 | 外部软件解码器与编解码辅助库 |
| OpenSSL / zlib / zimg / LCMS2 / libarchive | 静态 | 网络、压缩、缩放、色彩管理与压缩包支持 |
| FreeType / HarfBuzz / FriBidi / Fontconfig / libunibreak | 静态 | 字幕排版和字体支持 |
| Lua 5.2 / MuJS / ALSA core | 静态 | mpv 脚本与 Linux 音频核心 |
| `libc.so.6`, `libm.so.6`, ELF loader | 动态 | Linux 基础 ABI |
| `libEGL.so.1`, `libvulkan.so.1` | 动态 | Linux 显示/驱动 ABI；Vulkan Loader 上游在 Linux 不提供受支持的静态构建 |
| `libcuda.so.1`, `libnvcuvid.so.1` | 运行时加载 | NVIDIA 驱动 ABI，不可随应用静态重分发 |
| AMD `libamfrt64.so.1` | 运行时加载 | 只有安装 AMD AMF Runtime 的机器才会启用 AMF |
| Intel oneVPL implementation | 运行时加载 | 静态 dispatcher 选择系统提供的 `libmfx-gen.so` 等实现 |
| VAAPI / Vulkan / OpenCL 厂商驱动 | 运行时加载 | 分别由 libva、Vulkan Loader、OpenCL ICD loader 按设备发现 |
| ALSA 的 PipeWire/PulseAudio 插件 | 运行时按系统配置加载 | 音频服务端 ABI；`libasound` 本体仍为静态 |
| OpenGL/EGL/窗口系统驱动 | 由 Avalonia/Skia 运行时加载 | 显示驱动 ABI，不属于随应用分发的第三方库 |

## 已验证的关键路径

| 能力 | 状态 | 证据 |
|---|---|---|
| x86 汇编优化 | 启用 | FFmpeg x86asm、libass ASM 均为 1 |
| HEVC Main 10 软件解码 | 启用 | FFmpeg 原生 HEVC decoder |
| NVIDIA HEVC Main 10 硬解 | 启用并实播通过 | `hwdec-current=nvdec`, `hwdec-interop=cuda` |
| 零 CPU 回读渲染路径 | 启用 | NVDEC CUDA surface 通过 CUDA/OpenGL interop 进入 libmpv OpenGL render API |
| AMD AMF | FFmpeg 已启用，待 AMD 设备验证 | AMF headers 编入；runtime 由 FFmpeg 按需加载 |
| Intel QSV / oneVPL | FFmpeg 已启用，待 Intel 设备验证 | oneVPL dispatcher 静态链接，implementation 运行时发现 |
| Intel/AMD VAAPI | FFmpeg 与 libmpv 已启用，待对应设备验证 | 静态 libva/libdrm；DRM/DMABUF 与 OpenGL interop 已编入 |
| Vulkan Video | FFmpeg 与 libmpv 已启用，待对应设备验证 | Vulkan 1.4 headers、libplacebo、glslang/SPIRV-Tools 已编入 |
| OpenCL filters | FFmpeg 已启用，待对应设备验证 | 静态 ICD loader；厂商 ICD 运行时发现 |
| AV1 / VP8 / VP9 / Opus / Vorbis / JPEG 2000 | 启用 | dav1d、libvpx、libopus、libvorbis、libopenjpeg 均静态链接 |
| HTTPS/TLS、HTTP、FTP、WebDAV-over-HTTP | 启用 | FFmpeg 网络协议 + 静态 OpenSSL；WebDAV 复用 HTTP(S) |
| SMB | 不在 FFmpeg 静态闭包 | Samba/libsmbclient 静态闭包过大且不稳定；由 CanX 媒体流适配层接入 libmpv stream callback |

## FFmpeg 分类汇总

| 分类 | 启用 | 禁用 | 总计 |
|---|---:|---:|---:|
| 位流过滤器 | 48 | 0 | 48 |
| 协议 | 41 | 14 | 55 |
| 复用器 | 183 | 1 | 184 |
| 库与构建能力 | 199 | 154 | 353 |
| 硬件加速器 | 29 | 46 | 75 |
| 编码器 | 205 | 65 | 270 |
| 解复用器 | 356 | 8 | 364 |
| 解析器 | 64 | 0 | 64 |
| 解码器 | 530 | 66 | 596 |
| 输入设备 | 6 | 14 | 20 |
| 输出设备 | 4 | 6 | 10 |
| 音视频过滤器 | 537 | 48 | 585 |

说明：所有 FFmpeg 原生软件解码器均已启用；禁用的 decoder 均为其他平台硬件包装器或未纳入静态闭包的外部替代实现。

## libmpv 全部 feature

| Feature | 状态 | 未启用原因 |
|---|---|---|
| `HAVE_AAUDIO` | 禁用 | 仅 Android 适用，当前目标是 Linux x64 |
| `HAVE_ALSA` | 启用 | — |
| `HAVE_AMF` | 禁用 | mpv 的 AMF feature 是 Windows/D3D11 FRC；Linux AMF 由已启用的 FFmpeg 后端提供 |
| `HAVE_ANDROID_MEDIA_NDK` | 禁用 | 仅 Android 适用，当前目标是 Linux x64 |
| `HAVE_ANDROID` | 禁用 | 仅 Android 适用，当前目标是 Linux x64 |
| `HAVE_AUDIOTRACK` | 禁用 | 仅 Android 适用，当前目标是 Linux x64 |
| `HAVE_AUDIOUNIT` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_AVFOUNDATION` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_BSD_FSTATFS` | 禁用 | BSD/其他 Unix 平台能力，Linux 不适用 |
| `HAVE_BSD_THREAD_NAME` | 禁用 | BSD/其他 Unix 平台能力，Linux 不适用 |
| `HAVE_BUILD_DATE` | 禁用 | 发布构建关闭调试、时间戳或开发诊断能力 |
| `HAVE_CACA` | 禁用 | 终端图形输出不属于 GUI 应用运行路径 |
| `HAVE_CDDA` | 禁用 | 未引入光盘介质的额外静态依赖 |
| `HAVE_CLONE` | 启用 | — |
| `HAVE_COCOA` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_CONSIO_H` | 禁用 | BSD/其他 Unix 平台能力，Linux 不适用 |
| `HAVE_COREAUDIO` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_CPLUGINS` | 禁用 | NativeAOT/静态发布不加载不受控原生插件；应用插件使用 QuickJS |
| `HAVE_CUDA_HWACCEL` | 启用 | — |
| `HAVE_CUDA_INTEROP` | 启用 | — |
| `HAVE_D3D11` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_D3D9_HWACCEL` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_D3D_HWACCEL` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_DARWIN` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_DEBUG` | 禁用 | 发布构建关闭调试、时间戳或开发诊断能力 |
| `HAVE_DIRECT3D` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_DISABLE_PACKET_POOL` | 禁用 | 发布构建关闭调试、时间戳或开发诊断能力 |
| `HAVE_DMABUF_INTEROP_GL` | 启用 | — |
| `HAVE_DMABUF_WAYLAND` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_DOS_PATHS` | 禁用 | 当前 Linux x64 静态播放配置不适用或其可选依赖未纳入固定源码闭包 |
| `HAVE_DRM` | 启用 | — |
| `HAVE_DVBIN` | 启用 | — |
| `HAVE_DVDA` | 禁用 | 未引入光盘介质的额外静态依赖 |
| `HAVE_DVDNAV` | 禁用 | 未引入光盘介质的额外静态依赖 |
| `HAVE_EGL_ANDROID` | 禁用 | 仅 Android 适用，当前目标是 Linux x64 |
| `HAVE_EGL_ANGLE_LIB` | 禁用 | 当前 Linux x64 静态播放配置不适用或其可选依赖未纳入固定源码闭包 |
| `HAVE_EGL_ANGLE_WIN32` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_EGL_ANGLE` | 禁用 | 当前 Linux x64 静态播放配置不适用或其可选依赖未纳入固定源码闭包 |
| `HAVE_EGL_DRM` | 禁用 | 当前 Linux x64 静态播放配置不适用或其可选依赖未纳入固定源码闭包 |
| `HAVE_EGL_WAYLAND` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_EGL_X11` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_EGL` | 启用 | — |
| `HAVE_FFMPEG` | 启用 | — |
| `HAVE_FFNVCODEC` | 启用 | — |
| `HAVE_GBM` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_GLIBC_THREAD_NAME` | 启用 | — |
| `HAVE_GLOB_POSIX` | 启用 | — |
| `HAVE_GLOB_WIN32` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_GLOB` | 启用 | — |
| `HAVE_GL_COCOA` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_GL_DXINTEROP_D3D9` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_GL_DXINTEROP` | 禁用 | 当前 Linux x64 静态播放配置不适用或其可选依赖未纳入固定源码闭包 |
| `HAVE_GL_WIN32` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_GL_X11` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_GL` | 启用 | — |
| `HAVE_GPL` | 启用 | — |
| `HAVE_ICONV` | 启用 | — |
| `HAVE_IOS_GL` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_JACK` | 禁用 | 当前使用静态 ALSA；桌面音频服务通过系统 ALSA 插件桥接 |
| `HAVE_JAVASCRIPT` | 启用 | — |
| `HAVE_JPEG` | 启用 | — |
| `HAVE_LCMS2` | 启用 | — |
| `HAVE_LIBARCHIVE` | 启用 | — |
| `HAVE_LIBASS` | 启用 | — |
| `HAVE_LIBAVDEVICE` | 启用 | — |
| `HAVE_LIBBLURAY` | 禁用 | 未引入光盘介质的额外静态依赖 |
| `HAVE_LIBCURL` | 禁用 | FFmpeg 已提供 HTTP(S)/FTP；避免重复引入第二套网络与 TLS 闭包 |
| `HAVE_LIBDL` | 启用 | — |
| `HAVE_LIBPLACEBO` | 启用 | — |
| `HAVE_LINUX_FSTATFS` | 启用 | — |
| `HAVE_LUA` | 启用 | — |
| `HAVE_MACOS_10_15_4_FEATURES` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_MACOS_11_3_FEATURES` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_MACOS_11_FEATURES` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_MACOS_12_FEATURES` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_MACOS_COCOA_CB` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_MACOS_MEDIA_PLAYER` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_MACOS_TOUCHBAR` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_MAC_THREAD_NAME` | 禁用 | 当前 Linux x64 静态播放配置不适用或其可选依赖未纳入固定源码闭包 |
| `HAVE_MEMFD_CREATE` | 禁用 | 当前工具链/LibC 能力探测未满足，且有已启用的 Linux/POSIX 替代路径 |
| `HAVE_MEMRCHR` | 启用 | — |
| `HAVE_OPENAL` | 禁用 | 当前使用静态 ALSA；桌面音频服务通过系统 ALSA 插件桥接 |
| `HAVE_OPENSLES` | 禁用 | 仅 Android 适用，当前目标是 Linux x64 |
| `HAVE_OSS_AUDIO` | 禁用 | 当前使用静态 ALSA；桌面音频服务通过系统 ALSA 插件桥接 |
| `HAVE_PIPEWIRE` | 禁用 | 当前使用静态 ALSA；桌面音频服务通过系统 ALSA 插件桥接 |
| `HAVE_POSIX_SHM` | 启用 | — |
| `HAVE_POSIX` | 启用 | — |
| `HAVE_PPOLL` | 启用 | — |
| `HAVE_PTHREAD_CONDATTR_SETCLOCK` | 启用 | — |
| `HAVE_PTHREAD_DEBUG` | 禁用 | 发布构建关闭调试、时间戳或开发诊断能力 |
| `HAVE_PULSE` | 禁用 | 当前使用静态 ALSA；桌面音频服务通过系统 ALSA 插件桥接 |
| `HAVE_RFORK` | 禁用 | BSD/其他 Unix 平台能力，Linux 不适用 |
| `HAVE_RUBBERBAND` | 禁用 | 缺少 Rubber Band 及其 FFT/采样率依赖的固定静态闭包 |
| `HAVE_SDL2_AUDIO` | 禁用 | 当前使用静态 ALSA；桌面音频服务通过系统 ALSA 插件桥接 |
| `HAVE_SDL2_GAMEPAD` | 禁用 | 当前 Linux x64 静态播放配置不适用或其可选依赖未纳入固定源码闭包 |
| `HAVE_SDL2_VIDEO` | 禁用 | 当前 Linux x64 静态播放配置不适用或其可选依赖未纳入固定源码闭包 |
| `HAVE_SHADERC` | 禁用 | 已选择静态 glslang/SPIRV-Tools 作为 Vulkan shader 编译器 |
| `HAVE_SIXEL` | 禁用 | 终端图形输出不属于 GUI 应用运行路径 |
| `HAVE_SNDIO_1_9` | 禁用 | BSD/其他 Unix 平台能力，Linux 不适用 |
| `HAVE_SNDIO` | 禁用 | BSD/其他 Unix 平台能力，Linux 不适用 |
| `HAVE_SPIRV_CROSS` | 禁用 | 仅 mpv Windows D3D11 路径需要，Linux Vulkan 路径不依赖它 |
| `HAVE_SUBRANDR` | 禁用 | 缺少已锁定、可复现的 Rust 静态依赖闭包 |
| `HAVE_SWIFT` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_UCHARDET` | 启用 | — |
| `HAVE_UWP` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_VAAPI_DRM` | 启用 | — |
| `HAVE_VAAPI_WAYLAND` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_VAAPI_WIN32` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_VAAPI_X11` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_VAAPI` | 启用 | — |
| `HAVE_VAPOURSYNTH` | 禁用 | 依赖外部 VapourSynth/Python 运行时，无法成为自包含静态闭包 |
| `HAVE_VDPAU_GL_X11` | 禁用 | VDPAU 依赖 mpv 的 X11 输出路径；CanX 使用更现代且可用于 Wayland 的 NVDEC/CUDA |
| `HAVE_VDPAU` | 禁用 | VDPAU 依赖 mpv 的 X11 输出路径；CanX 使用更现代且可用于 Wayland 的 NVDEC/CUDA |
| `HAVE_VECTOR` | 启用 | — |
| `HAVE_VIDEOTOOLBOX_GL` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_VIDEOTOOLBOX_PL` | 禁用 | 仅 Apple 平台适用 |
| `HAVE_VK_KHR_DISPLAY` | 启用 | — |
| `HAVE_VT_H` | 启用 | — |
| `HAVE_VULKAN` | 启用 | — |
| `HAVE_WASAPI` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_WAYLAND` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_WIN32_DESKTOP` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_WIN32_EXECUTABLE` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_WIN32_SMTC` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_WIN32_THREADS` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_WIN32` | 禁用 | 仅 Windows 平台适用 |
| `HAVE_WSDISPLAY_USL_IO_H` | 禁用 | 当前 Linux x64 静态播放配置不适用或其可选依赖未纳入固定源码闭包 |
| `HAVE_X11_CLIPBOARD` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_X11` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_XV` | 禁用 | CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端 |
| `HAVE_ZIMG_ST428` | 启用 | — |
| `HAVE_ZIMG` | 启用 | — |
| `HAVE_ZLIB` | 启用 | — |

## FFmpeg 全部 feature

| Feature | 分类 | 状态 | 未启用原因 |
|---|---|---|---|
| `CONFIG_A3DSCOPE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_A64MULTI5_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_A64MULTI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_A64_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AAC_ADTSTOASC_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_AAC_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_AAC_AT_ENCODER` | 编码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_AAC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AAC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AAC_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AAC_FIXED_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AAC_LATM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AAC_LATM_PARSER` | 解析器 | 启用 | — |
| `CONFIG_AAC_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_AAC_MF_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_AAC_PARSER` | 解析器 | 启用 | — |
| `CONFIG_AANDCTTABLES` | 库与构建能力 | 启用 | — |
| `CONFIG_AAP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AASC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AAX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ABENCH_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ABITSCOPE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AC3DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_AC3_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_AC3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AC3_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AC3_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AC3_FIXED_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AC3_FIXED_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AC3_MF_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_AC3_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AC3_PARSER` | 解析器 | 启用 | — |
| `CONFIG_AC4_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AC4_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ACELP_KELVIN_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ACE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ACM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ACOMPRESSOR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ACONTRAST_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ACOPY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ACROSSFADE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ACROSSOVER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ACRUSHER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ACT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ACUE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADDROI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADECLICK_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADECLIP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADECORRELATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADELAY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADENORM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADERIVATIVE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ADPCM_4XM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_ADX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_ADX_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_AFC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_AGM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_AICA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_ARGO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_ARGO_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_CT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_DTK_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_EA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_EA_MAXIS_XA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_EA_R1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_EA_R2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_EA_R3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_EA_XAS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_G722_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_G722_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_G726LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_G726LE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_G726_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_G726_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_ACORN_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_ALP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_ALP_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_AMV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_AMV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_APC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_APM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_APM_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_CUNNING_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_DAT4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_DK3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_DK4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_EA_EACS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_EA_SEAD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_ISS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_MOFLEX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_MTF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_OKI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_QT_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_ADPCM_IMA_QT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_QT_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_RAD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_SMJPEG_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_SSI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_SSI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_WAV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_WAV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_WS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_WS_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_IMA_XBOX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_MS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_MS_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_MTAF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_PSX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_SANYO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_SBPRO_2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_SBPRO_3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_SBPRO_4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_SWF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_SWF_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_THP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_THP_LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_VIMA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_XA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_XMD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_YAMAHA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADPCM_YAMAHA_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ADPCM_ZORK_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ADP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ADRAWGRAPH_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ADTS_HEADER` | 库与构建能力 | 启用 | — |
| `CONFIG_ADTS_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ADX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ADX_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ADX_PARSER` | 解析器 | 启用 | — |
| `CONFIG_ADYNAMICEQUALIZER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ADYNAMICSMOOTH_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AEA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AEA_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AECHO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AEMPHASIS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AEVALSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AEVAL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AEXCITER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AFADE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AFC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AFDELAYSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AFFTDN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AFFTFILT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AFIREQSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AFIRSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AFIR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AFORMAT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AFREQSHIFT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AFWTDN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AGATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AGM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AGRAPHMONITOR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AHISTOGRAM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AIC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AIFF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AIFF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AIIR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AINTEGRAL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AINTERLEAVE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AIX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ALAC_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_ALAC_AT_ENCODER` | 编码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_ALAC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ALAC_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ALATENCY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ALIAS_PIX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ALIAS_PIX_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ALIMITER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ALLPASS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ALLRGB_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ALLYUV_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ALOOP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ALPHAEXTRACT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ALPHAMERGE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ALP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ALP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ALSA_INDEV` | 输入设备 | 启用 | — |
| `CONFIG_ALSA_OUTDEV` | 输出设备 | 启用 | — |
| `CONFIG_ALSA` | 库与构建能力 | 启用 | — |
| `CONFIG_ALS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AMERGE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AMETADATA_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AMF` | 库与构建能力 | 启用 | — |
| `CONFIG_AMIX_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AMOVIE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AMPLIFY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AMRNB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AMRNB_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AMRNB_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_AMRWB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AMRWB_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AMRWB_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_AMR_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AMR_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AMR_NB_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_AMR_PARSER` | 解析器 | 启用 | — |
| `CONFIG_AMULTIPLY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AMV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AMV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AMV_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ANDROID_CAMERA_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_ANDROID_CONTENT_PROTOCOL` | 协议 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_ANEQUALIZER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ANLMDN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ANLMF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ANLMS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ANM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ANM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ANOISESRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ANSI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ANULLSINK_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ANULLSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ANULL_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ANULL_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ANULL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_APAC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_APAC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_APAD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_APC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_APERMS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_APE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_APE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_APHASEMETER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_APHASER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_APHASESHIFT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_APM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_APM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_APNG_DECODER` | 解码器 | 启用 | — |
| `CONFIG_APNG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_APNG_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_APNG_MUXER` | 复用器 | 启用 | — |
| `CONFIG_APPKIT` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_APSNR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_APSYCLIP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_APTX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_APTX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_APTX_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_APTX_HD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_APTX_HD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_APTX_HD_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_APTX_HD_MUXER` | 复用器 | 启用 | — |
| `CONFIG_APTX_MUXER` | 复用器 | 启用 | — |
| `CONFIG_APULSATOR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_APV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_APV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_APV_METADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_APV_MUXER` | 复用器 | 启用 | — |
| `CONFIG_APV_PARSER` | 解析器 | 启用 | — |
| `CONFIG_AQTITLE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ARBC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AREALTIME_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ARESAMPLE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AREVERSE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ARGO_ASF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ARGO_ASF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ARGO_BRP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ARGO_CVG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ARGO_CVG_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ARGO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ARLS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ARNNDN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASDR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASEGMENT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASELECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASENDCMD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASETNSAMPLES_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASETPTS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASETRATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASETTB_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ASF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ASF_O_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ASF_STREAM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ASHOWINFO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASIDEDATA_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASISDR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASOFTCLIP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASPECTRALSTATS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASPLIT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASR_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_ASS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ASS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ASS_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ASS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASS_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ASTATS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASTREAMSELECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AST_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AST_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ASUBBOOST_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASUBCUT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASUPERCUT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASUPERPASS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASUPERSTOP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ASV1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ASV1_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ASV2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ASV2_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ASYNC_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_ATADENOISE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ATEMPO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ATILT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ATRAC1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ATRAC3AL_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ATRAC3PAL_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ATRAC3P_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ATRAC3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ATRAC9_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ATRIM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ATSC_A53` | 库与构建能力 | 启用 | — |
| `CONFIG_AUDIODSP` | 库与构建能力 | 启用 | — |
| `CONFIG_AUDIOTOOLBOX_OUTDEV` | 输出设备 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_AUDIOTOOLBOX` | 库与构建能力 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_AUDIO_FRAME_QUEUE` | 库与构建能力 | 启用 | — |
| `CONFIG_AURA2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AURA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AUTODETECT` | 库与构建能力 | 禁用 | 为保证可复现性关闭宿主自动探测，依赖均显式启用 |
| `CONFIG_AU_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AU_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AV1_AMF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AV1_AMF_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AV1_CUVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AV1_D3D11VA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_AV1_D3D11VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_AV1_D3D12VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_AV1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AV1_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AV1_DXVA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_AV1_FRAME_MERGE_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_AV1_FRAME_SPLIT_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_AV1_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_AV1_MEDIACODEC_ENCODER` | 编码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_AV1_METADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_AV1_MF_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_AV1_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_AV1_NVENC_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AV1_PARSER` | 解析器 | 启用 | — |
| `CONFIG_AV1_QSV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AV1_QSV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AV1_VAAPI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AV1_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_AV1_VDPAU_HWACCEL` | 硬件加速器 | 禁用 | NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用 |
| `CONFIG_AV1_VIDEOTOOLBOX_HWACCEL` | 硬件加速器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_AV1_VULKAN_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AV1_VULKAN_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_AVCODEC` | 库与构建能力 | 启用 | — |
| `CONFIG_AVDEVICE` | 库与构建能力 | 启用 | — |
| `CONFIG_AVECTORSCOPE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AVFILTER` | 库与构建能力 | 启用 | — |
| `CONFIG_AVFORMAT` | 库与构建能力 | 启用 | — |
| `CONFIG_AVFOUNDATION_INDEV` | 输入设备 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_AVFOUNDATION` | 库与构建能力 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_AVGBLUR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AVGBLUR_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AVGBLUR_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AVIF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AVIO_HTTP_SERVE_FILES_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_AVIO_LIST_DIR_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_AVIO_READ_CALLBACK_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_AVISYNTH_DEMUXER` | 解复用器 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_AVISYNTH` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_AVI_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AVI_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AVM2_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AVRN_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AVRP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AVRP_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AVR_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AVS2_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AVS2_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AVS2_PARSER` | 解析器 | 启用 | — |
| `CONFIG_AVS3_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AVS3_MUXER` | 复用器 | 启用 | — |
| `CONFIG_AVS3_PARSER` | 解析器 | 启用 | — |
| `CONFIG_AVSYNCTEST_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AVS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AVS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_AVUI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_AVUI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_AVUTIL` | 库与构建能力 | 启用 | — |
| `CONFIG_AXCORRELATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_AZMQ_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_BACKGROUNDKEY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BANDPASS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BANDREJECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BASS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BBOX_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BENCH_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BETHSOFTVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BETHSOFTVID_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BFI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BFI_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BFSTM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BILATERAL_CUDA_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_BILATERAL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BINKAUDIO_DCT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BINKAUDIO_RDFT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BINKA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BINK_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BINK_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BINTEXT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BINTEXT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BIQUAD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BITPACKED_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BITPACKED_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BITPACKED_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_BITPLANENOISE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BIT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BIT_MUXER` | 复用器 | 启用 | — |
| `CONFIG_BLACKDETECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BLACKDETECT_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BLACKFRAME_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BLEND_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BLEND_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BLOCKDETECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BLOCKDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_BLURAY_PROTOCOL` | 协议 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_BLURDETECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BM3D_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BMP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BMP_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_BMP_PARSER` | 解析器 | 启用 | — |
| `CONFIG_BMV_AUDIO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BMV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BMV_VIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BOA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BONK_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BONK_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BOXBLUR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BOXBLUR_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BRENDER_PIX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_BRSTM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_BS2B_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_BSFS` | 库与构建能力 | 启用 | — |
| `CONFIG_BSWAPDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_BWDIF_CUDA_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_BWDIF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BWDIF_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_BZLIB` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_C93_DECODER` | 解码器 | 启用 | — |
| `CONFIG_C93_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_CABAC` | 库与构建能力 | 启用 | — |
| `CONFIG_CACA_OUTDEV` | 输出设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_CACHE_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_CAF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_CAF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_CAS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CAVSVIDEO_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_CAVSVIDEO_MUXER` | 复用器 | 启用 | — |
| `CONFIG_CAVSVIDEO_PARSER` | 解析器 | 启用 | — |
| `CONFIG_CAVS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CBD2_DPCM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CBS_APV` | 库与构建能力 | 启用 | — |
| `CONFIG_CBS_AV1` | 库与构建能力 | 启用 | — |
| `CONFIG_CBS_H264` | 库与构建能力 | 启用 | — |
| `CONFIG_CBS_H265` | 库与构建能力 | 启用 | — |
| `CONFIG_CBS_H266` | 库与构建能力 | 启用 | — |
| `CONFIG_CBS_JPEG` | 库与构建能力 | 启用 | — |
| `CONFIG_CBS_MPEG2` | 库与构建能力 | 启用 | — |
| `CONFIG_CBS_VP8` | 库与构建能力 | 启用 | — |
| `CONFIG_CBS_VP9` | 库与构建能力 | 启用 | — |
| `CONFIG_CBS` | 库与构建能力 | 启用 | — |
| `CONFIG_CCAPTION_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CCREPACK_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CDGRAPHICS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CDG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_CDTOONS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CDXL_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CDXL_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_CELLAUTO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CELP_MATH` | 库与构建能力 | 启用 | — |
| `CONFIG_CFHD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CFHD_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_CHANNELMAP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CHANNELSPLIT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CHOMP_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_CHORUS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CHROMABER_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CHROMAHOLD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CHROMAKEY_CUDA_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_CHROMAKEY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CHROMANR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CHROMAPRINT_MUXER` | 复用器 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_CHROMAPRINT` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_CHROMASHIFT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CIESCOPE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CINEPAK_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CINEPAK_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_CINE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_CLEARVIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CLJR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CLJR_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_CLLC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CODEC2RAW_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_CODEC2RAW_MUXER` | 复用器 | 启用 | — |
| `CONFIG_CODEC2_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_CODEC2_MUXER` | 复用器 | 启用 | — |
| `CONFIG_CODECVIEW_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORBALANCE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORCHANNELMIXER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORCHART_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORCONTRAST_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORCORRECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORDETECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORHOLD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORIZE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORKEY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORKEY_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORLEVELS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORMAP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORMATRIX_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORSPACE_CUDA_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_COLORSPACE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORSPECTRUM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLORTEMPERATURE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLOR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COLOR_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COMFORTNOISE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_COMFORTNOISE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_COMPAND_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COMPENSATIONDELAY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CONCATF_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_CONCAT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_CONCAT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CONCAT_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_CONVOLUTION_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CONVOLUTION_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CONVOLVE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COOK_DECODER` | 解码器 | 启用 | — |
| `CONFIG_COOK_PARSER` | 解析器 | 启用 | — |
| `CONFIG_COPY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COREIMAGESRC_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_COREIMAGE_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_COREIMAGE` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_CORR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_COVER_RECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CPIA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CRC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_CRI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CRI_PARSER` | 解析器 | 启用 | — |
| `CONFIG_CROPDETECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CROP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CROSSFEED_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CRYPTO_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_CRYSTALIZER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CSCD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_CUDA_LLVM` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_CUDA_NVCC` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_CUDA_SDK` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_CUDA` | 库与构建能力 | 启用 | — |
| `CONFIG_CUE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CURVES_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_CUVID` | 库与构建能力 | 启用 | — |
| `CONFIG_CYUV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_D3D11VA` | 库与构建能力 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_D3D12VA_ENCODE` | 库与构建能力 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_D3D12VA` | 库与构建能力 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_DASH_DEMUXER` | 解复用器 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_DASH_MUXER` | 复用器 | 启用 | — |
| `CONFIG_DATASCOPE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DATA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DATA_MUXER` | 复用器 | 启用 | — |
| `CONFIG_DATA_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_DAUD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DAUD_MUXER` | 复用器 | 启用 | — |
| `CONFIG_DBLUR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DCA_CORE_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_DCA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DCA_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_DCA_PARSER` | 解析器 | 启用 | — |
| `CONFIG_DCSHIFT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DCSTR_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DCTDNOIZ_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DDAGRAB_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_DDS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DEBAND_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DEBLOCK_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DECIMATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DECKLINK_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_DECKLINK_OUTDEV` | 输出设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_DECKLINK` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_DECODERS` | 库与构建能力 | 启用 | — |
| `CONFIG_DECODE_AUDIO_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_DECODE_FILTER_AUDIO_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_DECODE_FILTER_VIDEO_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_DECODE_VIDEO_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_DECONVOLVE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DEDOT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DEESSER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DEFLATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DEFLATE_WRAPPER` | 库与构建能力 | 启用 | — |
| `CONFIG_DEFLICKER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DEINTERLACE_QSV_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DEINTERLACE_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DEJUDDER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DELOGO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DEMUXERS` | 库与构建能力 | 启用 | — |
| `CONFIG_DEMUX_DECODE_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_DENOISE_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DERAIN_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_DERF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DERF_DPCM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DESHAKE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DESHAKE_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DESPILL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DETELECINE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DFA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DFA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DFPWM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DFPWM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DFPWM_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_DFPWM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_DHAV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DIALOGUENHANCE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DILATION_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DILATION_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DIRAC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DIRAC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DIRAC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_DIRAC_PARSER` | 解析器 | 启用 | — |
| `CONFIG_DIRAC_PARSE` | 库与构建能力 | 启用 | — |
| `CONFIG_DISPLACE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DNN_CLASSIFY_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_DNN_DETECT_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_DNN_PROCESSING_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_DNN` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_DNXHD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DNXHD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DNXHD_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_DNXHD_MUXER` | 复用器 | 启用 | — |
| `CONFIG_DNXHD_PARSER` | 解析器 | 启用 | — |
| `CONFIG_DNXUC_PARSER` | 解析器 | 启用 | — |
| `CONFIG_DOC` | 库与构建能力 | 禁用 | 嵌入式 libav* 发布不构建 CLI、文档或调试产物 |
| `CONFIG_DOLBY_E_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DOLBY_E_PARSER` | 解析器 | 启用 | — |
| `CONFIG_DOUBLEWEAVE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DOVI_RPUDEC` | 库与构建能力 | 启用 | — |
| `CONFIG_DOVI_RPUENC` | 库与构建能力 | 启用 | — |
| `CONFIG_DOVI_RPU_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_DPX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DPX_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_DPX_PARSER` | 解析器 | 启用 | — |
| `CONFIG_DRAWBOX_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DRAWBOX_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DRAWGRAPH_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DRAWGRID_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DRAWTEXT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DRMETER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_DSD_LSBF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DSD_LSBF_PLANAR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DSD_MSBF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DSD_MSBF_PLANAR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DSF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DSHOW_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_DSICINAUDIO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DSICINVIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DSICIN_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DSS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DSS_SP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DST_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DTLS_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_DTS2PTS_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_DTSHD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DTS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DTS_MUXER` | 复用器 | 启用 | — |
| `CONFIG_DUMP_EXTRADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_DVAUDIO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DVAUDIO_PARSER` | 解析器 | 启用 | — |
| `CONFIG_DVBSUB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DVBSUB_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DVBSUB_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_DVBSUB_PARSER` | 解析器 | 启用 | — |
| `CONFIG_DVBTXT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DVDSUB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DVDSUB_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_DVDSUB_PARSER` | 解析器 | 启用 | — |
| `CONFIG_DVDVIDEO_DEMUXER` | 解复用器 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_DVD_NAV_PARSER` | 解析器 | 启用 | — |
| `CONFIG_DVPROFILE` | 库与构建能力 | 启用 | — |
| `CONFIG_DVVIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DVVIDEO_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_DV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DV_ERROR_MARKER_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_DV_MUXER` | 复用器 | 启用 | — |
| `CONFIG_DWT` | 库与构建能力 | 启用 | — |
| `CONFIG_DXA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DXA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_DXTORY_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DXVA2` | 库与构建能力 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_DXV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_DXV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_DYNAUDNORM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EAC3_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_EAC3_CORE_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_EAC3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_EAC3_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_EAC3_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_EAC3_MUXER` | 复用器 | 启用 | — |
| `CONFIG_EACMV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_EAMAD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_EARWAX_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EATGQ_DECODER` | 解码器 | 启用 | — |
| `CONFIG_EATGV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_EATQI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_EA_CDATA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_EA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_EBUR128_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EDGEDETECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EIA608_TO_SMPTE436M_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_EIGHTBPS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_EIGHTSVX_EXP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_EIGHTSVX_FIB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ELBG_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ENCODERS` | 库与构建能力 | 启用 | — |
| `CONFIG_ENCODE_AUDIO_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_ENCODE_VIDEO_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_ENTROPY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EPAF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_EPX_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EQUALIZER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EQ_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EROSION_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EROSION_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ERROR_RESILIENCE` | 库与构建能力 | 启用 | — |
| `CONFIG_ESCAPE124_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ESCAPE130_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ESTDIF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EVCPARSE` | 库与构建能力 | 启用 | — |
| `CONFIG_EVC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_EVC_FRAME_MERGE_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_EVC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_EVC_PARSER` | 解析器 | 启用 | — |
| `CONFIG_EVRC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_EXIF` | 库与构建能力 | 启用 | — |
| `CONFIG_EXPOSURE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EXR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_EXR_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_EXTRACTPLANES_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_EXTRACT_EXTRADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_EXTRACT_MVS_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_EXTRASTEREO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_F4V_MUXER` | 复用器 | 启用 | — |
| `CONFIG_FAANDCT` | 库与构建能力 | 启用 | — |
| `CONFIG_FAANIDCT` | 库与构建能力 | 启用 | — |
| `CONFIG_FAAN` | 库与构建能力 | 启用 | — |
| `CONFIG_FADE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FASTAUDIO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FAST_UNALIGNED` | 库与构建能力 | 启用 | — |
| `CONFIG_FBDEV_INDEV` | 输入设备 | 启用 | — |
| `CONFIG_FBDEV_OUTDEV` | 输出设备 | 启用 | — |
| `CONFIG_FDCTDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_FD_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_FEEDBACK_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FFMETADATA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_FFMETADATA_MUXER` | 复用器 | 启用 | — |
| `CONFIG_FFMPEG` | 库与构建能力 | 禁用 | 嵌入式 libav* 发布不构建 CLI、文档或调试产物 |
| `CONFIG_FFNVCODEC` | 库与构建能力 | 启用 | — |
| `CONFIG_FFPLAY` | 库与构建能力 | 禁用 | 嵌入式 libav* 发布不构建 CLI、文档或调试产物 |
| `CONFIG_FFPROBE` | 库与构建能力 | 禁用 | 嵌入式 libav* 发布不构建 CLI、文档或调试产物 |
| `CONFIG_FFRTMPCRYPT_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_FFRTMPHTTP_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_FFTDNOIZ_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FFTFILT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FFV1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FFV1_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_FFV1_PARSER` | 解析器 | 启用 | — |
| `CONFIG_FFV1_VULKAN_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_FFV1_VULKAN_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_FFVHUFF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FFVHUFF_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_FFWAVESYNTH_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FIC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FIELDHINT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FIELDMATCH_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FIELDORDER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FIELD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FIFO_MUXER` | 复用器 | 启用 | — |
| `CONFIG_FILE_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_FILLBORDERS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FILMSTRIP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_FILMSTRIP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_FILTERS` | 库与构建能力 | 启用 | — |
| `CONFIG_FILTER_AUDIO_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_FILTER_UNITS_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_FIND_RECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FIREQUALIZER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FITS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FITS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_FITS_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_FITS_MUXER` | 复用器 | 启用 | — |
| `CONFIG_FLAC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FLAC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_FLAC_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_FLAC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_FLAC_PARSER` | 解析器 | 启用 | — |
| `CONFIG_FLANGER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FLASHSV2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FLASHSV2_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_FLASHSV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FLASHSV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_FLIC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FLIC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_FLIP_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FLITE_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_FLOODFILL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FLV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FLV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_FLV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_FLV_MUXER` | 复用器 | 启用 | — |
| `CONFIG_FMTCONVERT` | 库与构建能力 | 启用 | — |
| `CONFIG_FMVC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FONTCONFIG` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_FORMAT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FOURXM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FOURXM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_FPS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FRAMECRC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_FRAMEHASH_MUXER` | 复用器 | 启用 | — |
| `CONFIG_FRAMEMD5_MUXER` | 复用器 | 启用 | — |
| `CONFIG_FRAMEPACK_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FRAMERATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FRAMESTEP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FRAME_THREAD_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_FRAPS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FREEZEDETECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FREEZEFRAMES_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FREI0R_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_FREI0R_SRC_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_FREI0R` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_FRM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_FRWU_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FSB_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_FSPP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FSYNC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_FTP_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_FTRAPV` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_FTR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_FTR_PARSER` | 解析器 | 启用 | — |
| `CONFIG_FWSE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_G2M_DECODER` | 解码器 | 启用 | — |
| `CONFIG_G722DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_G722_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_G722_MUXER` | 复用器 | 启用 | — |
| `CONFIG_G723_1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_G723_1_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_G723_1_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_G723_1_MUXER` | 复用器 | 启用 | — |
| `CONFIG_G723_1_PARSER` | 解析器 | 启用 | — |
| `CONFIG_G726LE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_G726LE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_G726_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_G726_MUXER` | 复用器 | 启用 | — |
| `CONFIG_G728_DECODER` | 解码器 | 启用 | — |
| `CONFIG_G728_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_G729_DECODER` | 解码器 | 启用 | — |
| `CONFIG_G729_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_G729_PARSER` | 解析器 | 启用 | — |
| `CONFIG_GBLUR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_GBLUR_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_GCRYPT` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_GDIGRAB_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_GDV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_GDV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_GEM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_GENH_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_GEQ_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_GIF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_GIF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_GIF_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_GIF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_GIF_PARSER` | 解析器 | 启用 | — |
| `CONFIG_GMP` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_GNUTLS` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_GOLOMB` | 库与构建能力 | 启用 | — |
| `CONFIG_GOPHERS_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_GOPHER_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_GPLV3` | 库与构建能力 | 启用 | — |
| `CONFIG_GPL` | 库与构建能力 | 启用 | — |
| `CONFIG_GRADFUN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_GRADIENTS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_GRAPHMONITOR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_GRAYWORLD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_GRAY` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_GREMLIN_DPCM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_GREYEDGE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_GSM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_GSM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_GSM_MS_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_GSM_MS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_GSM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_GSM_PARSER` | 解析器 | 启用 | — |
| `CONFIG_GUIDED_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_GXF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_GXF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_H261_DECODER` | 解码器 | 启用 | — |
| `CONFIG_H261_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_H261_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_H261_MUXER` | 复用器 | 启用 | — |
| `CONFIG_H261_PARSER` | 解析器 | 启用 | — |
| `CONFIG_H263DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_H263I_DECODER` | 解码器 | 启用 | — |
| `CONFIG_H263P_DECODER` | 解码器 | 启用 | — |
| `CONFIG_H263P_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_H263_DECODER` | 解码器 | 启用 | — |
| `CONFIG_H263_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_H263_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_H263_MUXER` | 复用器 | 启用 | — |
| `CONFIG_H263_PARSER` | 解析器 | 启用 | — |
| `CONFIG_H263_V4L2M2M_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_H263_V4L2M2M_ENCODER` | 编码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_H263_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_H263_VIDEOTOOLBOX_HWACCEL` | 硬件加速器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_H264CHROMA` | 库与构建能力 | 启用 | — |
| `CONFIG_H264DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_H264PARSE` | 库与构建能力 | 启用 | — |
| `CONFIG_H264PRED` | 库与构建能力 | 启用 | — |
| `CONFIG_H264QPEL` | 库与构建能力 | 启用 | — |
| `CONFIG_H264_AMF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_H264_AMF_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_H264_CUVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_H264_D3D11VA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_H264_D3D11VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_H264_D3D12VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_H264_DECODER` | 解码器 | 启用 | — |
| `CONFIG_H264_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_H264_DXVA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_H264_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_H264_MEDIACODEC_ENCODER` | 编码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_H264_METADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_H264_MF_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_H264_MMAL_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_H264_MP4TOANNEXB_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_H264_MUXER` | 复用器 | 启用 | — |
| `CONFIG_H264_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_H264_NVENC_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_H264_OH_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_H264_OH_ENCODER` | 编码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_H264_OMX_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_H264_PARSER` | 解析器 | 启用 | — |
| `CONFIG_H264_QSV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_H264_QSV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_H264_REDUNDANT_PPS_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_H264_RKMPP_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_H264_SEI` | 库与构建能力 | 启用 | — |
| `CONFIG_H264_V4L2M2M_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_H264_V4L2M2M_ENCODER` | 编码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_H264_VAAPI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_H264_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_H264_VDPAU_HWACCEL` | 硬件加速器 | 禁用 | NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用 |
| `CONFIG_H264_VIDEOTOOLBOX_ENCODER` | 编码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_H264_VIDEOTOOLBOX_HWACCEL` | 硬件加速器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_H264_VULKAN_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_H264_VULKAN_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_HAAS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HALDCLUTSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HALDCLUT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HAPQA_EXTRACT_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_HAP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HAP_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_HARDCODED_TABLES` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_HASH_MUXER` | 复用器 | 启用 | — |
| `CONFIG_HCA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HCA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_HCOM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HCOM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_HDCD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HDR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HDR_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_HDR_PARSER` | 解析器 | 启用 | — |
| `CONFIG_HDS_MUXER` | 复用器 | 启用 | — |
| `CONFIG_HEADPHONE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HEVCPARSE` | 库与构建能力 | 启用 | — |
| `CONFIG_HEVC_AMF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HEVC_AMF_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_HEVC_CUVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HEVC_D3D11VA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_D3D11VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_D3D12VA_ENCODER` | 编码器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_D3D12VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HEVC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_HEVC_DXVA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_MEDIACODEC_ENCODER` | 编码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_METADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_HEVC_MF_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_HEVC_MP4TOANNEXB_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_HEVC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_HEVC_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_HEVC_NVENC_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_HEVC_OH_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_OH_ENCODER` | 编码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_PARSER` | 解析器 | 启用 | — |
| `CONFIG_HEVC_QSV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HEVC_QSV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_HEVC_RKMPP_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_SEI` | 库与构建能力 | 启用 | — |
| `CONFIG_HEVC_V4L2M2M_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_HEVC_V4L2M2M_ENCODER` | 编码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_HEVC_VAAPI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_HEVC_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_HEVC_VDPAU_HWACCEL` | 硬件加速器 | 禁用 | NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用 |
| `CONFIG_HEVC_VIDEOTOOLBOX_ENCODER` | 编码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_VIDEOTOOLBOX_HWACCEL` | 硬件加速器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_HEVC_VULKAN_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_HEVC_VULKAN_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_HFLIP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HFLIP_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HIGHPASS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HIGHSHELF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HILBERT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HISTEQ_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HISTOGRAM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HLS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_HLS_MUXER` | 复用器 | 启用 | — |
| `CONFIG_HLS_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_HNM4_VIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HNM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_HPELDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_HQDN3D_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HQX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HQX_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HQ_HQA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HSTACK_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HSTACK_QSV_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HSTACK_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HSVHOLD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HSVKEY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HTMLPAGES` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_HTTPPROXY_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_HTTPS_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_HTTP_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_HUESATURATION_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HUE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HUFFMAN` | 库与构建能力 | 启用 | — |
| `CONFIG_HUFFYUVDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_HUFFYUVENCDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_HUFFYUV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HUFFYUV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_HWACCELS` | 库与构建能力 | 启用 | — |
| `CONFIG_HWDOWNLOAD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HWMAP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HWUPLOAD_CUDA_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HWUPLOAD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_HW_DECODE_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_HYMT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_HYSTERESIS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_IAC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_IAMFDEC` | 库与构建能力 | 启用 | — |
| `CONFIG_IAMFENC` | 库与构建能力 | 启用 | — |
| `CONFIG_IAMF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IAMF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_IAMF` | 库与构建能力 | 启用 | — |
| `CONFIG_ICCDETECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ICCGEN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ICECAST_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_ICONV` | 库与构建能力 | 启用 | — |
| `CONFIG_ICO_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ICO_MUXER` | 复用器 | 启用 | — |
| `CONFIG_IDCIN_DECODER` | 解码器 | 启用 | — |
| `CONFIG_IDCIN_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IDCTDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_IDENTITY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_IDET_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_IDF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_IDF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IEC61883_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_IFF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IFF_ILBM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_IFV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ILBC_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_ILBC_AT_ENCODER` | 编码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_ILBC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ILBC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ILBC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_IL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_IMAGE2PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE2PIPE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_IMAGE2_ALIAS_PIX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE2_BRENDER_PIX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE2_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE2_MUXER` | 复用器 | 启用 | — |
| `CONFIG_IMAGE_BMP_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_CRI_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_DDS_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_DPX_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_EXR_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_GEM_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_GIF_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_HDR_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_J2K_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_JPEGLS_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_JPEGXL_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_JPEG_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PAM_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PBM_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PCX_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PFM_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PGMYUV_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PGM_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PGX_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PHM_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PHOTOCD_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PICTOR_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PNG_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PPM_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_PSD_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_QDRAW_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_QOI_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_SGI_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_SUNRAST_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_SVG_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_TIFF_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_VBN_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_WEBP_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_XBM_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_XPM_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMAGE_XWD_PIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IMC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_IMF_DEMUXER` | 解复用器 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_IMM4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_IMM5_DECODER` | 解码器 | 启用 | — |
| `CONFIG_IMX_DUMP_HEADER_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_INDEO2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_INDEO3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_INDEO4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_INDEO5_DECODER` | 解码器 | 启用 | — |
| `CONFIG_INDEVS` | 库与构建能力 | 启用 | — |
| `CONFIG_INFLATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_INFLATE_WRAPPER` | 库与构建能力 | 启用 | — |
| `CONFIG_INGENIENT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_INTERLACE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_INTERLACE_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_INTERLEAVE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_INTERPLAY_ACM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_INTERPLAY_DPCM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_INTERPLAY_VIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_INTRAX8` | 库与构建能力 | 启用 | — |
| `CONFIG_IPFS_GATEWAY_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_IPMOVIE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IPNS_GATEWAY_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_IPOD_MUXER` | 复用器 | 启用 | — |
| `CONFIG_IPU_DECODER` | 解码器 | 启用 | — |
| `CONFIG_IPU_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IPU_PARSER` | 解析器 | 启用 | — |
| `CONFIG_IRCAM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IRCAM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ISMV_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ISO_MEDIA` | 库与构建能力 | 启用 | — |
| `CONFIG_ISO_WRITER` | 库与构建能力 | 启用 | — |
| `CONFIG_ISS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IV8_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IVF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_IVF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_IVIDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_IVR_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_JACK_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_JACOSUB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_JACOSUB_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_JACOSUB_MUXER` | 复用器 | 启用 | — |
| `CONFIG_JNI` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_JOIN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_JPEG2000_DECODER` | 解码器 | 启用 | — |
| `CONFIG_JPEG2000_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_JPEG2000_PARSER` | 解析器 | 启用 | — |
| `CONFIG_JPEGLS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_JPEGLS_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_JPEGTABLES` | 库与构建能力 | 启用 | — |
| `CONFIG_JPEGXL_ANIM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_JPEGXL_PARSER` | 解析器 | 启用 | — |
| `CONFIG_JV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_JV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_KERNDEINT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_KGV1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_KIRSCH_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_KMSGRAB_INDEV` | 输入设备 | 启用 | — |
| `CONFIG_KMVC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_KUX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_KVAG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_KVAG_MUXER` | 复用器 | 启用 | — |
| `CONFIG_LADSPA_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_LADSPA` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_LAF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_LAGARITH_DECODER` | 解码器 | 启用 | — |
| `CONFIG_LAGFUN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LARGE_TESTS` | 库与构建能力 | 启用 | — |
| `CONFIG_LATENCY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LATM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_LAVFI_INDEV` | 输入设备 | 启用 | — |
| `CONFIG_LC3_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_LC3_MUXER` | 复用器 | 启用 | — |
| `CONFIG_LCEVC_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_LCMS2` | 库与构建能力 | 启用 | — |
| `CONFIG_LEAD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_LENSCORRECTION_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LENSFUN_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_LGPLV3` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_LIBAMQP_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBAOM_AV1_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBAOM_AV1_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBAOM` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBARIBB24_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBARIBB24` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBARIBCAPTION_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBARIBCAPTION` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBASS` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBBLURAY` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBBS2B` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBCACA` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBCDIO_INDEV` | 输入设备 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBCDIO` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBCELT_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBCELT` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBCODEC2_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBCODEC2_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBCODEC2` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBDAV1D_DECODER` | 解码器 | 启用 | — |
| `CONFIG_LIBDAV1D` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBDAVS2_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBDAVS2` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBDC1394_INDEV` | 输入设备 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBDC1394` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBDRM` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBDVDNAV` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBDVDREAD` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBFDK_AAC_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBFDK_AAC_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBFDK_AAC` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBFLITE` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBFONTCONFIG` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBFREETYPE` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBFRIBIDI` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBGLSLANG` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBGME_DEMUXER` | 解复用器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBGME` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBGSM_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBGSM_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBGSM_MS_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBGSM_MS_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBGSM` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBHARFBUZZ` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBIEC61883` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBILBC_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBILBC_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBILBC` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBJACK` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBJXL_ANIM_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBJXL_ANIM_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBJXL_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBJXL_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBJXL` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBKLVANC` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBKVAZAAR_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBKVAZAAR` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBLC3_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBLC3_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBLC3` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBLCEVC_DEC` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBLENSFUN` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBMFX` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBMODPLUG_DEMUXER` | 解复用器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBMODPLUG` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBMP3LAME_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBMP3LAME` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBMYSOFA` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBNPP` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOAPV_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOAPV` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENCORE_AMRNB_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENCORE_AMRNB_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENCORE_AMRNB` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENCORE_AMRWB_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENCORE_AMRWB` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENCV` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENH264_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENH264_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENH264` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENJPEG_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_LIBOPENJPEG` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBOPENMPT_DEMUXER` | 解复用器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENMPT` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPENVINO` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBOPUS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_LIBOPUS_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_LIBOPUS` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBPLACEBO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LIBPLACEBO` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBPULSE` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBQRENCODE` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBQUIRC` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBRABBITMQ` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBRAV1E_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBRAV1E` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBRIST_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBRIST` | 库与构建能力 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBRSVG_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBRSVG` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBRTMPE_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBRTMPS_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBRTMPTE_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBRTMPT_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBRTMP_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBRTMP` | 库与构建能力 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBRUBBERBAND` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBSHADERC` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBSHINE_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBSHINE` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBSMBCLIENT_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBSMBCLIENT` | 库与构建能力 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBSNAPPY` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBSOXR` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBSPEEX_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBSPEEX_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBSPEEX` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBSRT_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBSRT` | 库与构建能力 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBSSH_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBSSH` | 库与构建能力 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBSVTAV1_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBSVTAV1` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBTENSORFLOW` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBTESSERACT` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBTHEORA_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBTHEORA` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBTLS` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBTORCH` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBTWOLAME_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBTWOLAME` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBUAVS3D_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBUAVS3D` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBV4L2` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBVIDSTAB` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBVMAF_CUDA_FILTER` | 音视频过滤器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBVMAF_FILTER` | 音视频过滤器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBVMAF` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBVORBIS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_LIBVORBIS_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_LIBVORBIS` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBVO_AMRWBENC_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBVO_AMRWBENC` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBVPL` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBVPX_VP8_DECODER` | 解码器 | 启用 | — |
| `CONFIG_LIBVPX_VP8_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_LIBVPX_VP9_DECODER` | 解码器 | 启用 | — |
| `CONFIG_LIBVPX_VP9_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_LIBVPX` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBVVENC_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBVVENC` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBWEBP_ANIM_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBWEBP_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBWEBP` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBX262_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBX262` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBX264RGB_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBX264_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBX264_HDR10` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBX264` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBX265_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBX265` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXAVS2_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXAVS2` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXAVS_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXAVS` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXCB_SHAPE` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXCB_SHM` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXCB_XFIXES` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXCB` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXEVD_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXEVD` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXEVE_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXEVE` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXML2` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXVID_ENCODER` | 编码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBXVID` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBZIMG` | 库与构建能力 | 启用 | — |
| `CONFIG_LIBZMQ_PROTOCOL` | 协议 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBZMQ` | 库与构建能力 | 禁用 | 可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责 |
| `CONFIG_LIBZVBI_TELETEXT_DECODER` | 解码器 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIBZVBI` | 库与构建能力 | 禁用 | 可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要 |
| `CONFIG_LIFE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LIMITDIFF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LIMITER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LINUX_PERF` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_LIVE_FLV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_LJPEG_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_LLAUDDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_LLVIDDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_LLVIDENCDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_LMLM4_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_LOAS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_LOCO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_LOOP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LOUDNORM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LOWPASS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LOWSHELF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LPC` | 库与构建能力 | 启用 | — |
| `CONFIG_LRC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_LRC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_LSCR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_LSP` | 库与构建能力 | 启用 | — |
| `CONFIG_LUMAKEY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LUODAT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_LUT1D_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LUT2_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LUT3D_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LUTRGB_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LUTYUV_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LUT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_LV2_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_LV2` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_LVF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_LXF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_LZF` | 库与构建能力 | 启用 | — |
| `CONFIG_LZMA` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_M101_DECODER` | 解码器 | 启用 | — |
| `CONFIG_M4V_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_M4V_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MACE3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MACE6_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MACOS_KPERF` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_MAGICYUV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MAGICYUV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MANDELBROT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MANPAGES` | 库与构建能力 | 启用 | — |
| `CONFIG_MASKEDCLAMP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MASKEDMAX_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MASKEDMERGE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MASKEDMIN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MASKEDTHRESHOLD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MASKFUN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MATROSKA_AUDIO_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MATROSKA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MATROSKA_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MBEDTLS` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_MCA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MCC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MCC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MCDEINT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MCOMPAND_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MD5_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MD5_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_MDEC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MEDIA100_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MEDIA100_TO_MJPEGB_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_MEDIACODEC` | 库与构建能力 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_MEDIAFOUNDATION` | 库与构建能力 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_MEDIAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MEMORY_POISONING` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_MERGEPLANES_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MESTIMATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_METADATA_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_METAL` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_METASOUND_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ME_CMP` | 库与构建能力 | 启用 | — |
| `CONFIG_MGSTS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MICRODVD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MICRODVD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MICRODVD_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MIDEQUALIZER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MIMIC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MINTERPOLATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MISC4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MISC4_PARSER` | 解析器 | 启用 | — |
| `CONFIG_MIX_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MJPEG2JPEG_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_MJPEGA_DUMP_HEADER_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_MJPEGB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MJPEG_2000_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MJPEG_CUVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MJPEG_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MJPEG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MJPEG_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MJPEG_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MJPEG_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_MJPEG_PARSER` | 解析器 | 启用 | — |
| `CONFIG_MJPEG_QSV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MJPEG_QSV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MJPEG_VAAPI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MJPEG_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_MKVTIMESTAMP_V2_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MLP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MLP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MLP_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MLP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MLP_PARSER` | 解析器 | 启用 | — |
| `CONFIG_MLV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MMAL` | 库与构建能力 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_MMF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MMF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MMSH_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_MMST_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_MMVIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MOBICLIP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MODS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MOFLEX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MONOCHROME_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MORPHO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MOTIONPIXELS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MOV2TEXTSUB_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_MOVIE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MOVTEXT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MOVTEXT_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MOV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MOV_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MP1FLOAT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MP1_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_MP1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MP2FIXED_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MP2FLOAT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MP2_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_MP2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MP2_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MP2_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MP3ADUFLOAT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MP3ADU_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MP3FLOAT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MP3ON4FLOAT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MP3ON4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MP3_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_MP3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MP3_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MP3_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_MP3_MF_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_MP3_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MP4_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MPC7_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPC8_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPC8_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MPC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MPDECIMATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MPEG1SYSTEM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MPEG1VCD_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MPEG1VIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPEG1VIDEO_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MPEG1VIDEO_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MPEG1_CUVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPEG1_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_MPEG1_V4L2M2M_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_MPEG1_VDPAU_HWACCEL` | 硬件加速器 | 禁用 | NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用 |
| `CONFIG_MPEG1_VIDEOTOOLBOX_HWACCEL` | 硬件加速器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_MPEG2DVD_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MPEG2SVCD_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MPEG2VIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPEG2VIDEO_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MPEG2VIDEO_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MPEG2VOB_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MPEG2_CUVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPEG2_D3D11VA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_MPEG2_D3D11VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_MPEG2_D3D12VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_MPEG2_DXVA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_MPEG2_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_MPEG2_METADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_MPEG2_MMAL_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_MPEG2_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_MPEG2_QSV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPEG2_QSV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MPEG2_V4L2M2M_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_MPEG2_VAAPI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MPEG2_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_MPEG2_VDPAU_HWACCEL` | 硬件加速器 | 禁用 | NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用 |
| `CONFIG_MPEG2_VIDEOTOOLBOX_HWACCEL` | 硬件加速器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_MPEG4AUDIO` | 库与构建能力 | 启用 | — |
| `CONFIG_MPEG4VIDEO_PARSER` | 解析器 | 启用 | — |
| `CONFIG_MPEG4_CUVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPEG4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPEG4_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MPEG4_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_MPEG4_MEDIACODEC_ENCODER` | 编码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_MPEG4_MMAL_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_MPEG4_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_MPEG4_OMX_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_MPEG4_UNPACK_BFRAMES_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_MPEG4_V4L2M2M_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_MPEG4_V4L2M2M_ENCODER` | 编码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_MPEG4_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_MPEG4_VDPAU_HWACCEL` | 硬件加速器 | 禁用 | NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用 |
| `CONFIG_MPEG4_VIDEOTOOLBOX_HWACCEL` | 硬件加速器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_MPEGAUDIODSP` | 库与构建能力 | 启用 | — |
| `CONFIG_MPEGAUDIOHEADER` | 库与构建能力 | 启用 | — |
| `CONFIG_MPEGAUDIO_PARSER` | 解析器 | 启用 | — |
| `CONFIG_MPEGAUDIO` | 库与构建能力 | 启用 | — |
| `CONFIG_MPEGPS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MPEGTSRAW_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MPEGTS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MPEGTS_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MPEGVIDEODEC` | 库与构建能力 | 启用 | — |
| `CONFIG_MPEGVIDEOENCDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_MPEGVIDEOENC` | 库与构建能力 | 启用 | — |
| `CONFIG_MPEGVIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPEGVIDEO_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MPEGVIDEO_PARSER` | 解析器 | 启用 | — |
| `CONFIG_MPEGVIDEO` | 库与构建能力 | 启用 | — |
| `CONFIG_MPEG_ER` | 库与构建能力 | 启用 | — |
| `CONFIG_MPJPEG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MPJPEG_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MPL2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MPL2_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MPSUB_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MPTESTSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MSA1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSAD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MSCC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MSMPEG4DEC` | 库与构建能力 | 启用 | — |
| `CONFIG_MSMPEG4ENC` | 库与构建能力 | 启用 | — |
| `CONFIG_MSMPEG4V1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSMPEG4V2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSMPEG4V2_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MSMPEG4V3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSMPEG4V3_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MSNSIREN_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSNWC_TCP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MSP2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MSRLE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSRLE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MSS1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSS2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSS34DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_MSVIDEO1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MSVIDEO1_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_MSZH_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MTAF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MTS2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MTV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MULTIPLY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_MUSX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MUXERS` | 库与构建能力 | 启用 | — |
| `CONFIG_MUX_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_MV30_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MVC1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MVC2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MVDV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MVHA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MVI_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MWSC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_MXF_D10_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MXF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MXF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MXF_OPATOM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_MXG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_MXPEG_DECODER` | 解码器 | 启用 | — |
| `CONFIG_NC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_NEGATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NELLYMOSER_DECODER` | 解码器 | 启用 | — |
| `CONFIG_NELLYMOSER_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_NEON_CLOBBER_TEST` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_NETWORK` | 库与构建能力 | 启用 | — |
| `CONFIG_NISTSPHERE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_NLMEANS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NLMEANS_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NLMEANS_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NNEDI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NOFORMAT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NOISE_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_NOISE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NONFREE` | 库与构建能力 | 禁用 | 许可证/稳定性策略未开启；GPLv3 功能已经开启 |
| `CONFIG_NORMALIZE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NOTCHLC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_NSP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_NSV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_NULLSINK_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NULLSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NULL_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_NULL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_NULL_MUXER` | 复用器 | 启用 | — |
| `CONFIG_NUT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_NUT_MUXER` | 复用器 | 启用 | — |
| `CONFIG_NUV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_NUV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_NVDEC` | 库与构建能力 | 启用 | — |
| `CONFIG_NVENC` | 库与构建能力 | 启用 | — |
| `CONFIG_OBU_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_OBU_MUXER` | 复用器 | 启用 | — |
| `CONFIG_OCR_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_OCV_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_OGA_MUXER` | 复用器 | 启用 | — |
| `CONFIG_OGG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_OGG_MUXER` | 复用器 | 启用 | — |
| `CONFIG_OGV_MUXER` | 复用器 | 启用 | — |
| `CONFIG_OHCODEC` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_OMA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_OMA_MUXER` | 复用器 | 启用 | — |
| `CONFIG_OMX_RPI` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_OMX` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_ON2AVC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_OPENAL_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_OPENAL` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_OPENCLSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_OPENCL` | 库与构建能力 | 启用 | — |
| `CONFIG_OPENGL` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_OPENSSL` | 库与构建能力 | 启用 | — |
| `CONFIG_OPUS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_OPUS_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_OPUS_METADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_OPUS_MUXER` | 复用器 | 启用 | — |
| `CONFIG_OPUS_PARSER` | 解析器 | 启用 | — |
| `CONFIG_OSCILLOSCOPE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_OSQ_DECODER` | 解码器 | 启用 | — |
| `CONFIG_OSQ_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_OSSFUZZ` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_OSS_INDEV` | 输入设备 | 启用 | — |
| `CONFIG_OSS_OUTDEV` | 输出设备 | 启用 | — |
| `CONFIG_OUTDEVS` | 库与构建能力 | 启用 | — |
| `CONFIG_OVERLAY_CUDA_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_OVERLAY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_OVERLAY_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_OVERLAY_QSV_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_OVERLAY_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_OVERLAY_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_OWDENOISE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PAD_CUDA_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_PAD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PAD_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PAD_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PAF_AUDIO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PAF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PAF_VIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PAL100BARS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PAL75BARS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PALETTEGEN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PALETTEUSE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PAM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PAM_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PARSERS` | 库与构建能力 | 启用 | — |
| `CONFIG_PBM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PBM_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_ALAW_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_PCM_ALAW_AT_ENCODER` | 编码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_PCM_ALAW_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_ALAW_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_ALAW_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_ALAW_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_BLURAY_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_BLURAY_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_DVD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_DVD_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_F16LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_F24LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_F32BE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_F32BE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_F32BE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_F32BE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_F32LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_F32LE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_F32LE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_F32LE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_F64BE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_F64BE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_F64BE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_F64BE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_F64LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_F64LE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_F64LE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_F64LE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_LXF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_MULAW_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_PCM_MULAW_AT_ENCODER` | 编码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_PCM_MULAW_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_MULAW_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_MULAW_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_MULAW_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_RECHUNK_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_PCM_S16BE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S16BE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_S16BE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S16BE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_S16BE_PLANAR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S16BE_PLANAR_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S16LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S16LE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_S16LE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S16LE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_S16LE_PLANAR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S16LE_PLANAR_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S24BE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S24BE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_S24BE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S24BE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_S24DAUD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S24DAUD_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S24LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S24LE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_S24LE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S24LE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_S24LE_PLANAR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S24LE_PLANAR_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S32BE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S32BE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_S32BE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S32BE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_S32LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S32LE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_S32LE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S32LE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_S32LE_PLANAR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S32LE_PLANAR_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S64BE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S64BE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S64LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S64LE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S8_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S8_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_S8_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_S8_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_S8_PLANAR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_S8_PLANAR_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_SGA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_U16BE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_U16BE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_U16BE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_U16BE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_U16LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_U16LE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_U16LE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_U16LE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_U24BE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_U24BE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_U24BE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_U24BE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_U24LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_U24LE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_U24LE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_U24LE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_U32BE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_U32BE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_U32BE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_U32BE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_U32LE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_U32LE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_U32LE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_U32LE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_U8_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_U8_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_U8_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_U8_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCM_VIDC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCM_VIDC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PCM_VIDC_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PCM_VIDC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PCX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PCX_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PDV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PDV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PERLIN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PERMS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PERSPECTIVE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PFM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PFM_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PGMYUV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PGMYUV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PGM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PGM_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PGSSUB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PGS_FRAME_MERGE_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_PGX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PHASE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PHM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PHM_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PHOTOCD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PHOTOSENSITIVITY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PICTOR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PIC` | 库与构建能力 | 启用 | — |
| `CONFIG_PIPE_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_PIXBLOCKDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_PIXDESCTEST_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PIXELIZE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PIXELUTILS` | 库与构建能力 | 启用 | — |
| `CONFIG_PIXLET_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PIXSCOPE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PJS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PJS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PMP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PNG_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PNG_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PNG_PARSER` | 解析器 | 启用 | — |
| `CONFIG_PNM_PARSER` | 解析器 | 启用 | — |
| `CONFIG_POCKETSPHINX` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_PODPAGES` | 库与构建能力 | 启用 | — |
| `CONFIG_PP7_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PPM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PPM_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PP_BNK_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PREMULTIPLY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PREWITT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PREWITT_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PROCAMP_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PROGRAM_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PROMPEG_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_PRORES_AW_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PRORES_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PRORES_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PRORES_KS_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_PRORES_METADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_PRORES_RAW_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PRORES_RAW_PARSER` | 解析器 | 启用 | — |
| `CONFIG_PRORES_RAW_VULKAN_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_PRORES_VIDEOTOOLBOX_ENCODER` | 编码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_PRORES_VIDEOTOOLBOX_HWACCEL` | 硬件加速器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_PROSUMER_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PROTOCOLS` | 库与构建能力 | 启用 | — |
| `CONFIG_PSD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PSEUDOCOLOR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PSNR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PSP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_PTX_COMPRESSION` | 库与构建能力 | 启用 | — |
| `CONFIG_PTX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_PULLUP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_PULSE_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_PULSE_OUTDEV` | 输出设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_PVA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_PVF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_QCELP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_QCP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_QDM2_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_QDM2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_QDMC_AT_DECODER` | 解码器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_QDMC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_QDRAW_DECODER` | 解码器 | 启用 | — |
| `CONFIG_QOA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_QOA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_QOI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_QOI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_QOI_PARSER` | 解析器 | 启用 | — |
| `CONFIG_QPEG_DECODER` | 解码器 | 启用 | — |
| `CONFIG_QPELDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_QP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_QRENCODESRC_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_QRENCODE_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_QSVDEC` | 库与构建能力 | 启用 | — |
| `CONFIG_QSVENC` | 库与构建能力 | 启用 | — |
| `CONFIG_QSVVPP` | 库与构建能力 | 启用 | — |
| `CONFIG_QSV_DECODE_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_QSV_TRANSCODE_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_QSV` | 库与构建能力 | 启用 | — |
| `CONFIG_QTRLE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_QTRLE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_QUIRC_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_R10K_DECODER` | 解码器 | 启用 | — |
| `CONFIG_R10K_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_R210_DECODER` | 解码器 | 启用 | — |
| `CONFIG_R210_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_R3D_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RALF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RANDOM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_RANGECODER` | 库与构建能力 | 启用 | — |
| `CONFIG_RASC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RAWVIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RAWVIDEO_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RAWVIDEO_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_RAWVIDEO_MUXER` | 复用器 | 启用 | — |
| `CONFIG_RA_144_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RA_144_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_RA_288_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RCWT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RCWT_MUXER` | 复用器 | 启用 | — |
| `CONFIG_READEIA608_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_READVITC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_REALTEXT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_REALTEXT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_REALTIME_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_REDSPARK_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_REMAP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_REMAP_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_REMOVEGRAIN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_REMOVELOGO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_REMOVE_EXTRADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_REMUX_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_REPEATFIELDS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_REPLAYGAIN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_RESAMPLE_AUDIO_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_RESOURCE_COMPRESSION` | 库与构建能力 | 启用 | — |
| `CONFIG_REVERSE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_RGBASHIFT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_RGBTESTSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_RIFFDEC` | 库与构建能力 | 启用 | — |
| `CONFIG_RIFFENC` | 库与构建能力 | 启用 | — |
| `CONFIG_RKA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RKA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RKMPP` | 库与构建能力 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_RL2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RL2_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ROBERTS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ROBERTS_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ROQ_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ROQ_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_ROQ_DPCM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ROQ_DPCM_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ROQ_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ROQ_MUXER` | 复用器 | 启用 | — |
| `CONFIG_ROTATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_RPL_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RPZA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RPZA_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_RSCC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RSD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RSO_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RSO_MUXER` | 复用器 | 启用 | — |
| `CONFIG_RTMPE_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_RTMPS_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_RTMPTE_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_RTMPTS_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_RTMPT_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_RTMP_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_RTPDEC` | 库与构建能力 | 启用 | — |
| `CONFIG_RTPENC_CHAIN` | 库与构建能力 | 启用 | — |
| `CONFIG_RTP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RTP_MPEGTS_MUXER` | 复用器 | 启用 | — |
| `CONFIG_RTP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_RTP_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_RTSP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_RTSP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_RTV1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RUBBERBAND_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_RUNTIME_CPUDETECT` | 库与构建能力 | 启用 | — |
| `CONFIG_RV10_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RV10_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_RV20_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RV20_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_RV30_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RV34DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_RV34_PARSER` | 解析器 | 启用 | — |
| `CONFIG_RV40_DECODER` | 解码器 | 启用 | — |
| `CONFIG_RV60_DECODER` | 解码器 | 启用 | — |
| `CONFIG_S302M_DECODER` | 解码器 | 启用 | — |
| `CONFIG_S302M_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_S337M_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SAB_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SAFE_BITSTREAM_READER` | 库与构建能力 | 启用 | — |
| `CONFIG_SAMI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SAMI_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SANM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SAP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SAP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SBC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SBC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SBC_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_SBC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SBC_PARSER` | 解析器 | 启用 | — |
| `CONFIG_SBG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SCALE2REF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SCALE2REF_NPP_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_SCALE_CUDA_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_SCALE_D3D11_FILTER` | 音视频过滤器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_SCALE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SCALE_NPP_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_SCALE_QSV_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SCALE_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SCALE_VIDEO_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_SCALE_VT_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_SCALE_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SCC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SCC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SCDET_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SCDET_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SCD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SCENE_SAD` | 库与构建能力 | 启用 | — |
| `CONFIG_SCHANNEL` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_SCHARR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SCPR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SCREENPRESSO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SCROLL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SCTP_PROTOCOL` | 协议 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_SDL2` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_SDNS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SDP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SDR2_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SDS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SDX2_DPCM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SDX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SECURETRANSPORT` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_SEGAFILM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SEGAFILM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SEGMENT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SEGMENT_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SELECTIVECOLOR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SELECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SENDCMD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SEPARATEFIELDS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SER_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SETDAR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SETFIELD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SETPARAMS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SETPTS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SETRANGE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SETSAR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SETTB_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SETTS_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_SGA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SGA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SGIRLE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SGI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SGI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_SHARED` | 库与构建能力 | 禁用 | 静态链接策略：只生成静态归档 |
| `CONFIG_SHARPEN_NPP_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_SHARPNESS_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHEAR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHEERVIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SHORTEN_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SHORTEN_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SHOWCQT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOWCWT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOWFREQS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOWINFO_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_SHOWINFO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOWPALETTE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOWSPATIAL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOWSPECTRUMPIC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOWSPECTRUM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOWVOLUME_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOWWAVESPIC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOWWAVES_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHOW_METADATA_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_SHUFFLEFRAMES_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHUFFLEPIXELS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SHUFFLEPLANES_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SIDECHAINCOMPRESS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SIDECHAINGATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SIDEDATA_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SIERPINSKI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SIFF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SIGNALSTATS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SIGNATURE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SILENCEDETECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SILENCEREMOVE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SIMBIOSIS_IMX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SIMBIOSIS_IMX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SINC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SINEWIN` | 库与构建能力 | 启用 | — |
| `CONFIG_SINE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SIPR_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SIPR_PARSER` | 解析器 | 启用 | — |
| `CONFIG_SIREN_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SITI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SLN_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SMACKAUD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SMACKER_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SMACKER_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SMALL` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_SMARTBLUR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SMC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SMC_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_SMJPEG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SMJPEG_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SMOOTHSTREAMING_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SMPTE436M_TO_EIA608_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_SMPTEBARS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SMPTEHDBARS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SMPTE_436M` | 库与构建能力 | 启用 | — |
| `CONFIG_SMUSH_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SMVJPEG_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SNAPPY` | 库与构建能力 | 启用 | — |
| `CONFIG_SNDIO_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_SNDIO_OUTDEV` | 输出设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_SNDIO` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_SNOW_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SNOW_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_SOBEL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SOBEL_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SOFALIZER_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_SOL_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SOL_DPCM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SONIC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SONIC_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_SONIC_LS_ENCODER` | 编码器 | 禁用 | 可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响 |
| `CONFIG_SOX_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SOX_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SP5X_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SPDIF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SPDIF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SPECTRUMSYNTH_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SPEECHNORM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SPEEDHQ_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SPEEDHQ_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_SPEEX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SPLIT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SPP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SPX_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SRGC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SRTP_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_SRTP` | 库与构建能力 | 启用 | — |
| `CONFIG_SRT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SRT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SRT_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_SRT_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SR_AMF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SR_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_SSA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SSA_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_SSIM360_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SSIM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_STARTCODE` | 库与构建能力 | 启用 | — |
| `CONFIG_STATIC` | 库与构建能力 | 启用 | — |
| `CONFIG_STEREO3D_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_STEREOTOOLS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_STEREOWIDEN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_STL_DECODER` | 解码器 | 启用 | — |
| `CONFIG_STL_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_STREAMHASH_MUXER` | 复用器 | 启用 | — |
| `CONFIG_STREAMSELECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_STREAM_SEGMENT_MUXER` | 复用器 | 启用 | — |
| `CONFIG_STR_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SUBFILE_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_SUBRIP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SUBRIP_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_SUBTITLES_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SUBVIEWER1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SUBVIEWER1_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SUBVIEWER_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SUBVIEWER_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SUNRAST_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SUNRAST_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_SUPER2XSAI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SUPEREQUALIZER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SUP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SUP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SURROUND_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SVAG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SVQ1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SVQ1_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_SVQ3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_SVS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SWAPRECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SWAPUV_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_SWF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_SWF_MUXER` | 复用器 | 启用 | — |
| `CONFIG_SWRESAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_SWSCALE_ALPHA` | 库与构建能力 | 启用 | — |
| `CONFIG_SWSCALE` | 库与构建能力 | 启用 | — |
| `CONFIG_TAK_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TAK_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_TAK_PARSER` | 解析器 | 启用 | — |
| `CONFIG_TARGA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TARGA_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_TARGA_Y216_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TBLEND_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TCP_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_TDSC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TEDCAPTIONS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_TEE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_TEE_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_TELECINE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TESTSRC2_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TESTSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TEXT2MOVSUB_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_TEXTUREDSPENC` | 库与构建能力 | 启用 | — |
| `CONFIG_TEXTUREDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_TEXT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TEXT_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_TG2_MUXER` | 复用器 | 启用 | — |
| `CONFIG_TGP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_THEORA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_THISTOGRAM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_THP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_THP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_THREEDOSTR_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_THRESHOLD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_THUMBNAIL_CUDA_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_THUMBNAIL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_THUMB` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_TIERTEXSEQVIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TIERTEXSEQ_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_TIFF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TIFF_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_TILE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TILTANDSHIFT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TILTSHELF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TINTERLACE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TLS_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_TLUT2_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TMEDIAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TMIDEQUALIZER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TMIX_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TMV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TMV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_TONEMAP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TONEMAP_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TONEMAP_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TPAD_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TPELDSP` | 库与构建能力 | 启用 | — |
| `CONFIG_TRACE_HEADERS_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_TRANSCODE_AAC_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_TRANSCODE_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_TRANSPOSE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TRANSPOSE_NPP_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_TRANSPOSE_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TRANSPOSE_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TRANSPOSE_VT_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_TRANSPOSE_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TREBLE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TREMOLO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TRIM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_TRUEHD_CORE_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_TRUEHD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TRUEHD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_TRUEHD_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_TRUEHD_MUXER` | 复用器 | 启用 | — |
| `CONFIG_TRUEMOTION1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TRUEMOTION2RT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TRUEMOTION2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TRUESPEECH_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TSCC2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TSCC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TTA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TTA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_TTA_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_TTA_MUXER` | 复用器 | 启用 | — |
| `CONFIG_TTML_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_TTML_MUXER` | 复用器 | 启用 | — |
| `CONFIG_TTY_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_TWINVQ_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TXD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_TXD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_TXTPAGES` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_TY_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_UDPLITE_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_UDP_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_ULTI_DECODER` | 解码器 | 启用 | — |
| `CONFIG_UNCODEDFRAMECRC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_UNIX_PROTOCOL` | 协议 | 启用 | — |
| `CONFIG_UNPREMULTIPLY_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_UNSHARP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_UNSHARP_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_UNTILE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_USM_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_USPP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_UTVIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_UTVIDEO_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_V210X_DECODER` | 解码器 | 启用 | — |
| `CONFIG_V210X_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_V210_DECODER` | 解码器 | 启用 | — |
| `CONFIG_V210_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_V210_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_V308_DECODER` | 解码器 | 启用 | — |
| `CONFIG_V308_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_V360_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_V408_DECODER` | 解码器 | 启用 | — |
| `CONFIG_V408_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_V410_DECODER` | 解码器 | 启用 | — |
| `CONFIG_V410_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_V4L2_INDEV` | 输入设备 | 启用 | — |
| `CONFIG_V4L2_M2M` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_V4L2_OUTDEV` | 输出设备 | 启用 | — |
| `CONFIG_VAAPI_1` | 库与构建能力 | 启用 | — |
| `CONFIG_VAAPI_ENCODE_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_VAAPI_ENCODE` | 库与构建能力 | 启用 | — |
| `CONFIG_VAAPI_TRANSCODE_EXAMPLE` | 库与构建能力 | 启用 | — |
| `CONFIG_VAAPI` | 库与构建能力 | 启用 | — |
| `CONFIG_VAGUEDENOISER_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VAG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VALGRIND_BACKTRACE` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_VAPOURSYNTH_DEMUXER` | 解复用器 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_VAPOURSYNTH` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_VARBLUR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VBLE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VBN_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VBN_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_VB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VC1DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_VC1IMAGE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VC1T_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VC1T_MUXER` | 复用器 | 启用 | — |
| `CONFIG_VC1_CUVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VC1_D3D11VA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_VC1_D3D11VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_VC1_D3D12VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_VC1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VC1_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VC1_DXVA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_VC1_MMAL_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_VC1_MUXER` | 复用器 | 启用 | — |
| `CONFIG_VC1_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_VC1_PARSER` | 解析器 | 启用 | — |
| `CONFIG_VC1_QSV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VC1_V4L2M2M_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_VC1_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_VC1_VDPAU_HWACCEL` | 硬件加速器 | 禁用 | NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用 |
| `CONFIG_VC2_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_VCR1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VDPAU` | 库与构建能力 | 禁用 | NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用 |
| `CONFIG_VECTORSCOPE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VERSION3` | 库与构建能力 | 启用 | — |
| `CONFIG_VFLIP_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VFLIP_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VFRDET_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VFWCAP_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_VIBRANCE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VIBRATO_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VIDEODSP` | 库与构建能力 | 启用 | — |
| `CONFIG_VIDEOTOOLBOX` | 库与构建能力 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_VIDSTABDETECT_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_VIDSTABTRANSFORM_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_VIF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VIGNETTE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VIRTUALBASS_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VIVIDAS_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VIVO_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VMAFMOTION_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VMDAUDIO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VMDVIDEO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VMD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VMIX_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VMNC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VNULL_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VNULL_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_VOBSUB_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VOC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VOC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_VOLUMEDETECT_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VOLUME_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VORBIS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VORBIS_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_VORBIS_PARSER` | 解析器 | 启用 | — |
| `CONFIG_VP3DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_VP3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP3_PARSER` | 解析器 | 启用 | — |
| `CONFIG_VP4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP56DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_VP5_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP6A_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP6F_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP6_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP7_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP8DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_VP8_CUVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP8_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP8_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_VP8_MEDIACODEC_ENCODER` | 编码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_VP8_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_VP8_PARSER` | 解析器 | 启用 | — |
| `CONFIG_VP8_QSV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP8_RKMPP_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_VP8_V4L2M2M_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_VP8_V4L2M2M_ENCODER` | 编码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_VP8_VAAPI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_VP8_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_VP9_AMF_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP9_CUVID_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP9_D3D11VA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_VP9_D3D11VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_VP9_D3D12VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_VP9_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP9_DXVA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_VP9_MEDIACODEC_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_VP9_MEDIACODEC_ENCODER` | 编码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_VP9_METADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_VP9_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_VP9_PARSER` | 解析器 | 启用 | — |
| `CONFIG_VP9_QSV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VP9_QSV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_VP9_RAW_REORDER_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_VP9_RKMPP_DECODER` | 解码器 | 禁用 | Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用 |
| `CONFIG_VP9_SUPERFRAME_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_VP9_SUPERFRAME_SPLIT_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_VP9_V4L2M2M_DECODER` | 解码器 | 禁用 | 嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标 |
| `CONFIG_VP9_VAAPI_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_VP9_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_VP9_VDPAU_HWACCEL` | 硬件加速器 | 禁用 | NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用 |
| `CONFIG_VP9_VIDEOTOOLBOX_HWACCEL` | 硬件加速器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_VP9_VULKAN_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_VPK_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VPLAYER_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VPLAYER_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VPP_AMF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VPP_QSV_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VQA_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VQC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VQF_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VSTACK_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VSTACK_QSV_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VSTACK_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_VULKAN_1_4` | 库与构建能力 | 启用 | — |
| `CONFIG_VULKAN_ENCODE` | 库与构建能力 | 启用 | — |
| `CONFIG_VULKAN_STATIC` | 库与构建能力 | 禁用 | Linux Vulkan Loader 上游不支持静态链接，保留系统 libvulkan.so.1 ABI |
| `CONFIG_VULKAN` | 库与构建能力 | 启用 | — |
| `CONFIG_VVC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VVC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_VVC_METADATA_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_VVC_MP4TOANNEXB_BSF` | 位流过滤器 | 启用 | — |
| `CONFIG_VVC_MUXER` | 复用器 | 启用 | — |
| `CONFIG_VVC_PARSER` | 解析器 | 启用 | — |
| `CONFIG_VVC_QSV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_VVC_SEI` | 库与构建能力 | 启用 | — |
| `CONFIG_VVC_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_W3FDIF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_W64_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_W64_MUXER` | 复用器 | 启用 | — |
| `CONFIG_WADY_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WADY_DPCM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WAVARC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WAVARC_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WAVEFORM_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_WAVPACK_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WAVPACK_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_WAV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WAV_MUXER` | 复用器 | 启用 | — |
| `CONFIG_WBMP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WBMP_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_WC3_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WCMV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WEAVE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_WEBM_CHUNK_MUXER` | 复用器 | 启用 | — |
| `CONFIG_WEBM_DASH_MANIFEST_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WEBM_DASH_MANIFEST_MUXER` | 复用器 | 启用 | — |
| `CONFIG_WEBM_MUXER` | 复用器 | 启用 | — |
| `CONFIG_WEBP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WEBP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_WEBP_PARSER` | 解析器 | 启用 | — |
| `CONFIG_WEBVTT_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WEBVTT_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WEBVTT_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_WEBVTT_MUXER` | 复用器 | 启用 | — |
| `CONFIG_WHIP_MUXER` | 复用器 | 启用 | — |
| `CONFIG_WHISPER_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_WHISPER` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_WMALOSSLESS_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WMAPRO_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WMAV1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WMAV1_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_WMAV2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WMAV2_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_WMAVOICE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WMA_FREQS` | 库与构建能力 | 启用 | — |
| `CONFIG_WMV1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WMV1_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_WMV2DSP` | 库与构建能力 | 启用 | — |
| `CONFIG_WMV2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WMV2_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_WMV3IMAGE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WMV3_D3D11VA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_WMV3_D3D11VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_WMV3_D3D12VA_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_WMV3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WMV3_DXVA2_HWACCEL` | 硬件加速器 | 禁用 | Windows 平台后端，Linux x64 不适用 |
| `CONFIG_WMV3_NVDEC_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_WMV3_VAAPI_HWACCEL` | 硬件加速器 | 启用 | — |
| `CONFIG_WMV3_VDPAU_HWACCEL` | 硬件加速器 | 禁用 | NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用 |
| `CONFIG_WNV1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WRAPPED_AVFRAME_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WRAPPED_AVFRAME_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_WSAUD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WSAUD_MUXER` | 复用器 | 启用 | — |
| `CONFIG_WSD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WSVQA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WS_SND1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_WTV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WTV_MUXER` | 复用器 | 启用 | — |
| `CONFIG_WVE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_WV_MUXER` | 复用器 | 启用 | — |
| `CONFIG_XAN_DPCM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XAN_WC3_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XAN_WC4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_XBIN_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XBIN_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_XBM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XBM_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_XBM_PARSER` | 解析器 | 启用 | — |
| `CONFIG_XBR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_XCBGRAB_INDEV` | 输入设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_XCORRELATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_XFACE_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XFACE_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_XFADE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_XFADE_OPENCL_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_XFADE_VULKAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_XLIB` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_XL_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XMA1_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XMA2_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XMA_PARSER` | 解析器 | 启用 | — |
| `CONFIG_XMD_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_XMEDIAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_XMM_CLOBBER_TEST` | 库与构建能力 | 禁用 | 上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标 |
| `CONFIG_XMV_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_XPM_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XPSNR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_XSTACK_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_XSTACK_QSV_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_XSTACK_VAAPI_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_XSUB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XSUB_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_XVAG_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_XV_OUTDEV` | 输出设备 | 禁用 | 采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O |
| `CONFIG_XWD_DECODER` | 解码器 | 启用 | — |
| `CONFIG_XWD_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_XWD_PARSER` | 解析器 | 启用 | — |
| `CONFIG_XWMA_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_Y41P_DECODER` | 解码器 | 启用 | — |
| `CONFIG_Y41P_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_YADIF_CUDA_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_YADIF_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_YADIF_VIDEOTOOLBOX_FILTER` | 音视频过滤器 | 禁用 | Apple 平台后端，Linux x64 不适用 |
| `CONFIG_YAEPBLUR_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_YLC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_YOP_DECODER` | 解码器 | 启用 | — |
| `CONFIG_YOP_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_YUV4MPEGPIPE_DEMUXER` | 解复用器 | 启用 | — |
| `CONFIG_YUV4MPEGPIPE_MUXER` | 复用器 | 启用 | — |
| `CONFIG_YUV4_DECODER` | 解码器 | 启用 | — |
| `CONFIG_YUV4_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_YUVTESTSRC_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ZERO12V_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ZEROCODEC_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ZLIB_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ZLIB_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ZLIB` | 库与构建能力 | 启用 | — |
| `CONFIG_ZMBV_DECODER` | 解码器 | 启用 | — |
| `CONFIG_ZMBV_ENCODER` | 编码器 | 启用 | — |
| `CONFIG_ZMQ_FILTER` | 音视频过滤器 | 禁用 | 该过滤器依赖未纳入的外部库或其他硬件 API |
| `CONFIG_ZONEPLATE_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ZOOMPAN_FILTER` | 音视频过滤器 | 启用 | — |
| `CONFIG_ZSCALE_FILTER` | 音视频过滤器 | 启用 | — |

## 复核说明

- 表中的状态来自本次 Linux x64 构建目录内的 `config.h` / `config_components.h`。
- 禁用原因按目标平台、静态闭包、许可证和嵌入架构分类；FFmpeg `config.log` 是单项探测的底层证据。
- 当前 NativeAOT ELF 直接依赖：`libEGL.so.1, libvulkan.so.1, libm.so.6, libc.so.6, ld-linux-x86-64.so.2`。
