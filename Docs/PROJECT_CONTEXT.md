# TractorTool 开发上下文

更新日期：2026-10-08。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；用户对上一版反馈“看着没问题”并允许继续。最新约定：凡需修改宿主、Core、Contracts、公共 UI 等软件框架代码，先停下说明原因和影响，交由用户安排架构侧分析；不要凭后续“继续”自行更改框架。约定已进入根 AGENTS.md。

本轮 E86 交付 SystemHud 0.4.9。用户要求可视化选色、直观解释变色条件，并把自动轮播放到左侧、在已保存方案间切换。复用宿主已有 ColorPicker 色块/色谱/调色板，显示“常规颜色/提醒颜色”和完整条件句，无百分比指标隐藏规则；色值配置保持 RGB 字符串兼容。

轮播放在左侧预览下方，开关/间隔独立即时保存，不再依赖“保存并使用”；显示顺序、状态/暂停原因和“下一个方案”。发现原逻辑把长条也列为展开暂停：现为短条/长条都轮播、详情/拖动暂停。保存只修改 Rotate/Seconds，失败回退，定时切换不写配置、不改变编辑选择或草稿。隐藏、暂停、电源提醒及草稿预览仍阻止轮播；预览到期计时保留原长条/详情暂停规则。

保留 E85 布局：默认 248×44、长条固定 360×48、详情宽 400/高不超过 160；详情右组 Auto（上限 210），左图标右名称，说明与时间固定两列。保留光学居中、320ms 弹簧、立即单击、中央双击和拖动互斥。本轮未改卡片布局、采样或框架。

沿用已授权 HUD --ui-only，内存方案检查已验证色块打开弹窗、选色双向绑定/保存、小写旧色值兼容、阈值边界及无百分比规则隐藏、轮播独立保存/失败回退、真实 DispatcherTimer 的 Net → CPU → Net、详情暂停/长条恢复和草稿隔离。原布局/绑定/输入检查通过，零绑定错误/检查失败；已查看宽窄页面和选色弹窗 PNG。未启动宿主/原生 HUD、采样、网络、真实配置/DPAPI 或默认逻辑套件。

HudChecks 构建成功（离线 NuGet 审计源产生 NU1900 警告）；E67 SDK 独立插件构建 0 警告/0 错误。ZIP 67 文件一致，无共享 SDK 副本，234 个宿主文件未变，5 个 AXAML 可解析。交付 artifacts/e86-hud-color-rotation/plugin/TractorTool.SystemHud-0.4.9.zip，证据见 [E86](PROJECT_HISTORY.md#evidence-hud-color-rotation)。未自动安装；保留用户 .gitignore/.vscode/launch.json，仅本地提交。

## 2. 保留的产品决定

- E62 用户已测试依赖安装/拒绝重试/修复清理、视频/GPU、插件升级、数据迁移，反馈暂未发现问题；不重复要求常规测试，不外推未说明的专项/硬件矩阵。
- E63 UI API 1.5 托盘注册支持一级插件子菜单、普通/勾选动作和状态句柄；宿主保留显示/退出。资源随插件生命周期，回调从 UI 线程开始，停止封闭入口并等待动作结束。A24 仍待真实运行证据。
- E64 Test.Independent 1.0.1 删除了 200ms 心跳和 tick 日志。源码构建不会替换已安装 1.0.0，需 ZIP 更新后显式退出重启。
- E60 外部依赖仅 Packages/依赖来源；默认 GitHub，可加公开 GitHub Enterprise API。插件声明精确仓库/tag/ZIP/hash/许可/工具，宿主负责确认、下载、事务、租约。无自有源，pip/AI/Worker 平台不留空入口。
- receipt v1/损坏记录保留可修复状态；身份/hash 冲突不暗中替换，引用不完整禁清理。来源配置在 DataRoot/Config/dependency-sources.json，旧缓存不读取、不删真实数据；查询仅实际申请时触发。
- E61 标签流程只创建 Release 草稿，不覆盖已有 Release。远程未配置，CI/Release 未实跑，公开发布由用户决定。
- E55 页面持有路径已修，历史隔离检查通过，实际长期内存曲线待复测。旧取消调试输出触发不明，Job 终止仍有 20ms/最多 3 秒轮询并警告一次。

## 3. 工作约定

- 用户接手运行测试，未经新指示不启动软件、检查程序、FFmpeg、UI 自动化，不接管真实配置/媒体/剪贴板/用户进程。用户已授权 HUD 专项离屏检查，E86 沿用并仅使用内存方案验证颜色/轮播，不外推为宿主、硬件或其他检查授权。构建、静态和 ZIP 校验不能冒充运行验收。
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

下一步由用户安装并显式退出重启 0.4.9，核对选色弹窗、提醒变色及左侧轮播在真实桌面中的保存/切换。离屏输入/绑定/布局及内存轮播已验证，原生动画/透明区域、DPI/跨屏恢复及硬件/保存专项继续按 A26/A27 保留。需要框架支持先停止并说明。宿主 M5 缺口见[验收台账](DEVELOPMENT.md#acceptance)。
