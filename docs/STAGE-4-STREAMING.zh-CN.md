# 阶段 4：Stream Broker 与网络媒体实现状态

- 日期：2026-09-11
- 状态：Linux x64 桌面实现完成；真实服务端人工互操作矩阵待验收
- 下一边界：暂停在阶段 5 插件系统之前，先讨论插件模型

## 已完成范围

CanX 的统一媒体数据通路是进程内 `Stream Broker` 与 libmpv `mpv_stream_cb_add_ro`。播放器只看到不透明、会话级的 `canx://stream/<token>/<name>`，不会获得原始地址、认证信息或底层协议对象，主路径也不监听本地端口。

```text
aria2 未完成文件 / HTTP(S) / WebDAV / FTP(S) / SMB
                         |
                         v
              IMediaByteStream + Stream Broker
                         |
                         v
             canx:// + mpv stream callback
                         |
                         v
                       libmpv
```

核心数据层已经具备：

- 每次 open 创建独立、可取消的 stream lease；注册注销或播放器销毁会取消阻塞读取并正确释放资源；
- read、绝对 seek、size、EOF、未知长度和不可 seek 数据源的一致语义；
- NativeAOT 安全的 native callback、结构布局与生命周期管理，托管异常不会越过 C ABI；
- HTTP/WebDAV Range 窗口读取；服务端不支持 Range 时复用响应并降级为不落盘的顺序流；
- 对超时、传输中断、HTTP 408/429/5xx 和可 seek 流伪 EOF 的有限、可取消重试；资源永久变化和调用者取消不会重试；
- aria2 piece bitfield 感知的稀疏文件读取，缺失区间等待并提高 piece 优先级，seek 后更新优先范围；
- WebDAV、FTP/FTPS、SMB 2/3 的目录浏览、stat、随机或顺序读取、视频播放和远程字幕加载。
- 当全局 `cache=auto` 时，宿主对 `canx://` 媒体使用文件级 `cache=yes`，弥补 mpv 无法把自定义 stream callback 自动识别为网络流的问题；显式的 `cache=no/yes` 仍由用户控制。

## 网络媒体界面

主窗口的网络媒体区已经支持：

- 在统一列表中保存多个具名的本地文件夹、WebDAV、FTP/FTPS 与 SMB 播放源，页面只保留一个应用内“添加播放源”入口；
- 添加网络源时先读取根目录验证地址和凭证，成功后持久化；双击本地文件夹或网络源均进入统一目录浏览器，列出全部文件，只有受支持的媒体项可以启动播放；
- 目录进入、返回上级、后退、前进，以及按协议和安全来源根隔离的持久化目录收藏；
- 按名称和媒体类型筛选，按名称、大小或修改时间排序，目录始终优先；
- 自动匹配同目录字幕或手动选择字幕，再把媒体和字幕作为独立 `canx://` stream 交给 libmpv；
- 数据源不可 seek 时禁用进度条与快进/快退，避免界面承诺底层做不到的随机访问。
- 设置页可分别调整缓存预读时长、向前/向后内存上限、缓存耗尽自动暂停、首次缓冲及恢复阈值；这些参数由 mpv 的 demuxer cache 执行，不另建 HTTP 服务或第二套应用缓存。

播放源配置只保存稳定 ID、显示名称、协议、无敏感参数的 endpoint、用户名和 `CredentialRef`，并允许同一协议存在多个来源；本地文件夹也作为无凭证的播放源保存。目录收藏只保存协议、来源根和根内路径。密码只进入 Windows Credential Manager、macOS Keychain 或 Linux Secret Service；安全存储不可用时认证源添加失败且不明文降级。含 user-info、query 或 fragment 的 endpoint 不持久化，日志不输出 endpoint、凭证或原始网络异常。

## 协议行为

| 来源 | 随机访问 | 故障恢复 | 当前实现边界 |
|---|---|---|---|
| WebDAV / HTTP(S) | 支持 Range 时可 seek；否则顺序读取 | 幂等查询、探测与 Range 响应体最多三次短退避重试 | 真实 WebDAV 服务端差异待人工验收 |
| FTP / FTPS | 服务端声明 `REST STREAM` 时直接启用 seek；未声明时用 `REST 0` 主动探测，接受 2xx/3xx 后同样启用 | 可 seek 流从最后交付位置重连；无 REST 时只允许首字节前自动重试 | 主动/被动、TLS 和不同服务端待人工矩阵 |
| SMB 2/3 | 通过 offset read 可 seek | 瞬时失败后从最后交付位置重新打开 | Samba 与 Windows 共享待人工矩阵 |
| aria2 活动任务 | 依据已完成 piece 读取并可 seek | 等待缺失 piece、动态提权、支持取消 | 当前入口选择最大的已选媒体文件 |

SMB 使用纯托管 SMBLibrary，不依赖 `libsmbclient`；FTP/FTPS 使用纯托管 FluentFTP。两者均被合入 NativeAOT 单文件发布，不携带额外 native sidecar。

## 自动验证

阶段 4 的自动化覆盖包括：

- `MediaStreamBroker` 的 token 校验、并发 lease、read/seek/size/cancel/close、注销和销毁顺序；
- HTTP Range、无 Range、未知长度、窗口缓存、瞬时响应错误、响应体中断、可 seek 流伪 EOF、永久资源变化和取消；
- aria2 未完成 piece 的等待、提权、seek 和 EOF；
- WebDAV XML/路径边界、FTP 与 SMB 客户端行为、浏览 ViewModel、连接测试、历史、收藏、筛选与排序；
- profile/favorite 的敏感信息拒绝与 OS 凭证引用；
- libmpv stream callback 的 Wayland/X11 真实解码、渲染和随机 seek；
- 本地认证 FTP fixture 与进程隔离 SMB fixture 的目录、读取、seek、字幕和无凭证日志验证。
- FTP fixture 会隐藏 `FEAT` 中的 `REST STREAM`、保留实际 `REST` 命令，防止再次把 rclone 一类能力列表不完整的服务端误判为不可 seek。

主要重复验证入口：

```bash
dotnet test tests/CanX.Foundation.Tests/CanX.Foundation.Tests.csproj -c Release
scripts/verify-playback-stream-linux-x64.sh
CANX_ALLOW_NEWER_GLIBC=1 scripts/publish-static-app-linux-x64.sh --skip-build
scripts/verify-ftp-media-playback-linux-x64.sh --skip-publish
scripts/verify-smb-media-playback-linux-x64.sh --skip-publish
```

当前 Foundation 测试共 257 项全部通过，其中覆盖播放源列表、本地文件夹完整目录枚举、不支持格式禁播、网络源添加前验证、同协议多来源和按来源 ID 持久化；隐藏 `REST STREAM` 的 FTP fixture 仍完成跨区间 seek、远程字幕和真实渲染。Linux x64 NativeAOT 生成 `artifacts/publish/linux-x64/CanX.App`，依赖审计为 `needed=5 sidecars=0`。本机构建显式绕过了 Ubuntu 22.04 的 glibc 2.35 门禁，因此它用于本机验证，不作为最低发行版正式包；正式产物仍须在固定 Ubuntu 22.04 CI 环境中重建。

## 延后人工验收

根据当前开发安排，以下测试集中留到用户具备服务端环境后执行：

- 至少两种真实 WebDAV 实现，包括 Range 支持差异；
- FTP 与 FTPS 的不同服务端、TLS、主动/被动模式；
- Samba 与 Windows SMB 共享；
- 播放期间断线、服务重启、网络切换、凭证失效及资源被替换；
- Windows、macOS、Linux ARM64 与移动端的协议和安全存储适配。

这些是发布兼容性验收项，不回退已经完成的 Linux x64 桌面实现，也不能在实际执行前标记为通过。

## 阶段 5 前的停止点

阶段 4 只保留稳定边界：插件未来提交经过校验的媒体描述符，宿主负责凭证、网络 I/O、Range、缓存、取消和 libmpv 注册；媒体二进制不穿过 QuickJS。可选 loopback HTTP Gateway 仅在 HLS/DASH 嵌套相对 URL、外部播放器或跨进程消费出现明确需求时再实现。

QuickJS runtime、manifest、权限模型、声明式 UI、搜索/字幕 provider、SDK 与插件开发工具尚未开始，需先讨论并冻结插件系统细节。
