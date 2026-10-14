# TractorTool 开发上下文

更新日期：2026-10-14。本页只保留接续工作所需信息；实现、验收和未关闭项统一看 [DEVELOPMENT](DEVELOPMENT.md#current-status)，命令与逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

**当前在 M5，本轮处理用户新报的“页面离开清理失败”。** E103 真实堆栈定位到剪贴板插件取消 WinRT 操作；仅插件改为原生读取结束后检查托管取消，1.1.3 独立包已生成并经子代理复审，安装入口及验证限制见 [台账](DEVELOPMENT.md#local-artifacts)。未运行应用/检查程序或自动安装；继续由用户测试，未暂存/提交。其他插件业务仍暂缓。

切页闪动未解决，Avalonia 12.1.4 升级已按用户反馈撤回至 12.1.2；保持默认渲染及完整动画，不恢复软件渲染开关。E103 与渲染问题没有相同调用链证据，不合并诊断。

PageHost 仅将两个 SplineEasing 四参数调用改为属性初始化，恢复 X1=0.1、Y1=0.9、X2=0.2、Y2=1；保留 500ms、18 DIP、淡入、RenderTransform 及既有渲染设置。NavigationChecks 未改；已撤回的布局位移候选不恢复。

10-13 [E102 升级与撤回](PROJECT_HISTORY.md#evidence-page-framework-upgrade)：依赖、UiApi.AvaloniaVersion、插件/夹具清单及编译引用均恢复 12.1.2，许可来源也恢复旧提交；兼容规则和业务未改。撤回后 15 工程 Release 构建 0 警告/0 错误，未运行应用或检查程序、未重新打包。

E102 独立 EXE/ZIP 仅保留为历史产物，不再作为运行入口；用户退出试验进程后使用原 VS Code 启动项重新构建。软件渲染略好只代表观感差异，升级已实测无效，根因仍未确定。不引入 mask/覆盖层/插件布局修正；E99 EOF 回归和其他验收缺口仍见[下一步](DEVELOPMENT.md#next)。

## 2. 接续时必须保留

- **测试归用户**：E62 已反馈常规测试暂未发现问题，不外推专项通过；缓动修正及版本升级未消闪，软件渲染仅略好。用户反馈不等于所有专项回归已通过。E94 两项 P2 继续暂缓。
- **已有工作区改动**：保留用户 `Directory.Build.props` 与既有 PageHost 缓动修正；E103 新增剪贴板 Windows 适配、1.1.3 清单、模型用例和文档修改，均未暂存/提交。依赖/SDK 的 Avalonia 声明、许可来源、Program/App 已无升级试验差异。CI/Release 尚未实跑。
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
