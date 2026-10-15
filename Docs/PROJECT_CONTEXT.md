# TractorTool 开发上下文

更新日期：2026-10-15。本页只保留接续工作所需信息；实现、验收和未关闭项统一看 [DEVELOPMENT](DEVELOPMENT.md#current-status)，命令与逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

**当前在 M5，最新代码基线为本地提交 `0070fbd`，未推送。** 存储迁移、默认重置与安装/卸载恢复修复已保存；编译及子代理复审完成，运行验证待用户执行。结果统一见[当前台账](DEVELOPMENT.md#current-status)，修复过程见 [E109](PROJECT_HISTORY.md#evidence-reset-transaction-retry)。本轮只整理文档，没有新增代码或运行证据。

接续先核对隔离数据下的迁移收尾、删除自定义根后默认初始化，以及重置失败后恢复旧根、安装/卸载中断后的重试。权限、离线或未完迁移不授权重置，目标冲突不能覆盖；不要改真实用户数据制造故障。E103 剪贴板 1.1.3 的原生回归入口见[开发操作](DEVELOPMENT.md#workflow)，其他插件业务继续暂缓。

切页末段闪动仍未解决，Avalonia 已回退至 12.1.2，E102 EXE/ZIP 只作历史产物；沿用原 VS Code 启动项。保留默认渲染、PageHost 缓动属性初始化及 500ms、18 DIP、淡入、RenderTransform，不恢复软件开关或布局位移候选。E103 与闪动没有同链证据；E99 EOF 回归及其他验收缺口见[下一步](DEVELOPMENT.md#next)。

## 2. 接续时必须保留

- **测试归用户**：E62 已反馈常规测试暂未发现问题，不外推专项通过；缓动修正及版本升级未消闪，软件渲染仅略好。用户反馈不等于所有专项回归已通过。E94 两项 P2 继续暂缓。
- **工作区与范围**：每次接续重新核对 Git 状态，不覆盖并行修改。存储修复已提交，不再把 E104–E109 当作未提交候选；当前优先处理框架可靠性，其他插件业务暂缓。CI/Release 尚未实跑。
- **已确认的框架边界**：外部依赖只有 Packages/依赖来源，默认 GitHub，不恢复自有源；发布自动识别 Plugins 一级目录的清单与唯一工程，不恢复额外名单。细节见[架构](ARCHITECTURE.md#packages)及[脚本规则](../scripts/README.md#随包插件自动识别)。
- **工作约定**：遵循根 [AGENTS.md](../AGENTS.md)，保留精干框架与独立插件边界；插件工作若需更改框架，先交架构侧分析。只改当前授权范围，不动真实用户数据、不推送；子代理显式使用 gpt-6.1-sol / high。

## 3. 代码入口

| 工作 | 入口 |
| --- | --- |
| 数据迁移与默认初始化 | [AppPaths](../TractorTool.Core/Storage/AppPaths.cs)、[DataDirectories](../TractorTool.Core/Storage/DataDirectories.cs)、[DataRootMigration](../TractorTool.Core/Storage/DataRootMigration.cs)、[PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[启动组装](../TractorTool/App.axaml.cs) |
| 进程清理与回归 | [WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) |
| 独立打包 | [Build-Plugin](../scripts/Build-Plugin.ps1)、[Verify-PluginHost](../scripts/Verify-PluginHost.ps1)、[脚本边界](../scripts/README.md) |
| 装载与公共接口 | [PluginLoadContext](../TractorTool.Core/Plugins/PluginLoadContext.cs)、[接入规范](PLUGIN_DEVELOPMENT.md) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 分发与维护 | [Build-Distribution](../scripts/Build-Distribution.ps1)、[自动检查入口](DEVELOPMENT.md#automated-checks) |
