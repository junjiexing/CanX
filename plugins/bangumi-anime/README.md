# Bangumi Anime

这个 CanX 插件使用 Bangumi 官方 API 提供新番日历、番剧搜索、条目详情和本机收藏。

页面使用宿主的 `viewStack` 保留主页、详情和设置视图。打开详情后返回不会重新构建主页，搜索内容、日历选择和结果会继续保留。
首页按“新番日历 / 我的收藏 / 搜索番剧”排列；封面卡片使用宿主的响应式 `gallery`
组件，会随页面宽度自动换列。封面 URL 通过独立的 `ui.remoteImages` 权限交给宿主安全拉取，
不再由插件下载并转换为 Base64。

插件使用以下公开接口：

- `GET https://api.bgm.tv/calendar`：每日放送；
- `POST https://api.bgm.tv/v0/search/subjects`：动画条目搜索；
- `GET https://api.bgm.tv/v0/subjects/{id}`：条目详情。

“我的收藏”只保存在 CanX 的插件隔离存储中，不会写入 Bangumi 账户。网络设置支持
无凭据的 HTTP 代理，例如 `http://127.0.0.1:7890` 或 `http://proxy.example.com:7890`。
配置的代理同时用于 Bangumi API 和宿主下载封面。
此版本申请所有 HTTP(S) 主机和远程图片主机的访问权限，需要配套更新后的 CanX 宿主；
安装后需要批准权限。宿主仍禁止直连私网地址，显式配置的 HTTP 代理可以位于本机或私网。

```powershell
npm install
npm test
npm run lint
npm run pack:canx
```

条目信息来自 [Bangumi 番组计划](https://bgm.tv/)，使用时应同时遵守其版权声明和开发者
平台协议。封面可能属于原作品权利人，插件仅在用户请求时获取缩略图，不随插件包分发。
