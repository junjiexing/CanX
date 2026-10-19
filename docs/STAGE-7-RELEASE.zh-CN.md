# 阶段 7：Linux x64 发布工程

- 日期：2026-09-15
- 状态：发布工程已完成；Ubuntu 22.04 正式 CI 首跑与外部人工矩阵待完成
- 目标：Linux x64、Ubuntu 22.04 / glibc 2.35 或更新版本
- 分发许可：GPL-3.0-or-later

## 已完成范围

Linux x64 现在具备从固定源码构建、审计、打包和校验无签名发布候选的完整流水线。正式构建在
Ubuntu 22.04 容器中执行，而不是把开发机 Ubuntu 26.04 产物直接发布。aria2、mpv、FFmpeg、
QuickJS、SkiaSharp native 与可控 codec/渲染依赖均静态合入 NativeAOT ELF；运行时只保留 glibc、
loader、数学库，以及 GPU 驱动 ABI 所需的 `libEGL.so.1` 和 `libvulkan.so.1` 动态边界。

发布元数据由已解析的 NuGet graph、固定 native lock 与 QuickJS lock 生成，包括 SPDX 2.3 SBOM、
第三方声明、许可证文件和对应源码说明。对应源码包同时包含 CanX 的确定 commit、构建脚本、两份
source lock，以及实际用于构建的完整 native/QuickJS 源码树；Git 元数据不会进入分发包。原生 Git
源码必须匹配 lock 中的 commit，QuickJS 的下载包 SHA-256 与展开源码必须同时匹配 lock。

构建基础也固定到不可变输入：GitHub Actions 使用完整 commit SHA；runner 固定 `ubuntu-24.04`；容器
基础镜像固定 Ubuntu 22.04 digest，APT 使用带时间戳的 Ubuntu snapshot 并关闭历史索引的
`Valid-Until` 检查；.NET SDK 10.0.400 从官方 tarball 安装并校验 SHA-512。Dockerfile 不依赖不存在的
`.NET 10 jammy` SDK tag，因此 tag 漂移或后续删除不会静默改变工具链。

## 发布产物

`artifacts/release/linux-x64/` 中一个版本应包含：

| 文件 | 内容 |
|---|---|
| `CanX-<version>-linux-x64.tar.gz` | NativeAOT 可执行文件、GPL、SBOM、第三方声明、许可证 bundle、对应源码说明 |
| `CanX-<version>-linux-x64-symbols.tar.gz` | 独立 `.dbg` / `.pdb` 调试符号 |
| `CanX-<version>-linux-x64-source.tar.gz` | CanX 与该二进制对应的完整原生源码、lock 和构建配方 |
| `SHA256SUMS` | 上述三个归档的 SHA-256 |

正式 publish 默认不编入应用内 smoke test；构建时将 `.dbg` / `.pdb` 移到
`artifacts/symbols/linux-x64/`，发布归档从该目录单独取符号。需要运行应用内 smoke 时使用
`CANX_INCLUDE_SMOKE_TESTS=true scripts/publish-static-app-linux-x64.sh`，验证版写入
`artifacts/verify/linux-x64/`，不能作为正式包的输入。

归档固定成员排序、owner、时间戳与 gzip header；在输入 commit、锁定源码和工具链一致时可重复生成。
文件先在临时目录完成，再原子移动到目标路径，失败不会留下同名半包。

## 正式构建与验证

在装有 Docker 或 Podman 的主机上运行：

```bash
scripts/run-ubuntu-22.04-linux-x64.sh bash -lc '
  set -euo pipefail
  dotnet test tests/CanX.Foundation.Tests/CanX.Foundation.Tests.csproj -c Release
  dotnet test tests/CanX.Plugins.Tests/CanX.Plugins.Tests.csproj -c Release
  dotnet test tests/CanX.Agent.Tests/CanX.Agent.Tests.csproj -c Release
  scripts/publish-static-app-linux-x64.sh
  scripts/package-release-linux-x64.sh --version 0.1.0
  scripts/verify-release-package-linux-x64.sh --version 0.1.0
'
```

也可由 GitHub Actions 的 `Linux x64 release candidate` workflow 手工输入版本，或推送 `v*` tag
触发。CI 缓存固定 native source/build 输出，在 Ubuntu 22.04 容器内完成测试、NativeAOT、动态依赖
审计、GLIBC 2.35 上限检查、打包和归档内容校验，最后只上传 30 天保留的 unsigned artifact；不会
自动创建 Release、push、签名或发布。

发布前必须看到：

```text
CANX_NATIVE_DEPENDENCY_AUDIT_OK ... sidecars=0
CANX_GLIBC_BASELINE_OK ... maximum=2.35
CANX_STATIC_APP_PUBLISH_OK platform=linux-x64
CANX_RELEASE_METADATA_OK ...
CANX_CORRESPONDING_SOURCE_OK ...
CANX_RELEASE_PACKAGE_OK ...
CANX_RELEASE_PACKAGE_VERIFY_OK ...
```

开发机可用 `CANX_ALLOW_NEWER_GLIBC=1` 验证链接、启动和打包逻辑，但这类 ELF 当前会要求
GLIBC 2.43，不能作为 Ubuntu 22.04 正式产物。`--skip-source` 也只用于快速诊断，不允许用于分发。

## 当前验证结果与剩余门槛

此前本机验证覆盖：三个 .NET 测试项目、Linux x64 静态 NativeAOT publish、无 native sidecar、应用
`--smoke`（109 个 Tool）、下载 smoke、发布元数据生成、三个归档的 checksum/gzip/成员结构，以及
对应源码归档不含 `.git`。同一输入连续生成的两个源码包已验证为逐字节一致。
拆分正式版与 smoke 验证版后的 Linux 发布流水线仍需在 Linux CI 上复验。
完整源码包约 1.05 GiB；实际大小随固定源码版本变化。

本机没有 Docker/Podman，因此以下项目仍明确保留为发布阻塞项，不能视为已通过：

- 首次 Ubuntu 22.04 CI/容器全量构建，以及 ELF 的 GLIBC 2.35 检查；
- Wayland/X11 多显示器、不同 DPI 和系统休眠/唤醒；
- 真实 WebDAV、FTP/FTPS、Samba 与 Windows SMB 服务端矩阵及断网/切网；
- 不同 NVIDIA/Intel/AMD 机器上的硬解、零拷贝路径、HDR 和驱动回退。

上述 Linux x64 门槛通过后，再按计划推进 Windows x64，而不是并行扩大平台变量。
