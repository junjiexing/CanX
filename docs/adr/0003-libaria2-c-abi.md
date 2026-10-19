# ADR-0003：通过版本化 C ABI 使用 libaria2

- 状态：已接受
- 日期：2026-09-08

## 决策

CanX 不直接从 .NET 绑定 libaria2 的 C++ 类和 STL 类型。仓库维护一个薄 C++ wrapper，对外只暴露 C calling convention、固定宽度整数、不透明 handle、长度显式的数组和带 `struct_size` 的版本化结构体。

Shim 负责阻止 C++ exception 穿过 ABI、翻译对象所有权并提供稳定错误信息；业务状态机、线程调度和重试策略仍放在托管层。

应用每个进程只创建一个 aria2 session。所有 session API 在专用 engine thread 串行执行；`run(RUN_ONCE)` 最长可能阻塞约一秒，不能在 UI 线程运行。native callback 只进入无异常的通知队列，不在 callback 内重入 aria2。

## 阶段 0 证据

`native/aria2-shim` 和 `spikes/CanX.Spikes.Aria2Interop` 已在 libaria2 1.37.0 上验证：

- C++ shim 可由 CMake/Clang 构建并动态链接 libaria2；
- .NET 10 NativeAOT 可通过 source-generated P/Invoke 使用 shim；
- URI 添加、暂停、暂停状态读取、恢复、进度读取和完成均正常；
- C callback 能安全进入托管的 unmanaged entry point；
- `shutdown -> run until zero -> sessionFinal -> libraryDeinit` 可干净退出；
- 下载结果与本地 HTTP 源逐字节一致。

重复验证入口是 `scripts/verify-aria2-shim.sh`，成功标志为 `CANX_ARIA2_SHIM_OK`。

## 阶段 2 扩展

ABI 4 已扩展 URI 镜像、Torrent/Metalink、文件列表、bitfield、队列、全局统计、options、capability manifest、peer/server、做种状态和 stopped-result 清理；当前 ABI 5 继续增加运行时 piece 区间优先级，同时保证 libaria2 header、STL 类型和 C++ 对象不暴露给托管项目。清理 stopped result 后，CanX 可以复用原请求和 `.aria2` 控制文件重建失败任务，而不是对终止状态错误调用 `unpause`。

上游公开 C++ API 无法提供 option handler 元数据、实时 peer/server 明细及 Torrent 逐文件优先级，因此正式构建固定到 `junjiexing/aria2` 的统一集成分支 `canx/integration`。fork 增加扩展查询 API 以及 `bt-prioritize-file` / `bt-deprioritize-file` 任务选项。Shim 通过 `CANX_ARIA2_EXTENDED_API` 显式选择扩展模式：

- 正式静态构建开启扩展，返回编译特性、完整 option metadata、peer/server 和做种信息；
- 发行版 libaria2 动态 smoke test 关闭扩展，仍提供下载基线并通过 `extendedApi=false` 明确降级；
- 扩展专用调用在降级模式返回 `unsupported`，不会伪造能力。

固定源码与可审计构建参数见 ADR-0006。Linux x64 静态 NativeAOT 端到端验证结果为 ABI 5、10 项编译特性和 200 项 option metadata；真实 HTTP 多镜像任务、下载结果逐字节比对及无第三方动态 sidecar 均已通过。
