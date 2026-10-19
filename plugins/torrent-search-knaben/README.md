# Knaben Torrent Search

无界面 Service：`org.canx.search.knaben/main`，实现 `org.canx.bittorrent.search@1.0`。默认调用 `https://api.knaben.org/v1`，无需 API Key。
调用参数及统一返回格式见 [`docs/TORRENT-SEARCH-PLUGINS.zh-CN.md`](../../docs/TORRENT-SEARCH-PLUGINS.zh-CN.md)。

`options` 支持 `endpoint`、`proxy`、`categoryIds`、`hideUnsafe` 和 `hideAdult`。

启用插件后可在 CanX 插件详情中打开“配置”，保存默认 HTTP 代理；调用方传入的 `options.proxy` 优先于该默认值。
