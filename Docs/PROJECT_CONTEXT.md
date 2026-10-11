# TractorTool 开发上下文

更新日期：2026-10-11。本页只保留接续工作所需信息；实现、验收和未关闭项统一看 [DEVELOPMENT](DEVELOPMENT.md#current-status)，命令与逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

**当前在 M5 可靠性与发布准备；先处理框架，插件问题暂缓。本轮按用户截图排查并修复页面切换闪动。** Windows 通知、pip/Python 和托盘动态方案列表不在本轮范围。

最近修复 [E100](PROJECT_HISTORY.md#evidence-page-entrance)：用户明确要求保持动画展示效果，已撤回上一版缩短/删除淡入及跳过首次动画的处理，恢复原参数。公共 PageHost 只调整已挂载页的启动时序，避免设为透明后再排队；未挂载页仍延迟启动并校验页面身份。启动时序、原动效、快速导航及动画开关断言已补并编译，未执行检查程序或真实 UI；调度空档风险已修正，实际闪动原因及效果仍待运行确认。未改公开 ABI、插件业务或 GPU 配置。

此前 E99 生产代码提交 `907d23e`、三代理审查记录 `7ee3e2d` 保留。**进程树清理成功但 reader 不给 EOF 的 P2 回归仍未补**；不把此次导航构建当作进程或整版运行验证。全部覆盖限制和后续顺序见[下一步](DEVELOPMENT.md#next)。

当前构建和插件包集中列在[本地产物](DEVELOPMENT.md#local-artifacts)。E100 输出位于 artifacts/e100-navigation，不是自包含完整分发；不要使用已从文档移除的旧测试目录。

## 2. 接续时必须保留

- **测试归用户**：E62 已反馈常规依赖/视频/GPU/升级/迁移测试暂未发现问题，不重复要求常规测试、不外推专项通过。未经新指示不启动应用、检查程序、FFmpeg 或接管 UI；此前 HUD 离屏授权不外推到框架测试。E94 插件两项 P2 仍暂缓，状态留在[台账](DEVELOPMENT.md#next)。
- **已有工作区改动**：保留用户 `.vscode/launch.json`、`.gitignore`、`Directory.Build.props`，不纳入本轮提交；产品版本只读取 props。被忽略但已跟踪的 Docs 用 `git add -u` 指定文件暂存。本地提交不等于远程授权，CI/Release 尚未实跑。
- **已确认的框架边界**：外部依赖只有 Packages/依赖来源，默认 GitHub，不恢复自有源；发布自动识别 Plugins 一级目录的清单与唯一工程，不恢复额外名单。细节见[架构](ARCHITECTURE.md#packages)及[脚本规则](../scripts/README.md#随包插件自动识别)。
- **工作约定**：遵循根 [AGENTS.md](../AGENTS.md)，保留精干框架与独立插件边界；插件工作若需更改框架，先交架构侧分析。只改当前授权范围，不动真实用户数据、不推送；子代理显式使用 gpt-6.1-sol / high。

## 3. 代码入口

| 工作 | 入口 |
| --- | --- |
| 页面切换与回归 | [PageHost](../TractorTool.UI/Controls/PageHost.cs)、[NavigationChecks](../tests/TractorTool.IntegrationChecks/NavigationChecks.cs)；设置/日志及视频内层复用同一控件 |
| 进程清理与回归 | [WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) |
| 独立打包 | [Build-Plugin](../scripts/Build-Plugin.ps1)、[Verify-PluginHost](../scripts/Verify-PluginHost.ps1)、[脚本边界](../scripts/README.md) |
| 装载与公共接口 | [PluginLoadContext](../TractorTool.Core/Plugins/PluginLoadContext.cs)、[接入规范](PLUGIN_DEVELOPMENT.md) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 分发与维护 | [Build-Distribution](../scripts/Build-Distribution.ps1)、[自动检查入口](DEVELOPMENT.md#automated-checks) |
