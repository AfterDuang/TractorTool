# TractorTool 开发上下文

更新日期：2026-11-15。本页只保留接续工作所需信息；实现、验收和未关闭项统一看 [DEVELOPMENT](DEVELOPMENT.md#current-status)，命令与逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

**E117：官方 Avalonia 12.1.6 已升级，用户测试后反馈暂未发现升级问题并授权本地提交，不推送。** 已同步宿主/公共 UI、三个正式插件与两个机制夹具的引用和清单；DataGrid 保持独立发布的 12.1.2，FluentAvaloniaUI 3.1.0、Contracts API 1.7 / UI API 1.5 不变。15 工程 Release 构建 0 警告/0 错误，三个正式插件用发布 SDK 独立打包，完整 ZIP 的 379 文件静态校验及共享程序集身份核对通过。用户测试时 FFmpeg 安装末尾曾拒绝移动目录，重试成功，未修改安装/进程代码，根因未定位。Agent 未启动应用或检查程序；测试入口、匹配插件版本及旧安装升级顺序统一见[升级指引](DEVELOPMENT.md#avalonia-1216-upgrade)，构建与用户反馈见 [E117](PROJECT_HISTORY.md#evidence-avalonia-1216-upgrade)。原动效与默认渲染未改，不宣称消闪。

**新增文档交接：** 用户拟开发面向普通用户的[文件夹版本历史插件](Plugins/FolderHistory.md)，本会话仅受托整理文档，后续由相关 Agent 接手开发。先区分用户已明确需求与自动保存、Kopia 等设计建议，核对凭据和大型历史库的框架边界；不得把文档当作开发/运行授权或自行扩展宿主。状态见[当前台账](DEVELOPMENT.md#current-status)，不替代原 M5 工作。

**当前在 M5，E113 已修正退出时页头绑定访问空呈现器的问题，未推送。** E112 的同步绑定在清理/恢复时遇到 Child=null；现用 Avalonia 空条件绑定处理，保留原动效和清理逻辑。宿主及集成检查工程构建通过，新增回归仅编译，真实退出待用户验证。E111 存储修复仍待运行验收；结果统一见[当前台账](DEVELOPMENT.md#current-status)，过程见 [E113](PROJECT_HISTORY.md#evidence-header-null-binding) / [E112](PROJECT_HISTORY.md#evidence-page-header-motion) / [E111](PROJECT_HISTORY.md#evidence-framework-failure-paths)。

接续先由用户确认退出不再出现 Opacity / RenderTransform 的 Child 空值诊断，再验宿主页头/自带页头互切、快速切页、离页取消与动画开关下的同步效果。存储验收继续核对隔离数据下的配置/祖先占位、卸载故障恢复和退出等待，再覆盖迁移收尾、默认初始化、重置失败后恢复旧根及安装移码重试。权限、离线或未完迁移不授权重置，目标冲突不能覆盖；不要改真实用户数据制造故障。剪贴板原生回归使用 E117 的 1.1.4 匹配包，入口见[开发操作](DEVELOPMENT.md#avalonia-1216-upgrade)；其他插件业务继续暂缓。

**切页闪动已归档，等待官方修复，不做自编译后端或定制包。** [E114–E116](PROJECT_HISTORY.md#evidence-page-flicker-precision) 的原生采样与真实宿主对照支持混合目标精度相关原因；用户确认高精度消闪、普通精度仍闪，具体量化/驱动细节未证实。E117 已升级官方 Avalonia 12.1.6，实际消闪效果仍待用户验证；保留默认渲染、缓动属性初始化、500ms、18 DIP、0→1 淡入及 RenderTransform；不能把候选结果记为正式修复。诊断工程/输出、采样器和测试入口已清理，受管排查修改已回退；原始结果、补丁与测试数据保留。后续只在官方相关修复或受支持配置可用时评估复验，不恢复旧候选或重复同样测试；未创建上游 Issue，也无上游修复承诺。其他缺口见[下一步](DEVELOPMENT.md#next)。

## 2. 接续时必须保留

- **测试归用户**：E62 已反馈常规测试暂未发现问题，不外推专项通过；此前缓动修正及 12.1.4 升级未消闪，软件渲染仅略好。E117 的 12.1.6 已收到一般使用反馈，不视为闪动或所有专项回归通过。E94 两项 P2 继续暂缓。
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
