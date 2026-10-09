# TractorTool 开发上下文

更新日期：2026-10-09。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；用户对上一版反馈“看着没问题”并允许继续。涉及宿主、Core、Contracts、公共 UI 的插件需求，仍遵守 AGENTS.md 的框架边界约定。

当前代码来自 E91，SystemHud 0.4.14：轮播秒数与百分比采用纯数字编辑，单位独立放在右侧，避免覆盖增减箭头；合法秒数键入即保存。方案切换不再主动清屏，待新读数就绪后整体替换；CPU/网速建立基线后最多补一次 250ms 读取，正常每秒采集不增加读数次数。内容按方案/指标身份以 200ms 淡入和 4 DIP 位移呈现，同方案普通数值更新不重播，关闭动效/分离时归位。参考 Apple Live Activities 的信息变化、避免重叠及减少动态效果原则，具体参数由本插件选择。

**用户最新决定：托盘方案列表先不做。** 用户确认目标仍是“系统数据HUD”子菜单中的已保存方案；当前公开 UI 1.5 仅注册期 Add 和项目状态 Update，无法提供分隔线及运行期增删。用户明确如缺少宿主实时更新支持则先不实现。本轮不改菜单、框架或注册接口，也不自动推进架构改造；需求及接口事实留在[暂缓记录](Plugins/SystemHud.md#tray-menu-pending)。

保留 E90 拖动位置不重置采样、E89 Fluent Gauge 图标、双滑块及合并预览/紧凑表单。稳定 ID/设置 schema 不变；无新依赖、公共接口或全方案后台采集。

沿用已授权 --ui-only：实际键盘输入/清空/逐字/Enter/失焦/增减、范围和小数校验、完整读数替换、内存源有界补读/取消、动画中间帧/中断/减少动效/分离清理及原回归全部通过，零绑定错误/检查失败。旧格式对照只复现小数误存，没有复现所有合法输入失败；初稿单位槽遮挡箭头已按截图修正为外侧单位并查看最终宽窄图。专项构建 0 错误/3 条 NU1900，独立构建 0 错误/2 条 NU1900；漏洞查询受网络限制，不能记为审计通过。

产物 artifacts/e91-hud-switching/plugin/TractorTool.SystemHud-0.4.14.zip。69 文件逐项哈希一致、无共享 SDK 副本、234 个宿主文件未变、5 个 AXAML 可解析；证据见 [E91](PROJECT_HISTORY.md#evidence-hud-switching)。E91 未自动安装或启动原生窗口，E92 已核对实际加载；未读真实配置/硬件；保留用户 .gitignore/.vscode/launch.json，仅本地提交。

E92 用户明确授权观察正在运行的软件一分钟，已只读采集 PID 40940 的 60 个进程快照（2026-10-09 21:39:45–21:40:45，UTC+8）。24 逻辑处理器归一化平均 CPU 0.0467%、最高单秒 0.3291%；工作集 325.11→325.57 MiB，线程 86–88；私有提交一度 285.50 MiB，结束回到 270.55 MiB。确认 Debug 宿主实际加载 0.4.14，DLL 哈希与 E91 包一致。未操作 UI/配置或读取活动方案状态；结论只覆盖整个进程当前一分钟，不证明 HUD 独立开销、游戏帧时间、GPU/功耗或长期内存。记录见 [E92](PROJECT_HISTORY.md#evidence-hud-live-performance)，原始数据 artifacts/e92-hud-live-performance。

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

E92 已确认运行进程加载 0.4.14，无需重复要求安装；手输秒数、单位避让、轮播/动效等交互仍以用户反馈为准。一分钟性能观察已完成，各指标隔离、帧时间和长期性能仍未测。托盘方案列表按用户决定暂缓，不安排框架改造；真实输入/硬件性能/原生动画/DPI/跨屏/保存仍按 A26/A27 保留。宿主 M5 缺口见[验收台账](DEVELOPMENT.md#acceptance)。
