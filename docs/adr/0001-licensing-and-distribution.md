# ADR-0001：许可证与分发基线

- 状态：已接受
- 日期：2026-09-08

## 背景

CanX 计划在同一应用中链接 libaria2、libmpv、FFmpeg 和 QuickJS，并发布到桌面与移动平台。许可证选择会直接影响链接方式、原生构建参数、源码交付和应用商店可用性。

当前确认的上游基线：

- aria2 1.37.0：GPL-2.0-or-later，并带 OpenSSL linking exception；
- mpv：最终许可证取决于构建配置和依赖；常规 GPL 构建按 GPL-2.0-or-later 路线处理；
- FFmpeg：许可证取决于 configure flags 和链接的 codec 库；本机发行版构建显示为 GPL，不能代表最终发布配置；
- QuickJS：MIT。

## 决策

1. CanX 自有源码以 `GPL-3.0-or-later` 作为工程和发布基线。
2. 阶段 0 优先验证功能，不把本机系统包直接复制进正式产物。
3. 正式 native 产物必须由仓库内的固定版本 build recipe 生成，记录 source URL、commit/tag、patch、configure flags、compiler、hash 和完整依赖许可证。
4. CI 为每个发布包生成 SBOM、third-party notices 和对应源码获取说明。
5. 不启用无法确认可再分发的 codec 或依赖；专利/地区限制作为独立发布清单维护。
6. iOS 技术适配继续进行，但 App Store 不是首选或阶段 0 必须支持的分发渠道。默认交付源码、可重复构建说明，以及工具链允许时可供重签名的未签名/临时签名 IPA。
7. iOS 用户使用自己的 Apple ID、开发者证书和 provisioning profile 完成签名和侧载。项目不提供签名服务，不收集签名凭证，也不保证第三方侧载工具、证书期限或设备策略；这些使用风险由用户承担。
8. 用户自行签名不免除项目的 GPL 义务。正式发布必须提供准确的对应源码、修改记录、构建材料、许可证告知及适用的 Installation Information。
9. 是否支持 App Store 延后到最终发布阶段决定，不阻塞阶段 1 或 iOS 技术验证。如果以后将 App Store 设为目标，必须另开 ADR 并完成法律与技术复核，在 iOS 替换 libaria2、仅连接远程 aria2、改变分发渠道或调整产品范围中作出显式选择。

## 结果

- 桌面和 Android 可按 GPL 兼容路线继续工程验证。
- iOS 可按“用户自行签名和侧载”路线继续工程验证；Apple 主机上的 native link、签名与安装 smoke test 仍是技术验收项。
- libmpv/FFmpeg 不必为了闭源目标强行裁剪，但仍应最小化依赖面并逐项审计。
- 仓库根目录包含完整 GPL-3.0-or-later `LICENSE`；正式应用源码从阶段 1 起采用一致的 SPDX 标识策略。
- 第三方插件的许可证由作者选择，但插件市场必须展示许可证；宿主 SDK 的分发许可在插件 SDK 阶段另行决定。

## 后续发布门槛

- 如果决定进入 iOS App Store，GPL 与 Apple 条款需要有资质的法律意见；本 ADR 不是法律意见。
- 各地区 codec 专利与媒体来源合规需要在进入公开测试前复核。
