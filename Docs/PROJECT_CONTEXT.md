# TractorTool 开发上下文

更新日期：2026-10-07。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；用户对上一版反馈“看着没问题”并允许继续。最新约定：凡需修改宿主、Core、Contracts、公共 UI 等软件框架代码，先停下说明原因和影响，交由用户安排架构侧分析；不要凭后续“继续”自行更改框架。约定已进入根 AGENTS.md。

本轮 E72 修复用户截图中的 HUD 编辑区挤压：SystemHud 0.3.1 在页面宽度至少 900 DIP 时左侧状态预览、右侧方案/位置编辑，两栏分别滚动；更窄时同一组控件上下排列并整页滚动，关闭栏内滚动。预览展开不再占用宽窗口编辑区高度，方案按钮随表单滚动，低高度也可访问；原有 72 个绑定和业务逻辑保留。

E70 已将 HUD 源码按 Configuration / Sampling / ViewModels / Views 分目录，[源码导航](../Plugins/TractorTool.Plugins.SystemHud/README.md) 标明修改落点。E71 [项目首页](../README.md) 只说明框架公共能力和源码导航，插件内部组织归[插件规范](PLUGIN_DEVELOPMENT.md#source-organization)，业务说明归插件文档；本轮不调整框架。

主页面遵循 Apple HIG 的可用空间适配和单一方向滚动边界；宽窄切换不重建控件、不清空草稿。设置仍保留六项长期偏好及独立 DeepSeek 凭据。配置兼容 schema 1，原五个指标编号不动；密钥使用当前用户 DPAPI，不写入方案/日志。隐藏/暂停停止采样，网络取消；方案级缓存包含尝试时间，轮播不能绕过刷新间隔，迟到结果不能覆盖新配置。

E72 引用现有 E67 SDK 独立 Rebuild 0 警告/0 错误；ZIP 67 文件与输出哈希一致，无共享 SDK 副本，234 个宿主文件未变。没有启动应用、检查程序、硬件/网络/DPAPI 或接管 UI；此前 HudChecks 编译不能当本轮布局运行证据。包路径见 [本地产物](DEVELOPMENT.md#local-artifacts)，证据见 [E72](PROJECT_HISTORY.md#evidence-hud-responsive-layout)；需用户安装 0.3.1 并显式退出重启确认布局。

本轮未改框架或重新发布宿主。硬件库 LibreHardwareMonitorLib 0.9.6 及运行依赖继续随插件分发，设置启用后按需检测。部分传感器需管理员/PawnIO，插件不装驱动或提权。首版不做任意 HTTP 头、混合指标、脚本、价格日程、桌面嵌入或全部参考变量，不声称完整复刻。

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

下一步由用户确认 0.3.1 在宽/窄/低高度和不同 DPI 下的分栏、滚动、草稿与页签保留；A26/A27 业务专项继续按台账验证。需要框架修改时先停下说明，等架构侧分析。A03/A25/A24 专项和宿主 M5 的远程发布、媒体/硬件、长期内存、迁移故障、跨会话及无 SDK 机器缺口继续看 [验收台账](DEVELOPMENT.md#acceptance)。
