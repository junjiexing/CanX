# Anime Find for CanX

这是 [`cocofhu/anime-find`](https://github.com/cocofhu/anime-find) 的 CanX 原生移植版。
它不包含 DSH React ToolView，而是使用 CanX 声明式 UI、Service Registry、标准下载确认页和
libmpv Stream Callback。

## 能力

- 聚合 Mikan、AniBT、AnimeGarden，并用 Bangumi 补充标题、评分、标签和简介；
- 在 CanX 独立“搜番”页面搜索、查看资源、确认下载和搜索在线播放剧集；
- 番剧页面分为“详情和评论”“在线资源”“下载资源”三个标签页，默认展示详情和评论；搜索、播放和下载结果只更新对应内容，保留当前标签页；
- 封面 URL 通过 `ui.remoteImages` 交给宿主安全拉取、校验和缓存，不再转成 Base64 塞入 UI JSON；
- 发布 AI/插件可发现的 `org.canx.anime.search@1.0` 与 `org.canx.anime.detail@1.0`；
- 发布标准 `org.canx.bittorrent.search@1.0`、`org.canx.media.search@1.0` 和
  `org.canx.media.resolve@1.0`，供 `search.torrents`、`search.media`、`search.download`、
  `search.play` 使用；
- 兼容静态 CSS 和受限 XPath 规则、MacCMS `player_aaaa.url`、Header 按域覆盖及 HLS；
- 媒体经不透明 `canx://` Stream Callback 直接交给 libmpv，不落地，也不启动本地 HTTP 服务。

AI 的标准搜索结果只含宿主生成的短期 ID。Magnet、Torrent URL、真实媒体 URL 和媒体 Header
不会进入模型上下文；下载和播放分别由 `search.download` / `search.play` 消费缓存结果。

## 构建与打包

```bash
npm ci --ignore-scripts
npm test
npm run lint
npm run pack:canx
```

产物位于 `artifacts/org.canx.anime-find-1.1.1.canxplugin`。安装后需在 CanX 插件页批准权限并启用。
默认搜索源仅启用 Mikan；AniBT、AnimeGarden 和在线播放规则可在“搜番设置”中调整。

规则只能访问 Manifest 已声明的域名。当前随包规则是上游的 xfdm 静态试点规则；依赖 WebView、
执行网页 JavaScript 或拦截浏览器请求的规则不受支持。

本移植的上游版权与 MIT 许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。CanX 新增代码
按仓库的 GPL-3.0-or-later 发布。
