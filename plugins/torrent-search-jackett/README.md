# Jackett Torrent Search

无界面 Service：`org.canx.search.jackett/main`，实现 `org.canx.bittorrent.search@1.0`。默认连接 `http://127.0.0.1:9117` 的 Torznab API。

在 CanX 插件详情的“配置”中可保存默认 HTTP 代理和 Jackett API Key，并可点击链接打开官方说明。
API Key 存入宿主的插件隔离凭据存储，不会写入普通配置文件；调用方仍可通过 `options.apiKey` 或
`options.proxy` 覆盖默认值。出于权限隔离，默认 manifest 只允许本机 IPv4 地址。远程部署时需要先修改
`http.scopes` 再打包安装。
