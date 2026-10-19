# CanX

CanX 是一个跨平台、可插件化、Agent-first 的视频播放器与下载工具。

计划已经确认，Linux x64 的阶段 2 下载中心已完成并通过 NativeAOT、协议、故障恢复、无障碍与缩放验收。阶段 3 已完成正式 libmpv Render API surface、本地及 HTTP/HTTPS 播放、独立播放窗口、完整默认键鼠输入、常用手势、轨道与字幕、播放列表与历史、章节与书签、高级播放控制、硬解/色彩/HDR/性能诊断、受限的运行时 property/command，以及从实际 libmpv 构建发现的版本化能力清单与零缺口覆盖测试；这些能力及对应 `playback.*` Tool 已通过 Wayland/X11 真实播放。Linux x64 还新增了可重复的 120 秒稳定性测试，覆盖连续呈现、周期 seek、窗口隐藏/恢复和 Render Context 重建；实际多显示器与系统休眠唤醒仍需在对应硬件环境验收。Android ARM64/x64 完整 NativeAOT APK 已构建，ARM64 已在 Android 16 真机完成启动、导航、内嵌对话框、模块初始化和 Activity 重建 smoke；x64 运行仍待模拟器。iOS native link/build 仍等待 macOS/Xcode 主机。

阶段 5 插件平台和阶段 6 Agent 已完成：内置 109 个受策略和审计保护的 Tool，支持自定义
OpenAI-compatible 网关、会话/预算/取消、聚合视频与字幕搜索，以及由 AI 在隔离 Workspace 中创建、
检查、试运行和经用户审核后安装插件。高风险操作仍由宿主确认，API Key 只进入系统凭据存储。
阶段 7 已进入 Linux x64 发布收尾：NativeAOT 二进制包、独立符号包、完整对应源码、SPDX 2.3
SBOM、第三方许可证 bundle 与 Ubuntu 22.04 CI 已落地；正式 glibc 2.35 容器首跑仍待完成。

- [产品与实施计划](docs/PLAN.zh-CN.md)
- [阶段 0 验证报告](docs/STAGE-0-REPORT.zh-CN.md)
- [阶段 2 下载能力与覆盖状态](docs/STAGE-2-DOWNLOADS.zh-CN.md)
- [阶段 3 播放器实现状态](docs/STAGE-3-PLAYBACK.zh-CN.md)
- [阶段 5 插件平台验收报告](docs/STAGE-5-PLUGINS.zh-CN.md)
- [插件目录与构建](plugins/README.md)
- [阶段 6 Agent 与 Tool 平台验收报告](docs/STAGE-6-AGENT.zh-CN.md)
- [阶段 7 Linux x64 发布工程](docs/STAGE-7-RELEASE.zh-CN.md)
- [阶段 7 Android 实现与验证](docs/STAGE-7-ANDROID.zh-CN.md)
- [AI 可用的全部 Tool 与调用参考](docs/AI-TOOLS.zh-CN.md)
- [Linux x64 FFmpeg / libmpv 功能矩阵](docs/MPV-FFMPEG-FEATURES-linux-x64.md)
- [mpv Property / Command 能力覆盖报告](docs/MPV-CAPABILITY-COVERAGE.zh-CN.md)
- [aria2 Option 作用域覆盖报告](docs/ARIA2-OPTION-COVERAGE.zh-CN.md)
- [原生依赖静态链接决策](docs/adr/0006-static-native-linking.md)

## 编译与发布

以下命令均在仓库根目录执行。桌面构建需要 `global.json` 指定的 .NET SDK 10.0.400，
也可使用同一 feature band 的更新补丁版本，例如 10.0.401。首次构建需要联网下载 NuGet 包和
锁定版本的原生源码，并预留足够的磁盘空间与编译时间。

正式发布优先使用下列脚本，而不是只执行 `dotnet publish`：脚本负责原生依赖链接、
产物审计和调试符号分离。跳过原生构建的参数只适用于对应平台、ABI 的缓存已完整生成，
且原生源码、补丁、构建配置及工具链未发生变化的情况。

| 目标 | 构建主机 | 当前发布方式 |
|---|---|---|
| Windows x64 | Windows x64 | NativeAOT 静态单文件 EXE |
| Linux x64 | Linux x64；Windows 可使用 WSL2 Linux 环境 | NativeAOT 静态主程序，正式发行基线为 Ubuntu 22.04 / glibc 2.35 |
| Android ARM64 / x64 | Linux 或 macOS；Windows 可使用 WSL2 Linux 环境 | NativeAOT APK；Android NativeAOT 仍为 experimental |
| macOS ARM64 | macOS ARM64 | 自包含 `.app`，当前打包脚本关闭 NativeAOT |
| iOS 设备 / 模拟器 | macOS + Xcode | 仅 NativeAOT 验证项目，尚无完整应用发布流水线 |

其他 RID 尚无完整发布入口，不能仅替换 `--runtime` 就视为受支持的发布方式。

### Windows x64

前置依赖：

- .NET SDK、Git 和 Visual Studio 2022。
- Visual Studio 的“使用 C++ 的桌面开发”、Windows SDK、CMake，以及 LLVM/Clang 组件。
- MSYS2，用于 aria2 的 Autotools/Make；默认检测 `C:\msys64`、`E:\msys64`、`D:\msys64`。

建议在 Visual Studio 的 Developer PowerShell 中执行：

```powershell
# 首次：构建全部原生依赖并发布
.\scripts\publish-static-app-windows-x64.ps1

# 后续：复用 .cache\windows-msvc 下的原生库
.\scripts\publish-static-app-windows-x64.ps1 -SkipNativeBuild
```

若 MSYS2 位于其他目录，先单独构建原生库，再发布：

```powershell
.\scripts\build-static-native-windows-x64.ps1 -Msys2Root 'F:\msys64'
.\scripts\publish-static-app-windows-x64.ps1 -SkipNativeBuild
```

正式产物为 `artifacts/publish/win-x64/CanX.App.exe`，不需要安装 .NET 运行时。
PDB 单独输出到 `artifacts/symbols/win-x64/`。脚本会执行 PE 原生依赖审计；看到
`CANX_WINDOWS_STATIC_APP_OK` 表示发布完成。使用自定义缓存时，构建和发布均需传入相同的
`-CacheRoot`。

需要构建并运行 shell smoke 验证版时使用：

```powershell
.\scripts\publish-static-app-windows-x64.ps1 -SkipNativeBuild -SmokeBuild
```

验证版输出到 `artifacts/verify/win-x64/`，符号位于
`artifacts/verify/win-x64-symbols/`，不会覆盖正式产物。

### Linux x64

#### 本机开发

普通托管构建会自动从固定源码生成开发用 `libcanx_aria2.so` 和 `libmpv.so`，并复制到应用输出目录：

```bash
dotnet build src/CanX.App/CanX.App.csproj -c Release
dotnet src/CanX.App/bin/Release/net10.0/linux-x64/CanX.App.dll
```

也可以直接使用 `dotnet run --project src/CanX.App/CanX.App.csproj -c Release`。首次构建需要生成固定版本的 aria2、mpv 及其原生依赖，后续构建为增量构建。开发 sidecar 内部静态包含两套引擎及其可控依赖；正式发行仍由 `scripts/publish-static-app-linux-x64.sh` 将相同 archive 直接合入 NativeAOT 可执行文件，不携带这些 sidecar。

#### NativeAOT 发布

正式发行需要 Linux x64 环境中的 Docker 或 Podman。容器会准备固定工具链和构建依赖，
以 Ubuntu 22.04 / glibc 2.35 为最低基线；Windows 用户可在 WSL2 中运行并确保容器引擎可用：

```bash
# 首次：构建容器、原生依赖和 NativeAOT 主程序
scripts/run-ubuntu-22.04-linux-x64.sh

# 后续：复用同一容器环境生成的原生缓存
scripts/run-ubuntu-22.04-linux-x64.sh \
	scripts/publish-static-app-linux-x64.sh --skip-build
```

程序输出到 `artifacts/publish/linux-x64/CanX.App`，符号位于
`artifacts/symbols/linux-x64/`。开发机上也可直接运行
`scripts/publish-static-app-linux-x64.sh`，但需要自行安装原生构建依赖，且较新的 glibc
可能使产物无法在 Ubuntu 22.04 运行，不能代替正式基线验收。

在正式构建环境中生成并校验带许可证、SBOM、符号和对应源码的发布候选：

```bash
scripts/run-ubuntu-22.04-linux-x64.sh bash -lc '
	set -euo pipefail
	scripts/package-release-linux-x64.sh --version 0.1.0
	scripts/verify-release-package-linux-x64.sh --version 0.1.0
'
```

打包要求干净的 Git 工作区且同版本归档不存在，输出到 `artifacts/release/linux-x64/`。
完整验收命令和待完成的发布门槛见 [Linux x64 发布工程](docs/STAGE-7-RELEASE.zh-CN.md)。

需要运行应用内 smoke 的验证版时，在同一构建环境中执行
`CANX_INCLUDE_SMOKE_TESTS=true scripts/publish-static-app-linux-x64.sh --skip-build`。
程序输出到 `artifacts/verify/linux-x64/`，符号位于
`artifacts/verify/linux-x64-symbols/`，不能用作正式发布包的输入。

FFmpeg/libmpv 构建不依赖本机 GPU 厂商，同时包含 NVIDIA NVDEC/CUDA、Intel QSV/oneVPL、Intel/AMD VAAPI、AMD AMF 与 Vulkan Video 能力；应用打开媒体时按目标机器的设备与驱动探测，失败后回退软件解码。

### Android ARM64 / x64

使用 Linux 或 macOS 的 Bash 环境。首次需要 Git、curl、Python 3/pip、CMake、Ninja、Make、
Perl、pkg-config、Autotools（包括 autoreconf）及 tar/xz 等工具。先准备锁定源码和 Meson：

```bash
scripts/prepare-android-sources.sh
python3 -m pip install --target .cache/python-tools meson==1.9.1
```

发布脚本会通过 `prepare-android-workload.sh` 自动准备仓库本地的 .NET SDK、Android workload、
Android SDK、JDK 和 NDK；准备过程会接受 Android SDK 许可证。按所需 ABI 执行：

```bash
# ARM64 真机
scripts/publish-static-app-android.sh arm64-v8a

# x64 模拟器
scripts/publish-static-app-android.sh x86_64

# 后续：复用对应 ABI 的原生缓存
scripts/publish-static-app-android.sh arm64-v8a --skip-native
scripts/publish-static-app-android.sh x86_64 --skip-native
```

APK 分别为 `artifacts/android/android-arm64-full/org.canx.app-Signed.apk` 和
`artifacts/android/android-x64-full/org.canx.app-Signed.apk`。对应的 NativeAOT
调试符号放在 `artifacts/symbols/android-arm64-full/` 或 `artifacts/symbols/android-x64-full/`，不进入 APK。
安装到手机时运行 `scripts/install-app-android.sh arm64-v8a`。不要安装普通 `dotnet build`
写入 `src/CanX.App.Android/bin/` 的 APK：它没有链接 FFmpeg/mpv 原生库，启动时会因找不到
`avcodec` 而退出。

需要 ADB `run-as` 等诊断能力时，可单独生成可调试验证包：

```bash
CANX_ANDROID_DEBUGGABLE=true scripts/publish-static-app-android.sh arm64-v8a --skip-native
```

它输出到 `artifacts/android/android-arm64-debuggable/`，不覆盖正式包。
ARM64 真机与 x64 模拟器的验证边界见 [Android 实现与验证](docs/STAGE-7-ANDROID.zh-CN.md)。

### macOS ARM64

当前 [macOS 打包脚本](scripts/package-app-macos.sh) 使用 `PublishAot=false`，生成自包含
`.app`，不是 NativeAOT 发布。它不是干净环境的一键构建入口：需要先准备 Xcode 命令行工具、
QuickJS/aria2 动态库、IINA Frameworks，以及脚本引用的 Homebrew SQLite、libssh2、c-ares、
OpenSSL 依赖。默认路径和覆盖变量见脚本开头。

上述输入齐全后，使用已安装的 .NET SDK 打包：

```bash
CANX_DOTNET="$(command -v dotnet)" scripts/package-app-macos.sh
```

应用输出到 `artifacts/macos/CanX.app`，PDB 单独放在 `artifacts/symbols/osx-arm64/`。
不能仅把 `PublishAot` 改为 `true` 就认为完成了 macOS NativeAOT 发布验收。

### iOS

目前仅有 `spikes/CanX.Spikes.Mobile.iOS` 验证项目，完整 CanX 应用尚无 iOS 发布入口。
[iOS 验证脚本](scripts/verify-ios-aot.sh) 设计为在安装匹配 Xcode 的 macOS 主机上执行：

```bash
scripts/verify-ios-aot.sh
```

该脚本尝试准备 .NET/iOS workload，并发布 `ios-arm64`、`iossimulator-arm64` 和
`iossimulator-x64` 验证产物到 `artifacts/spikes/ios-aot/`。当前 native link/build 仍待
macOS/Xcode 主机实际验收；脚本关闭代码签名，不产生可直接安装的完整应用发布包。
真机安装还需要自己的 Apple 开发签名配置。

## 许可证

CanX 采用 [GNU General Public License v3.0 or later](LICENSE)。iOS 默认提供构建说明及可重签名产物，由用户使用自己的 Apple ID 或开发者证书签名和侧载；App Store 支持留到最终发布阶段另行评估。
