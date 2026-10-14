# TractorTool 开发上下文

更新日期：2026-10-15。本页只保留接续工作所需信息；实现、验收和未关闭项统一看 [DEVELOPMENT](DEVELOPMENT.md#current-status)，命令与逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

**当前在 M5，E109 已修复 E108 两项 P2，运行待验证。** 卸载保留完整请求，仅纯代码卸载免除 DataRoot 一致性约束；取消安装/重置共用清理，确认未激活移码的归属后移回原 staging 再删除，成功后才清 Pending。3 名 gpt-6.1-sol / high 复审发现存在性探测可能吞访问故障，已补严格属性探测及文件占位回归；两名复核最后修改，未发现新增确定缺陷。两轮 15 工程 Release 均 0 错误/1 条 NU1900，新增 11 项检查仅编译，未运行检查程序或应用、未改真实数据。用户随后授权将本轮存储修复保存为本地提交，不推送。详见 [E109](PROJECT_HISTORY.md#evidence-reset-transaction-retry) 和[台账](DEVELOPMENT.md#current-status)。E107 目标副本复验、E105 缺失探测/旧目标引用区分/双根清理保留；离线、ACL、未完迁移不授权重置，目标冲突不得覆盖。下一步由用户在隔离数据上验证恢复和失败重试，不沿用旧运行证据。

E103 剪贴板 1.1.3 已提供独立包；WinRT 完成后检查托管取消，读取中离页、快速选择、重返页面及原生资源释放仍待用户回归，不能强制终止不支持取消的调用。安装入口见[开发操作](DEVELOPMENT.md#workflow)。其他插件业务继续暂缓。

切页末段闪动仍未解决，Avalonia 已撤回 12.1.2，E102 EXE/ZIP 只作历史产物；沿用原 VS Code 启动项。保留默认渲染、PageHost 缓动属性初始化及 500ms、18 DIP、淡入、RenderTransform，不恢复软件开关或布局位移候选。E103 与闪动没有同链证据；E99 EOF 回归及其他验收缺口见[下一步](DEVELOPMENT.md#next)。

## 2. 接续时必须保留

- **测试归用户**：E62 已反馈常规测试暂未发现问题，不外推专项通过；缓动修正及版本升级未消闪，软件渲染仅略好。用户反馈不等于所有专项回归已通过。E94 两项 P2 继续暂缓。
- **已有工作区改动**：核对实际工作区后继续，不覆盖并行修改或既有 PageHost 缓动修正。E104/E105 仅处理宿主/Core 存储与启动，剪贴板回归及其他插件问题不并入本轮。CI/Release 尚未实跑。
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
