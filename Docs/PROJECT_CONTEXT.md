# TractorTool 开发上下文

更新日期：2026-11-28。本页只保留接续工作所需信息。实现、验收和未关闭项统一看 [DEVELOPMENT](DEVELOPMENT.md#current-status)，命令与逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

**当前产品版本为 2.0.0；用户已授权提交并推送 `main` 与 `v2.0.0` 标签。** E161 及此前外部依赖扩展已在 `d37e2ec` 合并到本地 `main`。本轮只调整宿主产品版本和文档，不修改插件版本、SDK API 或共享程序集绑定身份。发布范围与验证见 [E162](PROJECT_HISTORY.md#evidence-release-200)，附件是否公开以标签工作流实际结果为准。

2.0.0 纳入 DLL、uv/Python、pnpm/Node、上游归档安装和插件历史版本清理。当前 Contracts API 1.11 / UI API 1.6，Avalonia 12.1.6；旧接口和收据兼容边界见[插件规范](PLUGIN_DEVELOPMENT.md)。[1.1.0](PROJECT_HISTORY.md#evidence-release-110) 为不含上述扩展的历史分发。

Packages 按工具、DLL、Python、Node 分类，来源默认 GitHub/PyPI/npm，可添加公开兼容来源和 HTTPS 目录。安装类型与来源分开；多来源不自动回退，已装依赖可校验后离线复用。框架不维护自有运行库发布包；[AB 审片](DEVELOPMENT.md#media-compare) 已使用官方归档，具体插件资料留在插件文档。

**接续重点**：由用户在原有安装/调试目录按当前源码构建，验证历史版本预览、取消、重启清理和手动删除后的记录释放；再检查 Packages 无引用清理及相同诊断行去重。隔离构建目录只保存编译证据，不作为原安装现场的验收启动入口。跨插件版本复用语言环境不在本轮范围；已有 Python/Node 测试包接口不变。[A04/A30–A33](DEVELOPMENT.md#acceptance) 其余专项继续待验。

2.0.0 宿主隔离构建通过，0 警告/0 错误，版本元数据已核对。此前 E161 宿主/Core 构建通过，集成工程有一条 NU1900 警告；GPT‑6.2‑SOL/high 静态复核无剩余阻断。26 项新增检查仍未在本地运行，标签工作流负责隔离回归和完整分发。真实验收由用户负责，未启动主窗口或操作真实应用数据。

此前清理时读取旧清单的 `FileNotFoundException` 已在 E161 修正，引用不完整时仍保留清理保护；增强用例仅编译，证据见 E161 的 `reference-feedback`。

用户最新确认 Ctrl+F5 启动后进程列表不再显示“已挂起”。现场对照指向调试启动相关状态，本轮不修改进程或窗口逻辑；该反馈不替代历史清理和依赖维护的专项验收。

## 2. 接续时必须保留

- **测试归用户**：已有常规使用反馈与隔离回归仅覆盖记录中的版本和范围；真实 UI、硬件及未关闭项见[验收台账](DEVELOPMENT.md#acceptance)。闪动保持官方后端，不恢复已撤回的实验；E94 两项 P2 继续暂缓。
- **工作区与范围**：接续时核对工作区和远端状态，保留其他开发者改动。普通分支 CI 的结果见开发台账中的 A22。
- **已确认的框架边界**：外部依赖只有 Packages/依赖来源，默认 GitHub/PyPI/npm，不恢复自有源；发布自动识别 Plugins 一级目录的清单与唯一工程，不恢复额外名单。细节见[架构](ARCHITECTURE.md#packages)及[脚本规则](../scripts/README.md#随包插件自动识别)。
- **语言环境**：Windows x64、固定工具链与锁输入，禁用安装脚本；迁移在最终目标重建。pnpm 缓存只兼容已核验的登记链接，不放宽载荷或未知链接保护；规则见[Python](PLUGIN_DEVELOPMENT.md#python-environments) / [Node](PLUGIN_DEVELOPMENT.md#node-environments)。
- **工作约定**：子代理选择、框架变更及发布授权遵循根 [AGENTS.md](../AGENTS.md) 和用户当前指示。

## 3. 代码入口

| 工作 | 入口 |
| --- | --- |
| 数据迁移与默认初始化 | [AppPaths](../TractorTool.Core/Storage/AppPaths.cs)、[DataDirectories](../TractorTool.Core/Storage/DataDirectories.cs)、[DataRootMigration](../TractorTool.Core/Storage/DataRootMigration.cs)、[PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[启动组装](../TractorTool/App.axaml.cs) |
| 进程清理与回归 | [WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) |
| 独立打包 | [Build-Plugin](../scripts/Build-Plugin.ps1)、[Verify-PluginHost](../scripts/Verify-PluginHost.ps1)、[脚本边界](../scripts/README.md) |
| AB 审片 | [源码入口](../Plugins/TractorTool.Plugins.MediaCompare/README.md)、[需求与验收](Plugins/MediaCompare.md)、[当前状态](DEVELOPMENT.md#media-compare) |
| 发现、装载与结果记录 | [PluginHost](../TractorTool.Core/Plugins/PluginHost.cs)、[PluginLoadContext](../TractorTool.Core/Plugins/PluginLoadContext.cs)、[MainViewModel](../TractorTool/ViewModels/MainViewModel.cs)、[接入规范](PLUGIN_DEVELOPMENT.md) |
| 归档依赖 | [PackageDownload](../TractorTool.Core/Packages/PackageDownload.cs)、[PackageArchive](../TractorTool.Core/Packages/PackageArchive.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs)、[组合安装](../TractorTool.Core/Packages/PackageInstallation.cs) |
| 外部依赖扩展 | [公开包契约](../TractorTool.Contracts/Packages/IExternalPackageService.cs)、[包定义](../TractorTool.Core/Packages/PackageDefinitions.cs)、[包服务](../TractorTool.Core/Packages/ExternalPackageService.cs)、[清单投影](../scripts/Read-PluginManifest.ps1)；新增设计与目录见[架构 7.3](ARCHITECTURE.md#dependency-extension) |
| Python 环境 | [声明与工具链](../TractorTool.Core/Packages/Python/PythonEnvironmentDefinitions.cs)、[安装](../TractorTool.Core/Packages/Python/PythonEnvironmentInstaller.cs)、[服务与占用](../TractorTool.Core/Packages/Python/PythonEnvironmentService.cs)、[迁移重建](../TractorTool.Core/Packages/Python/PythonEnvironmentMigration.cs)、[真实夹具](../tests/PythonEnvironmentProbePlugin/README.md) |
| Node 与共享环境 | [Node 安装](../TractorTool.Core/Packages/Node/NodeEnvironmentInstaller.cs)、[输入策略](../TractorTool.Core/Packages/Node/NodeInputPolicy.cs)、[共享事务](../TractorTool.Core/Packages/Environments/ManagedEnvironmentService.cs)、[混合迁移](../TractorTool.Core/Storage/LanguageEnvironmentDataMigration.cs)、[真实 npm 夹具](../tests/NodeEnvironmentProbePlugin/README.md) |
| 分发与维护 | [Build-Distribution](../scripts/Build-Distribution.ps1)、[自动检查入口](DEVELOPMENT.md#automated-checks) |
