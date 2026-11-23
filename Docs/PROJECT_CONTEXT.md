# TractorTool 开发上下文

更新日期：2026-11-23。本页只保留接续工作所需信息。实现、验收和未关闭项统一看 [DEVELOPMENT](DEVELOPMENT.md#current-status)，命令与逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

1.1.0 宿主和四个随包插件沿用 Avalonia 12.1.6 基线。框架依赖由 [Directory.Packages.props](../Directory.Packages.props) 维护，业务私有库在对应插件工程中定版。插件清单须匹配目标宿主。

1.1.0 插件隔离与分发校验见 [E137](PROJECT_HISTORY.md#evidence-release-110)，设备验收缺口见[当前台账](DEVELOPMENT.md#current-status)。推送正式版本标签即触发 GitHub Actions 构建、校验并公开发布。

框架待确认项集中在数据迁移、退出及进程清理；切页末段闪动保留默认渲染与动效，等待上游修复。插件当前验收见[验收台账](DEVELOPMENT.md#acceptance)，特别是文件夹历史的真实 CLI/窗口回归和 HUD 硬件场景。

## 2. 接续时必须保留

- **测试归用户**：E62 已反馈常规测试暂未发现问题，不外推专项通过；此前缓动修正及 12.1.4 升级未消闪，软件渲染仅略好。E117 的 12.1.6 已收到一般使用反馈，不视为闪动或所有专项回归通过。E94 两项 P2 继续暂缓。
- **工作区与范围**：接续时核对工作区和远端状态，保留其他开发者改动。普通分支 CI 的结果见开发台账中的 A22。
- **已确认的框架边界**：外部依赖只有 Packages/依赖来源，默认 GitHub，不恢复自有源；发布自动识别 Plugins 一级目录的清单与唯一工程，不恢复额外名单。细节见[架构](ARCHITECTURE.md#packages)及[脚本规则](../scripts/README.md#随包插件自动识别)。
- **工作约定**：子代理选择、框架变更及发布授权遵循根 [AGENTS.md](../AGENTS.md) 和用户当前指示。

## 3. 代码入口

| 工作 | 入口 |
| --- | --- |
| 数据迁移与默认初始化 | [AppPaths](../TractorTool.Core/Storage/AppPaths.cs)、[DataDirectories](../TractorTool.Core/Storage/DataDirectories.cs)、[DataRootMigration](../TractorTool.Core/Storage/DataRootMigration.cs)、[PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[启动组装](../TractorTool/App.axaml.cs) |
| 进程清理与回归 | [WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) |
| 独立打包 | [Build-Plugin](../scripts/Build-Plugin.ps1)、[Verify-PluginHost](../scripts/Verify-PluginHost.ps1)、[脚本边界](../scripts/README.md) |
| 装载与公共接口 | [PluginLoadContext](../TractorTool.Core/Plugins/PluginLoadContext.cs)、[接入规范](PLUGIN_DEVELOPMENT.md) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 分发与维护 | [Build-Distribution](../scripts/Build-Distribution.ps1)、[自动检查入口](DEVELOPMENT.md#automated-checks) |
