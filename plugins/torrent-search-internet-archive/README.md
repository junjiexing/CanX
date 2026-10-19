# Internet Archive Torrent Search

无界面 Service：`org.canx.search.internet-archive/main`，实现 `org.canx.bittorrent.search@1.0`。搜索 Internet Archive 的开放内容，
并返回条目自动生成的 `.torrent` 地址。

`options` 支持 `endpoint`、`mediaType`、`collection` 和 `proxy`。

启用插件后可在 CanX 插件详情中打开“配置”，保存默认 HTTP 代理；调用方传入的 `options.proxy` 优先于该默认值。
