# GitHub CI 与发布

## 触发方式

| 事件 | 行为 |
|---|---|
| Pull Request、非 main 分支推送 | Windows x64、Linux x64 / ARM64 上运行 Foundation、Plugins、Agent 三组测试，上传 TRX |
| main 推送 | 测试通过后构建各平台，上传 Actions Artifacts |
| 推送 `v*` tag | 构建 tag 对应提交，上传 Artifacts；已有同名 Release 时追加附件 |
| 发布 Release，包括预发布版本 | 构建该 Release 的 tag，上传附件 |
| Actions 手动运行 `Build and publish` | 默认构建选择的 ref；可填写已有 tag，构建并更新它的 Release 附件 |

版本来自 tag（移除开头的 `v`），普通分支构建使用 `0.1.0-dev.<commit>`。
测试与发布使用同一个解析后的提交。手动 tag 仅允许字母、数字、点、下划线、加号与连字符，
并且必须是实际存在的 tag，而不是分支名。

Foundation 分为主体、图像解码、播放器 headless 三个独立测试进程，避免图像解码的全局 Skia / Avalonia
初始化与其他控件测试竞争线程归属。三次测试命令的范围互补，不跳过测试，TRX 一并上传。

Release 不会被自动创建。先推 tag、之后才创建 Release 也可以：发布 Release 事件会重新构建并上传。
草稿 Release 不触发 `published`，但可通过 tag 推送或手动构建为已有草稿补充附件。
由其他工作流使用 `GITHUB_TOKEN` 创建的 tag / Release 通常不会再次触发工作流；这种情况下应手动运行构建。

## 平台与产物

| 平台 | Runner | 附件 |
|---|---|---|
| Linux x64 | Ubuntu 24.04 runner 内的 Ubuntu 22.04 容器 | 应用、符号、完整对应源码、校验文件；沿用现有 SBOM / 许可证打包与审计 |
| Linux ARM64 | Ubuntu 24.04 ARM64 runner 内的原生 Ubuntu 22.04 ARM64 容器 | NativeAOT 应用、独立符号、完整对应源码与校验文件，同样执行 GLIBC 2.35 基线审计 |
| Windows x64 | Windows 2022 | NativeAOT 应用 ZIP、独立符号 ZIP、SHA-256 校验文件 |
| Windows ARM64 | Windows 2022 x64 runner，MSVC ARM64 交叉工具链 | NativeAOT 应用 ZIP、独立符号 ZIP、SHA-256 校验文件，校验 PE 的 ARM64 Machine 字段 |
| Android ARM64 / x64 | Ubuntu 24.04，两个 ABI 独立任务 | NativeAOT APK、独立符号归档、许可证、SHA-256 校验文件 |
| macOS ARM64 | 显式启用的专用自托管 runner | `.app` ZIP、独立符号归档、许可证、SHA-256 校验文件 |

每个平台的 Actions Artifact 名为 `release-<rid>`，保留 30 天；测试结果保留 14 天。
附件名称包含版本与平台，校验文件也按平台命名，下载后可在同目录执行 `sha256sum -c <校验文件>`。
macOS 可使用 `shasum -a 256 -c <校验文件>`。
Windows ZIP 自带主仓库 LICENSE / README；原生依赖完整发行合规材料仍应按对应平台的发布门槛审核。

Windows、Linux、Android 均使用独立架构矩阵和 `fail-fast: false`；某个平台失败时，已完成平台的包仍可上传到已有 Release，
但整个工作流仍会显示失败，不能把部分附件视为所有平台发布完成。重跑时同名附件会被替换，其他附件不变。

Windows / Linux ARM64 各自产出 `release-win-arm64` / `release-linux-arm64`，不与 x64 合包。
Linux ARM64 原生构建并运行托管测试，不使用 QEMU。Windows ARM64 在 x64 runner 上交叉编译，
不会声称已在 Windows ARM64 设备完成启动、播放或硬解验收。
ARM64 不强制链接 Intel QSV / oneVPL，也不启用这套 x64 发行配置中的 CUDA / AMF / NVENC；
Linux 保留 VAAPI、Vulkan、OpenCL 和 V4L2 M2M 构建路径，Windows 保留 D3D 与 Vulkan 等通用路径，
实际硬解仍取决于设备及驱动。Windows ARM64 使用 Lua，不使用 LuaJIT。

## macOS 准备

当前 macOS 打包脚本不是干净主机的一键构建入口，也没有 NativeAOT 发布验收。
因此默认跳过 macOS，而不是在 GitHub 托管 runner 上生成缺少原生依赖的应用包。

启用前需要：

1. 注册专用 ARM64 macOS runner，标签包含 `self-hosted`、`macOS`、`ARM64`、`canx-release`。
2. 按 [手动编译说明](../README.md#macos-arm64) 和 [打包脚本](../scripts/package-app-macos.sh)
   准备 Xcode 命令行工具、IINA Frameworks、QuickJS / aria2 shim、libplacebo 及 Homebrew 依赖。
   仓库本地的 `.cache` 输入需要位于 runner 实际检出的工作目录；脚本支持的外部输入可通过 runner 环境变量配置。
3. 在仓库 Settings → Secrets and variables → Actions → Variables 中设置 `CANX_MACOS_CI_ENABLED=true`。

该 runner 只由可信的 main / tag / Release / 手动发布流程调用，PR 测试不会使用它。
checkout 保留未跟踪缓存以复用原生输入，因此必须使用专用、可信的工作目录，不要留有手动修改的源码。
macOS 包沿用脚本的 ad-hoc 签名，不是 Developer ID 签名或 Apple 公证包。
iOS 当前只有验证项目，没有完整应用发布入口，未加入发布矩阵。

## 权限与缓存

构建与测试默认只有 `contents: read`。仅最后的附件上传任务具有 `contents: write`，
使用内置 `GITHUB_TOKEN`，无需 PAT。仓库或组织策略必须允许工作流使用该权限。
上传前查询对应 Release：不存在时保留 Actions Artifacts，其他 API 错误则让任务失败。
不会对 PR 使用 `pull_request_target`，也不会向 PR 任务提供发布凭据。

原生依赖使用按平台、ABI、锁文件、补丁和构建脚本区分的 Actions Cache。
第一次构建需要下载和编译大量依赖，可能接近托管 runner 的六小时上限；缓存容量不足会被 GitHub 淘汰。
工具链或 runner 镜像重大变化后，应更新工作流中的缓存版本或删除缓存，避免复用不兼容的构建目录。
Linux 两种架构的发行基线均由 Ubuntu 22.04 容器和 GLIBC 版本审计保证。
容器为不同架构下载各自固定 SHA-512 的 .NET SDK；x64 继续使用 Ubuntu snapshot，
ARM64 沿用 Ubuntu 22.04 的 ports 软件源，系统构建工具并非同日期快照。
保留原有 `*-x64` 脚本与 Linux 锁文件名以兼容现有入口，Windows 用 `-Architecture arm64`，
Linux 脚本按本机架构选择目标；Linux 可设置 `CANX_LINUX_ARCHITECTURE` 进行校验，但不支持跨架构构建。

Android 沿用现有发布脚本的 SDK / workload / NDK 安装与签名行为，并接受 Android SDK 许可证。
没有配置稳定发布密钥时，托管 runner 的签名密钥不保证跨构建一致，APK 不应作为可持续覆盖升级的正式签名发行版。
正式分发前应另行配置受保护的稳定 Android keystore；本流程不将签名私钥上传到 Artifact 或缓存。

## 工作流检查

提交工作流修改前可在仓库根目录运行 `actionlint`。
[actionlint 配置](../.github/actionlint.yaml) 声明了 macOS 的自定义 runner 标签。
本地静态检查不能代替 GitHub 托管 runner 首次全量原生构建；磁盘空间、下载可用性、缓存命中和平台签名仍需在实际运行中确认。