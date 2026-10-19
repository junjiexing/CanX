# Mock Magnet Search 示例插件

该示例通过本机 HTTP Mock API 搜索资源，并在 CanX 左侧导航中声明独立的 `Magnet Search` 标签页。

插件页使用 `searchInput`、`button`、`text` 和虚拟化 `list`。每条结果显示 API 返回的 PNG 封面、标题、简介、大小及做种信息；API 未提供封面时由宿主显示占位图。点击条目右下角的“Download”按钮后，插件解析 Magnet 链接并调用 `canx.downloads.requestAdd()`；仅选择条目不会开始下载。搜索按钮后的“Options”可配置并持久化 HTTP 代理，搜索、封面和下载地址解析请求都会使用该代理。

先启动不依赖第三方包的 Python Mock Server：

```powershell
python .\mock_server.py
```

然后构建、测试并打包插件：

```powershell
npm install
npm test
npm run lint
npm run pack:canx
```

在 CanX 的“插件”页面安装 `artifacts/org.canx.examples.mock-magnet-search-1.5.0.canxplugin`，批准以下权限并启用：

- `ui.page`：提供独立标签页；
- `downloads.prompt`：从用户点击事件打开标准下载确认页；
- `http`（仅 `127.0.0.1`）：访问 Mock Server 和本机 HTTP 代理；
- `storage`：持久化 HTTP 代理地址。

“Options”中的代理地址为空时不设置插件专用代理；非空时填写类似
`http://127.0.0.1:7890` 的地址。因为代理主机同样受 HTTP allowlist 约束，本示例只允许使用
`127.0.0.1` 上的代理。

Mock Server 提供：

- `GET /health`
- `GET /search?q=<关键词>&limit=<数量>&cursor=<偏移>`（结果包含标题、简介及封面 URL；部分结果故意省略封面以验证占位图）
- `GET /covers/<结果 ID>.png`
- `GET /resolve/<结果 ID>`

返回的 Magnet 使用测试 info-hash，不保证网络中存在对应内容；它用于验证插件页面、HTTP 权限、搜索、解析和下载确认流程。
