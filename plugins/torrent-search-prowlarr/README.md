# Prowlarr Torrent Search

无界面 Service：`org.canx.search.prowlarr/main`，实现 `org.canx.bittorrent.search@1.0`。默认连接 `http://127.0.0.1:9696/api/v1/search`。

在 CanX 插件详情的“配置”中可保存默认 HTTP 代理和 Prowlarr API Key，并可点击链接打开官方说明。
API Key 存入宿主的插件隔离凭据存储，不会写入普通配置文件；调用方仍可通过 `options.apiKey` 或
`options.proxy` 覆盖默认值。远程部署时需要按服务域名修改 manifest 的 `http.scopes`。
