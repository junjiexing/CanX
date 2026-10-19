# bitmagnet Torrent Search

无界面 Service：`org.canx.search.bitmagnet/main`，实现 `org.canx.bittorrent.search@1.0`。默认调用
`http://127.0.0.1:3333/graphql`，搜索本地 bitmagnet DHT 索引。

`options` 支持 `endpoint` 和 `proxy`。

启用插件后可在 CanX 插件详情中打开“配置”，保存默认 HTTP 代理；调用方传入的 `options.proxy` 优先于该默认值。
