# TractorTool 开发上下文

更新日期：2026-10-09。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；用户对上一版反馈“看着没问题”并允许继续。涉及宿主、Core、Contracts、公共 UI 的插件需求，仍遵守 AGENTS.md 的框架边界约定。

本轮 E90 为 SystemHud 0.4.13，修复拖动保存位置错误重置采样：SavePosition 原来复用 Save 时触发 InvalidateSample，取消在途读取并清空 CPU/网速基线；下一次读取只能建立基线，因此短暂显示“—”。现移除通用保存路径中的失效动作；保存位置保留在途采样、计数基线和显示，保存偏好仍通过原 EndPreview 路径使采样失效。失败回退、只合并位置及草稿隔离保留。

**用户最新决定：托盘方案列表先不做。** 用户确认目标仍是“系统数据HUD”子菜单中的已保存方案；当前公开 UI 1.5 仅注册期 Add 和项目状态 Update，无法提供分隔线及运行期增删。用户明确如缺少宿主实时更新支持则先不实现。本轮不改菜单、框架或注册接口，也不自动推进架构改造；需求及接口事实留在[暂缓记录](Plugins/SystemHud.md#tray-menu-pending)。

保留 E89 Fluent System Icons Gauge 彩色图标、E88 更名/双滑块、E87 合并预览/紧凑表单及原布局、轮播和颜色规则。仅调整采样失效触发，稳定 ID/设置 schema 不变。

沿用用户已授权 HUD --ui-only：新增位置保存成功/无变化/失败、草稿隔离、采样基线/在途取消与保存偏好失效检查。旧代码准确复现 1 项失败，修复后全部通过，零绑定错误/检查失败；没有启动宿主、原生 HUD 或真实采样。专项构建 0 错误/3 条 NU1900，独立插件构建 0 错误/2 条 NU1900，均因网络限制无法查询 NuGet 漏洞数据；审计未记为通过。

产物 artifacts/e90-hud-drag-sampling/plugin/TractorTool.SystemHud-0.4.13.zip。69 文件逐项哈希一致、无共享 SDK 副本、234 个宿主文件未变、5 个 AXAML 可解析；证据见 [E90](PROJECT_HISTORY.md#evidence-hud-drag-sampling)。未安装、未读真实配置/硬件；保留用户 .gitignore/.vscode/launch.json，仅本地提交。

## 2. 保留的产品决定

- E62 用户已测试依赖安装/拒绝重试/修复清理、视频/GPU、插件升级、数据迁移，反馈暂未发现问题；不重复要求常规测试，不外推未说明的专项/硬件矩阵。
- E63 UI API 1.5 托盘注册支持一级插件子菜单、普通/勾选动作和状态句柄；宿主保留显示/退出。资源随插件生命周期，回调从 UI 线程开始，停止封闭入口并等待动作结束。A24 仍待真实运行证据。
- E64 Test.Independent 1.0.1 删除了 200ms 心跳和 tick 日志。源码构建不会替换已安装 1.0.0，需 ZIP 更新后显式退出重启。
- E60 外部依赖仅 Packages/依赖来源；默认 GitHub，可加公开 GitHub Enterprise API。插件声明精确仓库/tag/ZIP/hash/许可/工具，宿主负责确认、下载、事务、租约。无自有源，pip/AI/Worker 平台不留空入口。
- receipt v1/损坏记录保留可修复状态；身份/hash 冲突不暗中替换，引用不完整禁清理。来源配置在 DataRoot/Config/dependency-sources.json，旧缓存不读取、不删真实数据；查询仅实际申请时触发。
- E61 标签流程只创建 Release 草稿，不覆盖已有 Release。远程未配置，CI/Release 未实跑，公开发布由用户决定。
- E55 页面持有路径已修，历史隔离检查通过，实际长期内存曲线待复测。旧取消调试输出触发不明，Job 终止仍有 20ms/最多 3 秒轮询并警告一次。

## 3. 工作约定

- 用户接手运行测试，未经新指示不启动软件、检查程序、FFmpeg、UI 自动化，不接管真实配置/媒体/剪贴板/用户进程。用户已授权 HUD 专项离屏检查，E88 沿用并仅用内存替身验证页面/菜单注册/颜色/轮播，不外推为宿主、原生托盘、硬件或其他检查授权。构建、静态和 ZIP 校验不能冒充运行验收。
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
| HUD 规则/检查 | [插件文档](Plugins/SystemHud.md)、[默认逻辑检查](../tests/TractorTool.HudChecks/Program.cs)、[专项离屏检查](../tests/TractorTool.HudChecks/HudVisualChecks.cs) |
| 托盘公共接口 | [PluginTray](../TractorTool.UI/PluginTray.cs)、[接入规范](PLUGIN_DEVELOPMENT.md#tray-menu) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 分发与维护 | [脚本导航](../scripts/README.md)、[Build-Distribution](../scripts/Build-Distribution.ps1) |

下一步可安装 0.4.13 并显式退出重启，确认 CPU/网速在拖动后不再因位置保存变成“—”，同时保留读数更新和位置恢复。托盘方案列表按用户最新决定暂缓；不再安排架构侧改造作为本轮下一步。原生动画/透明区域、DPI/跨屏、硬件/保存仍按 A26/A27 保留。宿主 M5 缺口见[验收台账](DEVELOPMENT.md#acceptance)。
