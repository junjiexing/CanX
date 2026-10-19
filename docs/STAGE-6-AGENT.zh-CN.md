# 阶段 6：Agent 与 Tool 平台验收报告

- 日期：2026-09-15
- 状态：功能与退出条件已完成
- 当前发布验证：Linux x64 / .NET 10 NativeAOT 静态单文件
- 后续边界：其他桌面与移动 RID、发行签名及系统集成属于阶段 7

## 已完成范围

CanX 现在使用 provider-neutral `IAgentChatProvider` 驱动有界单 Agent 循环。当前内置
OpenAI-compatible 实现允许设置任意公司网关或本地兼容服务的 Base URL、模型、温度和输出上限，
不固定官方地址。远程 Base URL 必须使用 HTTPS；HTTP 只允许 loopback 上的本地模型服务。API Key 仅由
系统凭据存储在发请求时读取，不写入设置、会话、Tool 输出或审计日志。

桌面端提供会话列表、流式回答、停止按钮、Token/轮次/Tool 数量状态和逐次操作确认。模型只能看到
宿主注册的 Tool；只读 Tool 可直接运行，网络、本地写入和破坏性 Tool 默认每次都由宿主确认。调用
经过 JSON Schema、超时、输出大小、幂等和审计边界。网络与破坏性 Tool 在执行前必须持久化
`Started` 记录，审计不可用时不执行；提交后的 `Completed` 审计失败仅记入应用日志。网页、字幕、媒体元数据、插件返回及 Tool 返回
统一标记为不可信内容。持久化文件中的伪造 system/tool 消息不会替换宿主策略。

桌面端和 Android 端的 Agent 设置提供“显示工具调用记录”（默认关闭）和“自动批准工具调用
（YOLO 模式）”（默认关闭）。前者只控制聊天界面显示，完整工具记录仍随会话保存，开启后可再次查看；
后者从下一次发送消息起跳过工具调用的人工确认，包括写入和删除操作，其他校验与审计仍然生效。
YOLO 模式只能由用户在设置中修改。API Key 输入后点击“保存设置”即写入系统凭据存储，
无需单独保存；留空保留原 Key，删除仍通过“删除 Key”执行。

## Tool coverage

`diagnostics.tools` 在运行时返回完整、机器可读的 coverage：Schema 版本、总数、namespace/risk 统计、
每个 Tool 的说明、输入/输出 Schema、风险、是否需要确认、可取消性和 dry-run 能力。当前共注册 109 个：

逐项参数、调用顺序和作用见 [AI 可用的全部 Tool 与调用参考](AI-TOOLS.zh-CN.md)。

| namespace | 数量 | 覆盖的用户能力 |
|---|---:|---|
| `downloads.*` | 15 | 任务列表/详情、添加/批量添加、暂停/继续/删除、队列、任务与全局选项、连接、计划及完成动作 |
| `playback.*` | 56 | 打开文件/URL/文件夹、播放列表、播放控制、seek/逐帧/A-B、轨道/字幕、音频设备、滤镜/shader、截图、章节、书签、历史、属性/命令、能力与性能诊断 |
| `media.*` | 5 | 新增匿名/本地来源、删除来源，并仅通过 opaque handle 列出、浏览和播放本地/SMB/FTP/WebDAV 内容 |
| `search.*` | 5 | 聚合搜索视频、字幕和 Torrent，并用缓存结果 ID 播放或下载 |
| `services.*` | 3 | 发现、描述和调用允许 AI 使用的插件 Service |
| `plugins.*` | 17 | 插件列表/启停/重载/回滚/卸载，以及 Workspace 创建、文件编辑、diff、固定检查、审核安装和丢弃 |
| `settings.*` | 2 | 读取及修改允许自动化的通用、Agent 和播放设置；aria2 全局设置由 `downloads.global.*` 覆盖 |
| `diagnostics.*` | 3 | 脱敏运行状态、分组件日志和本 coverage 报告 |
| `ui.*` | 3 | 有限页面导航、通知和当前页面上下文 |

以下属于宿主安全控制面，不向模型暴露为 Tool：操作系统文件/目录选择器；写入或删除 LLM API Key、
网络密码和插件 secret；批准当前 Tool；停止/删除 Agent 会话；插件私钥签名和公共发布。用户仍可从 UI
执行这些操作。这样不会让模型通过“调用负责批准自己的 Tool”或凭证 Tool 绕过权限边界。

## 搜索到播放/下载闭环

搜索只调用 Manifest 明确声明为 AI audience 的版本化 Service。宿主并行查询、规范化、去重和评分，
把标题、来源、语言、格式、大小等可展示信息返回给模型；解析器私有参数、真实媒体 URL、Magnet 和
字幕 URL 保留在有过期时间的宿主缓存中。后续 `search.play` / `search.download` 只能提交搜索 ID 和
结果 ID，默认再次经过网络操作确认；用户开启 YOLO 模式后自动批准这些调用。

## Plugin Workspace

AI 不直接改已安装插件，而是在应用数据目录下的随机 ID 工作区中操作：

1. 新建模板或复制一个已安装插件；
2. 只读写白名单文本类型，单文件最多 1 MiB，总计最多 16 MiB/1024 文件；
3. canonical path 校验拒绝绝对路径、`..`、隐藏路径、符号链接、reparse point 和 `node_modules`；
4. `lint/build/test/pack` 是固定命令，没有 shell 或 `package.json` script 入口；TypeScript 只由可信
   compiler 的 `transpileModule` 转译，外部进程使用绝对路径、清空环境、超时后终止整个进程树；
5. 试运行使用独立 QuickJS runtime，限制 16 MiB 内存、512 KiB 栈和 250 ms 执行时间，禁用真实
   Tool、网络与凭据；
6. `prepareInstall` 生成源码 diff、Manifest 权限、固定检查结果、工作区 digest 和安装包 SHA-256；
7. 宿主从自身状态生成确认 UI，不信任模型对权限或测试结果的复述；文件或包发生任何变化都会使
   review token 失效；确认后使用阶段 5 的原子安装，并保留可调用 `plugins.rollback` 的旧版本。

## 红队与故障验证

| 场景 | 防线与结果 |
|---|---|
| Tool/搜索结果中的 prompt injection | 内容带 `untrusted` 标记；默认仍需宿主确认，模型不能自行开启 YOLO 模式 |
| 伪造持久化 system/tool 历史 | Runner 丢弃，仅注入编译进宿主的 system policy |
| 恶意参数、路径穿越、额外字段、异常大数字 | Schema pattern/range/property 限制及领域二次校验，在 handler 前拒绝 |
| API Key、密码、Authorization/Cookie、URL user-info/敏感 query | API Key 只进 HTTP Authorization header；结果递归脱敏；审计不记录参数；日志写入时脱敏 |
| 非法 Provider 输出或无限调用 | 执行前拒绝负 Token、重复 Tool call ID、非法角色、过大参数及整批超预算调用；另有最大轮次、Tool 数、Token 和总时长限制 |
| 超大网关/插件请求与响应 | 网关响应上限 16 MiB；Tool 和插件请求上限 1 MiB，并限制结构深度、输出大小和执行超时 |
| 用户取消、Provider/Tool 超时 | CancellationToken 贯穿 Provider、Tool 与 UI，停止后不再继续执行 |
| 插件伪造用户确认 | 插件调用使用独立主体和精确 grant，不继承 UI 或 Agent 的 `HasUserApproval` |
| Workspace symlink/traversal/陈旧审核/包篡改 | 全部拒绝；安装同时绑定工作区 digest、review token 和 package SHA-256 |

## 可重复验证

```bash
dotnet test tests/CanX.Foundation.Tests/CanX.Foundation.Tests.csproj -c Release --no-restore
dotnet test tests/CanX.Plugins.Tests/CanX.Plugins.Tests.csproj -c Release --no-restore
dotnet test tests/CanX.Agent.Tests/CanX.Agent.Tests.csproj -c Release --no-restore

cd sdk/canx-plugin-api
npm test
npm run typecheck
npm run lint

cd ../..
for plugin in plugins/* plugins/example/*; do
  [ -f "$plugin/package.json" ] || continue
  (cd "$plugin" && npm ci --ignore-scripts && npm run build && npm run lint && npm test)
done

CANX_ALLOW_NEWER_GLIBC=1 scripts/publish-static-app-linux-x64.sh --skip-build
```

本次结果：

```text
Passed: CanX.Foundation.Tests 261/261
Passed: CanX.Plugins.Tests 208/208
Passed: CanX.Agent.Tests 19/19
Node SDK: 21/21, typecheck=passed, lint=passed
Plugins: 24/24, lint/build=14/14
CANX_NATIVE_DEPENDENCY_AUDIT_OK ... needed=5 sidecars=0
CANX_STATIC_APP_PUBLISH_OK platform=linux-x64
```

Linux 发布构建使用了已存在的 native archive，并显式允许当前开发机高于 Ubuntu 22.04 的 glibc；
Ubuntu 22.04 基线应继续由固定容器/CI 校验。NativeAOT 仅剩 Avalonia DataGrid、SMBLibrary 与
FluentFTP 的既有第三方 trim/AOT 分析警告，本阶段新增代码没有分析警告。真实公司 LLM 网关测试需要
用户本机凭据并会产生外部请求，因此自动验收使用了记录请求的假网关，验证了自定义 Base URL、SSE
Tool call 重组、Authorization header 隔离、响应上限和取消。

## 退出条件

| 条件 | 结论 |
|---|---|
| 所有写操作有策略和审计 | 通过：非只读 Tool 均需当前确认或显式 grant；高风险调用执行前写 `Started`，完成后写 `Completed` |
| 高风险操作无法绕过确认 | 通过：模型文本、历史消息和 Tool 输出均不能设置 `HasUserApproval` |
| 自动化结果可解释 | 通过：UI 显示 Tool 名称、风险、结果；安装额外显示宿主校验的权限、检查、diff 与 digest |
| 自动化可取消 | 通过：会话停止、全局 deadline 和单 Tool timeout 均有自动测试 |
| 自动化可复现 | 通过：Tool Schema/版本可查询，调用有 correlation/call/idempotency 标识；插件安装绑定确定性包哈希 |
