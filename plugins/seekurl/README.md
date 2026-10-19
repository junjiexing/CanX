# SEEKURL for CanX

通过秀云公开的 SEEKURL 接口搜索电影、电视剧，在 CanX 原生页面选择来源和剧集，直接使用 CanX/libmpv 在线播放。

## 使用

1. 安装插件包并启用，在导航栏打开 **SEEKURL**。
2. 输入片名，可加年份缩小范围；结果显示年份、分辨率、来源编号和集数。
3. 选择资源，点击对应剧集的“播放”。结果和剧集超过 50 项时可翻页。
4. 链接过期时重新搜索；片源拒绝访问或不可用时选择其他来源。

插件也提供 `org.canx.media.search@1.0`、`org.canx.media.resolve@1.0`，可以通过 Agent 的 `search.media`、`search.play` 使用。

## 接口与播放

- 搜索：`POST https://xiuyun.org/index/seek/get`，JSON 请求体为 `{"kw":"关键词"}`。
- 解析：`POST https://xiuyun.org/index/Seek/getm3u8`，JSON 请求体为 `{"token":"剧集令牌"}`。
- 解析令牌来自搜索结果的 `urls` 字段，格式为 `剧集名$令牌#剧集名$令牌`。每次点击播放时重新解析，令牌只在内存中保存，不写入 UI、AI 搜索结果或设置文件。
- HLS 播放前读取主列表、子列表及音轨引用，确定本次媒体请求所需的域名。分片、音频和字幕由 CanX 的媒体流宿主读取，原始媒体字节交给 libmpv；插件不转码或处理音频。
- 整集进度条、跨分片拖动与连续播放需要包含 HLS Gateway 修复的 CanX 主程序。宿主仅为 HLS 开启一个临时本机端口，普通文件、SMB 和边下边播仍使用原有通道；旧版宿主可能把分片当成独立视频，单独更新插件无法修复。
- 支持 HLS 及直接媒体文件地址。媒体域名由接口动态返回，因此 Manifest 声明通用 HTTP 权限；每个播放会话只允许其媒体地址和播放列表引用的域名，网络访问仍经过 CanX 的宿主检查。
- 接口不要求配置 API Key。片源可用性由上游决定。

## 构建

```bash
npm ci --ignore-scripts
npm test
npm run lint
npm run pack:canx
```

产物为 `artifacts/org.canx.seekurl-1.0.0.canxplugin`。
