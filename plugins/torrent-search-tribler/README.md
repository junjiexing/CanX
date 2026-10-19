# Tribler Torrent Search

无界面 Service：`org.canx.search.tribler/main`，实现 `org.canx.bittorrent.search@1.0`。默认调用
`http://127.0.0.1:8085/api/metadata/search/local`。

`options` 支持 `endpoint`、`apiKey`、`category`、`hideAdult`、`sortBy` 和 `proxy`。当前只返回
本地元数据索引结果；Tribler 远端搜索通过异步事件流返回，不属于本 Service 的同步分页契约。

在 CanX 插件详情的“配置”中可保存默认 HTTP 代理和可选 API Key，并可点击链接打开 REST API 说明。
API Key 存入宿主的插件隔离凭据存储；调用方传入的 `options.apiKey` 或 `options.proxy` 优先于默认值。
