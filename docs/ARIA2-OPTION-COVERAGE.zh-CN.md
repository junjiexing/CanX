# aria2 Option 作用域覆盖报告

更新时间：2026-09-10

## 结论

Linux x64 正式 NativeAOT 构建所链接的 CanX aria2 fork 导出了 200 项 option metadata，全部具有唯一且非空的名称，未发现无法归类或无法从 CanX 访问的项目。

| 指标 | 数量 | CanX 入口 |
|---|---:|---|
| 总数 | 200 | 下载中心“能力”页、`downloads.capabilities` |
| 新任务可设置 | 115 | 新任务高级编辑器、常用设置、`downloads.add` / `downloads.addBatch` |
| 活动任务可修改 | 7 | 任务高级编辑器、常用设置、`downloads.options.set` |
| 等待任务可修改 | 108 | 任务高级编辑器、常用设置、`downloads.options.set` |
| 全局可修改 | 120 | 全局高级编辑器、常用设置、`downloads.global.set` |
| 隐藏 | 0 | 不在普通 UI 展示，仍保留于 capability 结果 |
| 只读 metadata | 72 | 能力页和 `downloads.capabilities` 可查看 |
| 未覆盖 | 0 | 无 |

同一个 option 可以属于多个可变作用域，所以各作用域数量之和不应等于总数。“只读”表示当前生命周期没有安全的修改作用域，并不表示从 capability manifest 中隐藏。

## 构建基线

- aria2：1.37.0，`junjiexing/aria2` commit `0782d46235cf4fa0b9931fcebc2a5eb1ed56fe8c`
- CanX aria2 ABI：5
- 编译功能：10 项
- 产物：Linux x64 NativeAOT 单文件，libaria2 及其第三方依赖静态链接

## 验证方式

`DownloadOptionCoverage` 检查空名称和重复名称，并将每项 metadata 映射为隐藏、只读或一个以上的可变作用域。正式 App 的下载 smoke 会从实际静态链接的 libaria2 读取 manifest，并输出：

```text
CANX_APP_CAPABILITIES abi=5 extended=true features=10 options=200 uncovered=0
CANX_APP_OPTION_SCOPE total=200 initial=115 active=7 reserved=108 global=120 hidden=0 readOnly=72 uncovered=0
```

重复验证：

```bash
CANX_STATIC_ROOT="$PWD/.cache/static-native/linux-x64" \
  scripts/verify-static-app-linux-x64.sh --skip-build
```

使用不带 CanX 扩展 ABI 的系统动态 libaria2 时，应用会明确报告 `extendedApi=false`，该兼容模式不作为正式发行版的完整覆盖依据。
