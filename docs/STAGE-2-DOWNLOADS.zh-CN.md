# 阶段 2：下载中心状态与能力覆盖

更新时间：2026-09-09

## 当前结论

阶段 2 已完成并关闭。Linux x64 正式 App 已静态链接 CanX aria2 fork，并可作为 NativeAOT 单文件下载客户端运行。当前原生 capability manifest 为 ABI 5、10 项编译特性和 200 项 option metadata；发行版动态 libaria2 仅作为兼容性测试，明确报告 `extendedApi=false`。

已通过的自动验证包括：

- HTTP 下载、暂停/恢复/删除、队列移动、批量添加、全局/任务 options、持久化恢复与稳定 UUID；
- 多 URI 镜像作为同一个 aria2 任务传入，而不是创建多个重复任务；
- Torrent/Metalink 创建入口、文件列表、tracker、bitfield、做种状态、peer/server ABI 与托管映射；
- 新建下载采用两步确认：首屏只输入 URL/Magnet 或选择 Torrent/Metalink 文件，解析后再显示必要信息；多文件 Torrent 默认全选并支持高/普通/低逐文件优先级；
- 新建下载使用主窗口内的模态遮罩面板，不创建额外原生窗口；底层页面在面板关闭前不可交互，便于后续复用到 Android/iOS 页面导航；
- 下载页只常驻任务列表、进行中/已完成筛选、添加按钮和底部状态栏；单任务操作集中到右键菜单与任务选项对话框，全局 aria2 选项集中到按标题和分割线组织的设置页；
- 本地 Torrent、Magnet 与 HTTP Torrent 均以暂停的内存预览读取元数据；正式确认前不发布任务，也不在目标目录或临时预览目录落地任何文件；
- Magnet 元数据获取较慢时可再次确认；临时预览会被安全清理，并使用正式下载目录创建任务，元数据完成后自动开始下载全部文件；重复确认保持幂等；
- Magnet metadata 尚未获得时以 `[METADATA] <40 位 BTIH hex>` 显示，其中 Base32 BTIH 也会规范化为小写 hex，多个等待 metadata 的任务可直接区分；
- 添加任务会在核心层按 Magnet BTIH、Torrent info-hash 或规范化 URL 判重，忽略 Magnet 的 tracker、显示名、参数顺序和 BTIH 大小写；界面在主窗口遮罩内二次确认，Tool/插件默认得到 `duplicate_download`，显式设置 `allowDuplicate` 后仍可添加；
- 本地生成的多文件 Torrent 实际下载：文件选择、HTTP tracker、peer 连接、逐文件比对及做种状态；
- Metalink 双文件拆分为两个任务、实时 server 信息、SHA-256 校验和逐文件比对；
- 本地认证 FTP 与 SFTP 下载、实时 server 信息和逐字节比对；SFTP 同时校验临时服务器 host-key 指纹；
- FTP 错误密码会进入可观察的错误状态（aria2 错误码 21），测试输出不包含认证密码；
- 失败的单任务 URI/Torrent 可清除 aria2 stopped result 后原位重试，保持 CanX UUID 和目标路径；本地 FTP fixture 已验证首次认证失败、人工重试后成功；
- 下载列表显示 ETA，文件页显示逐文件进度，诊断页显示聚合分片图、可读错误和 aria2 原始状态；
- `downloads.get` 返回完整且脱敏的任务诊断 JSON，包括分片、文件 URI 状态、Torrent metadata、任务关系和 options；
- 常用设置页按 capability 作用域提供任务/全局限速、并发数、单服务器连接数、分片、校验和、代理及做种比率/时长控件；可写入新任务或应用到当前可变作用域；
- 会话 schema 2 使用 `CredentialRef` 将认证 URI、Header/Cookie、用户名、密码和 token 移入系统凭证库；Linux 已验证 Secret Service，且旧 schema 1 会话可安全迁移；
- 故障注入覆盖连接中断后自动续传、三次连接失败后重试耗尽，以及向 `/dev/full` 写入时的磁盘空间不足（aria2 错误码 9）；
- 新任务与现有任务均可配置完成通知，以及安全限定的“打开文件/打开目录”动作；`downloads.completion.set` 提供同一能力，完成事件按任务去重；
- 新建对话框支持正常粘贴单个 URL/Magnet，并可拖放单个 Torrent/Metalink；批量添加由 `downloads.addBatch` 提供，现有任务可在任务选项或通过 `downloads.schedule.set` 设置/取消计划开始时间，计划状态跨重启恢复；
- 下载中心主要控件具有 AT-SPI 可访问名称和状态播报，支持 Tab / Shift+Tab、Ctrl+L、Ctrl+Shift+L、F6 / Shift+F6 导航，并为常用下载命令提供快捷键；
- Linux x64 NativeAOT 产物已在 Xvfb 中通过 100%、200%、300% 缩放和可见焦点截图验收；截图及 SHA-256 清单位于 `artifacts/ui/download-center/linux-x64/`；
- `downloads.*` Tool 对下载控制、能力发现和实时连接信息的访问；
- Linux x64 静态 NativeAOT 发布，真实 HTTP 下载逐字节比对，最终 ELF 无 libaria2/OpenSSL/libssh2 等第三方动态依赖。

## Option 覆盖规则

固定版本的每个 aria2 option 都由 fork 直接从 `OptionParser` 导出名称、说明、默认值、取值范围、参数类型、隐藏标记和四类可变范围。CanX 不维护一份容易漂移的手写 option 清单。

| aria2 metadata | CanX 入口 |
|---|---|
| `initial` | 新建任务的高级 `key=value` 编辑器与 `downloads.add` / `downloads.addBatch` 的 `options` |
| `changeActive` / `changeReserved` | 所选任务高级编辑器与 `downloads.options.set` |
| `changeGlobal` | 全局高级编辑器与 `downloads.global.set` |
| `hidden` | 不在普通 UI 展示；仍保留在 capability Tool 结果中，用于诊断和版本审计 |
| 无可变范围 | 只读 metadata；不能在当前生命周期安全修改 |

能力页展示所有非隐藏 option 及其范围，支持按名称、说明和取值搜索。选择 option 后可查看默认值与作用域，并按 metadata 一键写入新任务、活动/等待任务或全局配置。专用控件负责高频操作，通用高级编辑器覆盖长尾 option；因此 option 新增不会静默形成 UI/Tool 空洞。输入最终仍由 libaria2 校验，错误通过统一状态消息和 Tool 失败结果返回。

`DownloadOptionCoverage` 会在测试和静态 App smoke 中检查 option 名称完整且不重复，并报告隐藏、只读和各修改作用域的数量；正式构建当前结果为 `uncovered=0`。完整统计与构建基线见 [aria2 Option 作用域覆盖报告](ARIA2-OPTION-COVERAGE.zh-CN.md)。

## 阶段退出条件

- 本机日常下载所需协议、队列、计划、文件选择、诊断、完成动作和长尾 option 均有 UI 或高级入口；
- 会话重启恢复、稳定任务 ID 和凭证不落盘已验证；
- option scope 覆盖为 `200/200`，`uncovered=0`；
- 键盘导航、AT-SPI、可见焦点及 100%–300% 缩放截图已由正式 NativeAOT 产物验证。

凭证存储的实现与动态依赖边界见 [ADR 0007](adr/0007-platform-credential-storage.md)。Android Keystore 与 iOS Keychain 属于阶段 7 平台适配，不阻塞当前 Linux x64 阶段 2 验收。

## 重复验证

```bash
dotnet test tests/CanX.Foundation.Tests/CanX.Foundation.Tests.csproj -c Release
scripts/verify-aria2-shim.sh
scripts/verify-download-manager.sh
CANX_STATIC_ROOT="$PWD/.cache/static-native/linux-x64" \
  scripts/verify-static-app-linux-x64.sh --skip-build
scripts/verify-download-fixtures-linux-x64.sh --skip-publish
scripts/verify-ftp-sftp-linux-x64.sh --skip-publish
scripts/verify-download-failures-linux-x64.sh --skip-publish
scripts/verify-download-ui-linux-x64.sh --skip-publish
```

静态 App 验收输出应包含：

```text
CANX_APP_CAPABILITIES abi=5 extended=true features=10 options=200 uncovered=0
CANX_APP_OPTION_SCOPE total=200 initial=115 active=7 reserved=108 global=120 hidden=0 readOnly=72 uncovered=0
CANX_STATIC_APP_OK platform=linux-x64
CANX_DOWNLOAD_FIXTURES_OK torrent=5 metalink=1
CANX_FTP_SFTP_OK ftp=true sftp=true authentication=true
CANX_DOWNLOAD_FAILURES_OK recovery=true retries=3 diskFull=true
CANX_DOWNLOAD_UI_OK backend=X11 scales=100,200,300 accessibility=at-spi keyboard=true screenshots=6
```

FTP/SFTP 测试服务端使用固定版本的 `pyftpdlib` 与 `Paramiko`，只安装到仓库忽略的 `.cache/download-protocol-tests`，不进入 CanX 发布包或运行时依赖。

UI 验收脚本需要 Xvfb、xdotool、ImageMagick、AT-SPI、PyGObject 和对应的 Python GI binding；它会在隔离的 D-Bus/X11 会话中运行，不修改用户桌面会话。
