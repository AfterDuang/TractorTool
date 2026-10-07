# TractorTool 开发上下文

更新日期：2026-10-07。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；用户对上一版反馈“看着没问题”并允许继续。最新约定：凡需修改宿主、Core、Contracts、公共 UI 等软件框架代码，先停下说明原因和影响，交由用户安排架构侧分析；不要凭后续“继续”自行更改框架。约定已进入根 AGENTS.md。

本轮 E77 交付 SystemHud 0.4.0。用户实测 0.3.5 展开后空白、无法收起；静态追查发现 opacity=0 回调可能把正在淡入的场景永久隐藏。已删除双场景与该回调，顶部按钮持续存在。按用户最新决定实现同一指标自由布局：默认 248×44 三格、长条 400×48 四格、详细 400×184 顶部复用四格（左/中/右上/右下）加底部。单击默认与长条往返，双击两者进入详细，再双击回默认；详细单击不改变状态。手势按系统双击时限判定，6 DIP 移动取消点击并拖动。

区域配置在 HudLayout，编辑在 HudModel.Layout；HudPresentation 把同一读数投影到所有区域，HudSlotView 共用文字/图标/进度环，HudCard 管理布局/手势/形变，HudWindow 管理原生窗口。旧方案缺少 Layout 时自动补默认，配置记录支持保存/撤销/复制及草稿隔离。没有混合指标、脚本引擎、空动作接口或新依赖。E70 源码职责已同步[插件导航](../Plugins/TractorTool.Plugins.SystemHud/README.md)；完整设计见[插件文档](Plugins/SystemHud.md)。

保留插件内 420ms 弹性宽高/圆角、180ms 出现动画和全局/已保存动画偏好；移除会相互隐藏的内容淡入逻辑。透明绘制边界 432×208，用 Win32 圆角区域与命中测试排除留白遮挡。四边避让不写配置，拖动结束保存中心一次；旧锚点、DPI/相对位置、保存失败恢复保持。长条/详细或拖动期间暂停预览到期与轮播，显式隐藏/关闭优先；点击穿透默认关闭，旧 true 保留。

E77 HudChecks 编译与引用 E67 已发布 SDK 的独立 Rebuild 均 0 警告/0 错误；仅编译未执行检查程序。ZIP 67 文件逐项哈希相符，无共享 SDK 副本，5 个 AXAML XML 可解析，234 个宿主文件哈希/数量未变。交付 `artifacts/e77-hud-layout/plugin-final/TractorTool.SystemHud-0.4.0.zip`，详见 [E77](PROJECT_HISTORY.md#evidence-hud-three-state-layout)。没有启动软件、硬件/业务网络、DPAPI 或 UI 自动化，没有替用户安装。故障修正和三态布局的真实视觉/交互仍待用户确认。

本轮未改框架或重新发布宿主。硬件库、采集器、凭据和宿主 M5 状态不变。部分温度传感器需要管理员/PawnIO，插件不装驱动或提权。E71 框架文档职责与打包脚本限制继续有效，见[文档导航](README.md)和[脚本边界](../scripts/README.md#插件打包的现有适用范围)。

## 2. 保留的产品决定

- E62 用户已测试依赖安装/拒绝重试/修复清理、视频/GPU、插件升级、数据迁移，反馈暂未发现问题；不重复要求常规测试，不外推未说明的专项/硬件矩阵。
- E63 UI API 1.5 托盘注册支持一级插件子菜单、普通/勾选动作和状态句柄；宿主保留显示/退出。资源随插件生命周期，回调从 UI 线程开始，停止封闭入口并等待动作结束。A24 仍待真实运行证据。
- E64 Test.Independent 1.0.1 删除了 200ms 心跳和 tick 日志。源码构建不会替换已安装 1.0.0，需 ZIP 更新后显式退出重启。
- E60 外部依赖仅 Packages/依赖来源；默认 GitHub，可加公开 GitHub Enterprise API。插件声明精确仓库/tag/ZIP/hash/许可/工具，宿主负责确认、下载、事务、租约。无自有源，pip/AI/Worker 平台不留空入口。
- receipt v1/损坏记录保留可修复状态；身份/hash 冲突不暗中替换，引用不完整禁清理。来源配置在 DataRoot/Config/dependency-sources.json，旧缓存不读取、不删真实数据；查询仅实际申请时触发。
- E61 标签流程只创建 Release 草稿，不覆盖已有 Release。远程未配置，CI/Release 未实跑，公开发布由用户决定。
- E55 页面持有路径已修，历史隔离检查通过，实际长期内存曲线待复测。旧取消调试输出触发不明，Job 终止仍有 20ms/最多 3 秒轮询并警告一次。

## 3. 工作约定

- 用户接手运行测试，未经新指示不启动软件、检查程序、FFmpeg、UI 自动化，不接管真实配置/媒体/剪贴板/用户进程。构建、静态和 ZIP 校验不能冒充运行验收。
- 保留 .vscode/launch.json 空白修改、.gitignore 的 Docs/ 规则，不纳入提交。已跟踪文档用 git add -u -- Docs；新文档指定文件 git add -f。使用子代理时显式 gpt-6.1-sol / high；本地提交不代表远程推送授权。
- 框架精简，业务留插件，只依赖 Contracts/UI。VS Code、SDK 10.0.401，不更换 IDE 配置，不启用 AOT/未经验证裁剪。
- 数据根与软件目录分开；迁移保持互斥及恢复记录，不迁移插件代码、不删除旧根。分发只写新目录，ZIP 不覆盖升级，安装器保留插件和用户数据。跨会话维护仍待验。
- 日志只记录重要事件，正常取消不记 Error；任务在日志页，标题栏不放独立任务按钮。

## 4. 入口与下一步

| 工作 | 入口 |
| --- | --- |
| 插件依赖装载 | [PluginLoadContext](../TractorTool.Core/Plugins/PluginLoadContext.cs)、[Core 清单嵌入](../TractorTool.Core/TractorTool.Core.csproj)、[机制夹具](../tests/IndependentPlugin/ProbePlugin.cs) |
| HUD 方案/编辑 | [HudProfile](../Plugins/TractorTool.Plugins.SystemHud/Configuration/HudProfile.cs)、[编辑模型](../Plugins/TractorTool.Plugins.SystemHud/ViewModels/HudModel.Profiles.cs)、[共享卡片](../Plugins/TractorTool.Plugins.SystemHud/Views/HudCard.axaml) |
| HUD 页面/生命周期 | [HudView](../Plugins/TractorTool.Plugins.SystemHud/Views/HudView.axaml)、[HudModel](../Plugins/TractorTool.Plugins.SystemHud/ViewModels/HudModel.cs) |
| HUD 数据源/凭据 | [HudSampler](../Plugins/TractorTool.Plugins.SystemHud/Sampling/HudSampler.cs)、[NetworkMetrics](../Plugins/TractorTool.Plugins.SystemHud/Sampling/NetworkMetrics.cs)、[HardwareMetrics](../Plugins/TractorTool.Plugins.SystemHud/Sampling/HardwareMetrics.cs)、[HudSecrets](../Plugins/TractorTool.Plugins.SystemHud/Configuration/HudSecrets.cs) |
| Windows 采集/窗口 | [WindowsMetrics](../Plugins/TractorTool.Plugins.SystemHud/Sampling/WindowsMetrics.cs)、[HudWindow](../Plugins/TractorTool.Plugins.SystemHud/Views/HudWindow.axaml.cs) |
| HUD 规则/纯逻辑检查 | [插件文档](Plugins/SystemHud.md)、[HudChecks](../tests/TractorTool.HudChecks/Program.cs) |
| 托盘公共接口 | [PluginTray](../TractorTool.UI/PluginTray.cs)、[接入规范](PLUGIN_DEVELOPMENT.md#tray-menu) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 分发与维护 | [脚本导航](../scripts/README.md)、[Build-Distribution](../scripts/Build-Distribution.ps1) |

下一步由用户安装并显式退出重启 0.4.0，优先确认三态都有内容、单击/双击可返回、拖动互斥；验证区域编辑、保存/撤销/复制及旧配置。两级动画偏好、透明区域、DPI/四边/跨屏恢复、计时与写入失败继续按 A26/A27 保留。需要框架改动时先停下说明并等架构侧分析。宿主 M5 的远程发布、长期内存、迁移故障、跨会话及无 SDK 机器缺口见[验收台账](DEVELOPMENT.md#acceptance)。
