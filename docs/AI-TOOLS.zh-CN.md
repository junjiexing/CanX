# CanX AI Tool 完整参考

- 基线日期：2026-09-14
- Tool Schema 版本：1
- 静态注册 Tool：109 个
- 风险分布：只读 26、本地写入 52、网络 17、破坏性 14
- 运行时权威来源：`diagnostics.tools`

本文列出当前宿主向 AI 暴露的全部静态 Tool。插件可以在运行时注册新的 Service；这类动态能力通过
`services.list`、`services.describe` 和 `services.invoke` 发现和调用，不计入上述 109 个静态 Tool。

## 调用协议

AI 通过 OpenAI-compatible function/tool calling 发出名称和 JSON 参数，例如：

```json
{
  "name": "playback.seek",
  "arguments": {
    "seconds": 30,
    "relative": true
  }
}
```

`arguments` 必须是 JSON object。表格中没有参数的 Tool 使用 `{}`；`?` 表示可选参数，`A/B` 表示
联合类型，`{a/b}` 表示只允许列出的值。未列出的字段通常会被 JSON Schema 拒绝。

AI 只负责提出调用，不能传入或伪造调用主体、授权状态、call ID、超时等宿主上下文。风险不是“提示级别”，
而是实际执行策略：

| 风险 | AI 调用行为 |
|---|---|
| 只读 | Schema 校验通过后可直接执行 |
| 本地写入 | 修改应用或本地状态，执行前由宿主向用户确认 |
| 网络 | 发起网络访问或开始可能访问网络的任务，执行前确认 |
| 破坏性 | 删除、覆盖或执行高级 mpv 操作，执行前确认 |

用户拒绝时返回 `permission_denied`。调用成功返回结构化 JSON；失败返回稳定的错误码和脱敏消息。单次参数
最多 1 MiB，输出默认最多 1 MiB，调用可取消且受 Agent 总时长和单 Tool 超时约束。API Key、网络密码、
Cookie 和插件 secret 不会作为 Tool 参数或结果暴露给 AI。

网络和破坏性 Tool 只有在执行前 `Started` 审计成功落盘后才会运行；否则返回 `audit_unavailable`。
执行结束后追加 `Completed`，该记录失败不会改变已经完成的业务结果。此表描述 Agent 可调用范围；插件还受
独立策略约束：除只读 Tool 外只能调用宿主固定的可逆播放/下载控制；`downloads.add`、`downloads.addBatch` 和
`search.download` 只能通过标准下载确认页完成。Agent 通过 `downloads.add`/`downloads.addBatch` 添加的任务只能
保存到设置中的下载目录或其子目录。

## 下载：`downloads.*`（15）

下载任务 `id` 是 `downloads.list`/`downloads.add` 返回的 GUID。`options` 是 aria2 选项名到字符串值的
对象，例如 `{ "max-download-limit": "1048576" }`；先用 `downloads.capabilities` 查询当前构建支持的选项。

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `downloads.list` | `{}` | 只读 | 列出下载任务摘要、状态、速度、进度和队列位置。 |
| `downloads.get` | `id:string` | 只读 | 读取一个任务的完整信息，包括文件、Torrent、分片、错误和引擎诊断。 |
| `downloads.global.get` | `{}` | 只读 | 读取 aria2 全局状态及当前全局选项。 |
| `downloads.capabilities` | `{}` | 只读 | 读取编译进 aria2 的功能及选项元数据。 |
| `downloads.connections` | `id:string` | 只读 | 列出任务当前连接的 BT Peer、HTTP/FTP 服务器。 |
| `downloads.add` | `source:string, destinationDirectory:string, kind?:{uri/torrent/metalink}, outputName?:string, startPaused?:boolean, allowDuplicate?:boolean, position?:integer, mirrorUris?:string[], options?:object, notifyOnCompletion?:boolean, completionAction?:{none/open-file/open-directory}, scheduledStartAt?:string` | 网络 | 添加一个 URI、Torrent 文件或 Metalink 任务；`scheduledStartAt` 使用 ISO 8601 UTC 时间。 |
| `downloads.addBatch` | `sources:string[], destinationDirectory:string, kind?:{uri/torrent/metalink}, outputName?:string, startPaused?:boolean, allowDuplicate?:boolean, position?:integer, mirrorUris?:string[], options?:object, notifyOnCompletion?:boolean, completionAction?:{none/open-file/open-directory}, scheduledStartAt?:string` | 网络 | 在下载 worker 中整批添加 URI；任一项失败或取消时回滚本批已创建的 aria2 任务和会话状态，不暴露部分成功。 |
| `downloads.pause` | `id:string` | 本地写入 | 暂停任务。 |
| `downloads.resume` | `id:string` | 网络 | 恢复暂停或失败的任务。 |
| `downloads.remove` | `id:string, deleteFiles?:boolean=false, force?:boolean=false` | 破坏性 | 删除任务；可选择删除本地文件或强制移除。 |
| `downloads.options.set` | `id:string, options?:object` | 本地写入 | 修改单个任务可在运行时变更的 aria2 选项；仅接受自动化白名单内的限速、分片、Tracker、做种等选项。 |
| `downloads.completion.set` | `id:string, notify?:boolean=false, action?:{none/open-file/open-directory}` | 本地写入 | 设置完成通知和安全的下载后动作；`open-file` 只直接打开任务目录内的音视频/字幕文件，其他类型改为打开所在目录。 |
| `downloads.schedule.set` | `id:string, startAt?:string` | 本地写入 | 设置任务组的 ISO 8601 UTC 启动时间；省略 `startAt` 表示清除计划。 |
| `downloads.global.set` | `options:object` | 本地写入 | 修改 aria2 全局选项；仅接受自动化白名单内的选项，日志/会话/Cookie 文件、代理、证书、目录和事件 hook 只能在下载中心由用户修改。 |
| `downloads.move` | `id:string, offset?:integer=0, mode?:{set/current/end}=set` | 本地写入 | 调整等待任务的队列位置。 |

## 播放：`playback.*`（56）

### 状态、诊断与高级 mpv 接口

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `playback.get` | `{}` | 只读 | 读取当前媒体、播放状态、进度、轨道、列表、章节、书签和常用属性。 |
| `playback.capabilities` | `{}` | 只读 | 返回当前 libmpv 的版本化 property/command 清单、CanX 策略和覆盖状态。 |
| `playback.diagnostics.get` | `{}` | 只读 | 返回实际解码器、硬解路径、渲染链路、色彩信息和性能计数。 |
| `playback.property.get` | `name:string` | 只读 | 读取一个非敏感 mpv runtime property；可用名称见 `playback.capabilities`。 |
| `playback.property.set` | `name:string, value:string` | 破坏性 | 设置策略允许的 mpv property；宿主控制项和敏感项会被拒绝。 |
| `playback.command.execute` | `command:string, arguments?:string[]` | 破坏性 | 执行策略允许的 mpv command；启动进程、任意文件访问、退出宿主等命令会被拒绝。 |
| `playback.color.set` | 至少一个色彩字段，见下文 | 本地写入 | 动态调整输出色彩空间、HDR 峰值、tone mapping 和 gamut mapping。 |

`playback.color.set` 接受以下可选字段，且至少提供一个：

- `targetColorspaceHint`: `auto|yes|no`
- `targetColorspaceHintMode`: `target|source|source-dynamic`
- `targetPrimaries`: `auto|bt.470m|bt.601-525|bt.601-625|bt.709|bt.2020|apple|adobe|prophoto|cie1931|dci-p3|display-p3|v-gamut|s-gamut`
- `targetTransfer`: `auto|bt.1886|srgb|linear|gamma1.8|gamma2.0|gamma2.2|gamma2.4|gamma2.6|gamma2.8|prophoto|pq|hlg|v-log|s-log1|s-log2|scrgb`
- `targetPeak`: string 或 number
- `toneMapping`: `auto|clip|mobius|reinhard|hable|bt.2390|gamma|linear|spline|bt.2446a|st2094-40|st2094-10`
- `gamutMappingMode`: `auto|clip|perceptual|relative|saturation|absolute|desaturate|darken|warn|linear`
- `hdrComputePeak`: `auto|yes|no`

### 打开媒体、播放列表与历史

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `playback.open` | `source:string` | 网络 | 打开本地媒体文件或 HTTP(S) URL。远程共享应使用 `media.play`。 |
| `playback.folder.open` | `directory:string, recursive?:boolean=false` | 本地写入 | 从本地目录中的支持格式文件建立播放列表。 |
| `playback.playlist.open` | `sources:string[], startIndex?:integer=0` | 网络 | 替换整个播放列表并从指定的零基索引开始。 |
| `playback.playlist.add` | `sources:string[], playNow?:boolean=false` | 网络 | 追加媒体；可立即播放第一个新增项。 |
| `playback.playlist.play` | `index:integer` | 本地写入 | 播放零基索引指定的列表项。 |
| `playback.playlist.next` | `{}` | 本地写入 | 播放下一项。 |
| `playback.playlist.previous` | `{}` | 本地写入 | 播放上一项。 |
| `playback.playlist.remove` | `index:integer` | 本地写入 | 移除零基索引指定的列表项。 |
| `playback.playlist.move` | `index:integer, newIndex:integer` | 本地写入 | 移动列表项。 |
| `playback.playlist.clear` | `{}` | 本地写入 | 清除排队项，但保留当前播放项。 |
| `playback.recent.open` | `source:string` | 网络 | 打开历史项目；启用续播时恢复未完成位置。 |
| `playback.recent.remove` | `source:string` | 破坏性 | 删除一条播放历史。 |
| `playback.recent.clear` | `{}` | 破坏性 | 清空全部播放历史。 |

### 基本控制、章节、书签与截图

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `playback.play` | `{}` | 本地写入 | 继续播放。 |
| `playback.pause` | `{}` | 本地写入 | 暂停播放。 |
| `playback.togglePause` | `{}` | 本地写入 | 切换播放/暂停。 |
| `playback.stop` | `{}` | 本地写入 | 停止播放。 |
| `playback.seek` | `seconds:number, relative?:boolean=false` | 本地写入 | 跳到绝对秒数；`relative=true` 时按正负秒数相对跳转。 |
| `playback.frame.next` | `{}` | 本地写入 | 暂停并前进一帧。 |
| `playback.frame.previous` | `{}` | 本地写入 | 暂停并后退一帧。 |
| `playback.abLoop.set` | `startSeconds?:number, endSeconds?:number` | 本地写入 | 设置一个或两个 A-B 循环点，至少提供一个非负值。 |
| `playback.abLoop.clear` | `{}` | 本地写入 | 清除 A-B 循环。 |
| `playback.chapter.list` | `{}` | 只读 | 列出章节及当前章节索引。 |
| `playback.chapter.open` | `index:integer` | 本地写入 | 跳转到零基章节索引。 |
| `playback.bookmark.list` | `{}` | 只读 | 列出当前媒体的书签。 |
| `playback.bookmark.add` | `title?:string, positionSeconds?:number` | 本地写入 | 在当前位置或指定非负秒数添加书签。 |
| `playback.bookmark.open` | `id:string` | 网络 | 打开书签；必要时加载对应媒体。 |
| `playback.bookmark.rename` | `id:string, title:string` | 本地写入 | 重命名书签。 |
| `playback.bookmark.remove` | `id:string` | 破坏性 | 删除书签。 |
| `playback.bookmark.clear` | `all?:boolean=false` | 破坏性 | 清除当前媒体书签；`all=true` 清除所有媒体书签。 |
| `playback.screenshot` | `path?:string, mode?:{video/subtitles/window}=subtitles` | 本地写入 | 截图到新文件，不覆盖已有文件；模式分别为纯视频、带字幕、完整窗口。 |

### 音视频、滤镜、Shader 与字幕

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `playback.volume.set` | `volume:number` | 本地写入 | 设置音量，范围 0–130。 |
| `playback.mute.set` | `muted:boolean` | 本地写入 | 设置静音状态。 |
| `playback.speed.set` | `speed:number` | 本地写入 | 设置播放速度，范围 0.1–4。 |
| `playback.brightness.set` | `brightness:number` | 本地写入 | 设置视频亮度，范围 -100–100。 |
| `playback.audioDevice.list` | `{}` | 只读 | 列出音频输出设备和当前设备。 |
| `playback.audioDevice.set` | `name:string` | 本地写入 | 按 mpv 设备名切换音频输出设备。 |
| `playback.filter.video.set` | `filters:string` | 本地写入 | 用 mpv filter 语法替换视频滤镜链，最长 32768 字符。 |
| `playback.filter.audio.set` | `filters:string` | 本地写入 | 用 mpv filter 语法替换音频滤镜链，最长 32768 字符。 |
| `playback.filter.clear` | `type:{video/audio/all}` | 本地写入 | 清除视频、音频或全部滤镜链。 |
| `playback.shader.list` | `{}` | 只读 | 列出当前用户 Shader。 |
| `playback.shader.add` | `path:string` | 本地写入 | 加载本地 GLSL 或 mpv hook Shader 文件。 |
| `playback.shader.remove` | `path:string` | 本地写入 | 按路径移除一个 Shader。 |
| `playback.shader.clear` | `{}` | 本地写入 | 清除所有用户 Shader。 |
| `playback.track.select` | `type:{video/audio/subtitle}, id:integer/null` | 本地写入 | 选择轨道；`id=null` 禁用该类轨道。 |
| `playback.subtitle.add` | `source:string, select?:boolean=true` | 本地写入 | 添加本地字幕或宿主管理的字幕流，并可立即选中。 |
| `playback.subtitle.visibility.set` | `visible:boolean` | 本地写入 | 显示或隐藏当前字幕。 |
| `playback.subtitle.delay.set` | `seconds:number` | 本地写入 | 设置字幕延迟，可为负数。 |
| `playback.subtitle.style.set` | `fontSize?:number, position?:number` | 本地写入 | 设置字号 1–9000、垂直位置 0–150；至少提供一个字段。 |

## 媒体源：`media.*`（5）

网络凭据只能由用户在界面中保存。AI 新增的 WebDAV/FTP/SMB 来源必须匿名可访问，且 endpoint 不能含
userinfo、query 或 fragment。`media.browse` 返回的 `location` 是宿主生成的 32 位 opaque handle，默认
30 分钟过期；后续浏览和播放必须回传该 handle，不能传真实路径或 URL。

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `media.sources.list` | `{}` | 只读 | 列出已配置来源的 `id`、名称和协议，不返回 endpoint 或凭据。 |
| `media.sources.add` | `name:string, protocol:{local/webdav/ftp/smb}, endpoint:string` | 网络 | 验证并添加本地目录或匿名网络来源；WebDAV 用 HTTP(S)，FTP 用 FTP(S)，SMB 用 `smb://`。 |
| `media.sources.remove` | `sourceId:string` | 破坏性 | 删除来源及其凭据引用。 |
| `media.browse` | `sourceId:string, location?:string` | 网络 | 浏览来源根目录；继续进入子目录时使用上次结果中的目录 handle。 |
| `media.play` | `sourceId:string, location:string, subtitleLocation?:string` | 网络 | 播放 handle 指向的视频，可附加同一来源中字幕文件的 handle。 |

## 聚合搜索：`search.*`（5）

三个搜索 Tool 只调用声明为 AI audience 的插件 Service。结果中的真实播放 URL、Magnet 和解析参数留在
宿主缓存；AI 仅能看到并回传 `searchId`/`resultId`。ID 默认 30 分钟过期，不能跨搜索混用。

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `search.media` | `query:string, limit?:integer=20, preferredLanguages?:string[]` | 网络 | 并行搜索所有视频源，标准化、去重并评分；`limit` 范围 1–100，单 Provider 最多 50。 |
| `search.subtitles` | `query:string, limit?:integer=20, preferredLanguages?:string[]` | 网络 | 聚合搜索字幕并返回 opaque 结果 ID。 |
| `search.torrents` | `query:string, limit?:integer=20, preferredLanguages?:string[]` | 网络 | 聚合搜索 Torrent/Magnet 并返回 opaque 结果 ID。 |
| `search.play` | `mediaSearchId:string, mediaResultId:string, subtitleSearchId?:string, subtitleResultId?:string, sourceIndex?:integer=0` | 网络 | 解析缓存的视频结果和可选字幕结果，直接流式交给播放器；`sourceIndex` 范围 0–32。 |
| `search.download` | `searchId:string, resultId:string` | 网络 | 将缓存的 Torrent 搜索结果加入默认下载目录。 |

典型搜索播放链路：先调用 `search.media`，需要字幕时再调用 `search.subtitles`，最后把两次返回的 ID 交给
`search.play`。AI 从始至终看不到 Provider 的私有解析参数或最终 URL。

## 插件 Service：`services.*`（3）

Service 是插件提供的动态、版本化 JSON Schema 接口。只有 Manifest 明确包含 AI audience 的 Service
才可见；调用风险由宿主 Tool 固定为网络操作，并仍需用户确认。

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `services.list` | `contract?:string, majorVersion?:integer` | 只读 | 列出 AI 可见 Service，可按 contract 和非负主版本过滤。 |
| `services.describe` | `serviceId:string` | 只读 | 返回一个 Service 的完整元数据、输入 Schema 和输出 Schema。 |
| `services.invoke` | `serviceId:string, input:any` | 网络 | 按该 Service 的 Schema 校验 `input` 后调用插件。 |

正确顺序是 list → describe → invoke；不要根据其他插件或旧会话猜测 `input` 结构。

## 插件生命周期与 Workspace：`plugins.*`（17）

### 已安装插件

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `plugins.list` | `{}` | 只读 | 列出插件版本、权限、签名、启用状态、runtime 状态和可回滚版本。 |
| `plugins.enable` | `id:string` | 破坏性 | 批准当前声明的权限并启用插件；确认框展示宿主读取的权限清单、签名和包摘要。 |
| `plugins.disable` | `id:string` | 本地写入 | 禁用插件。 |
| `plugins.reload` | `id:string` | 本地写入 | 重载已启用插件的 QuickJS runtime。 |
| `plugins.rollback` | `id:string` | 破坏性 | 回滚到保留的上一安装版本。 |
| `plugins.uninstall` | `id:string` | 破坏性 | 卸载插件，但保留隔离数据和系统凭据。 |

### AI 插件开发 Workspace

`workspaceId` 和 `reviewToken` 都是 32 位小写十六进制 ID。路径必须是 Workspace 内的相对路径；绝对路径、
`..`、隐藏路径、符号链接、reparse point 和 `node_modules` 均被拒绝。单个文本文件最多 1 MiB，Workspace
最多 16 MiB/1024 个文件。

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `plugins.workspace.list` | `{}` | 只读 | 列出隔离 Workspace 及其验证状态。 |
| `plugins.workspace.create` | `pluginId:string, name:string, fromPluginId?:string` | 本地写入 | 新建插件模板；可复制一个已安装插件作为起点。 |
| `plugins.workspace.files` | `workspaceId:string` | 只读 | 列出 Workspace 文件。 |
| `plugins.workspace.read` | `workspaceId:string, path:string` | 只读 | 读取受限 UTF-8 文本文件。 |
| `plugins.workspace.write` | `workspaceId:string, path:string, content:string` | 本地写入 | 原子写入一个受支持的文本文件。 |
| `plugins.workspace.delete` | `workspaceId:string, path:string` | 破坏性 | 删除一个 Workspace 文件。 |
| `plugins.workspace.diff` | `workspaceId:string` | 只读 | 显示相对初始或上次安装基线的有界 diff。 |
| `plugins.workspace.run` | `workspaceId:string, command:{lint/build/test/pack}` | 本地写入 | 执行固定检查命令；不接受 shell 或任意 `package.json` script。 |
| `plugins.workspace.prepareInstall` | `workspaceId:string` | 本地写入 | 执行 lint/build/沙箱测试/打包，生成权限、diff、digest 和安装审核信息。 |
| `plugins.workspace.install` | `workspaceId:string, reviewToken:string` | 破坏性 | 安装用户刚审核的精确构建；文件或包变化会让 token 失效。 |
| `plugins.workspace.discard` | `workspaceId:string` | 破坏性 | 永久丢弃 Workspace 及生成物。 |

推荐开发链路：create → files/read/write → run → diff → prepareInstall → install。安装确认内容由宿主重新
计算，AI 对权限、测试结果或 digest 的文字陈述不能替代审核。

## 设置：`settings.*`（2）

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `settings.get` | `section:{general/agent/playback}` | 只读 | 读取允许自动化的设置段；秘密永不返回。 |
| `settings.set` | `section:{general/agent/playback}, values:object` | 本地写入 | 部分更新一个设置段；未知字段、秘密字段，以及 `downloadDirectory`、`baseUrl`、`advancedOptions` 等只能由用户在设置页修改的字段会被拒绝。 |

`values` 白名单：

- `general`：`colorTheme:string`、`downloadDirectory:string`、`enableLogging:boolean`。
- `agent`：`baseUrl:string`、`model:string`、`temperature:number(0–2)`、
  `maximumOutputTokens:integer(1–131072)`、`maximumRounds:integer(1–64)`、
  `maximumToolCalls:integer(1–256)`、`maximumTotalTokens:integer(1–10000000)`、
  `maximumDurationSeconds:integer(1–3600)`、`toolTimeoutSeconds:integer(1–3600)`。
  `baseUrl`/`model` 必须同时设置或同时为空；远程 `baseUrl` 必须为 HTTPS，HTTP 仅允许 loopback；Tool timeout 不能大于总时长。API Key 不可通过 Tool 修改。
- `playback`：`hardwareDecoding`、`videoSync`、`deinterlace`、`savePositionOnQuit`、`resumePlayback`、
  `interpolation`、`scale`、`chromaScale`、`downscale`、`deband`、`sigmoidUpscaling`、
  `targetColorspaceHint`、`targetColorspaceHintMode`、`targetPrimaries`、`targetTransfer`、`targetPeak`、
  `toneMapping`、`gamutMappingMode`、`hdrComputePeak`、`audioDevice`、`audioChannels`、`audioExclusive`、
  `audioPitchCorrection`、`audioLanguages`、`subtitleLanguages`、`subtitleAuto`、`subtitleFont`、
  `subtitleFontSize`、`subtitleScaleWithWindow`、`cache`、`cacheSeconds`、`demuxerMaxBytesMiB`、
  `demuxerMaxBackBytesMiB`、`cachePause`、`cachePauseInitial`、`cachePauseWaitSeconds`、
  `networkTimeoutSeconds`、`advancedOptions`。布尔项必须传 boolean，其余当前均传 string。

aria2 全局设置不在 `settings.*`，应使用 `downloads.global.get/set`。

## 诊断：`diagnostics.*`（3）

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `diagnostics.get` | `{}` | 只读 | 返回脱敏的版本、OS、架构、日志状态和插件数量。 |
| `diagnostics.logs` | `component:{app/aria2/mpv/quickjs/tool-audit/plugin-audit}, maximumLines?:integer=100` | 只读 | 读取指定组件日志尾部；行数范围 1–500，内容会截断和脱敏。 |
| `diagnostics.tools` | `{}` | 只读 | 返回当前完整 Tool 描述、输入/输出 Schema、风险、确认、取消和 dry-run 能力。 |

当本文与运行中的应用不一致时，以 `diagnostics.tools` 为准，因为插件状态、构建版本或后续代码可能已经变化。

## 界面：`ui.*`（3）

| Tool | 参数 | 风险 | 作用 |
|---|---|---|---|
| `ui.context` | `{}` | 只读 | 读取主窗口当前顶层页面，不检查任意控件或用户输入。 |
| `ui.navigate` | `page:{downloads/playback/agent/plugins/settings}` | 本地写入 | 切换主窗口页面。 |
| `ui.notify` | `message:string, severity?:{info/success/warning/error}=info` | 本地写入 | 显示最多 500 字符、约 5 秒后自动隐藏的非模态通知。 |

## 完整性检查

本文覆盖的静态 Tool 数量应保持为 109，按 namespace 分布如下：

| namespace | 数量 |
|---|---:|
| `downloads` | 15 |
| `playback` | 56 |
| `media` | 5 |
| `search` | 5 |
| `services` | 3 |
| `plugins` | 17 |
| `settings` | 2 |
| `diagnostics` | 3 |
| `ui` | 3 |

开发时可调用 `diagnostics.tools` 导出当前运行时清单，并将名称集合与本文逐项比较；CI 中的
`AutomationToolCatalogTests` 同时验证 namespace 覆盖、Schema 元数据和非只读调用的授权边界。
