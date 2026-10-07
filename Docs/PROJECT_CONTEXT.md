# TractorTool 开发上下文

更新日期：2026-10-07。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；用户对上一版反馈“看着没问题”并允许继续。最新约定：凡需修改宿主、Core、Contracts、公共 UI 等软件框架代码，先停下说明原因和影响，交由用户安排架构侧分析；不要凭后续“继续”自行更改框架。约定已进入根 AGENTS.md。

本轮 E79 交付 SystemHud 0.4.2。用户报告 0.4.1 大量 HudDisplay/HudSlotDisplay 绑定转换错误、左右预览重复和详细态内容重叠/重复。本轮明确授权仅 HUD 离屏绑定/布局检查，不启动主程序或采集。检查在修正前复现 39 条绑定错误、长条 Third/Fourth 重叠及状态切换的 Grid 索引异常；修正后对应回归通过。

HudSlotView 改用 Item 属性接收区域数据，内部 Grid 才切换数据上下文；控件自身继续继承 HudDisplay。HudCard 固定四列两行定义，仅调整尺寸和位置。详细态为左图标、中央下主值、右上身份/环、右下次值，底部显示补充信息，不再拼接全部摘要。HudReading.Context 提供可用内存、接口范围等现有采样的补充内容，不持久化。左侧改为唯一“编辑预览”，使用 EditorDisplay 草稿示例；右侧重复卡片及“最近显示的内容”已删除。

区域仍是开发者组织显示代码的约定，不提供区域编辑器。HudPresentation 填 Compact、Strip、Detail；HudCard 管理布局/手势/形变，HudWindow 管理原生窗口。旧 layout 字段继续自然忽略，其他配置保留。不新增混合指标、FPS、空动作接口或生产依赖；只在现有测试工程加 Avalonia.Headless。阅读入口见[插件导航](../Plugins/TractorTool.Plugins.SystemHud/README.md)与[插件文档](Plugins/SystemHud.md)。

三态尺寸仍是默认 248×44、长条 400×48、详细 400×184 DIP。单击默认与长条往返，双击两者进入详细，再双击回默认；详细单击不改变状态。手势按系统双击时限判定，6 DIP 移动取消点击并拖动。保留持续存在的顶部与 E77 空白回退修正，不恢复双场景透明度隐藏回调。

保留插件内 420ms 弹性宽高/圆角、180ms 出现动画和全局/已保存动画偏好；移除会相互隐藏的内容淡入逻辑。透明绘制边界 432×208，用 Win32 圆角区域与命中测试排除留白遮挡。四边避让不写配置，拖动结束保存中心一次；旧锚点、DPI/相对位置、保存失败恢复保持。长条/详细或拖动期间暂停预览到期与轮播，显式隐藏/关闭优先；点击穿透默认关闭，旧 true 保留。

E79 HudChecks 编译与引用 E67 已发布 SDK 的独立 Rebuild 均 0 警告/0 错误。获准执行 --ui-only：12 类指标三态往返、读数/空上下文/长文、重新挂载、单一草稿预览及宽窄切换通过，零绑定错误/区域重叠；有 Skia 离屏截图。未执行默认逻辑/内存 HTTP 套件或真实 Windows UI/硬件/网络/DPAPI。ZIP 67 文件哈希一致，无共享 SDK 副本，5 个 AXAML 可解析，234 个宿主文件未变。交付 `artifacts/e79-hud-review/plugin/TractorTool.SystemHud-0.4.2.zip`，详见 [E79](PROJECT_HISTORY.md#evidence-hud-binding-review)。未替用户安装；原生手势、动画、DPI 和硬件不能由离屏结论替代。

本轮未改框架或重新发布宿主。采样频率/算法、硬件库、凭据和宿主 M5 状态不变。部分温度传感器需要管理员/PawnIO，插件不装驱动或提权。E71 框架文档职责与打包脚本限制继续有效，见[文档导航](README.md)和[脚本边界](../scripts/README.md#插件打包的现有适用范围)。

## 2. 保留的产品决定

- E62 用户已测试依赖安装/拒绝重试/修复清理、视频/GPU、插件升级、数据迁移，反馈暂未发现问题；不重复要求常规测试，不外推未说明的专项/硬件矩阵。
- E63 UI API 1.5 托盘注册支持一级插件子菜单、普通/勾选动作和状态句柄；宿主保留显示/退出。资源随插件生命周期，回调从 UI 线程开始，停止封闭入口并等待动作结束。A24 仍待真实运行证据。
- E64 Test.Independent 1.0.1 删除了 200ms 心跳和 tick 日志。源码构建不会替换已安装 1.0.0，需 ZIP 更新后显式退出重启。
- E60 外部依赖仅 Packages/依赖来源；默认 GitHub，可加公开 GitHub Enterprise API。插件声明精确仓库/tag/ZIP/hash/许可/工具，宿主负责确认、下载、事务、租约。无自有源，pip/AI/Worker 平台不留空入口。
- receipt v1/损坏记录保留可修复状态；身份/hash 冲突不暗中替换，引用不完整禁清理。来源配置在 DataRoot/Config/dependency-sources.json，旧缓存不读取、不删真实数据；查询仅实际申请时触发。
- E61 标签流程只创建 Release 草稿，不覆盖已有 Release。远程未配置，CI/Release 未实跑，公开发布由用户决定。
- E55 页面持有路径已修，历史隔离检查通过，实际长期内存曲线待复测。旧取消调试输出触发不明，Job 终止仍有 20ms/最多 3 秒轮询并警告一次。

## 3. 工作约定

- 用户接手运行测试，未经新指示不启动软件、检查程序、FFmpeg、UI 自动化，不接管真实配置/媒体/剪贴板/用户进程。E79 仅获准本轮 HUD 离屏绑定/布局检查，不外推为宿主、硬件或其他检查授权。构建、静态和 ZIP 校验不能冒充运行验收。
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

下一步由用户安装并显式退出重启 0.4.2，重点确认离屏未覆盖的原生单/双击返回、拖动互斥、动效、透明区域和 DPI/跨屏恢复；单一草稿预览和区域绑定已离屏检查。真实保存/撤销/复制、计时与写入失败继续按 A26/A27 保留。需要框架改动时先停下说明并等架构侧分析。宿主 M5 缺口见[验收台账](DEVELOPMENT.md#acceptance)。
