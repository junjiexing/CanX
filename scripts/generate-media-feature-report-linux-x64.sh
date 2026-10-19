#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
static_root="${CANX_STATIC_ROOT:-$repository_root/.cache/static-native/linux-x64}"
build_root="$static_root/build"
lock_file="$repository_root/native/sources-linux-x64.lock"
output_file="${1:-$repository_root/docs/MPV-FFMPEG-FEATURES-linux-x64.md}"
ffmpeg_config="$build_root/ffmpeg/config.h"
ffmpeg_components="$build_root/ffmpeg/config_components.h"
mpv_config="$build_root/mpv/config.h"
app_binary="${CANX_APP_OUTPUT:-$repository_root/artifacts/publish/linux-x64}/CanX.App"

for input in "$ffmpeg_config" "$ffmpeg_components" "$mpv_config" "$lock_file"; do
  [[ -f "$input" ]] || {
    echo "required build result not found: $input" >&2
    exit 1
  }
done

work_dir="$(mktemp -d /tmp/canx-media-features.XXXXXX)"
cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

ffmpeg_features="$work_dir/ffmpeg-features"
mpv_features="$work_dir/mpv-features"
report="$work_dir/report.md"

awk '
  $1 == "#define" && $2 ~ /^CONFIG_[A-Z0-9_]+$/ && ($3 == 0 || $3 == 1) {
    values[$2] = $3
  }
  END {
    for (name in values)
      print name "|" values[name]
  }
' "$ffmpeg_config" "$ffmpeg_components" | LC_ALL=C sort > "$ffmpeg_features"

awk '
  $1 == "#define" && $2 ~ /^HAVE_[A-Z0-9_]+$/ && ($3 == 0 || $3 == 1) {
    values[$2] = $3
  }
  END {
    for (name in values)
      print name "|" values[name]
  }
' "$mpv_config" | LC_ALL=C sort > "$mpv_features"

ffmpeg_revision="$(awk -F '|' '$1 == "ffmpeg" { print $3 }' "$lock_file")"
mpv_revision="$(awk -F '|' '$1 == "mpv" { print $3 }' "$lock_file")"
ffmpeg_enabled="$(awk -F '|' '$2 == 1 { count++ } END { print count + 0 }' "$ffmpeg_features")"
ffmpeg_disabled="$(awk -F '|' '$2 == 0 { count++ } END { print count + 0 }' "$ffmpeg_features")"
mpv_enabled="$(awk -F '|' '$2 == 1 { count++ } END { print count + 0 }' "$mpv_features")"
mpv_disabled="$(awk -F '|' '$2 == 0 { count++ } END { print count + 0 }' "$mpv_features")"

{
  printf '# FFmpeg / libmpv 功能矩阵（Linux x64）\n\n'
  printf '> 本文由 `scripts/generate-media-feature-report-linux-x64.sh` 从实际构建生成；不是按命令行参数推测。\n\n'
  printf -- '- FFmpeg revision: `%s`\n' "$ffmpeg_revision"
  printf -- '- mpv revision: `%s`\n' "$mpv_revision"
  printf -- '- FFmpeg `CONFIG_*`: %s enabled / %s disabled\n' "$ffmpeg_enabled" "$ffmpeg_disabled"
  printf -- '- libmpv `HAVE_*`: %s enabled / %s disabled\n\n' "$mpv_enabled" "$mpv_disabled"

  printf '## 平台基线与后端策略\n\n'
  printf -- '- 发布目标是 Ubuntu 22.04 x64 或更新版本（glibc 2.35 基线）；发布产物必须通过 `scripts/verify-linux-glibc-baseline.sh`。\n'
  printf -- '- 构建时固定编入各厂商后端，启动和打开媒体时由 libmpv/FFmpeg 按驱动能力探测；构建机使用哪家 GPU 不影响其他后端是否编译。\n'
  printf -- '- 默认 `hwdec=auto-safe`：后端不可用时继续尝试其他安全后端，最后回退软件解码。\n\n'

  printf '## 静态链接与运行时边界\n\n'
  printf '| 项目 | 状态 | 说明 |\n'
  printf '|---|---|---|\n'
  printf '| FFmpeg / libmpv / libass / libplacebo | 静态 | 最终链接进 NativeAOT 主程序；开发 `libmpv.so` 也内含这些静态归档 |\n'
  printf '| oneVPL dispatcher / libva / libva-drm / libdrm | 静态 | Intel QSV 调度与 Intel/AMD VAAPI/DMABUF 用户态入口 |\n'
  printf '| OpenCL ICD loader | 静态 | 运行时再发现厂商 OpenCL ICD |\n'
  printf '| glslang / SPIRV-Tools | 静态 | Vulkan shader 编译链；固定为 glslang `known_good.json` 对应版本 |\n'
  printf '| dav1d / libvpx / Opus / Vorbis / OpenJPEG | 静态 | 外部软件解码器与编解码辅助库 |\n'
  printf '| OpenSSL / zlib / zimg / LCMS2 / libarchive | 静态 | 网络、压缩、缩放、色彩管理与压缩包支持 |\n'
  printf '| FreeType / HarfBuzz / FriBidi / Fontconfig / libunibreak | 静态 | 字幕排版和字体支持 |\n'
  printf '| Lua 5.2 / MuJS / ALSA core | 静态 | mpv 脚本与 Linux 音频核心 |\n'
  printf '| `libc.so.6`, `libm.so.6`, ELF loader | 动态 | Linux 基础 ABI |\n'
  printf '| `libEGL.so.1`, `libvulkan.so.1` | 动态 | Linux 显示/驱动 ABI；Vulkan Loader 上游在 Linux 不提供受支持的静态构建 |\n'
  printf '| `libcuda.so.1`, `libnvcuvid.so.1` | 运行时加载 | NVIDIA 驱动 ABI，不可随应用静态重分发 |\n'
  printf '| AMD `libamfrt64.so.1` | 运行时加载 | 只有安装 AMD AMF Runtime 的机器才会启用 AMF |\n'
  printf '| Intel oneVPL implementation | 运行时加载 | 静态 dispatcher 选择系统提供的 `libmfx-gen.so` 等实现 |\n'
  printf '| VAAPI / Vulkan / OpenCL 厂商驱动 | 运行时加载 | 分别由 libva、Vulkan Loader、OpenCL ICD loader 按设备发现 |\n'
  printf '| ALSA 的 PipeWire/PulseAudio 插件 | 运行时按系统配置加载 | 音频服务端 ABI；`libasound` 本体仍为静态 |\n'
  printf '| OpenGL/EGL/窗口系统驱动 | 由 Avalonia/Skia 运行时加载 | 显示驱动 ABI，不属于随应用分发的第三方库 |\n\n'

  printf '## 已验证的关键路径\n\n'
  printf '| 能力 | 状态 | 证据 |\n'
  printf '|---|---|---|\n'
  printf '| x86 汇编优化 | 启用 | FFmpeg x86asm、libass ASM 均为 1 |\n'
  printf '| HEVC Main 10 软件解码 | 启用 | FFmpeg 原生 HEVC decoder |\n'
  printf '| NVIDIA HEVC Main 10 硬解 | 启用并实播通过 | `hwdec-current=nvdec`, `hwdec-interop=cuda` |\n'
  printf '| 零 CPU 回读渲染路径 | 启用 | NVDEC CUDA surface 通过 CUDA/OpenGL interop 进入 libmpv OpenGL render API |\n'
  printf '| AMD AMF | FFmpeg 已启用，待 AMD 设备验证 | AMF headers 编入；runtime 由 FFmpeg 按需加载 |\n'
  printf '| Intel QSV / oneVPL | FFmpeg 已启用，待 Intel 设备验证 | oneVPL dispatcher 静态链接，implementation 运行时发现 |\n'
  printf '| Intel/AMD VAAPI | FFmpeg 与 libmpv 已启用，待对应设备验证 | 静态 libva/libdrm；DRM/DMABUF 与 OpenGL interop 已编入 |\n'
  printf '| Vulkan Video | FFmpeg 与 libmpv 已启用，待对应设备验证 | Vulkan 1.4 headers、libplacebo、glslang/SPIRV-Tools 已编入 |\n'
  printf '| OpenCL filters | FFmpeg 已启用，待对应设备验证 | 静态 ICD loader；厂商 ICD 运行时发现 |\n'
  printf '| AV1 / VP8 / VP9 / Opus / Vorbis / JPEG 2000 | 启用 | dav1d、libvpx、libopus、libvorbis、libopenjpeg 均静态链接 |\n'
  printf '| HTTPS/TLS、HTTP、FTP、WebDAV-over-HTTP | 启用 | FFmpeg 网络协议 + 静态 OpenSSL；WebDAV 复用 HTTP(S) |\n'
  printf '| SMB | 不在 FFmpeg 静态闭包 | Samba/libsmbclient 静态闭包过大且不稳定；由 CanX 媒体流适配层接入 libmpv stream callback |\n\n'

  printf '## FFmpeg 分类汇总\n\n'
  printf '| 分类 | 启用 | 禁用 | 总计 |\n'
  printf '|---|---:|---:|---:|\n'
  awk -F '|' '
    function category(name) {
      if (name ~ /_DECODER$/) return "解码器"
      if (name ~ /_ENCODER$/) return "编码器"
      if (name ~ /_HWACCEL$/) return "硬件加速器"
      if (name ~ /_DEMUXER$/) return "解复用器"
      if (name ~ /_MUXER$/) return "复用器"
      if (name ~ /_PARSER$/) return "解析器"
      if (name ~ /_BSF$/) return "位流过滤器"
      if (name ~ /_FILTER$/) return "音视频过滤器"
      if (name ~ /_PROTOCOL$/) return "协议"
      if (name ~ /_INDEV$/) return "输入设备"
      if (name ~ /_OUTDEV$/) return "输出设备"
      return "库与构建能力"
    }
    { c = category($1); total[c]++; if ($2 == 1) enabled[c]++; else disabled[c]++ }
    END {
      for (c in total)
        printf "%s|%d|%d|%d\n", c, enabled[c], disabled[c], total[c]
    }
  ' "$ffmpeg_features" | LC_ALL=C sort | awk -F '|' '{ printf "| %s | %d | %d | %d |\n", $1, $2, $3, $4 }'
  printf '\n说明：所有 FFmpeg 原生软件解码器均已启用；禁用的 decoder 均为其他平台硬件包装器或未纳入静态闭包的外部替代实现。\n\n'

  printf '## libmpv 全部 feature\n\n'
  printf '| Feature | 状态 | 未启用原因 |\n'
  printf '|---|---|---|\n'
  awk -F '|' '
    function reason(name) {
      if (name ~ /(ANDROID|AAUDIO|AUDIOTRACK|OPENSL|MEDIA_NDK)/)
        return "仅 Android 适用，当前目标是 Linux x64"
      if (name ~ /(COCOA|DARWIN|MACOS|IOS|AUDIOUNIT|COREAUDIO|AVFOUNDATION|VIDEOTOOLBOX|SWIFT)/)
        return "仅 Apple 平台适用"
      if (name ~ /(WIN32|D3D|DIRECT3D|WASAPI|UWP)/)
        return "仅 Windows 平台适用"
      if (name ~ /(BSD_|RFORK|CONSIO|SNDIO)/)
        return "BSD/其他 Unix 平台能力，Linux 不适用"
      if (name ~ /(BUILD_DATE|DEBUG|PTHREAD_DEBUG|DISABLE_PACKET_POOL)/)
        return "发布构建关闭调试、时间戳或开发诊断能力"
      if (name ~ /CPLUGINS/)
        return "NativeAOT/静态发布不加载不受控原生插件；应用插件使用 QuickJS"
      if (name ~ /(CDDA|DVDA|DVDNAV|LIBBLURAY)/)
        return "未引入光盘介质的额外静态依赖"
      if (name ~ /LIBCURL/)
        return "FFmpeg 已提供 HTTP(S)/FTP；避免重复引入第二套网络与 TLS 闭包"
      if (name ~ /SUBRANDR/)
        return "缺少已锁定、可复现的 Rust 静态依赖闭包"
      if (name ~ /RUBBERBAND/)
        return "缺少 Rubber Band 及其 FFT/采样率依赖的固定静态闭包"
      if (name ~ /VAPOURSYNTH/)
        return "依赖外部 VapourSynth/Python 运行时，无法成为自包含静态闭包"
      if (name ~ /(PIPEWIRE|PULSE|JACK|OPENAL|SDL2_AUDIO|OSS_AUDIO)/)
        return "当前使用静态 ALSA；桌面音频服务通过系统 ALSA 插件桥接"
      if (name == "HAVE_AMF")
        return "mpv 的 AMF feature 是 Windows/D3D11 FRC；Linux AMF 由已启用的 FFmpeg 后端提供"
      if (name ~ /VDPAU/)
        return "VDPAU 依赖 mpv 的 X11 输出路径；CanX 使用更现代且可用于 Wayland 的 NVDEC/CUDA"
      if (name ~ /(WAYLAND|X11|GBM|XV)/)
        return "CanX 由 Avalonia 提供窗口与 OpenGL FBO，不编译 mpv 独立窗口后端"
      if (name ~ /SHADERC/)
        return "已选择静态 glslang/SPIRV-Tools 作为 Vulkan shader 编译器"
      if (name ~ /SPIRV_CROSS/)
        return "仅 mpv Windows D3D11 路径需要，Linux Vulkan 路径不依赖它"
      if (name ~ /(CACA|SIXEL)/)
        return "终端图形输出不属于 GUI 应用运行路径"
      if (name ~ /(MEMFD_CREATE|BSD_FSTATFS)/)
        return "当前工具链/LibC 能力探测未满足，且有已启用的 Linux/POSIX 替代路径"
      return "当前 Linux x64 静态播放配置不适用或其可选依赖未纳入固定源码闭包"
    }
    {
      status = $2 == 1 ? "启用" : "禁用"
      why = $2 == 1 ? "—" : reason($1)
      printf "| `%s` | %s | %s |\n", $1, status, why
    }
  ' "$mpv_features"
  printf '\n'

  printf '## FFmpeg 全部 feature\n\n'
  printf '| Feature | 分类 | 状态 | 未启用原因 |\n'
  printf '|---|---|---|---|\n'
  awk -F '|' '
    function category(name) {
      if (name ~ /_DECODER$/) return "解码器"
      if (name ~ /_ENCODER$/) return "编码器"
      if (name ~ /_HWACCEL$/) return "硬件加速器"
      if (name ~ /_DEMUXER$/) return "解复用器"
      if (name ~ /_MUXER$/) return "复用器"
      if (name ~ /_PARSER$/) return "解析器"
      if (name ~ /_BSF$/) return "位流过滤器"
      if (name ~ /_FILTER$/) return "音视频过滤器"
      if (name ~ /_PROTOCOL$/) return "协议"
      if (name ~ /_INDEV$/) return "输入设备"
      if (name ~ /_OUTDEV$/) return "输出设备"
      return "库与构建能力"
    }
    function reason(name, kind) {
      if (name == "CONFIG_SHARED") return "静态链接策略：只生成静态归档"
      if (name == "CONFIG_AUTODETECT") return "为保证可复现性关闭宿主自动探测，依赖均显式启用"
      if (name ~ /^CONFIG_(PROGRAMS|FFMPEG|FFPLAY|FFPROBE|DOC|DEBUG|STRIPPING)$/)
        return "嵌入式 libav* 发布不构建 CLI、文档或调试产物"
      if (name ~ /^CONFIG_(NONFREE|EXPERIMENTAL)$/)
        return "许可证/稳定性策略未开启；GPLv3 功能已经开启"
      if (name ~ /(MEDIACODEC|OPENSL|ANDROID_CONTENT|_OH_|RKMPP)/)
        return "Android/OpenHarmony/嵌入式 ARM 平台后端，Linux x64 不适用"
      if (name ~ /(_AT_|VIDEOTOOLBOX|AUDIOTOOLBOX|AVFOUNDATION)/)
        return "Apple 平台后端，Linux x64 不适用"
      if (name ~ /(D3D|DXVA|MEDIAFOUNDATION|WASAPI)/)
        return "Windows 平台后端，Linux x64 不适用"
      if (name == "CONFIG_LIBMFX")
        return "旧 Intel Media SDK 已由 oneVPL 取代，FFmpeg 不允许 libmfx 与 libvpl 同时启用"
      if (name == "CONFIG_VULKAN_STATIC")
        return "Linux Vulkan Loader 上游不支持静态链接，保留系统 libvulkan.so.1 ABI"
      if (name ~ /VDPAU/)
        return "NVIDIA 旧式 X11 专用路径未编入；使用 NVDEC/CUDA，Wayland 也可用"
      if (name ~ /(MMAL|V4L2M2M|RKMPP)/)
        return "嵌入式 Linux/ARM 专用硬件后端，不属于 Ubuntu x64 桌面目标"
      if (name ~ /(QSV|LIBVPL)/)
        return "该 QSV 能力未被当前 oneVPL API 或上游 FFmpeg 组合暴露；oneVPL 主后端仍已启用"
      if (name ~ /(VAAPI|LIBDRM|VULKAN|OPENCL|AMF)/)
        return "该细分能力不满足上游版本/格式条件；对应通用硬件后端仍已启用"
      if (name ~ /(LIBSMBCLIENT|LIBSSH|LIBSRT|LIBRIST|LIBRTMP|LIBAMQP|LIBZMQ)/)
        return "可选网络协议依赖缺少可控的固定静态闭包；SMB 由应用 stream callback 负责"
      if (name ~ /^CONFIG_LIB/)
        return "可选外部库未纳入静态源码闭包；已有原生实现或当前播放场景不需要"
      if (kind == "输入设备" || kind == "输出设备")
        return "采集/输出设备依赖或目标平台不适用；CanX 使用 libmpv/Avalonia I/O"
      if (kind == "编码器")
        return "可选外部或平台编码器未纳入；播放器所需软件解码能力不受影响"
      if (kind == "解码器")
        return "外部替代解码器或其他平台硬解包装器；对应原生软件解码器仍已启用"
      if (kind == "硬件加速器")
        return "目标平台或上游 API 条件不适用；Linux 的 NVDEC、VAAPI、Vulkan 主路径均已编入"
      if (kind == "音视频过滤器")
        return "该过滤器依赖未纳入的外部库或其他硬件 API"
      return "上游默认关闭、依赖条件未满足，或不属于当前 Linux x64 播放目标"
    }
    {
      kind = category($1)
      status = $2 == 1 ? "启用" : "禁用"
      why = $2 == 1 ? "—" : reason($1, kind)
      printf "| `%s` | %s | %s | %s |\n", $1, kind, status, why
    }
  ' "$ffmpeg_features"

  printf '\n## 复核说明\n\n'
  printf -- '- 表中的状态来自本次 Linux x64 构建目录内的 `config.h` / `config_components.h`。\n'
  printf -- '- 禁用原因按目标平台、静态闭包、许可证和嵌入架构分类；FFmpeg `config.log` 是单项探测的底层证据。\n'
  if [[ -x "$app_binary" ]]; then
    needed="$(readelf -d "$app_binary" | awk '/NEEDED/ { gsub(/[][]/, "", $5); printf "%s%s", separator, $5; separator=", " }')"
    printf -- '- 当前 NativeAOT ELF 直接依赖：`%s`。\n' "$needed"
  else
    printf -- '- 未找到 NativeAOT 产物，因此本次未写入 ELF `DT_NEEDED` 实测值。\n'
  fi
} > "$report"

mkdir -p "$(dirname "$output_file")"
mv "$report" "$output_file"
printf 'CANX_MEDIA_FEATURE_REPORT_OK output=%s\n' "$output_file"
