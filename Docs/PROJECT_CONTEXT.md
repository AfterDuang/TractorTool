# TractorTool 开发上下文

更新日期：2026-10-12。本页只保留接续工作所需信息；实现、验收和未关闭项统一看 [DEVELOPMENT](DEVELOPMENT.md#current-status)，命令与逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

**当前在 M5，切页末段闪烁仍未解决；插件问题暂缓。** 2026-10-12 用户确认卡片/边框在切页快结束时仍闪，要求查 Avalonia 官方及社区。本轮只读调研和既有图片复核，未运行应用或修改生产代码。已回滚的三轮起始时序/快照尝试不恢复。

[E100 末段调研](PROJECT_HISTORY.md#evidence-page-flicker-research)：现行 PageHost 保留文字 Hinting/基线、整体透明度和灰度抗锯齿设置。此前 11 项检查通过只证明对应夹具；既有图片在更早的 0.05→0.01 DIP 区间仍有填充/边框亮度变化，位移与透明度同时改变，尚不能归因。社区末帧报告只是线索，不能套用旧版本结论或 Bounds 绕行方案。

下一步应分离纯位移/纯淡入/原组合，并对照软件与原生 GPU 的整个末段；先取得定位证据再改生产实现，不改变用户要求保留的动效。对照尚未执行，运行授权仍按用户约定；E99 reader 不给 EOF 的 P2 回归及其他缺口仍未处理，验证状态统一见[下一步](DEVELOPMENT.md#next)。

## 2. 接续时必须保留

- **测试归用户**：E62 已反馈常规测试暂未发现问题，不外推专项通过；E100 最新指示仅授权隔离切页预览/诊断，不扩展为其他运行测试，不接管真实配置/媒体/插件。E94 两项 P2 继续暂缓。
- **已有工作区改动**：保留用户 `.vscode/launch.json`、`.gitignore`、`Directory.Build.props`，不纳入本轮提交；产品版本只读取 props。被忽略但已跟踪的 Docs 用 `git add -u` 指定文件暂存。本地提交不等于远程授权，CI/Release 尚未实跑。
- **已确认的框架边界**：外部依赖只有 Packages/依赖来源，默认 GitHub，不恢复自有源；发布自动识别 Plugins 一级目录的清单与唯一工程，不恢复额外名单。细节见[架构](ARCHITECTURE.md#packages)及[脚本规则](../scripts/README.md#随包插件自动识别)。
- **工作约定**：遵循根 [AGENTS.md](../AGENTS.md)，保留精干框架与独立插件边界；插件工作若需更改框架，先交架构侧分析。只改当前授权范围，不动真实用户数据、不推送；子代理显式使用 gpt-6.1-sol / high。

## 3. 代码入口

| 工作 | 入口 |
| --- | --- |
| 进程清理与回归 | [WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) |
| 独立打包 | [Build-Plugin](../scripts/Build-Plugin.ps1)、[Verify-PluginHost](../scripts/Verify-PluginHost.ps1)、[脚本边界](../scripts/README.md) |
| 装载与公共接口 | [PluginLoadContext](../TractorTool.Core/Plugins/PluginLoadContext.cs)、[接入规范](PLUGIN_DEVELOPMENT.md) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 分发与维护 | [Build-Distribution](../scripts/Build-Distribution.ps1)、[自动检查入口](DEVELOPMENT.md#automated-checks) |
