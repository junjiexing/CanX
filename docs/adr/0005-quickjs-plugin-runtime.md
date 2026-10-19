# ADR-0005：QuickJS 插件运行时边界

- 状态：已接受
- 日期：2026-09-08

## 决策

每个插件使用独立的 QuickJS runtime/context。托管层不直接持有 `JSValue`，而通过版本化 C ABI 管理 runtime、求值、Promise、job queue 和字符串所有权。

插件调用宿主采用异步消息边界：JavaScript 的 `canx.invokeTool(name, inputJson)` 立即返回 Promise；native callback 只把 request ID 和复制后的参数放进托管队列；宿主执行统一 Tool 和权限检查后，在插件线程上显式 resolve/reject，再 pump QuickJS jobs。二进制媒体不通过此桥传输。

每个 runtime 必须配置内存、栈、单次执行 deadline 和 interrupt handler。网络、文件、密钥、计时器和 UI 均由 capability API 提供，默认 JS 环境不注入这些权限。

## 阶段 0 证据

QuickJS 2025-09-13 已从固定 URL/hash 构建为 native shim，并由 .NET 10 NativeAOT 验证：

- ES module 可执行；
- unmanaged callback 可把 Tool 请求安全交给托管层；
- Tool 结果可在稍后 resolve Promise，job queue 推进后 JS 得到结果；
- 16 MiB memory limit 会拒绝 64 MiB `ArrayBuffer`；
- 25 ms deadline 能中断无限循环；
- runtime/context/Pending Promise 可有序释放。

阶段 5 的生产 Host 验收进一步覆盖入口模块和 Service callback 的显式异常、OOM 与死循环。C ABI 会保留并检查 ES module 求值 Promise，避免把顶层 `throw` 误判为启动成功；插件执行位于专用线程，200 ms deadline 实测不会阻塞调用方，受控失败后的同一 runtime 仍可继续执行健康 callback。

Linux 原始 ABI 验证入口为 `scripts/verify-quickjs-shim.sh`；Windows 静态 NativeAOT 生产 Host 验证入口为 `scripts/verify-quickjs-host-windows-x64.ps1`。成功标志均为 `CANX_QUICKJS_OK`。

## 安全边界

内存和时间限制只提升可用性，不构成强安全沙箱。QuickJS 或宿主 native bridge 的漏洞仍可能影响进程；桌面端的非信任插件后续增加进程级隔离，移动端至少保持相同的消息协议和最小权限。阶段 5 已完成包内模块 loader、路径约束、签名和 API 版本契约；进程级隔离、其他目标 RID 与 iOS 商店政策留在阶段 7 验收。
