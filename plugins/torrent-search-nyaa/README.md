# Nyaa Torrent Search

无界面 Service：`org.canx.search.nyaa/main`，实现 `org.canx.bittorrent.search@1.0`。通过 Nyaa RSS 搜索并返回标准 Torrent 结果。

`options` 支持 `endpoint`、`category`、`filter`、`user`、`sort`、`order` 和 `proxy`。

启用插件后可在 CanX 插件详情中打开“配置”，保存默认 HTTP 代理；调用方传入的 `options.proxy` 优先于该默认值。
