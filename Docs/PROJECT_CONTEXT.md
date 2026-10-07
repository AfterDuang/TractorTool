# TractorTool 开发上下文

更新日期：2026-10-07。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；用户对上一版反馈“看着没问题”并允许继续。最新约定：凡需修改宿主、Core、Contracts、公共 UI 等软件框架代码，先停下说明原因和影响，交由用户安排架构侧分析；不要凭后续“继续”自行更改框架。约定已进入根 AGENTS.md。

本轮 E81 交付 SystemHud 0.4.4。用户明确要求长条恢复短条也立即响应，并重做详细态层级。E80 保留的收起等待取舍已被本轮需求取代，不能重新引入。

修正仅在插件：删除收起计时器及调度路径，长短条松手即切换。双击沿系统 ClickCount 接续；第二击需落在收起后仍可见的中央区域，不保证已经消失的长条两端。已向用户说明该取舍；未添加透明输入阻挡或跨窗口捕获。显式状态变化、拖动/隐藏/分离清理点击候选，键盘仍先于 Button 默认动作清理鼠标状态。

三态为 248×44、360×48、360×152 DIP。详细态顶部图标与名称同排，下一行主值 24 号、右侧辅助值 15 号，正文 12 号，时间 11 号独立置底。Expanded 复用四槽数据类型，按指标填写，不强套长条顺序；环不再重复图标。CPU/内存/网速使用相同层级，自定义说明留在滚动正文。参考 Apple HIG 的紧凑内容、对齐和信息层级，不冒称尺寸是 Apple 标准。保留 320ms 弹簧、180ms 出现、动画偏好、432×208 透明边界、位置/拖动/穿透；采样与生命周期未改。

E79 的 Slot.Item、固定行列和唯一左侧 EditorDisplay 预览继续保留；区域是开发者约定，不开放用户排布。没有修改宿主/Core/Contracts/公共 UI，未新增生产依赖。设计入口见[插件文档](Plugins/SystemHud.md)，职责入口见[插件导航](../Plugins/TractorTool.Plugins.SystemHud/README.md)。

本轮沿用已获准的 HUD 专项离屏检查范围，未运行主程序/原生 HudWindow/真实配置/硬件/业务网络/DPAPI 或默认逻辑套件。原始鼠标验证长条中央/两端立即收起、中央第二击继续详情，键盘/拖动/显式状态重置和动效反转通过。12 指标/三态/绑定/长文/挂载/单一预览/宽窄切换，以及正文与时间边界、自定义说明保留通过；零绑定错误/检查失败。已查看 CPU、内存、网速及浅色按下离屏 PNG。

HudChecks 与 E67 已发布 SDK 独立 Rebuild 均 0 警告/0 错误；ZIP 67 文件哈希一致，无共享 SDK 副本，234 个宿主文件未变，5 个 AXAML 可解析。交付 `artifacts/e81-hud-detail/plugin-final/TractorTool.SystemHud-0.4.4.zip`，证据见 [E81](PROJECT_HISTORY.md#evidence-hud-detail-refinement)。未自动安装；原生窗口/DPI/硬件仍待确认。保留用户 .gitignore/.vscode/launch.json，仅本地提交。

## 2. 保留的产品决定

- E62 用户已测试依赖安装/拒绝重试/修复清理、视频/GPU、插件升级、数据迁移，反馈暂未发现问题；不重复要求常规测试，不外推未说明的专项/硬件矩阵。
- E63 UI API 1.5 托盘注册支持一级插件子菜单、普通/勾选动作和状态句柄；宿主保留显示/退出。资源随插件生命周期，回调从 UI 线程开始，停止封闭入口并等待动作结束。A24 仍待真实运行证据。
- E64 Test.Independent 1.0.1 删除了 200ms 心跳和 tick 日志。源码构建不会替换已安装 1.0.0，需 ZIP 更新后显式退出重启。
- E60 外部依赖仅 Packages/依赖来源；默认 GitHub，可加公开 GitHub Enterprise API。插件声明精确仓库/tag/ZIP/hash/许可/工具，宿主负责确认、下载、事务、租约。无自有源，pip/AI/Worker 平台不留空入口。
- receipt v1/损坏记录保留可修复状态；身份/hash 冲突不暗中替换，引用不完整禁清理。来源配置在 DataRoot/Config/dependency-sources.json，旧缓存不读取、不删真实数据；查询仅实际申请时触发。
- E61 标签流程只创建 Release 草稿，不覆盖已有 Release。远程未配置，CI/Release 未实跑，公开发布由用户决定。
- E55 页面持有路径已修，历史隔离检查通过，实际长期内存曲线待复测。旧取消调试输出触发不明，Job 终止仍有 20ms/最多 3 秒轮询并警告一次。

## 3. 工作约定

- 用户接手运行测试，未经新指示不启动软件、检查程序、FFmpeg、UI 自动化，不接管真实配置/媒体/剪贴板/用户进程。用户已授权 HUD 专项离屏检查，E81 在同一问题修正中沿用，不外推为宿主、硬件或其他检查授权。构建、静态和 ZIP 校验不能冒充运行验收。
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

下一步由用户安装并显式退出重启 0.4.4，核对长短条立即往返、中央双击、详细态层级及真实动画/缩放。离屏输入/绑定/布局已验证，原生透明区域、DPI/跨屏恢复及硬件/保存专项继续按 A26/A27 保留。需要框架支持先停止并说明。宿主 M5 缺口见[验收台账](DEVELOPMENT.md#acceptance)。
