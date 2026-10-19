# 阶段 5：插件平台验收报告

- 日期：2026-09-18
- 状态：功能与退出条件已完成
- 当前发布验证：Windows x64 / .NET 10 NativeAOT 静态单文件
- 后续边界：Agent Plugin Workspace 属于阶段 6；其他 RID、非信任插件进程隔离和商店渠道属于阶段 7

## 已完成范围

CanX 插件使用 JavaScript ES modules。每个已启用插件拥有独立 QuickJS runtime、context、
专用线程、内存限制和执行 deadline。插件不接触 Avalonia 对象、.NET 反射、libmpv 指针、
libaria2 session 或任意本机文件 API，只能经过冻结的版本化 SDK 请求宿主能力。

```text
.canxplugin 包
      |
      v
严格 Manifest / 签名与完整性 / 原子安装与单级回滚
      |
      v
持久化生命周期与权限审批
      |
      v
独立 QuickJS runtime + 冻结 SDK
      |
      +-- Tool policy --> 播放 / 下载
      +-- Service -----> JSON Schema 描述的跨插件 / AI 接口
      +-- HTTP --------> Manifest 域名 allowlist
      +-- Media Stream -> Header/HLS 改写 + Stream Callback / HLS loopback Gateway
      +-- Secrets -----> OS 凭证存储中的插件专属命名空间
      +-- Storage -----> 插件专属、限额、原子 JSON 存储
      +-- UI ----------> 固定 slot 和组件白名单
```

具体能力如下：

- Manifest v1 严格拒绝未知字段、非法 ID/SemVer/API 版本、非便携入口和非法 capability；
  未声明权限时默认无访问权。
- 本地 `.canxplugin` 安装会限制文件数量、单文件及总大小，拒绝路径穿越、符号链接、
  大小写冲突和缺失入口；升级使用同卷原子替换并保留一个可持久化的回滚版本。
- 包可使用 ECDSA P-256 签名；安装时验证清单中每个文件，记录 signer 与包哈希。安装、启停、
  重载、升级、回滚和卸载先持久化 `Started` 审计再提交状态，成功后补写 `Completed`；前置审计
  失败时关闭失败，状态落盘失败时恢复目录/runtime，完成审计失败只记日志，不把已提交操作报成失败。
- 新安装默认停用。用户批准后才启动；权限增加会自动停用并要求重新批准，单纯移除权限不会
  无故中断已启用插件。插件管理页覆盖安装/升级、权限 diff、启用、停用、重载、回滚与卸载。
- 包内模块在创建 runtime 前完成边界与配额检查，随后以内存模块加载；默认 QuickJS 环境不注入
  `std`、`os`、文件、进程、socket 或 native module 能力。
- `canx.tools` 只允许 Manifest 精确声明的 Tool scope；`downloads.add`、`downloads.addBatch` 和 `search.download` 即使被
  声明也始终拒绝，新增任务只能由直接用户手势调用 `downloads.prompt` 打开标准确认页。播放与下载
  SDK 只公开稳定、有限、非破坏性动作，并为状态观察提供限频、数量受限且可取消的订阅。
- `canx.http` 只接受 HTTP(S) 和批准的 host/wildcard，逐跳重新检查重定向，跨源时移除
  Authorization/Cookie，限制并发、超时、请求体、响应体和 Header。生产连接会解析全部地址，
  拒绝 loopback、私网、链路本地和保留地址，并直接连接已验证 IP，防止 DNS rebinding。插件可为
  只需页面开头的解析请求设置更小的响应上限并显式接受截断，避免大型 HTML 尾部进入 QuickJS。
- `canx.secrets` 只允许 Manifest 精确声明的秘密名称，数据进入操作系统凭证存储；平台存储
  不可用时关闭失败，不降级为明文。`canx.storage` 按插件 ID 隔离并限制键、值和总容量。
- `canx.media.stream` 把带 Header 的 HTTP(S) 文件和 HLS 播放列表映射为不透明 `canx://`
  句柄；所有重定向、子播放列表、分片、密钥和初始化片段同时受 recipe host 与
  Manifest `http` scope 限制。HLS 专用 loopback Gateway 将分片交给 FFmpeg 作为完整时间线读取，
  仅监听一个临时本机端口，全部 HLS 会话释放后关闭；普通文件仍使用 Stream Callback。
  直接播放还必须来自插件页面用户事件。
- 通用 Service 注册与插件生命周期绑定；contract、版本、用途、风险、受众和输入输出 JSON
  Schema 可统一发现，调用经过权限、载荷、深度、循环与 Schema 校验。搜索、字幕、媒体解析和
  下载发现不再使用宿主特判的专用接口类型；下载仍只能由用户通过标准确认页添加任务。
- 声明式 UI 固定支持导航、主页、下载详情、播放器侧栏、命令面板、菜单、设置和状态卡片 slot；
- 页面远程图片使用独立 `ui.remoteImages` 权限，支持精确域名、子域通配和经用户批准的 `*`；
  图片由宿主以无 Cookie/认证的 GET 拉取，逐跳执行公网地址检查，并限制为 2 MiB 的
  PNG/JPEG/WebP 后缓存，UI 控件本身不直接联网；
  只渲染设计系统白名单组件/属性，交互只能回调插件声明的 handler；响应式 `gallery` 可按页面
  宽度自动排列经过校验的封面卡片。
- 声明了 `ui.page` 的插件在桌面端获得独立导航页；所有声明式 UI slot 已挂载到实际页面或菜单。
- TypeScript SDK 提供类型、机器可读 API 契约、严格 Manifest schema、mock host，以及
  `init`、`lint`、确定性 `pack`、P-256 `keygen`。开发模式支持防抖热重载。
- 插件管理页的只读调试控制台仅显示 `quickjs.log`、其滚动备份和 `plugin-audit.jsonl` 的
  有界尾部，并支持手动重载；不提供可绕过权限审批的任意 JavaScript REPL。
- 示例包含 Internet Archive 视频搜索/解析、OpenSubtitles 字幕搜索、播放器滤镜/UI 扩展、
  通过 Python HTTP Mock API 返回 Magnet 的下载搜索、Bangumi 新番日历/搜索/详情/本机收藏，
  以及八个实现统一 BitTorrent 搜索 contract 的独立服务。第十四个 Anime Find 原生移植示例聚合
  Mikan、AniBT、AnimeGarden 和 Bangumi，提供声明式搜索/详情/下载/播放页面与 AI Service；其静态
  规则媒体通过受限 Header/HLS 重写和不透明 `canx://` 句柄播放，HLS 使用宿主专用 Gateway，不包含 DSH React ToolView。
  十四者均有 TypeScript 构建、mock-host 单元测试、lint 和可安装包验证。

完整 Manifest、SDK 与 UI 契约见 [插件 Manifest v1](PLUGIN-MANIFEST.zh-CN.md)。

## 退出条件

| 条件 | 实现与自动证据 | 结论 |
|---|---|---|
| 不能越权读取文件 | 无文件 API 或 native module；包路径、module 路径与 media descriptor 均做边界检查 | 通过 |
| 不能越权读取密钥 | secret 名称需精确 scope，按插件命名空间访问 OS 凭证存储，不可用时关闭失败 | 通过 |
| 不能越权访问网络 | HTTP host/wildcard 默认拒绝，重定向逐跳检查；解析地址拒绝非公网 IP 并固定连接，media URL 复用同一权限 | 通过 |
| 异常或死循环不冻结 UI | 每插件独立线程；真实 QuickJS 验证顶层/handler 异常、OOM、deadline 中断及后续恢复 | 通过 |
| API 契约有自动测试 | `contract.json` 对齐全部 capability、稳定动作、UI 组件和 slot；宿主、Schema、CLI 与 TypeScript 类型均只接受 API 2.0 | 通过 |

这里的内存上限和 deadline 是可用性及权限边界的一部分，不把同进程 native runtime 宣称为
操作系统级强沙箱。对完全不受信任社区插件增加辅助进程隔离，属于阶段 7 的纵深防御工作，
不改变当前默认拒绝、无原始主机能力的 SDK 边界。

## 可重复验证

Windows x64 的统一入口为：

```powershell
.\scripts\verify-plugin-platform-windows-x64.ps1 `
  -SkipNativeBuild `
  -CacheRoot .cache\windows-msvc
```

不传 `-SkipNativeBuild` 时会从固定源码和哈希重新构建静态 QuickJS。脚本依次执行：

- `CanX.Plugins.Tests`：208 项；
- `CanX.Foundation.Tests`：261 项，包括桌面生命周期、Service Tool 和只读诊断控制台；
- SDK lint、TypeScript contract typecheck 和 21 项 Node 测试；Mock Host 会执行与真实宿主一致的
  声明式 UI 组件、属性、长度、布局和资源边界校验；
- 十四个插件（9 个实际插件、5 个演示插件）的 lint、TypeScript 构建、24 项测试及确定性打包；下载演示插件测试会启动真实 Python HTTP Mock Server；
- 真实 QuickJS 生产 Host 的 NativeAOT 发布、PE 依赖审计和故障恢复 smoke。

本次结果：

```text
Passed: CanX.Plugins.Tests 208/208
Passed: CanX.Foundation.Tests 261/261
Node SDK: 21/21
Plugins: 24/24, packages=14
CANX_WINDOWS_NATIVE_IMPORTS_OK ... libraries=9 api_sets=0
CANX_QUICKJS_OK ... exception=... timeout=... memory=... recovery=42 host=ok isolation=ok,deadline_ms=167
CANX_PLUGIN_STAGE_OK managed=passed sdk=passed plugins=14 native=passed
```

## 完整应用发布验证

包含插件管理、Service 发现、声明式 UI 和调试控制台的完整应用已重新执行：

```powershell
.\scripts\publish-static-app-windows-x64.ps1 `
  -SkipNativeBuild `
  -CacheRoot .cache\windows-msvc
```

产物仅有一个 `CanX.App.exe`，QuickJS、aria2、mpv、FFmpeg、图形与编解码依赖均静态合入；
PE 剩余依赖均为 Windows 系统 ABI，没有动态 UCRT 或第三方 DLL。正式版不编入 smoke test，
PDB 单独放在 `artifacts/symbols/win-x64/`。如需运行下面的 shell smoke，另用
`-SmokeBuild` 参数构建验证版，输出到 `artifacts/verify/win-x64/`。

```text
CANX_WINDOWS_NATIVE_IMPORTS_OK ... libraries=30 api_sets=5
CANX_WINDOWS_STATIC_APP_OK ... symbols=... smoke=false bytes=... sha256=...
```

发布期间 Avalonia DataGrid、FluentFTP 和 SMBLibrary 仍会报告已知 trimming/AOT 分析警告；
它们没有阻止 NativeAOT 生成或依赖审计。其他桌面/移动 RID 的同类发布验收按阶段 7 执行。
