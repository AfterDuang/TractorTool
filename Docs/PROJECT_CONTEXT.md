# TractorTool 开发上下文

更新日期：2026-10-11。本页只保留接续工作所需信息；实现、验收和未关闭项统一看 [DEVELOPMENT](DEVELOPMENT.md#current-status)，命令与逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

**当前在 M5，切页动画末段抖动修正待用户复测；插件问题暂缓。** 用户明确“动画快结束时闪一下，人眼看像抖动”，关动画后不出现。三轮失败尝试已回滚；用户随后批准采用官方文字渲染方案。

[E100 文字渲染修正](PROJECT_HISTORY.md#evidence-page-text-rendering)：仅在公共 PageHost 设置 `TextHintingMode.None` 和 `BaselinePixelAlignment.Unaligned`，保留全部原动画参数和启动调度。构建、导航回归及离屏文字对照已执行；单独 Unaligned 无改善，两项组合使夹具中的跳变减小但未完全连续。这不是显示器逐帧证据，不将官方建议或测试结果当作本机根因确认。

下一步由用户复测相同切页和静止文字清晰度，不接着改首帧或引入快照。E99 reader 不给 EOF 的 P2 回归及其他缺口仍未处理，验证状态统一见[下一步](DEVELOPMENT.md#next)。

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
