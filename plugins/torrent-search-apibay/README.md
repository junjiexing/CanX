# apibay Torrent Search

无界面 Service：`org.canx.search.apibay/main`，实现 `org.canx.bittorrent.search@1.0`。默认调用 `https://apibay.org`，无需 API Key。
公共服务不保证可用性，不建议作为唯一数据源。

`options` 支持 `endpoint`、`category` 和 `proxy`。

启用插件后可在 CanX 插件详情中打开“配置”，保存默认 HTTP 代理；调用方传入的 `options.proxy` 优先于该默认值。
