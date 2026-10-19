# ASSRT 字幕插件

此插件使用 [ASSRT API](https://assrt.net/api/doc) 提供 `org.canx.subtitle.search@1.0` 和
`org.canx.media.resolve@1.0` 服务，可供 CanX 的 `search.subtitles`、`search.play` 等工具使用。
搜索结果注明来自 assrt.net。

安装后打开插件设置，在 [ASSRT 用户面板](https://assrt.net/usercp.php) 获取并填入 API Token。
Token 保存在 CanX 的插件隔离凭据中，通过 HTTPS 请求的 `Authorization: Bearer` 标头发送，
不会写入插件包、搜索结果或下载 URL。

播放视频时，打开播放器的“轨道与字幕”，点击字幕轨右侧的放大镜按钮。
CanX 会用当前视频名称预填关键词；在“搜索接口”中选择 ASSRT 字幕，点击“搜索”，
选择结果和具体字幕文件，再点击“加载”。
文件夹按钮仍用于加载本地文件或媒体源中的字幕。

搜索接口每次最多返回 15 条，插件使用游标接续查询。选择字幕后才调用详情接口获取临时链接，
不会缓存会过期的下载地址。压缩包中的 SRT、ASS、SSA、VTT、SMI 文件若有独立下载链接，
也可直接播放；仅包含压缩包、图形字幕或缺少独立链接的结果无法直接播放。
ASSRT 的文件服务器可能返回 HTTP 链接，插件会保留该协议，并只接受 `fileN.assrt.net` 的地址。

```bash
npm ci --ignore-scripts
npm test
npm run lint
npm run pack:canx
```
