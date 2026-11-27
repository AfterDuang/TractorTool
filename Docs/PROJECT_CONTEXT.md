# TractorTool 开发上下文

更新日期：2026-11-27。本页只保留接续工作所需信息。实现、验收和未关闭项统一看 [DEVELOPMENT](DEVELOPMENT.md#current-status)，命令与逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

1.1.0 宿主和四个随包插件沿用 Avalonia 12.1.6 基线。框架依赖由 [Directory.Packages.props](../Directory.Packages.props) 维护，业务私有库在对应插件工程中定版。插件清单须匹配目标宿主。

1.1.0 插件隔离与分发校验见 [E137](PROJECT_HISTORY.md#evidence-release-110)，设备验收缺口见[当前台账](DEVELOPMENT.md#current-status)。推送正式版本标签即触发 GitHub Actions 构建、校验并公开发布。

框架待确认项集中在数据迁移、退出及进程清理；切页末段闪动保留默认渲染与动效，等待上游修复。插件当前验收见[验收台账](DEVELOPMENT.md#acceptance)，特别是文件夹历史的真实 CLI/窗口回归和 HUD 硬件场景。

`Dev` 已完成 DLL、uv/Python 和 pnpm/Node 接入，三个实现提交止于 `1848ca2`。[E151](PROJECT_HISTORY.md#evidence-dependency-review-fixes) 外部依赖修复之后，[E152](PROJECT_HISTORY.md#evidence-host-review-fixes) 补齐宿主管理事务/回退现场保护、启动退出收尾、暂时 I/O 重验及 CI 门禁；主代理自审与 SOL 分项复核完成，授权的隔离回归通过，已本地提交 `789c4f6`，尚未推送。Contracts API 1.10 / UI API 1.6、旧插件接口及 Python 身份/记录不变。接续由用户确认恢复交互及真实使用效果，A04/A30–A33 未验场景保留；不推送或发版。

Packages 导航为“外部工具、DLL 运行库、Python 环境、Node 环境”，搜索、统计、批量清理限定当前可见范围；缓存维护归对应语言。来源默认 GitHub/PyPI/npm，可添加公开兼容来源，不恢复自有源。当前测试产物统一见[开发台账](DEVELOPMENT.md#development-artifacts)，历史构建不作为当前运行入口。

语言环境首轮限定 Windows x64、固定工具链和锁文件，禁用安装脚本；Node 使用 hoisted+copy，业务脚本显式定位环境模块。迁移在目标最终路径按原锁重建，失败保留原数据。版本与接入规则统一见 [Python](PLUGIN_DEVELOPMENT.md#python-environments) / [Node](PLUGIN_DEVELOPMENT.md#node-environments)。E151/E152 仅运行隔离自动回归，未启动真实主窗口或安装真实语言依赖；NU1900 导致在线漏洞数据查询未完成，CI 新门禁尚无远端执行证据，已发布 1.1.0 不变。

[AB 审片 0.1.7](DEVELOPMENT.md#media-compare) 已提交并推送 `e025a04`；构建、静态 Review 和独立打包证据见 [E145](PROJECT_HISTORY.md#evidence-media-compare-scrub-optimization)。用户反馈手感“也还行”，专项性能及音画验收仍开放。保持插件侧“只编译和 Review，用户运行测试”的约定，后续验证方法及限制统一见台账；保留设备锁与 `.vscode/settings.json`。

## 2. 接续时必须保留

- **测试归用户**：已有常规使用反馈与隔离回归仅覆盖记录中的版本和范围；真实 UI、硬件及未关闭项见[验收台账](DEVELOPMENT.md#acceptance)。闪动保持官方后端，不恢复已撤回的实验；E94 两项 P2 继续暂缓。
- **工作区与范围**：接续时核对工作区和远端状态，保留其他开发者改动。普通分支 CI 的结果见开发台账中的 A22。
- **已确认的框架边界**：外部依赖只有 Packages/依赖来源，默认 GitHub/PyPI/npm，不恢复自有源；发布自动识别 Plugins 一级目录的清单与唯一工程，不恢复额外名单。细节见[架构](ARCHITECTURE.md#packages)及[脚本规则](../scripts/README.md#随包插件自动识别)。
- **工作约定**：子代理选择、框架变更及发布授权遵循根 [AGENTS.md](../AGENTS.md) 和用户当前指示。

## 3. 代码入口

| 工作 | 入口 |
| --- | --- |
| 数据迁移与默认初始化 | [AppPaths](../TractorTool.Core/Storage/AppPaths.cs)、[DataDirectories](../TractorTool.Core/Storage/DataDirectories.cs)、[DataRootMigration](../TractorTool.Core/Storage/DataRootMigration.cs)、[PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[启动组装](../TractorTool/App.axaml.cs) |
| 进程清理与回归 | [WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) |
| 独立打包 | [Build-Plugin](../scripts/Build-Plugin.ps1)、[Verify-PluginHost](../scripts/Verify-PluginHost.ps1)、[脚本边界](../scripts/README.md) |
| AB 审片 | [源码入口](../Plugins/TractorTool.Plugins.MediaCompare/README.md)、[需求与验收](Plugins/MediaCompare.md)、[当前状态](DEVELOPMENT.md#media-compare) |
| 装载与公共接口 | [PluginLoadContext](../TractorTool.Core/Plugins/PluginLoadContext.cs)、[接入规范](PLUGIN_DEVELOPMENT.md) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 外部依赖扩展 | [公开包契约](../TractorTool.Contracts/Packages/IExternalPackageService.cs)、[包定义](../TractorTool.Core/Packages/PackageDefinitions.cs)、[包服务](../TractorTool.Core/Packages/ExternalPackageService.cs)、[清单投影](../scripts/Read-PluginManifest.ps1)；新增设计与目录见[架构 7.3](ARCHITECTURE.md#dependency-extension) |
| Python 环境 | [声明与工具链](../TractorTool.Core/Packages/Python/PythonEnvironmentDefinitions.cs)、[安装](../TractorTool.Core/Packages/Python/PythonEnvironmentInstaller.cs)、[服务与占用](../TractorTool.Core/Packages/Python/PythonEnvironmentService.cs)、[迁移重建](../TractorTool.Core/Packages/Python/PythonEnvironmentMigration.cs)、[真实夹具](../tests/PythonEnvironmentProbePlugin/README.md) |
| Node 与共享环境 | [Node 安装](../TractorTool.Core/Packages/Node/NodeEnvironmentInstaller.cs)、[输入策略](../TractorTool.Core/Packages/Node/NodeInputPolicy.cs)、[共享事务](../TractorTool.Core/Packages/Environments/ManagedEnvironmentService.cs)、[混合迁移](../TractorTool.Core/Storage/LanguageEnvironmentDataMigration.cs)、[真实 npm 夹具](../tests/NodeEnvironmentProbePlugin/README.md) |
| 分发与维护 | [Build-Distribution](../scripts/Build-Distribution.ps1)、[自动检查入口](DEVELOPMENT.md#automated-checks) |
