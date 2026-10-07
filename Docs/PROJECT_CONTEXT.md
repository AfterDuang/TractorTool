# TractorTool 开发上下文

更新日期：2026-10-07。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；用户对上一版反馈“看着没问题”并允许继续。最新约定：凡需修改宿主、Core、Contracts、公共 UI 等软件框架代码，先停下说明原因和影响，交由用户安排架构侧分析；不要凭后续“继续”自行更改框架。约定已进入根 AGENTS.md。

本轮 E68 仅推进 HUD 第二步，SystemHud 0.2.0 支持最多 32 个单指标方案，新增/复制/删除/重命名、保存并使用、撤销、3–300 秒顺序轮播；五个文字变量及单百分比阈值颜色规则。编辑预览标明示例数据，真实桌面草稿预览 6 秒；两者和运行 HUD 复用 HudCard。主页面负责这些功能和共用位置，设置页仍只有五项持久偏好。

方案独立保存在 profiles.json schema 1；旧 hud.json 不迁移，首次由旧 Metric 建立默认方案。三处保存互不提交草稿；坏文件保留到用户显式保存，保存失败不更新内存列表。隐藏/暂停停止采样与轮播，提醒/草稿预览暂挂轮播，到期恢复原方案及最近读数；未知百分比不冒充零。未增加依赖库或公共 API。

HudChecks 与插件隔离构建、引用现有 E67 宿主 SDK 的独立 Rebuild 均 0 警告/0 错误。ZIP 9 文件与输出一致，无共享 SDK DLL，234 个宿主文件哈希未变。纯逻辑检查仅编译未执行，Windows 交互未接管，A26 待验；A25 只增加用户基本反馈，不外推专项硬件通过。当前 ZIP 路径只见 [本地产物](DEVELOPMENT.md#local-artifacts)，证据见 [E68](PROJECT_HISTORY.md#evidence-hud-profiles)。

本轮没有修改框架源码或重新发布宿主。在已更新 E67 加载器的宿主上只需安装 0.2.0 ZIP、显式退出重启。第三步 GPU/温度/硬件传感器/探测/HTTP/JSON/DeepSeek 尚未实施，不能把前两步当作整个参考程序已复刻。

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
| HUD 方案/编辑 | [HudProfile](../Plugins/TractorTool.Plugins.SystemHud/HudProfile.cs)、[编辑模型](../Plugins/TractorTool.Plugins.SystemHud/HudModel.Profiles.cs)、[共享卡片](../Plugins/TractorTool.Plugins.SystemHud/HudCard.axaml) |
| HUD 页面/生命周期 | [HudView](../Plugins/TractorTool.Plugins.SystemHud/HudView.axaml)、[HudModel](../Plugins/TractorTool.Plugins.SystemHud/HudModel.cs) |
| Windows 采集/窗口 | [WindowsMetrics](../Plugins/TractorTool.Plugins.SystemHud/WindowsMetrics.cs)、[HudWindow](../Plugins/TractorTool.Plugins.SystemHud/HudWindow.axaml.cs) |
| HUD 规则/纯逻辑检查 | [插件文档](Plugins/SystemHud.md)、[HudChecks](../tests/TractorTool.HudChecks/Program.cs) |
| 托盘公共接口 | [PluginTray](../TractorTool.UI/PluginTray.cs)、[接入规范](PLUGIN_DEVELOPMENT.md#tray-menu) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 分发与维护 | [脚本导航](../scripts/README.md)、[Build-Distribution](../scripts/Build-Distribution.ps1) |

下一步由用户验证 0.2.0 的 A26 交互，再按三步计划接续高级数据源；需要框架修改时先停下说明，等架构侧分析。A03/A25/A24 专项和宿主 M5 的远程发布、媒体/硬件、长期内存、迁移故障、跨会话及无 SDK 机器缺口继续看 [验收台账](DEVELOPMENT.md#acceptance)。
