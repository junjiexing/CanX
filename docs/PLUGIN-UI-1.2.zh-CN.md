# CanX 插件 UI API 2.0

API 2.0 允许一个插件声明一个独立顶层标签页。插件仍运行在隔离的 QuickJS runtime 中，只向宿主提交声明式 JSON；插件不能创建 Avalonia 控件、注入 XAML、CSS 或脚本。

## Manifest

有页面的插件必须同时声明 `ui` 和 `ui.page`：

```json
{
  "manifestVersion": 1,
  "id": "org.example.catalog",
  "name": "Catalog",
  "version": "1.0.0",
  "apiVersion": "2.0",
  "entry": "dist/main.mjs",
  "ui": {
    "title": "资源目录",
    "icon": { "builtin": "search" }
  },
  "permissions": [
    { "capability": "ui.page" }
  ]
}
```

`ui.icon` 可使用宿主内置名称，也可引用插件包内的位图：

```json
{ "builtin": "search" }
```

```json
{ "asset": "assets/icon.webp" }
```

包内图标只接受 PNG、JPEG 和 WebP，不接受 SVG。安装器会检查路径、精确大小写、文件头和 2 MiB 大小上限；CLI 打包时会自动包含 Manifest 引用的图标。

## 挂载与生命周期

```ts
const page = await canx.ui.page.mount({
  content: {
    type: "stack",
    props: { spacing: "md" },
    children: [
      {
        id: "query",
        type: "searchInput",
        props: { label: "关键词", key: "query", placeholder: "输入关键词" },
      },
      {
        id: "search",
        type: "button",
        props: { label: "搜索", action: "search", variant: "primary" },
      },
      { id: "status", type: "text", props: { text: "等待搜索", tone: "muted" } },
    ],
  },
  async onEvent(event) {
    if (event.type === "change" && event.key === "query") {
      // event.value 是输入值
    }
    if (event.type === "action" && event.action === "search") {
      // 处理按钮、列表或表格事件
    }
  },
  onActivate() {
    // 用户切换到插件标签页
  },
  onDeactivate() {
    // 用户离开插件标签页
  },
});
```

一个 runtime 只能挂载一次 Manifest 页面。插件停用、重载或异常退出时，页面、回调和侧栏入口会一起移除。

## 组件

所有组件使用相同结构：

```ts
{
  id?: string,
  type: string,
  props?: object,
  children?: UiComponent[],
  layout?: {
    column?: number,
    columnSpan?: number,
    row?: number,
    rowSpan?: number,
    horizontalAlignment?: "left" | "center" | "right" | "stretch",
    verticalAlignment?: "top" | "center" | "bottom" | "stretch",
    width?: number
  }
}
```

支持的组件分组如下：

| 分组 | 组件 |
|---|---|
| 布局 | `stack`、`viewStack`、`grid`、`scroll`、`card`、`separator`、`spacer`、`expander`、`tabs`、`tab` |
| 展示 | `text`、`link`、`icon`、`image`、`badge`、`progress`、`spinner`、`alert`、`emptyState`、`keyValue` |
| 输入 | `button`、`textInput`、`searchInput`、`textArea`、`numberInput`、`toggle`、`select`、`radioGroup`、`slider` |
| 数据 | 虚拟化 `list`、响应式 `gallery`、虚拟化 `dataTable` |

`image.props.asset` 与 Manifest 位图一样，只能引用插件包内的 PNG/JPEG/WebP。远程图片不能绕过 `http` 权限直接加载。
`link` 使用 `label` 和 `url`，仅接受绝对 HTTPS 地址，并且只有用户点击后才会交给操作系统打开。
`textInput.props.password: true` 会隐藏输入内容，适合 API Token 等敏感配置；插件应通过 `secrets` 保存该值。

`tabs` 的直接子节点必须是 `tab`。`scroll` 和 `expander` 最多有一个直接子节点。`grid.props.columns` 为 1–12；子节点通过 `layout.column/row/columnSpan/rowSpan` 定位。

`stack` 是横向或纵向排列子组件的布局容器；`viewStack` 是保留页面状态的视图容器。宿主会创建并保留它的所有直接子视图，仅显示 `props.active` 指向的子视图。每个直接子视图必须有唯一 `id`，切换页面时只需更新容器：

```ts
await page.update([
  { target: "root-view", props: { active: "detail-view" } },
]);
```

此操作不会销毁其他子视图，其中的输入内容、选择状态和已构建结果均会保留。`viewStack` 仅可用于 Manifest 插件页面。

`list.props.items` 的条目结构为 `{ id, title, description?, badge?, image? }`。`image` 是最多 128 KiB 的
PNG/JPEG/WebP base64 data URI；缺少图片或解码失败时，宿主显示统一占位图。列表设置 `action` 后，
每个条目右下角显示由 `actionLabel` 指定文本的操作按钮，点击按钮产生 `action` 事件并在
`event.itemId` 中返回条目 ID；仅选择条目不会触发操作。`dataTable.props.columns` 使用
`{ key, title, width? }`，`rows` 只接受标量单元格；表格的操作事件在 `event.row` 中返回所选行。

`gallery` 使用与 `list` 相同的条目结构，把封面置于标题上方，并根据可用宽度自动换列。可通过
`itemWidth`（96–360）、`imageHeight`（96–540）和 `spacing` 调整卡片；设置 `action` 后点击整张
卡片产生带 `event.itemId` 的操作事件。独立的 `image` 组件也可用 `source` 显示相同格式和大小
限制的内联位图；`source` 与包内 `asset` 不能同时设置。

## 增量更新和徽标

完整替换页面：

```ts
await page.render(nextContent);
```

按组件 `id` 原子更新属性：

```ts
await page.update([
  { target: "status", props: { text: "完成", tone: "success" } },
  { target: "results", props: { rows: resultRows } },
]);
```

属性值设为 `null` 表示删除该属性。所有 patch 会在副本中合并，整棵树重新通过 schema 后才一次性生效；目标缺失、目标重复或新属性非法时，原页面保持不变。

侧栏徽标：

```ts
await page.setBadge({ text: "3", tone: "accent" });
await page.clearBadge();
```

徽标最多 8 个字符。

## 下载确认

需要从页面把 Magnet 或 HTTP 资源交给下载中心时，声明 `downloads.prompt`：

```json
{ "capability": "downloads.prompt" }
```

然后只能在页面 `action`/`change` 用户事件处理期间调用：

```ts
await canx.downloads.requestAdd({
  kind: "magnet",
  source: "magnet:?xt=urn:btih:...",
});
```

宿主会验证事件令牌和 URI，并打开 CanX 标准“添加下载任务”确认页。该权限不允许插件静默创建任务；生命周期回调、启动代码和后台任务调用会返回 `user_gesture_required`。

## 限制

- 页面树最多 8 层、256 个节点、4 MiB JSON；普通工具调用仍使用较小的通用消息限制；
- 单次增量更新 1–64 个 target；每个页面最多 30 次更新/秒；
- `id` 在页面内必须唯一；
- 列表和表格最多 1000 行，表格最多 20 列；
- 不接受未知组件、未知属性、任意样式类、XAML、Avalonia 类型名或矢量图片。

## 版本要求

宿主只加载 `apiVersion: 2.0`。独立页面还必须声明 Manifest `ui` 与 `ui.page`，并在启动时调用一次
`canx.ui.page.mount()`。给需要局部刷新的组件增加唯一 `id` 后可使用 `page.update()`；如需从页面
添加下载，还要声明 `downloads.prompt`，并在用户事件内调用 `requestAdd()`。

可参考 [mock-magnet-search](../plugins/example/mock-magnet-search/README.md)。它演示独立页面、HTTP 搜索、数据表、增量更新和下载确认。

## 本地测试

`@canx/plugin-api/mock-host` 支持 `host.page`、`host.invokePage(event)` 和 `host.downloadPrompts`：

```ts
const host = await createMockHost({ workspace: ".", http: mockHttp });
await host.loadPlugin("./dist/main.mjs");
await host.invokePage({ type: "action", action: "search" });
```

Mock Host 仍会执行 Manifest 权限检查，网络访问必须由测试显式提供 handler。
