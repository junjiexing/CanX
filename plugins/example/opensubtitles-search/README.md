# OpenSubtitles 字幕搜索

安装后在插件设置中填写 OpenSubtitles API Key。Key 保存在插件独立的系统凭据存储中，不会写入插件包或日志。

播放视频时打开“选择字幕” → “在线搜索”，在“搜索接口”中选择 OpenSubtitles，然后搜索。
搜索结果选中后可加载对应字幕。

```sh
npm install
npm test
npm run lint
npm run pack:canx
```

接口使用受 OpenSubtitles 服务条款和请求配额限制。
