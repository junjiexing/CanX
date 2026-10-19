# CanX 插件

`plugins/` 下的插件提供可直接使用的搜索与媒体能力，包括 Bangumi Anime、Anime Find、SEEKURL 在线视频、ASSRT 字幕和各个 BitTorrent 搜索源。
`plugins/example/` 下的四个插件用于演示 SDK 和宿主能力，其 Manifest ID 以 `org.canx.examples.` 开头。

每个插件都是独立的 Node 项目，包含 `canx-plugin.json`、源码和测试。以 Anime Find 为例：

```bash
cd plugins/anime-find
npm ci --ignore-scripts
npm test
npm run lint
npm run pack:canx
```

`pack:canx` 生成可安装的 `.canxplugin` 文件。其他插件和演示插件使用相同命令，在各自目录执行即可。
