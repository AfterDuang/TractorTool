# TractorTool 开发上下文

更新日期：2026-10-09。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；用户对上一版反馈“看着没问题”并允许继续。最新约定：凡需修改宿主、Core、Contracts、公共 UI 等软件框架代码，先停下说明原因和影响，交由用户安排架构侧分析；不要凭后续“继续”自行更改框架。约定已进入根 AGENTS.md。

本轮 E88 插件内调整为 SystemHud 0.4.11。用户要求托盘“显示 HUD”下以分隔线列出保存方案、删除暂停/预览/下一方案；主页面左侧删预览、显示与暂停改滑块；更名“系统数据HUD”并换彩色图标。随后明确不生成图标，应沿用独立插件同系列素材。现已完成更名、微软 Fluent Emoji Bar chart 3D 原始图标/许可、左侧两个双向状态开关和删除旧托盘三项；插件 ID/设置 schema 不变。未改框架。

**尚未完成：托盘分隔线与动态方案菜单。** 公开 UI 1.5 只有注册期 Add/状态 Update，宿主在 Commit 后禁止增加项目，无分隔线/删除接口。按 AGENTS 先交架构侧，需求及源码入口见[托盘接口交接](Plugins/SystemHud.md#tray-menu-pending)。当前包托盘仅有“显示 HUD”，不得说整项已完成；不使用反射、文字横线或固定占位项目绕过边界。

保留 E87 单预览卡片、文字/颜色按 640 DIP 内容宽度分组，E86 可视化选色/条件说明/轮播独立保存与失败回退，及 E85 固定长条/详情布局和原交互。右侧“预览草稿”及左侧轮播“下一个方案”保留；删除的是左侧当前方案预览与托盘快捷项。显示/暂停是临时运行状态，不自动保存偏好或编辑草稿。

沿用已授权 HUD --ui-only，左侧仅双滑块、实际状态初始绑定/启动前禁用和 setter 保护、内存托盘只注册显示项通过；四档宽度、颜色/轮播/回退及原布局/绑定/输入回归通过，零绑定错误/检查失败。已查看新页面渲染和原始图标。未启动宿主/原生 HUD/托盘，未验证运行后滑块点击/拖动，不读真实配置/硬件/凭据；默认逻辑套件未运行。

HudChecks 与 E67 SDK 独立插件构建均 0 警告/0 错误。ZIP 69 文件一致，含 256×256 图标和 MIT 许可，无共享 SDK 副本，234 个宿主文件未变，5 个 AXAML 可解析。产物 artifacts/e88-hud-controls/release/TractorTool.SystemHud-0.4.11.zip，证据见 [E88](PROJECT_HISTORY.md#evidence-hud-controls)。未自动安装；保留用户 .gitignore/.vscode/launch.json，仅本地提交。

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

下一步由用户安排架构侧分析托盘分隔符/动态列表，再回到插件侧接方案切换和同步。0.4.11 可先安装并显式退出重启，核对新名称/图标、显示与暂停滑块、托盘旧项删除；不把此包当作完整托盘方案菜单交付。原生动画/透明区域、DPI/跨屏、硬件/保存仍按 A26/A27 保留。宿主 M5 缺口见[验收台账](DEVELOPMENT.md#acceptance)。
