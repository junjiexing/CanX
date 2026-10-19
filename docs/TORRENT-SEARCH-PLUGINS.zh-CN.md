# 无界面 BitTorrent 搜索 Service

CanX API 2.0 不再内置 Torrent Provider 类型。每个无界面搜索插件都向统一 Service Registry
注册 `org.canx.bittorrent.search@1.0`，由聚合插件、其他业务插件或后续 AI Tool 发现和调用。

## 权限与调用

提供者声明其发布的 contract：

```json
{ "capability": "services.provide", "scopes": ["org.canx.bittorrent.search"] }
```

调用者可按 contract 或完整 Service ID 授权：

```json
{ "capability": "services.invoke", "scopes": ["org.canx.bittorrent.search"] }
```

```ts
const services = await canx.services.list({
  contract: "org.canx.bittorrent.search",
  majorVersion: 1,
});
const result = await canx.services.invoke(services[0].id, {
  query: "ubuntu",
  limit: 20,
  options: { hideUnsafe: true },
});
```

每个描述符都包含标题、用途、风险、受众以及完整输入/输出 JSON Schema。统一输入包含 `query`、
可选 `limit`、`cursor` 和 `options`；各实现通过自己的输入 Schema 描述 `options`。统一结果包含
`items`、可选 `nextCursor` 和 `total`，每个条目至少有 `id` 与 `title`，并可提供 InfoHash、
Magnet、Torrent URL、详情、封面、大小、热度、日期、分类、来源和附加属性。

调用插件只获得经过 Schema 校验的 JSON，不会继承服务插件的 HTTP、密钥或存储权限。面向用户
添加下载时仍须由界面插件在直接用户操作中调用 `canx.downloads.requestAdd()`，由宿主显示统一
确认界面。AI 通过 `search.torrents` 获取聚合结果：有 InfoHash 时返回可复制的公开 Magnet，
无凭据的公开 Torrent 直链也会返回。包含私有参数或本机地址的原始链接只保存在本次搜索缓存中；
AI 可用结果 ID 调用 `search.download`，由 CanX 添加下载。

## 随仓库提供的 Service

| 目录 | Service ID | `options` 要点 |
|---|---|---|
| `torrent-search-knaben` | `org.canx.search.knaben/main` | endpoint、代理、分类和安全过滤 |
| `torrent-search-apibay` | `org.canx.search.apibay/main` | endpoint、分类和代理 |
| `torrent-search-nyaa` | `org.canx.search.nyaa/main` | 分类、过滤、用户、排序和代理 |
| `torrent-search-internet-archive` | `org.canx.search.internet-archive/main` | 媒体类型、collection 和代理 |
| `torrent-search-jackett` | `org.canx.search.jackett/main` | endpoint、API Key、indexer 和分类 |
| `torrent-search-prowlarr` | `org.canx.search.prowlarr/main` | endpoint、API Key、indexerIds 和分类 |
| `torrent-search-bitmagnet` | `org.canx.search.bitmagnet/main` | GraphQL endpoint 和代理 |
| `torrent-search-tribler` | `org.canx.search.tribler/main` | endpoint、可选 API Key、分类和排序 |

Jackett、Prowlarr、bitmagnet 和 Tribler 默认只允许访问 `127.0.0.1`。如服务部署在其他主机，
需要在安装前按实际域名调整 manifest 的 `http.scopes`。Tribler 当前只封装同步返回的本地元数据
搜索，其远端异步事件流不属于该 contract。

八个搜索 Service 都声明了插件配置页，可保存默认本机 HTTP 代理。Jackett、Prowlarr 和 Tribler
还提供隐藏的 API Key 输入框与官方获取说明链接，密钥通过 `canx.secrets` 写入系统凭据存储。
单次调用的 `options.proxy` / `options.apiKey` 优先于插件默认值。

## 构建和打包

在任一插件目录运行：

```powershell
npm install
npm test
npm run pack:canx
```

生成的 `.canxplugin` 位于插件的 `artifacts` 目录。公共索引服务可能变更或下线，API 可用性和
搜索内容的授权状态由服务提供方决定。
