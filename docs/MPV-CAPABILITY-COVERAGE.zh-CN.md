# mpv Property / Command 能力覆盖报告

更新时间：2026-09-15

## 结论

CanX 不维护容易随 mpv 升级失真的手写名称表。libmpv 初始化后，应用直接读取当前构建的 `property-list` 与 `command-list`，并为每项能力附加 CanX 的访问策略和入口。当前 Linux x64 构建发现 968 项 property、87 项 command，共 1,055 项，未覆盖 0 项。

| 类型 | 总数 | 专用 Tool | 受限高级入口 | 明确禁用 |
|---|---:|---:|---:|---:|
| Property | 968 | 66 | 888 | 14 |
| Command | 87 | 16 | 24 | 47 |

“专用 Tool”表示该能力至少有一个经过参数约束的 `playback.*` 入口；“受限高级入口”表示通过 `playback.property.get/set` 或 `playback.command.execute` 访问；“明确禁用”表示其可能泄露凭证、破坏宿主契约、任意读写文件、加载代码/媒体、注入输入、启动进程或退出宿主。高级命令采用保守 allowlist，只有明确证明不触及文件、进程、代码/媒体加载和宿主生命周期的命令可执行；未知命令默认禁用。某些具有安全专用 Tool 的原生命令（例如 `loadfile`、`sub-add`、`screenshot-to-file`）仍禁止从通用命令入口执行，因此统计时归入“专用 Tool”。

`CanRead`、`CanWrite` 和 `CanExecute` 描述 CanX 的安全策略，不承诺 property 在当前播放状态下可用，也不把只读 property 错误声明为原生可写；最终类型、范围、当前可用性和只读限制仍由 libmpv 校验。manifest 只列出当前平台构建实际注册的能力，编译开关同时保存在 `configuration` 字段；因此不同 RID 的差异可以直接比较，而不会误报当前二进制不存在的后端。

## 版本字段

- manifest schema：1；
- mpv：0.41.0，固定 fork commit `0c9de6514f63788686750cb000fdd5a55af234d5`；
- libmpv client API：2.5；
- 同时记录 `ffmpeg-version`、`platform` 和完整 `mpv-configuration`；
- 对插件和 Agent 的入口：`playback.capabilities`。

升级 mpv 或改变编译配置后，不需要手工复制官方文档中的名称；真实播放 smoke 会读取新清单并重新执行覆盖检查。schema 的字段语义发生不兼容变化时才增加 `schemaVersion`。

## 验证规则

`PlaybackCapabilityCoverage` 会拒绝：

- 空名称或重复的 property/command；
- 未归类的能力；
- 声明使用专用 Tool 但没有 Tool 名称的能力；
- 声明使用高级入口但实际没有对应访问权限的能力；
- 禁用但仍可从通用入口访问，或没有解释禁用原因的能力；
- 名称或类型为空的 command 参数。

真实 libmpv 回归输出：

```text
CANX_APP_PLAYBACK_OK ... capabilities=true propertyCapabilities=968 propertyDedicated=66 propertyAdvanced=888 propertyBlocked=14 commandCapabilities=87 commandDedicated=16 commandAdvanced=24 commandBlocked=47 capabilityUncovered=0 ...
```

重复验证：

```bash
dotnet test CanX.slnx -c Release --no-restore
CANX_APP_OUTPUT="$PWD/src/CanX.App/bin/Release/net10.0/linux-x64" \
  scripts/verify-playback-linux-x64.sh --skip-publish
```
