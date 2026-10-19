# ADR 0007：应用凭证使用操作系统安全存储

- 状态：Accepted
- 日期：2026-09-08

## 决策

下载会话只保存 `CredentialRef`，不得把带 user-info/query/fragment 的认证 URI，以及名称包含 password、passwd、secret、token、cookie、header、authorization 或 `-user` 的 aria2 option 值写入 `downloads.json`。

网络媒体源遵循同一边界。`media-sources.json` 为每个播放源保存稳定 ID、显示名称、协议、无 user-info/query/fragment 的 endpoint、用户名和 `CredentialRef`；同一协议可保存多个来源，本地文件夹作为不含凭证的来源。密码只写入操作系统凭证库。更新或删除来源时同时清理不再引用的凭证；删除来源也清理该来源根下的目录收藏。系统凭证库不可用时，认证播放源添加失败，禁止明文 fallback。

桌面端使用固定版本 `Latchkey` 0.1.0（MIT）访问操作系统凭证库：

- Windows Credential Manager；
- macOS Keychain；
- Linux Secret Service（例如 GNOME Keyring）。

选择自动原生后端且禁止明文文件 fallback。安全存储不可用时，保存认证任务必须明确失败，不能为了可用性退回明文。Android Keystore 与 iOS Keychain 适配在对应移动平台阶段实现，但继续使用同一个 `ICredentialStore` 边界。

会话 schema 2 支持从 schema 1 迁移：载入旧会话时先把明文凭证写入系统凭证库并原子重写会话文件，再把凭证补回仅存在于进程内的运行时模型。任务删除后清理不再使用的凭证引用。

## 静态链接说明

Latchkey 和 CliWrap 是托管程序集，会进入 NativeAOT 单文件。Windows/macOS 调用系统 API；Linux 的 Secret Service 适配会在运行时加载系统提供的 `libsecret-1.so.0`，后者属于桌面密钥环集成边界，不随 CanX 捆绑，也不会出现在 ELF `DT_NEEDED` 中。将 libsecret、GLib 和 D-Bus 客户端完整静态并入应用会扩大攻击面并破坏系统安全组件更新，因此不采用；发布检查仍要求除 glibc 基础库外没有第三方 `DT_NEEDED` 或 native sidecar。

## 后果

- `downloads.json`、Tool 输出和测试日志均不包含认证秘密；
- 系统凭证库被锁定或不可用时，认证任务无法持久化，界面必须显示原始错误；
- 用户复制会话文件到另一台设备时，凭证引用不会随文件迁移，需要重新录入凭证；
- Latchkey、CliWrap 及其许可证必须进入最终 third-party notices 与 SBOM。
