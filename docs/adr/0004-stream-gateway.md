# ADR-0004：用进程内 Stream Broker 实现边下边播与网络媒体

- 状态：已接受
- 日期：2026-09-08
- 更新：2026-09-10（阶段 0 的 loopback HTTP 主路径被进程内 mpv stream callback 取代）

## 决策

libmpv 不直接打开 aria2 的未完成文件或带凭证的远程地址。CanX 在媒体来源与播放器之间提供进程内 Stream Broker，通过固定 `canx` scheme 和 `mpv_stream_cb_add_ro` 向 libmpv 暴露可读、可 seek、可取消的 byte stream。

对于 aria2 下载任务，已完成区间依据 piece bitfield 从正在写入的稀疏文件读取；缺失区间会阻塞等待并请求 aria2 提高对应 piece 的优先级。对于 WebDAV、FTP/FTPS 与 SMB，宿主协议适配器负责 Range/offset、重连、凭证和降级顺序读取。libmpv 只看到不透明、会话级的 `canx://stream/<token>/<name>`。

主路径不监听端口。仅当 HLS/DASH 嵌套相对 URL、外部播放器或跨进程消费者无法使用 callback 时，才按明确需求增加可选 loopback HTTP Gateway。

2026-09-28：为插件托管 HLS 启用该兼容路径。FFmpeg 的 HLS 嵌套读取不能调用 mpv 的自定义 stream callback，
原有 `canx://` 子资源会使 HLS 解复用失败，并退回逐分片播放，表现为时长错误、无法跨片 seek 和切片停顿。
HLS 注册显式选择 `LoopbackHttp`；主列表、子列表、音轨、初始化片段和密钥均由受控宿主提供，
播放器仍负责完整时间线、解码和缓存。Gateway 仅绑定 `127.0.0.1` 的一个系统分配端口，使用随机会话路径，
支持 GET、HEAD 和单段 Range，复用 Broker 的独立 lease 和取消机制；最后一个 HLS 注册释放后关闭。
同一文件的 `EXT-X-BYTERANGE` 分片使用各自稳定的资源身份，并把隐式偏移展开为显式偏移，
避免当前 FFmpeg 对同 URL 的 HTTP 连接复用在首个 fMP4 范围后提前结束；源站读缓冲也限制在分片大小以内。
插件 API 和播放历史继续使用 `canx://` 身份。普通 HTTP 文件、SMB、WebDAV、FTP 和 aria2 边下边播不启用 Gateway。

原生回归入口：`CanX.App --playback-hls-smoke <master.m3u8> <整集秒数>`，使用至少 18 秒的合成 HLS fixture，
验证 HLS 解复用器、整集时长、单项播放列表、跨片连续播放、向前/向后 seek 和实际画面渲染。
Windows 测试脚本 `scripts/verify-playback-hls-windows-x64.ps1` 生成 TS、fMP4 单文件字节范围和 AES-128 fixture。

## 阶段 0 证据

`spikes/CanX.Spikes.Streaming` 曾用 loopback HTTP 验证底层边下边播可行性：

- libaria2 使用 `file-allocation=none`、单连接和 `stream-piece-selector=inorder` 持续写入文件；
- Gateway 在文件未下载完成时按可用长度向 libmpv 持续供给数据；
- libmpv 可通过 Gateway 播放 fast-start MP4、尾部索引 MP4 和 MKV，且三者都在下载完成前渲染画面；
- 请求尚未下载的任意中部区间和尾部区间时，Gateway 可从支持 Range 的源站补取，返回字节与源文件逐字节一致；
- 源站忽略 Range 时，Gateway 能识别 `200` 响应并返回明确的 `503`，不会把错误偏移的数据伪装成 `206`；顺序下载仍可完成；
- 源站在 512 KiB 后强制断开一次时，aria2 能续传并最终得到逐字节一致的文件；
- Gateway 会为 MP4 与 Matroska 返回相应的媒体类型；
- aria2 仍独立完成持久化下载，最终文件与源逐字节一致；
- Gateway、下载器和播放器均以 .NET 10 NativeAOT 运行。

该实验保留为历史证据，验证入口为 `scripts/verify-stream-gateway.sh`，完整通过标记为 `CANX_STREAM_GATEWAY_MATRIX_OK`。正式主路径由 `scripts/verify-playback-stream-linux-x64.sh` 验证，成功标记为 `CANX_PLAYBACK_STREAM_ALL_OK`。

## 边界与失败策略

- 源站不支持 Range/offset 时，数据源降级为顺序读取，UI 禁用 seek，而不是返回错误偏移的媒体数据。
- 多连接下载、BitTorrent 和稀疏文件不能只看文件长度，必须根据 aria2 bitfield 建立真实 available-range map。
- 带认证的来源由宿主持有 `CredentialRef`，不得把 Cookie、Header、密码或源 URL 暴露给 mpv、插件或 LLM。
- `canx://` token 使用密码学安全随机值、绑定当前注册和会话，不持久化，也不包含路径或凭证。
- stream callback API 仍是 libmpv 的不稳定接口；项目固定自有 mpv fork，并对 ABI 布局、回调生命周期、seek、cancel 和 EOF 建立测试。
- 插件不得逐块搬运媒体字节，只能返回由宿主验证并执行的 `MediaSource` 请求描述。
