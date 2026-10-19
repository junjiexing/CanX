# CanX 插件 Manifest v1

插件包根目录必须包含 UTF-8 编码的 `canx-plugin.json`。当前宿主只接受
`manifestVersion: 1` 和 `apiVersion: 2.0`；旧版或未知字段都会被拒绝。

```json
{
  "manifestVersion": 1,
  "id": "org.example.download-search",
  "name": "Example Download Search",
  "description": "Searches the example download catalog.",
  "version": "1.0.0",
  "apiVersion": "2.0",
  "entry": "dist/main.mjs",
  "permissions": [
    { "capability": "services.provide", "scopes": ["org.example.download.search"] },
    { "capability": "http", "scopes": ["api.example.org", "*.cdn.example.org"] }
  ]
}
```

`id` 使用小写反向域名格式，`version` 使用 SemVer 2.0。`entry` 必须是插件包内的
`.js` 或 `.mjs` 相对路径，不能包含反斜杠、盘符、空路径段、`.` 或 `..`。

权限默认拒绝。省略 `permissions` 等同于没有任何宿主权限。v1 支持：

- `tools.invoke`：scope 是允许调用的 Tool 名称；只能授予只读 Tool 和宿主固定的可逆播放/下载
  控制（暂停、继续、跳转、音量、字幕、滤镜、排队等；读取文件的滤镜会被拒绝），设置、插件管理、Workspace、mpv property/command、
  aria2 选项、下载完成动作与删除类 Tool 即使列入 scope 也会以 `tool_not_plugin_invocable` 拒绝；
- `services.provide`：scope 是允许发布的 Service contract；
- `services.invoke`：scope 是允许调用的 contract、完整 Service ID 或 `*`；
- `ui.page`、`ui.configuration`、`ui.contribute` 与 `downloads.prompt`；
- `ui.remoteImages`：允许声明式页面显示远程 PNG/JPEG/WebP；scope 可为精确主机名、
  `*.example.org` 子域通配或 `*`；
- `media.stream`：创建由宿主管理的不透明媒体流；
- `playback.read`、`playback.control`、`downloads.read` 和 `downloads.manage`；
- `http`：scope 是允许访问的主机名、通配子域名或 `*`（所有 HTTP(S) 主机），不接受完整 URL；
- `storage`：插件独享的宿主存储；
- `secrets`：scope 是插件可以使用的秘密名称。

插件不会因为安装而自动得到文件系统、网络、密钥、播放器或下载任务权限。
宿主把授权转换成不可变权限集合；Tool 名称和 secret 名称必须精确匹配，HTTP 只允许
`http`/`https` 且按 URI 的规范化主机名匹配。`*.example.org` 只覆盖其子域，不覆盖
`example.org` 本身。`http: ["*"]` 允许访问任意 HTTP(S) 主机，但直接请求私网或本机地址仍受网络防护限制；
具有 `http: ["*"]` 权限的插件可使用显式配置的本机或私网 HTTP 代理。升级时宿主会分别计算新增、移除和 scope 变化，任何新增权限都需要
重新确认。

`ui.remoteImages` 与通用 `http` 权限彼此独立。插件只需显示封面时可以只申请前者，不会因此
获得 `canx.http.request()`。页面中的 `image.source` 以及 `list`/`gallery` item 的 `image`
可直接填写 HTTP(S) URL；宿主负责下载，Avalonia 控件不会直接联网。即使用户批准 `*`，宿主仍会
对初始地址和每次重定向重新检查协议与公网地址，拒绝 loopback、私网、链路本地和保留地址，且不带
Cookie、认证信息或插件自定义 Header。请求只使用 GET，仅接受签名匹配的 PNG/JPEG/WebP，单图上限
2 MiB，并在插件 runtime 生命周期内做有界内存缓存。插件停止后缓存与在途请求一并释放。

## 插件包

本地插件包使用 ZIP 容器，建议扩展名为 `.canxplugin`。Manifest 和其声明的入口模块
必须存在于包内。安装器限制压缩及解压大小、文件数量和单文件大小，并拒绝绝对路径、
父目录穿越、符号链接及大小写冲突的重复路径。新安装先完整解压到同卷 staging 目录，
验证成功后再原子移动到正式目录；升级必须由调用方明确允许，并在切换失败时恢复旧版本。
每次成功升级保留一个同卷回滚版本；插件管理页可显式交换当前版本与回滚版本，回滚点和旧版
启用/权限批准状态会跨应用重启持久化。卸载插件时同时清理对应回滚版本。

包可包含根目录 `canx-signature.json`。签名格式固定为 ECDSA P-256/SHA-256，覆盖包内除
签名文件自身外的精确文件清单和每个文件的 SHA-256；安装器在切换目录前校验文件清单、
哈希、公钥标识与签名。签名有效和签名者受信任是两个独立状态，宿主策略可以分别要求
“必须签名”或“必须由受信 key ID 签名”。安装、升级、回滚、权限批准、停用、卸载和开发模式
热重载会追加写入 `logs/plugin-audit.jsonl`；每项变更包含执行前的 `Started` 和提交后的 `Completed`
阶段，记录前后版本、包哈希、签名状态、权限指纹和结构化权限变化。`Started` 无法持久化时不执行；
提交后 `Completed` 写入失败只记入应用日志，避免向调用方伪报“失败”而诱发重复操作。

## 运行时边界

每个已启动插件拥有独立 QuickJS runtime/context 和专用线程。入口以 ES module 求值；
原生 callback 只复制并入队 Tool 请求，不在 callback 中重入 QuickJS。宿主在异步边界验证
`tools.invoke` 的精确 scope、JSON 格式及输入输出大小，完成后回到插件线程 resolve/reject
Promise 并 pump job queue。默认限制为 16 MiB runtime 内存、512 KiB 栈、250 ms 单次 JS
执行 deadline、30 秒 Tool 超时和 128 个排队请求。异常、拒绝和超时写入独立
`quickjs.log`，但不会把 Tool 参数或返回值写入日志。

启用应用日志后，插件的 `console.debug/log/info/warn/error` 会写入 `quickjs.log`，每条都带
插件 ID；宿主限制为每个插件每秒 100 条、每条至多 16 KiB UTF-8，超限日志会被丢弃。
跨插件 Service 调用只记录 caller、目标、contract、耗时、成功状态和错误码，不记录请求或
响应正文。全部插件加载完成以及注册表稳定后，宿主会输出包含完整输入/输出 JSON Schema 的
Service Registry 快照，供诊断和后续 AI Tool 发现使用。

插件包中的全部 `.js`/`.mjs` 模块会在入口执行前经过数量、单文件大小、总大小和
reparse-point 检查，然后注册到 QuickJS 的内存模块表。相对 `import` 只会解析到该表中
已经验证的包内路径，缺失模块或试图越过插件根目录都会终止插件启动，模块加载过程不会
回调托管文件系统。

## 生命周期

宿主启动时扫描插件目录并读取独立的 `.plugin-state.json`。新安装默认停用；用户批准
当前权限集合后才会启动。批准记录绑定到规范化权限指纹，升级新增 capability 或 scope
会自动停用插件并要求再次确认；只移除权限的升级可以保持原启用状态。禁用会销毁对应
runtime，应用退出只停止 runtime 而保留下次启动意图。卸载先把目录移动到同卷隐藏目录，
状态持久化成功后才清理，避免状态写入失败留下半卸载结果。
回滚使用与升级相同的安装锁和同卷目录交换；若状态持久化失败会把目录交换回原状态，并恢复
原版本运行意图。回滚到曾经批准的权限集合时可恢复启用状态，未获批准的集合仍保持停用。

## JavaScript SDK v2.0

宿主在入口模块之前注入只读的 `canx` 对象。当前首个稳定接口为：

```js
const state = await canx.tools.invoke("playback.get", {});
console.log(canx.apiVersion); // "2.0"
```

SDK 负责 JSON 编解码；成功时 Promise 返回已解析的值，失败时以包含 `code` 和
`message` 的对象 reject。插件看到的是冻结后的 SDK，原始 native bridge 不再暴露。
调用仍需同时通过 Manifest 中 `tools.invoke` 的精确 scope 和插件可调用 Tool 白名单
（`ToolRegistryPluginDispatcher.PluginInvocableWriteTools`，只读 Tool 之外只允许可逆的播放/下载控制），
审计 caller 使用 `plugin:<插件 ID>`。安装时的一次性权限批准不能替代逐次确认，因此高风险 Tool
只能由宿主界面或经用户逐次批准的 Agent 调用。`downloads.add`、`downloads.addBatch` 和 `search.download` 是不可授予的
确认专用 Tool：即使 Manifest 显式列为 scope，插件调用仍返回 `user_confirmation_required`；新增任务
必须由直接用户手势通过 `downloads.prompt` 打开宿主确认页。

`canx.storage` 提供 `get/set/remove/keys/clear`，数据按插件 ID 隔离并原子持久化；默认限制
单值 64 KiB、总量 1 MiB 和 1024 个键。`canx.secrets` 只允许操作 Manifest 中逐项声明的
secret 名称，凭证复用系统安全存储且不能枚举。`canx.http.request()` 只接受 HTTP(S)，
初始 URL 和每次重定向都重新应用域名 allowlist，并限制超时、并发、Header、请求体和
响应体；跨源重定向不会转发认证 Header。生产网络 handler 会在每次 TCP 连接时解析完整地址集合，
只允许公网地址并直接连接已验证 IP；loopback、RFC 1918/CGNAT、链路本地和保留地址均被拒绝，
因此域名 allowlist 不能通过 DNS rebinding 转入本机或内网。拥有 `http: ["*"]` 权限时，
显式配置的 HTTP 代理可以位于本机或私网；直连仍经过公网地址检查。请求设置 `responseType: "base64"` 时可安全读取
图片等二进制响应，返回值的 `bodyEncoding` 会标记实际编码，默认仍为 UTF-8 `text`。可选的
`proxy` 只接受无凭据和路径的绝对 HTTP 代理地址；目标主机及代理主机都必须通过同一份 `http`
scope allowlist。
对于目标数据位于页面开头、但页面尾部很大的场景，请求可设置不超过宿主上限的
`maximumResponseBytes` 并显式启用 `truncateResponse`；宿主读取到该上限后停止，响应以
`truncated` 标记。该模式只适合能够安全解析不完整文档的调用方，不会提高插件的全局响应上限。

`canx.media.stream.create()` 接受 HTTP(S) 文件或 HLS recipe，并返回不透明的 `canx://` 地址。
recipe 的主地址及 `allowedHosts` 必须同时通过 Manifest `http` scope；初始请求、重定向、HLS
子播放列表、分片、密钥和初始化片段都会逐跳复查域名与公网地址。宿主只接受
`Accept`、`Accept-Language`、`Origin`、`Referer`、`User-Agent`，支持按 host 覆盖 Header，
不会把真实 URL 或 Header 交给 libmpv。普通文件继续使用 `canx://` Stream Callback；HLS 则在宿主内部
映射到仅监听 `127.0.0.1` 的临时 HTTP Gateway，让 FFmpeg 按完整 HLS 时间线解复用、seek 和预读。
所有 HLS 会话共用一个系统分配的端口，只暴露显式注册的 HLS 资源；全部 HLS 句柄释放后关闭端口。
插件仍只获得不透明的 `canx://` 句柄，播放列表和历史记录也不会保存临时 HTTP 地址。
句柄释放、插件停用或退出时全部注册和在途请求都会撤销。
`canx.media.stream.play()` 只能由插件页面的直接用户事件调用；后台解析器可使用 `create()`
生成供标准 `org.canx.media.resolve` Service 返回的媒体源。

`canx.html.extract()` 对插件已经取得的 HTML 执行受限的批量 CSS 选择器提取。插件提供行选择器和
一组相对字段选择器，字段可读取文本、内部/外部 HTML 或属性，也可返回多值数组。解析由宿主完成，
不向插件暴露 DOM 对象、脚本执行或网络能力；单次文档、字段、结果行和输出大小均有硬限制。该接口
不授予网络权限，HTML 仍须通过已获授权的 `canx.http.request()` 获取。

插件通过 `canx.services.register()` 向唯一的 Service Registry 发布 JSON 接口。定义包含插件内
ID、稳定 contract、contractVersion、标题、用途说明、风险等级、`plugin`/`ai` 受众、输入与
输出 JSON Schema，以及 `invoke()` 实现。注册项绑定 runtime 生命周期；宿主对每次调用的输入
和输出执行 Schema 校验，并限制载荷、调用深度和调用环，插件停止时自动撤销服务。

调用者通过 `canx.services.list()` 按 contract、majorVersion 查找服务，通过
`canx.services.describe()` 读取完整 Schema，并通过 `canx.services.invoke()` 调用。发布需要
`services.provide`，发现和调用需要 `services.invoke`；scope 可绑定 contract、完整 Service ID
或 `*`。被调用插件的 HTTP、密钥、存储等权限不会传递给调用者。宿主不再内置搜索、字幕、
媒体解析、下载或 Torrent 专用接口类型；这些能力都以普通 contract 表达。随仓库提供的
BitTorrent 服务统一实现 `org.canx.bittorrent.search@1.0`。

AI 不能通过通用 `services.invoke` 直接调用标准 media/subtitle/torrent search 与 media
resolver contract，必须使用 `search.media`、`search.subtitles`、`search.torrents` 取得宿主缓存的
不透明结果 ID，再使用 `search.play` 或 `search.download`。Torrent 搜索结果会向 AI 展示可分享的
Magnet 或无凭据的公开 Torrent URL；含私有参数的原始下载地址、媒体 URL、解析参数和媒体 Header
留在宿主缓存中。插件间调用仍可按 `services.invoke` 权限正常使用这些 contract。

服务返回的 Magnet、HTTP 媒体地址或其他数据本身不会直接触发宿主动作。界面插件要添加下载，
仍需在直接用户操作中调用 `canx.downloads.requestAdd()`，由桌面端显示标准确认界面。

`canx.playback.invoke()` 和 `canx.downloads.invoke()` 只接受 SDK 类型中列出的稳定动作，
再映射到统一 Tool Registry；读操作与控制/管理操作分别要求 `*.read` 和
`*.control`/`*.manage` capability。`downloads.manage` 不包含新增/批量新增任务；任意 mpv
command/property、带路径的打开操作和删除下载文件不在插件稳定动作集合中。两者的 `subscribe()` 使用宿主定时采样，默认每秒一次、最短
500 ms，每插件最多 8 个订阅；返回的 subscription 可取消，插件停止时宿主也会统一取消。

`canx.ui.contribute()` 只接受声明式贡献。v1 固定支持插件导航页、主页/下载详情/播放器侧栏、
命令面板、应用/下载/播放菜单、插件设置和状态卡片 slot；组件白名单为 `stack`、`text`、
`button`、`badge`、`progress`、`icon`、`separator`、`toggle`、`select` 与 `textInput`。
组件属性、颜色语义、排版、间距、图标和按钮样式都使用宿主 token 白名单，树深最多 8 层、
节点最多 256 个。注册表仅保存验证后的 JSON，并把交互回调绑定到 runtime 生命周期；XAML、
Avalonia 类型名、样式类、脚本字符串和未知属性都会被拒绝。

## TypeScript 开发工具

仓库的 `sdk/canx-plugin-api` 同时提供类型声明、模板、Node 模拟宿主和 `canx-plugin` CLI。
`canx-plugin init` 创建 TypeScript 工作区；`lint` 对 manifest、权限、入口路径和大小写做严格
检查；模拟宿主默认拒绝未声明的 Tool、HTTP 域名、secret 和 capability，测试不会自行发起
网络访问。`pack` 只收集 `package.json.files`、manifest 与入口，固定 ZIP 文件顺序和时间戳，
相同输入可得到逐字节一致的未签名包。`keygen` 可创建 P-256 密钥，`pack --key` 生成与宿主
安装器兼容的签名包。私钥应保存在仓库外，不能随插件包分发。

开发模式开启时，宿主监视已安装插件目录；模块变化经过防抖后会销毁原 runtime，并用新的
包内文件重新创建独立 runtime。关闭开发模式或退出应用会停止监视器。Manifest 或权限变化
仍应通过重新打包和升级流程进入，不能借热重载扩大权限。

插件管理页提供只读调试控制台，最多显示 `quickjs.log`、上一份滚动日志和
`plugin-audit.jsonl` 的最近记录，并可手动重载所选插件。控制台不提供任意 JavaScript REPL，
避免绕过包签名、权限审批和执行 deadline。
