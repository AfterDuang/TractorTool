# TractorTool 开发上下文

更新日期：2026-10-07。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；本轮修复用户实测的 HUD 常驻服务失败，并补齐插件主页面，证据见 [E66](PROJECT_HISTORY.md#evidence-hud-startup-page)。根因是插件加载器没有把 Microsoft.Win32.Primitives 归为 .NET 框架程序集，导致 Win32Exception 连同错误处理都无法解析。Core 补上 Win32 框架名称，未放开任意 Microsoft.* 或缺失私有依赖兜底。

SystemHud 0.1.1 使用既有 AddNavigationPage：主页面放运行/失败状态、显示/隐藏、暂停/恢复、预览、最近内容和布局；设置保留启用、默认常驻、提醒、穿透、动画。两处保存各自字段，不隐式提交另一页草稿；托盘临时指标与持久指标分开。启动失败禁用所有控制，错误进入统一日志；采样策略未增加主页面轮询。

15 工程隔离 Release 构建、宿主自包含发布及新 SDK 独立插件打包通过；回归夹具覆盖 Win32Exception 在默认上下文解析，保留私有依赖缺失失败用例，只编译未运行。ZIP 9 文件哈希一致、无共享 DLL，打包前后 234 个发布宿主文件未变。实际修复效果未运行确认，A25 保留待复测；不是“第一步运行已通过”。

产物路径只见 [本地产物](DEVELOPMENT.md#local-artifacts)。本次需要更新宿主及插件：F5 用户先显式退出并重新构建宿主，再更新 0.1.1 ZIP、显式退出重启；旧加载器仅更新插件仍会失败。不覆盖已安装 DLL/数据，不启动程序。三步计划仍为基础 HUD → 多方案/轮播/模板/颜色规则/编辑 → 高级传感器/数据源，后两步未实施。

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
| HUD 页面/生命周期 | [HudView](../Plugins/TractorTool.Plugins.SystemHud/HudView.axaml)、[HudModel](../Plugins/TractorTool.Plugins.SystemHud/HudModel.cs) |
| Windows 采集/窗口 | [WindowsMetrics](../Plugins/TractorTool.Plugins.SystemHud/WindowsMetrics.cs)、[HudWindow](../Plugins/TractorTool.Plugins.SystemHud/HudWindow.axaml.cs) |
| HUD 规则/纯逻辑检查 | [插件文档](Plugins/SystemHud.md)、[HudChecks](../tests/TractorTool.HudChecks/Program.cs) |
| 托盘公共接口 | [PluginTray](../TractorTool.UI/PluginTray.cs)、[接入规范](PLUGIN_DEVELOPMENT.md#tray-menu) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 分发与维护 | [脚本导航](../scripts/README.md)、[Build-Distribution](../scripts/Build-Distribution.ps1) |

下一步先由用户确认 E66 启动修复与主页面/设置分工，再接续 HUD 第二步；A25/A24 未关闭。宿主 M5 的远程发布、媒体/硬件细项、长期内存、迁移故障、跨会话及无 SDK 机器缺口继续看 [验收台账](DEVELOPMENT.md#acceptance)，不因新增插件而视为已关闭。
