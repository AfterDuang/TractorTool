# TractorTool 开发上下文

更新日期：2026-10-09。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

发布层 E98 按用户更正改为自动发现：删除 E97 的 packaging/plugins.json，扫描 Plugins 一级子目录中带 plugin.json 和唯一同目录工程的插件，ID/版本读取清单。无名单维护，不递归纳入测试或构建输出；该正式目录中符合规则的工程都会随包发布。校验/安装器仍使用 release.json.plugins 快照。只改发布发现、回归和文档，语法/引用静态核对通过，回归与完整发布未执行。详见[当前台账](DEVELOPMENT.md#current-status)。

宿主仍处于 M5 发布准备；用户对上一版反馈“看着没问题”并允许继续。涉及宿主、Core、Contracts、公共 UI 的插件需求，仍遵守 AGENTS.md 的框架边界约定。

最近的 E94 是用户要求的插件全量 Review：三个子代理显式使用 gpt-6.1-sol / high，主代理去重复核。对 5f33fff / 0.4.15 确认 2 项 P2 未修复：ApplyPreferences 改硬件开关后没有清缓存，600 秒方案可持续显示旧禁用提示/读数；非硬件方案 SensorId=null 绕过验证，在 SelectSavedSensor 的 .Length 处使初始化失败，页面无法注册。建议在硬件开关实际变化且保存成功时清缓存，并统一处理可选字段 null，补两条实际入口回归。UI 未发现其他确定缺陷。E94 仅做代码审查及文档记录，没有修改生产代码或运行测试；详见 [E94](PROJECT_HISTORY.md#evidence-hud-full-review)。

当前代码来自 E96，SystemHud 0.4.17：按稳定传感器 ID 类型显示硬件图标和编辑示例，错误读数/重启后未检测也保持类型。CPU 温度/功率/电压缺少 PawnIO 或管理员权限时显示具体原因和未知值，不把无访问条件的零值当有效功率；检测列表标记暂不可读，重新检测成功后清读数缓存。本机只读未发现 PawnIO 安装记录或服务，需安装后以管理员身份显式重启验证温度；未自动安装或读取真实传感器。保留 E95 删除草稿预览，以及 E93 每方案 0.2–600 秒独立采样、到期缓存和首次基线有界重试。

**用户最新决定：托盘方案列表先不做。** 用户确认目标仍是“系统数据HUD”子菜单中的已保存方案；当前公开 UI 1.5 仅注册期 Add 和项目状态 Update，无法提供分隔线及运行期增删。用户明确如缺少宿主实时更新支持则先不实现。本轮不改菜单、框架或注册接口，也不自动推进架构改造；需求及接口事实留在[暂缓记录](Plugins/SystemHud.md#tray-menu-pending)。

保留 E90 拖动位置不重置采样、E91 完整读数替换/200ms 内容动效/单位在输入框外，以及既有 Fluent 图标、双滑块和紧凑表单。稳定 ID/设置 schema 1 不变；新字段 SamplingSeconds 支持小数，旧 RefreshSeconds 在插件内导入，本地原有 1 秒、网络原间隔保留但最长收敛至 600 秒；加载不主动写文件，保存不再写旧字段。无新依赖、框架改动或全方案后台采集。

E96 沿用已授权 --ui-only，八种硬件单位/三态图标、编辑选择、错误时类型保持、缺失驱动/权限与合法零值区分检查及全部既有回归通过，零绑定错误/检查失败。已查看功率/占用率截图；专项及独立构建最终 0 错误/0 警告。未运行默认完整检查、真实 HTTP/硬件/原生窗口；E94 两项 P2 仍待修复。

最终产物 artifacts/e96-hud-hardware/plugin/TractorTool.SystemHud-0.4.17.zip。旧 E67 SDK 已不存在，本轮引用 TractorTool/bin/Release/net10.0/win-x64 既有 DLL 独立构建；69 文件哈希一致、无共享 SDK 副本、所引用目录 234 个宿主文件未变、5 个 AXAML 可解析，见 [E96](PROJECT_HISTORY.md#evidence-hud-hardware-types)。未自动安装/重启/读取用户配置，保留用户 .gitignore/.vscode/launch.json，仅本地提交。

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

E94 两项 P2 尚未修复，建议先在插件内修复并补回归，0.4.17 仅在重新检测后清缓存，不等于修复偏好保存入口，不能作为关闭两项审查问题的版本。CPU 温度需补 PawnIO 后实测。此后仍需核对独立间隔重启保留、慢方案轮播复用及原生交互。E92 旧版一分钟结果不证明 5Hz 硬件性能；各指标隔离、帧时间和长期性能仍未测。托盘方案列表按用户决定暂缓；其他真实输入/原生动画/DPI/跨屏/保存仍按 A26/A27 保留。宿主 M5 缺口见[验收台账](DEVELOPMENT.md#acceptance)。
