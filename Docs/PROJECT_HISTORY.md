# TractorTool 项目历史记录

本文件保存已经结束的实施轮次、当时的命令、失败与验收证据；**不在 Agent 默认阅读链中**。当前状态和待办只看 [DEVELOPMENT.md](DEVELOPMENT.md#current-status)，现行设计看 [ARCHITECTURE.md](ARCHITECTURE.md)。需要追查具体问题时，通过下面索引定位，或用 `rg -n 'E编号|关键词' Docs/PROJECT_HISTORY.md` 搜索对应片段，不必通读。

## 阅读约定

- M1 开发已转入维护，用户允许继续 M2；不等于所有历史验收项都已逐项通过。M2 仍在收敛，归档某次实施记录也不代表整个阶段结束。
- 原记录中的“本轮”“当前”“下一步”仅指记录发生时；旧方案、版本、产物路径和命令不可直接当作当前操作指南。
- 历史通过只覆盖当时的代码。早期 artifacts 曾被外部清理，保留日志路径不保证文件仍存在；失败、未执行及证据缺口均保留。
- 本次从开发文档迁入 E01–E20，保留编号、日期、结果和命令，只调整内部链接指向；不补造运行证据。

## 记录索引

| 阶段 | 记录 |
| --- | --- |
| M4（进行中） | [E31 · 视频码率模式、GPU 偏好与紧凑布局（2026-10-04）](#evidence-video-rate-control) |
| M4（进行中） | [E30 · 视频双功能工作区与多预设串行（2026-10-04）](#evidence-video-workspace) |
| M3（进行中） | [E29 · 框架边界复审与视频单次导出（2026-10-04）](#evidence-video-export) |
| 后续规划 | [E28 · Python / pip 依赖扩展边界（2026-10-04）](#evidence-python-plan) |
| M3（进行中） | [E27 · 依赖拒绝/缺失路径异常与全局计数修正（2026-10-04）](#evidence-dependency-feedback) |
| M3（进行中） | [E26 · 设置层级与视频读取入口（2026-10-04）](#evidence-settings-hierarchy) |
| M3（进行中） | [E25 · 依赖申请、引用清理与设置分类（2026-10-04）](#evidence-dependency-flow) |
| M1 | [E01 · 首轮工作与证据（2026-10-03）](#evidence-baseline) |
| M1 | [E02 · 用户截图反馈后的修正（2026-10-03）](#evidence-adjustment) |
| M1 | [E03 · 进一步 UI 反馈与参考源码调研（2026-10-03）](#evidence-research) |
| M1 | [E04 · 用户授权继续后的 UI 补完（2026-10-03）](#evidence-ui) |
| M1 | [E05 · 标题栏故障与核心代码审查修复（2026-10-03）](#evidence-review) |
| M1 | [E06 · 统一产品版本和应用图标（2026-10-03）](#evidence-version) |
| M1 | [E07 · 调试绑定错误与 VS Code 预览复核（2026-10-03）](#evidence-binding) |
| M1 | [E08 · 欢迎主页与已安装插件卡片（2026-10-03）](#evidence-home) |
| M1 | [E09 · Review 修复与彩色图标接入（2026-10-03）](#evidence-icons-review) |
| M1 | [E10 · 收紧导航按钮比例（2026-10-04）](#evidence-nav-density) |
| M1 | [E11 · 窗口大小变化时的正文对齐（2026-10-04）](#evidence-page-layout) |
| M1 | [E12 · GPT-6.1-Sol Review 与外观即时生效（2026-10-04）](#evidence-appearance-review) |
| M1 | [E13 · 移除常驻说明与深浅色过渡（2026-10-04）](#evidence-theme-transition) |
| M1 | [E14 · 设置预览与确定死代码清理（2026-10-04）](#evidence-settings-cleanup) |
| M1 | [E15 · 页面滚动条靠右（2026-10-04）](#evidence-scroll-edge) |
| M2（进行中） | [E16 · 本地 Git、插件管理与剪贴板插件（2026-10-04）](#evidence-m2) |
| M2（进行中） | [E17 · ZIP 拖放、启停开关与剪贴板重做（2026-10-04）](#evidence-clipboard-ux) |
| M2（进行中） | [E18 · 拖放边界、卡片布局与空选择绑定修复（2026-10-04）](#evidence-ui-fixes) |
| M2（进行中） | [E19 · VS Code 解决方案与插件开发组织（2026-10-04）](#evidence-project-layout) |
| M2（进行中） | [E20 · 公共 UI、宿主与插件样式职责复核（2026-10-04）](#evidence-style-ownership) |
| M2（进行中） | [E21 · 卡片信息/操作分组及旧插件绑定诊断（2026-10-04）](#evidence-card-layout) |
| M2（进行中） | [E22 · 统一动效与管理卡片原位更新（2026-10-04）](#evidence-shared-motion) |
| M3（进行中） | [E23 · M2 复审与公共进程服务起步（2026-10-04）](#evidence-process-service) |
| M3（进行中） | [E24 · 外部包事务、租约与设置入口（2026-10-04）](#evidence-package-service) |

按需补充：[M1 人工回归清单](#m1-manual-checklist)、[M1 阶段出口](#m1-exit)、[早期文档整理](#early-docs)、[UI 参考源码调研](#ui-reference)、[图标参考调研](#icon-reference)。

## M1：宿主基础与公共 UI

<a id="evidence-baseline"></a>

<details>
<summary>E01 · 首轮工作与证据（2026-10-03）</summary>

### E01 · 首轮工作与证据（2026-10-03）

- 初始源码与用户提供的起点一致：只有 Avalonia 初始工程。先阅读根 AGENTS 和三份文档，再检查文件；未覆盖已存在的额外业务实现。
- 环境：Windows `10.0.26200` / `win-x64`；SDK `10.0.401`、MSBuild `18.9.11`，已装运行时 `10.0.12`。
- 原项目 `dotnet restore` 通过。第一次 Debug 构建因沙箱禁止 Avalonia BuildServices 写本地日志失败；获得正常构建权限后重试成功，0 警告/0 错误。证据在 `artifacts/evidence/baseline-restore.txt`、`baseline-build.txt`、`baseline-build-retry.txt`。后续脚本使用 `-m:1 -nr:false`，避免复用受限的 MSBuild 节点。
- M1 主工程 Debug 构建通过。最终 Release 解决方案构建、自包含发布、独立插件构建、集成检查工程构建均通过，最终各构建 0 警告/0 错误。最初夹具的两个警告已修正；最终验证在用户接手测试前已启动并完成。
- **核心自动化 33/33 通过**：软件/数据/启动路径分离、默认/自定义指针、相对路径与目录重叠拒绝、不可用自定义目录不创建替代、配置版本、原子替换与备份、并发保存、写失败保留原文件、损坏配置/指针保留、插件设置隔离、实例锁、日志边界、清单/SDK/平台/路径校验、禁用/重复/坏 DLL/缺失 DLL、插件目录受阻不重定向。
- **发布产物集成及 Headless UI 21/21 通过**：宿主发布后单独编译测试插件，仅引用发布 SDK；加载真实 DLL/私有依赖；导航/设置工厂实例化；插件与宿主设置保存读取；主题应用；三个固定入口；页面切换/隐藏期间常驻活动继续；停止/释放；禁用/API/Avalonia/平台不匹配不执行构造器；入口/初始化/注册/页面异常；共享 SDK 副本冲突及缺少私有依赖；一个失败插件不阻止另一个加载。
- **独立发布证据**：先发布宿主并计算所有原有文件 SHA256，再构建/复制插件并测试；原有 **231 个宿主文件哈希全部未变**。不是源码引用或假加载。
- 最终证据目录：`artifacts/m1-20261003-153553-4b56cf/`；包含 `build.txt`、`core-checks.txt`、`publish.txt`、`plugin-build.txt`、`integration-build.txt`、`integration-checks.txt`、`host-hashes-before.json`、`host-hashes-after.json`、`hash-result.txt`。控制台汇总为 `artifacts/evidence/final-verification-console.txt`。测试数据和故障插件副本均隔离；核心检查额外使用随机命名的系统临时目录，位置打印于检查结果末尾。
- **真实 Windows 启动已观察到，交互未验收**：曾启动 `artifacts/m1-20261003-153221-489f42/host/TractorTool.exe --profile-root .../ui-profile`，窗口枚举返回 `TractorTool`，PID 45004（仅为当时观测）。该副本 `ui-profile/Logs` 记录启动、托盘创建、真实插件/私有依赖加载、常驻计数。托盘创建日志只证明程序执行到该分支，不证明用户实际可以点击恢复。
- Computer Use 获取/激活窗口被拒绝，返回 `Computer Use was not approved to use tractortool`，未取得窗口截图或交互结果。随后用户明确表示“测试部分我自己来就行”，**停止后续测试及 UI 操作，保留已启动的隔离窗口供用户检查**。
- 当前人工待验：Windows 真实主题/布局/导航、重启持久化、托盘隐藏/恢复/菜单退出、再次启动恢复、`--no-tray` 兜底。没有声称这些已通过。无界面测试不替代 Windows UI 验收。
- 先前文档整理的 Markdown 链接检查是历史记录，不能当作本次应用验证；本次已经核对工程依赖方向和未静态引用具体插件，文档修改后未再启动自动检查（用户已接手测试）。

</details>

<a id="evidence-adjustment"></a>

<details>
<summary>E02 · 用户截图反馈后的修正（2026-10-03）</summary>

### E02 · 用户截图反馈后的修正（2026-10-03）

- 用户指出初版主窗口纯黑、导航像普通按钮、左下角多出“退出”。这些属于 M1 视觉实现不足；之前的构建及 Headless 结果没有验证颜色和最终观感，不能据此宣称 UI 已完成验收。
- 已补充 `TractorTool.UI/Styles/ThemeResources.axaml`，使用深灰分层背景和对应浅色资源；宿主 Fluent palette 复用这些颜色。增加导航图标、选中/悬停、卡片及路径框样式；插件页整理为本地目录卡片和空状态。移除面向用户的 M1/M2/M3/M5 开发阶段文案。
- 左下角只保留三个固定入口，显式退出改在托盘及“设置 → 关于”；无托盘时的状态提示指向设置中的退出入口。
- 用户截图中的两项工作区诊断来自 `RequirePublishedSdk` / `RequirePublishedHost` 在 IDE 设计时也执行。现将其限制为正式构建；设计时通过条件项目引用提供 IntelliSense，正式构建仍用指定 `HostSdkDir` 的 DLL。独立插件新增同版本 Avalonia 12.1.2 的**仅编译引用**，不复制第二套 Avalonia 运行时，也不引用宿主/Core 实现。
- 已做源码与项目配置核对。生产宿主编译、独立插件正常构建、集成检查工程正常构建均 0 警告/0 错误；两个工程的 MSBuild 设计时 `Compile`（`SkipCompilerExecution=true`）及 `ReferencePath` 解析均成功。证据在 `artifacts/evidence/ui-adjustment-build.txt`、`ui-adjustment-plugin-build.txt`、`ui-adjustment-integration-build.txt`、`independent-design-time.txt`、`integration-design-time.txt`。
- **未执行新的自动化测试、集成程序或真实 UI 操作**，遵守用户自行测试的决定。未直接观察 VS Code 问题面板刷新，也未验证本次实际颜色/布局/托盘交互；旧的 33/21 项测试及发布哈希是修正前的证据，不自动适用于当前改动。
- M0 的定义从一开始就是需求、架构和 Agent 文档整理；文档已经存在。本轮没有跳过 M0，问题是 M1 视觉未达到参考效果。当时仅完成局部修正与编译；进一步反馈后的当前结论见[E03](#evidence-research)，不能继续写成只差人工验收。

本次实际执行的编译/设计时解析命令（未运行检查程序）：

```powershell
dotnet build TractorTool/TractorTool.csproj -c Debug -m:1 -nr:false --no-restore -o artifacts/ui-adjustment-build
dotnet restore tests/IndependentPlugin/IndependentPlugin.csproj -m:1 -nr:false
dotnet msbuild tests/IndependentPlugin/IndependentPlugin.csproj -t:Compile -p:DesignTimeBuild=true -p:BuildProjectReferences=false -p:SkipCompilerExecution=true -p:ProvideCommandLineArgs=true -m:1 -nr:false -getItem:ReferencePath
dotnet msbuild tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -t:Compile -p:DesignTimeBuild=true -p:BuildProjectReferences=false -p:SkipCompilerExecution=true -p:ProvideCommandLineArgs=true -m:1 -nr:false -getItem:ReferencePath
dotnet build tests/IndependentPlugin/IndependentPlugin.csproj -c Debug -m:1 -nr:false --no-restore -p:HostSdkDir=H:\ProjH\TractorTool\artifacts\m1-20261003-153553-4b56cf\host -o artifacts/ui-adjustment-plugin-build
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Debug -m:1 -nr:false --no-restore -p:HostSdkDir=H:\ProjH\TractorTool\artifacts\ui-adjustment-build -o artifacts/ui-adjustment-integration-build
```

</details>

<a id="evidence-research"></a>

<details>
<summary>E03 · 进一步 UI 反馈与参考源码调研（2026-10-03）</summary>

### E03 · 进一步 UI 反馈与参考源码调研（2026-10-03）

用户再次提供 Watt Toolkit 与 TractorTool 对比截图，指出整体 UI 不一致、切换动画缺失，并要求分析可复用实现。**当时结论：M1 基础设施已有实现，公共 UI 实现尚未完成；A01 未通过。** [E02](#evidence-adjustment)的改色和图标补丁没有解决完整界面组织问题。

这一调研轮只读源码、查询公开依赖及维护文档；后续授权实现见[E04](#evidence-ui)。当时源码核对发现：

- `MainWindow.axaml` 使用 `ColumnDefinitions="200,*"`、三个手写 PathIcon 与普通 ContentControl；没有页面过渡容器或动画。
- `SettingsView.axaml` 是多层 StackPanel，主题下拉框全宽拉伸；没有复用设置行和合理内容宽度约束。
- `MainViewModel` 直接创建插件管理、日志等控件；公共视觉规则未形成完整 View/模板体系。
- `IPluginPageRegistry` 只有 id/title/factory，没有图标元数据；当前共享程序集名单也未包含候选新控件库，需要随 UI SDK 一起处理。

参考源码依据、固定提交、候选版本和加载边界集中在 [架构 UI 参考依据](#ui-reference)。已确认 Watt 使用 FluentAvalonia 导航/Frame、定制设置行和现成过渡，彩色导航使用图片资源。首选评估 FluentAvaloniaUI 3.1.0 与统一 Fluent 图标；包元数据允许 net10.0/Avalonia 12.1.2 基线，但尚无本项目编译或运行证据。当时项目仍使用原来的 Avalonia.Themes.Fluent，没有接入这些候选依赖。

实际只读查询包括：

```powershell
git status --short # 返回 not a git repository；未创建或重置仓库
rg -n 'ContentControl|ColumnDefinitions|PathIcon|Transitions|Animation|CurrentPage|AddNavigationPage' TractorTool/Views/MainWindow.axaml TractorTool/ViewModels/MainViewModel.cs TractorTool.UI/PluginUi.cs
Invoke-RestMethod -Uri 'https://api.github.com/repos/BeyondDimension/SteamTools/git/trees/develop?recursive=1'
Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/fluentavaloniaui/3.1.0/fluentavaloniaui.nuspec'
Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/fluenticons.avalonia.fluent/2.1.333/fluenticons.avalonia.fluent.nuspec'
```

调研轮静态核对完成；当时构建、自动化测试、发布集成测试与真实 Windows UI 验证均**未执行**。用户自行测试的决定保持有效。参考软件的源码实现和包声明不是 TractorTool 的验收结果。

</details>

<a id="evidence-ui"></a>

<details>
<summary>E04 · 用户授权继续后的 UI 补完（2026-10-03）</summary>

### E04 · 用户授权继续后的 UI 补完（2026-10-03）

本轮从现有工作区继续，没有重建仓库或进入 M2。编辑前源码快照在 `artifacts/ui-foundation-before/`。用户自行测试的决定仍有效：**只进行了源码核对、还原、编译、隔离发布及文件哈希核对，没有运行应用、核心检查、Headless 或集成检查程序。**

实际改动：

- 新增 FluentAvaloniaUI 3.1.0，统一窗口、导航、控件主题、符号图标与 Frame；Avalonia 相关托管包统一 12.1.2、MVVM 保持 8.4.2。最终依赖清单在 `artifacts/ui-foundation-ready/ui-dependencies.txt`，不含旧 Avalonia.Themes.Fluent 或具体插件引用。
- 标题区与内容区统一；导航从 200 改为 88 逻辑单位的图文侧栏，底部只有日志/插件/设置。应用及托盘使用项目自绘图标，生成脚本为 `scripts/Generate-AppIcon.ps1`。
- 三个宿主页面独立为 View/VM。设置改为有内容宽度限制的分组设置行；插件页提供空态、版本/错误信息、名称查找与打开工具；日志页提供只读文本、复制、暂停和打开目录；页面工厂异常有重试与日志入口。
- 公共 SettingRow、ToolIcon、PageHost 和样式放在 UI 工程。切页使用 FAFrame 自带过渡，关闭缓存和导航栈；保存动画开关，保留离页清理与后台服务分离。标题栏仍调用既有关闭/托盘服务，未改变数据及插件目录语义。
- UI API 增至 1.1，保留 1.0 注册方法签名，接受旧 UI 1.0 清单；新元数据包含图标与说明。共享程序集增加 FluentAvalonia，插件不得携带共享副本。示例插件只通过已发布 Contracts/UI 编译，使用新公共设置行和图标。核心检查新增兼容性用例，集成检查同步错误页断言及新主题依赖；**这些用例仅编译，未执行**。

| 检查层级 | 本轮结果与限制 |
| --- | --- |
| 静态核对 | 已核对模板层级、绑定/页面工厂、插件依赖方向及最终依赖清单；修正跨模板导航选择器及派生窗口字体规则；59 个本地文档链接无缺失；不等于真实渲染通过 |
| 还原与构建 | 还原通过；宿主 Debug 构建通过。最终解决方案使用隔离中间/输出目录构建，0 警告/0 错误；独立插件及集成检查工程最终均 0 警告/0 错误 |
| 发布与文件 | win-x64 自包含发布成功；发布后独立构建插件，安装至该副本 Plugins/Test.Independent/1.0.0；原有 234 个宿主文件 SHA256 未变化 |
| 自动化测试 | 未执行；旧 33 项通过不覆盖新增兼容性/动画设置 |
| 集成/Headless 测试 | 未执行；独立编译和文件复制不等于真实加载。旧 21 项通过不覆盖新 UI 底座 |
| 真实 Windows UI | 未执行，由用户验收；标题栏、导航比例、主题、动效、DPI、托盘及重启持久化均不能记为通过 |

编译过程中曾修正缺少 Clipboard/资源查找扩展命名空间、示例注册参数顺序；最终编译通过。默认 Debug 解决方案构建另遇 `AVLN9999`：`TractorTool/obj/Debug/net10.0/TractorTool.dll` 被其他进程占用。未终止用户进程，改用各项目 `obj/UIFoundation/`、`bin/UIFoundation/` 构建成功；不声称默认 Debug 文件占用已解除。

最终产物为 `artifacts/ui-foundation-ready/`：`host/` 已放入独立测试插件，`plugin/` 是插件编译输出，`checks/` 是只编译未运行的集成程序；`host-hashes-before.json`、`host-hashes-after.json`、`hash-result.txt` 记录 234/0 的文件结果。默认运行不触碰真实用户数据需要使用[开发操作](DEVELOPMENT.md#workflow)的 profile 参数。

实际命令（从仓库根目录执行，均未启动测试）：

```powershell
dotnet restore TractorTool.slnx -m:1 -nr:false
dotnet build TractorTool/TractorTool.csproj -c Debug -m:1 -nr:false --no-restore -o artifacts/ui-foundation-build
# 默认构建遇到中间文件占用；保留失败日志，随后用独立目录成功编译
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false --no-restore -p:IntermediateOutputPath=obj/UIFoundation/ -p:OutputPath=bin/UIFoundation/
# 最终发布目标当时为空；此命令不会覆盖已有发布目录
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/ui-foundation-ready/host
dotnet build tests/IndependentPlugin/IndependentPlugin.csproj -c Release -m:1 -nr:false --no-restore -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/ui-foundation-ready/host -o artifacts/ui-foundation-ready/plugin
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false --no-restore -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/ui-foundation-ready/host -o artifacts/ui-foundation-ready/checks
```

文件核对使用 `Get-FileHash -Algorithm SHA256`，发布后先枚举宿主文件，插件构建/复制后只对相同路径再次计算。执行记录为 `artifacts/evidence/ui-foundation-restore.txt`、`ui-foundation-build.txt`、`ui-foundation-solution-build.txt`（文件占用失败）、`ui-foundation-solution-build-isolated.txt`、`ui-foundation-ready-publish.txt`、`ui-foundation-ready-plugin-build.txt`、`ui-foundation-ready-integration-build.txt`。早期试编译及中间发布 `ui-foundation-publish/`、`ui-foundation-final/` 也保留；最后修正了派生窗口的字体样式匹配，用户应使用包含该修正的 ready 副本。

</details>

<a id="evidence-review"></a>

<details>
<summary>E05 · 标题栏故障与核心代码审查修复（2026-10-03）</summary>

### E05 · 标题栏故障与核心代码审查修复（2026-10-03）

用户报告窗口无法拖动，要求参考软件实现并让子代理审查核心代码；同时明确日常 IDE 为 **VS Code**。已按阅读顺序核对文档/实际源码，保留现有 `.vscode/launch.json`，未切换 IDE。修改前快照为 `artifacts/drag-review-before/`。

标题栏根因：`ExtendsContentIntoTitleBar` 只调整内容/标题布局，自定义 Border 覆盖了框架默认的 TitleBar 区域，却没有声明 Avalonia 12 非客户区角色。已在 `TitleBarDragRegion` 设置 `WindowDecorationProperties.ElementRole="TitleBar"`，纯图文内容设置 `IsHitTestVisible=false`，保留库的系统按钮。复核了 Watt 固定提交的 TitleBar、FluentAvalonia 3.1 的窗口模板/Windows 适配，以及 [Avalonia 12 窗口说明](https://docs.avaloniaui.net/docs/app-development/window-management)；没有照搬旧 Avalonia 11 的标题栏属性，也没有叠加自写 Win32 拖动逻辑。**故障来自用户实际反馈，修复后的拖动/双击/缩放尚未实机验证。**

三个子代理分别审查插件加载与生命周期、存储与恢复、UI 导航。修复了确认的问题，并对退出/路径及最终导航代码做二次只读复核：

- 同一 profile 有无末尾分隔符必须产生同一 Windows 恢复事件，复用实例锁的 `AppPaths.Absolute` 规则。
- 目录包含判断不再给卷根重复追加分隔符；拒绝卷根 DataRoot，避免错误创建业务目录或绕过插件/数据互含限制。
- 私有托管/本机依赖与入口 DLL 共享版本包路径校验；拒绝解析出包外路径及包内任何父级链接。没有把加载上下文当作安全沙箱。
- 显式退出先阻止新保存和界面操作，已有宿主保存与插件停止共用外层 10 秒等待，之后释放锁和关闭；超时记日志。
- 每个插件独立取消、共享幂等清理任务；按初始化完成 → Stop → Background/取消回调完成 → Dispose 的顺序清理。取消回调异常不跳过其他实例，超时不并发释放仍在运行的对象；晚于退出快照完成构造/初始化的实例仍纳入清理且不注册 UI。内部统一等待 9 秒，超时继续观察迟到结束。
- PageHost 用同步 presenter 回报成功/取消/异常；成功后才提交标题/选择并清理旧页，取消保留旧页，失败换掉内部状态可能受损的 Frame 后显示错误页。拒绝已有父级的候选页面，阻止导航重入和退出后工厂调用；保留 FAFrame 过渡。FANavigationView 自行选中的项目也恢复到实际已提交的选择。

新增 7 项路径回归、5 项生命周期/设置/标题栏检查和 5 项导航检查（均为源码用例）。生命周期夹具仍单独构建成真实插件 DLL，只依赖 Contracts/UI，通过每例隔离插件数据目录中的标记控制延迟，失败后也释放等待条件。**没有执行这些检查，不把已编译写成已通过测试。**

| 检查层级 | 本轮事实 |
| --- | --- |
| 静态检查 | 三份专项审查及二次复核已完成；依赖方向、文档链接与源码差异另记录于 `artifacts/evidence/drag-review-static.txt`；不代表运行正确性 |
| 构建与发布 | 核心检查工程、完整解决方案隔离 Debug 构建、Release 发布、发布后独立插件和集成工程编译均成功；各 build 汇总 0 警告/0 错误；发布包含正常还原 |
| 文件集成准备 | 新副本 `artifacts/drag-review/host` 已复制独立示例插件；构建/复制插件前后的 234 个原有宿主文件 SHA256 全部一致，没有重编宿主 |
| 自动化/集成/Headless | 本轮未执行；新增回归只编译。旧 33/21 项结果不覆盖本轮修复 |
| 真实 Windows UI | 本轮未启动或操作，由用户手测；标题栏、页面动画/取消、保存后立即退出、托盘及等价路径恢复均未验收 |

实际命令（仅构建/发布，未运行应用或检查程序）：

```powershell
dotnet build tests/TractorTool.Checks/TractorTool.Checks.csproj -c Debug -m:1 -nr:false --no-restore -p:IntermediateOutputPath=obj/DragReview/ -p:OutputPath=bin/DragReview/
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false --no-restore -p:IntermediateOutputPath=obj/DragReview/ -p:OutputPath=bin/DragReview/
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/drag-review/host
dotnet build tests/IndependentPlugin/IndependentPlugin.csproj -c Release -m:1 -nr:false --no-restore -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/drag-review/host -o artifacts/drag-review/plugin
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false --no-restore -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/drag-review/host -o artifacts/drag-review/checks
```

日志分别为 `artifacts/evidence/drag-review-core-build.txt`、`drag-review-solution-build.txt`、`drag-review-publish.txt`、`drag-review-plugin-build.txt`、`drag-review-integration-build.txt`。文件核对仍用 `Get-FileHash -Algorithm SHA256`，JSON 清单和 `hash-result.txt` 位于 `artifacts/drag-review/`。没有执行 Verify-M1.ps1，因为用户已经接手测试。默认 Debug 的旧文件占用未重新验证，也没有结束任何用户进程。

边界：进程内插件同步卡死构造/注册、任意延迟绘制/异步事件异常仍可能影响整个进程；有界退出不能保证不合作插件或持续阻塞磁盘一定完成。M1 继续待验收，未进入 M2。

</details>

<a id="evidence-version"></a>

<details>
<summary>E06 · 统一产品版本和应用图标（2026-10-03）</summary>

### E06 · 统一产品版本和应用图标（2026-10-03）

用户指出版本显示写死，并要求扳手/瑞士军刀主题图标。已确认关于页的 `0.1.0` 是重复字面量，且之前全局固定 AssemblyVersion 使宿主文件版本也停留在 `1.0.0.0`。

- 根 `Directory.Build.props` 的 `Version` 继续作为唯一默认产品版本；当前仍为 `0.1.0`，没有无依据升版。关于页通过 `SettingsViewModel.ApplicationVersion` 读取宿主 `AppInfo.Version`，启动日志复用同一值。宿主程序集/文件版本交给 SDK 派生；公开 SDK 库的程序集身份和 API/UI 兼容版本保持不变。
- `Publish-Host.ps1` 新增可选 `-Version` 参数，直接传递 MSBuild Version；普通发布仍使用根配置，界面无需同步改字面量。Win32 manifest 的固定激活标识增加说明，不与产品版本混淆。
- 使用内置 imagegen 生成透明的红色折叠工具柄、银色扳手和短螺丝刀图标。母版及提示词位于 `Artwork/`，窗口/关于页/EXE/托盘全部继续引用同一套导出资源。更新 Generate-AppIcon.ps1，导出 256 PNG 和含 8 个尺寸的 ICO；没有增加运行时图像依赖。
- 修改前曾备份到 `artifacts/version-icon-before/`，该目录随后随 artifacts 被外部清理；开发 IDE 和现有 `.vscode` 配置未改。

验证事实：隔离编译 `-p:Version=0.1.0-version-check` 成功，0 警告/0 错误；读取产物元数据得到产品版本 `0.1.0-version-check`、文件版本 `0.1.0.0`，Contracts/UI 的 AssemblyVersion 仍为 `1.0.0.0`。随后使用默认版本正常发布，新 EXE 和 DLL 的产品版本均为 `0.1.0`、文件版本均为 `0.1.0.0`。独立插件和集成检查工程也重新编译成功，均 0 警告/0 错误，复制插件后原有 234 个宿主文件哈希未变。

已查看导出的 PNG，核对透明通道及 ICO 的 16/20/24/32/48/64/128/256 尺寸目录。文档链接、版本字面量和脚本语法做静态检查。**没有运行应用、自动化/集成/Headless 测试或真实 Windows UI；以上不是关于页实际显示、托盘可辨识度和 Windows 图标缓存刷新的运行证据。**

首批构建/发布成功后，发现 artifacts 已在外部被清理，因此重新生成下列最终副本及日志。一次文件复制核对因源目录消失而未完成，其空清单输出已明确标记 INVALID，不作为证据；最终完整清单记录 234/0。

实际执行命令（最终重建副本；Generate-AppIcon 在清理前已成功导出至源码 Assets）：

```powershell
./scripts/Generate-AppIcon.ps1
dotnet build TractorTool/TractorTool.csproj -c Release -m:1 -nr:false --no-restore -p:Version=0.1.0-version-check -p:IntermediateOutputPath=obj/VersionProbe/ -p:OutputPath=bin/VersionProbe/
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/version-icon-final/host
dotnet build tests/IndependentPlugin/IndependentPlugin.csproj -c Release -m:1 -nr:false --no-restore -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/version-icon-final/host -o artifacts/version-icon-final/plugin
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false --no-restore -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/version-icon-final/host -o artifacts/version-icon-final/checks
```

证据：`artifacts/evidence/version-icon-override-build.txt`、`version-icon-override-metadata.json`、`version-icon-publish.txt`、`version-icon-published-metadata.json`、`version-icon-plugin-build.txt`、`version-icon-integration-build.txt`、`version-icon-assets.txt`、`version-icon-static.txt`，以及 `artifacts/version-icon-final/host-hashes-*.json` / `hash-result.txt`。该次隔离副本为 `artifacts/version-icon-final/host`，包含上一轮拖动/导航/退出修复和示例插件，尚未启动。

</details>

<a id="evidence-binding"></a>

<details>
<summary>E07 · 调试绑定错误与 VS Code 预览复核（2026-10-03）</summary>

### E07 · 调试绑定错误与 VS Code 预览复核（2026-10-03）

用户截图暴露了此前静态审查的遗漏，不能把已有编译/Review 记录当作运行绑定无误的证明。

- `PluginsView` 的 `!!OperationStatus` 把字符串交给布尔转换，空字符串会产生截图中的运行绑定错误。同页 `!!DirectoryError` 存在同类问题。改为 VM 的 `HasOperationStatus` / `HasDirectoryError` 布尔属性，使用 `IsNullOrWhiteSpace`，并在对应源字段上添加 `NotifyPropertyChangedFor`，保证状态更新后可见性跟随变化。
- 复用 UI 子代理只读审查全部 5 个宿主 AXAML、ViewModel、导航模板、共享设置行/图标/主题，核对 DataContext、命令来源、通知和控件属性。剩余 `!` 绑定均用于 bool；没有发现新的确定 P1/P2 绑定问题。另修正 `caption` 只匹配 `TextBlock` 的样式遗漏，使用 `:is(TextBlock).caption` 覆盖可选择的插件路径文字；类型关系依据 [Avalonia 12.1.2 源码](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/SelectableTextBlock.cs)。此审查范围不代表全仓库没有缺陷。
- `ApplicationVersion` 当前在 SettingsViewModel 中确实存在，MainViewModel 也设置了正确的页面 DataContext。此次重建前，默认 Debug DLL 就已有该公共属性，不能诊断为当前源码或磁盘 DLL 缺少属性。读取 VS Code Avalonia 扩展 12.4.3 的既有日志，确认 18:03:40 预览仍报告缺失，扩展也记录了构建期间 `SIGSTOP` / `SIGCONT` 不支持。预览进程使用旧程序集/旧元数据是当前推断，尚未确认其内部状态或唯一根因。
- 已实际更新 VS Code 默认 `bin/Debug/net10.0` / `obj/Debug/net10.0` 产物；只读 PE 元数据确认 `ApplicationVersion`、`HasOperationStatus`、`HasDirectoryError` 在这两个宿主 DLL 中都有公共 getter。没有删除 obj/bin、结束用户进程、修改 VS Code 配置或升级依赖。

实际构建命令（第一条在 caption 修正前，第二条在全部源码修正后成功）：

```powershell
dotnet build TractorTool/TractorTool.csproj -c Debug -m:1 -nr:false --no-restore -p:IntermediateOutputPath=obj/BindingFix/ -p:OutputPath=bin/BindingFix/
dotnet build TractorTool/TractorTool.csproj -c Debug -m:1 -nr:false --no-restore
```

| 检查类型 | 本次结果 |
| --- | --- |
| 静态审查 | 上述源码/通知/样式复核完成；宿主及共享 AXAML 无 `!!` 绑定；默认 bin/obj 的 6 条属性核对全部存在 |
| 构建 | 两条命令均 0 警告、0 错误；本次默认 Debug 未复现早前的文件占用 |
| 自动化 / 集成 / Headless 测试 | 未执行，继续遵守用户自行测试的决定 |
| 真实 Windows UI / VS Code 预览刷新 | 未操作、未验证；不能宣称绑定日志已在真实运行中清零或预览已恢复 |

证据位于 `artifacts/evidence/binding-fix-build.txt`、`binding-fix-default-debug-build.txt`、`binding-fix-metadata.json`、`binding-fix-static.txt`、`binding-fix-previewer-log.txt`。元数据通过 PowerShell 的 `PEReader` / `MetadataReader` 读取，没有执行应用程序集。预览日志是用户既有会话的只读摘录，不是本轮运行测试。

本次未重新发布；**`version-icon-final` 是[E06](#evidence-version)的旧发布副本，不含此次绑定和 caption 修正**。最新修正位于源码及默认 Debug 输出。用户可先在预览错误页点击 **Restart Previewer** 使其重新加载已构建的程序集；以后改动 C# 属性需先 **Rebuild Project**。若预览仍保持旧状态，可执行 `Developer: Reload Window`。以上操作尚未代用户执行；恢复后再确认关于页版本、插件页空状态及打开目录失败提示。M1 仍待运行验收，不进入 M2。

</details>

<a id="evidence-home"></a>

<details>
<summary>E08 · 欢迎主页与已安装插件卡片（2026-10-03）</summary>

### E08 · 欢迎主页与已安装插件卡片（2026-10-03）

用户要求参考 Watt 的主页显示欢迎内容与已安装插件。核对固定提交的 HomePage.axaml / HomePageViewModel.cs，参考文本存放在 `artifacts/reference-icons/`；采用其卡片聚合与导航思路，没有复制参考源码或素材。修改前快照为 `artifacts/home-before-20261003-184852/`。开发 IDE 仍为 VS Code，依赖版本未变。

- 新增 HomeView/VM，启动默认进入主页，左侧第一项为主页，底部保留日志/插件/设置。欢迎区使用已有应用图标，提供插件管理和外观设置入口。
- 安装卡片复用 PluginsViewModel 的真实记录及导航命令，显示名称、版本、说明和状态；可打开的记录进入首个普通页面，其余进入插件页。按可用宽度换行，沿用统一主题与 Frame 动效，不提前创建插件页面。主页不受管理页搜索影响，启动加载完成也不强制改回主页。
- 加载中、未安装、目录不可用明确区分。单色 Home 图标追加至现有枚举末尾，未改变原值、注册签名或 API/UI 兼容版本。彩色导航素材和图片协议仍待实现，本次未将该项标为完成。
- 集成工程新增 4 项主页检查，覆盖默认加载态、真实插件卡片及搜索隔离、空安装、目录受阻；已有独立页面、注册、故障与导航取消检查同步更新，覆盖卡片复用导航、失败入口回退和启动期间保留已选页面。**仅编译这些用例，未执行。**

| 检查层级 | 本次结果 |
| --- | --- |
| 静态 | 核对新 AXAML 的类型/布尔可见性、数据来源、命令与选择同步、旧图标枚举顺序、文档链接；未发现新的确定问题，不能证明实际布局无误 |
| 构建 | 默认 Debug 构建通过，0 警告/0 错误；独立插件与集成工程均 0 警告/0 错误 |
| 发布与文件 | win-x64 自包含发布成功；发布后单独构建并复制示例插件，234 个原有宿主文件 SHA256 未变 |
| 自动化 / 集成 / Headless | 未执行；遵守用户自行测试的决定 |
| 真实 Windows UI / VS Code 预览 | 未启动或操作；欢迎布局、卡片换行/键盘/长名称/主题、点击与导航选择、返回主页均待用户验收 |

首次受限 Debug 构建在 AvaloniaStatsTask 写入 `LocalAppData/AvaloniaUI/BuildServices/buildtasks.log` 时权限失败；允许必要的构建日志写入后，同一构建命令成功。没有关闭构建任务、改动依赖或结束用户进程。失败与成功日志分别为 `artifacts/evidence/home-debug-build.txt`、`home-debug-build-approved.txt`。

实际命令（前三类只编译/发布，没有运行应用或检查程序）：

```powershell
dotnet build TractorTool/TractorTool.csproj -c Debug -m:1 -nr:false --no-restore
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/home-ready/host
dotnet build tests/IndependentPlugin/IndependentPlugin.csproj -c Release -m:1 -nr:false --no-restore -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/home-ready/host -o artifacts/home-ready/plugin
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false --no-restore -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/home-ready/host -o artifacts/home-ready/checks
```

发布、插件及集成编译日志为 `artifacts/evidence/home-publish.txt`、`home-plugin-build.txt`、`home-integration-build.txt`。`artifacts/home-ready/host` 已放入 `Plugins/Test.Independent/1.0.0`；文件清单和 `hash-result.json` 位于 `artifacts/home-ready/`，使用 Get-FileHash 对发布前后相同的原有宿主路径计算。源码/文档静态核对记录为 `artifacts/evidence/home-static.json`。

用户可从仓库根目录执行下面的隔离启动命令；**此命令仅供用户验收，本轮未执行**。该副本同时含 E07 的绑定修正：

```powershell
& .\artifacts\home-ready\host\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\home-manual-profile
```

默认 VS Code 调试仍使用其原有启动配置。不要用缺少本次主页及绑定修正的旧发布副本判断结果。M1 保持待验收，未进入 M2。

</details>

<a id="evidence-icons-review"></a>

<details>
<summary>E09 · Review 修复与彩色图标接入（2026-10-03）</summary>

### E09 · Review 修复与彩色图标接入（2026-10-03）

按用户要求继续 Review 并补齐 M1 图标。复用 review_ui 子代理进行两轮只读审查：首轮确认问题，修正后复核初始化、状态通知、绑定、图片所有权和公开 SDK；复审未发现额外确定缺陷，不代表证明没有其他缺陷。审查没有运行程序。修改前主要文件快照在 `artifacts/icons-review-before-20261003-190421/`。

| 确认的问题 | 修正 |
| --- | --- |
| MainViewModel 再次初始化会重复追加导航和设置入口，虽然底层插件只加载一次 | 缓存整次初始化 Task，在 UI 线程共用首次结果；重复及并发调用都不重复构建入口 |
| 插件页加载时显示“无匹配”，目录不可访问时又同时显示“未安装” | 引入加载状态；空态、搜索无结果排除加载和目录错误，补齐派生属性通知与 AXAML 条件 |

继续补完的图标能力：

- 固定来源的 Fluent Emoji 3D PNG 用于主页/日志/插件/设置导航，示例插件使用随包拼图图片；设置操作继续使用现有单色符号。没有升级依赖或增加图标运行库。来源提交、5 张原文件 SHA256 和 MIT 许可见 `artifacts/reference-emoji/`、[Artwork](../Artwork/README.md)、[第三方声明](../THIRD-PARTY-NOTICES.md)。素材原图已查看，不等同于实际 UI 显示验收。
- UI API 1.2 新增可选 IconPath，保留原构造函数、注册签名、枚举旧值和共享程序集身份；源代码兼容规则接受 1.0/1.1/1.2。还只读核对了旧 home-ready SDK 的 SettingRow.IconSource 元数据，原类型已是 FAIconSource，没有更改该返回类型。旧插件二进制在新宿主的实际加载没有执行。
- 宿主校验 PNG 包内路径、重解析点边界、2 MiB 长度及最多 1024×1024 尺寸后解码；源文件随即关闭，图片/失败结果缓存。控件只呈现 IImage，缺图/损坏回退符号并记录日志。Detach 和托盘隐藏不释放缓存，窗口 Closed 后才释放，避免退出等待期间绘制已释放图片。
- 核心和集成检查新增/更新兼容矩阵、重复与并发初始化、状态互斥、缓存/文件释放、控件切换、坏图/越界/尺寸限制、真实插件缺图仍可导航，以及退出时图片寿命。图像集成检查配置 Headless + Skia 解码；**只编译，全部未执行**。

| 检查层级 | 本次结果 |
| --- | --- |
| 静态 | 两轮源码审查完成；文档本地链接、AXAML/工程 XML、素材/许可随包及宿主文件哈希核对通过；范围见 icons-review-static.json |
| 构建 | 最终默认 Debug 解决方案、独立插件、集成检查工程均 0 警告/0 错误；win-x64 自包含发布成功 |
| 发布与文件 | 发布后再引用 SDK 独立编译插件；完整复制插件资源及许可到临时 Plugins，234 个原有宿主文件 SHA256 未变 |
| 自动化 / 集成 / Headless | 未执行；未运行 Verify-M1.ps1，不把编译、拷贝和哈希等同于插件加载成功 |
| 真实 Windows UI / VS Code 预览 | 未启动或操作；彩色图片、主题/高 DPI、缺图回退、窗口拖动、动画及退出仍待用户验收 |

环境为 Windows、.NET SDK 10.0.401、PowerShell 7.6.5；沿用 VS Code，未改编辑器配置。最初 `dotnet build TractorTool.slnx -c Debug -m:1 -nr:false --no-restore` 因前次 Release 还原留下的条件依赖资产导致 WithDeveloperTools 无法解析；正常还原构建后成功，没有更改依赖。集成工程首编译出现两处 Bitmap.Save 的弃用警告，改用 PngBitmapEncoderOptions 重编后 0 警告。

实际成功执行的构建/发布命令：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
./scripts/Publish-Host.ps1 -Output H:\ProjH\TractorTool\artifacts\icons-ready\host
dotnet build tests/IndependentPlugin/IndependentPlugin.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:\ProjH\TractorTool\artifacts\icons-ready\host -o H:\ProjH\TractorTool\artifacts\icons-ready\plugin
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:\ProjH\TractorTool\artifacts\icons-ready\host -o H:\ProjH\TractorTool\artifacts\icons-ready\checks
```

发布前后哈希使用 Get-ChildItem / Get-FileHash 记录相同的原有宿主路径；独立编译后用以下命令复制到新创建的测试插件目录（宿主工程没有具体插件引用）：

```powershell
$pluginTarget = 'H:\ProjH\TractorTool\artifacts\icons-ready\host\Plugins\Test.Independent\1.0.0'
New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
Get-ChildItem -LiteralPath artifacts/icons-ready/plugin | Copy-Item -Destination $pluginTarget -Recurse
```

构建日志位于 `artifacts/evidence/icons-review-debug-final.txt`、`icons-review-publish.txt`、`icons-review-plugin-build.txt`、`icons-review-integration-build-final.txt`。早期失败/警告日志保留为 `icons-review-debug-build.txt`、`icons-review-integration-build.txt`。宿主逐文件证据为 `icons-review-host-before.json` / `icons-review-host-after.json`，静态核对命令与结果为 `Inspect-IconsReview.ps1` / `icons-review-static.json`。发布后再次正常构建 Debug，恢复 VS Code 日常配置对应的依赖资产，见 `icons-review-debug-workspace.txt`。

最新隔离副本包含本次修正和独立示例插件。下面只供用户手动验收，**未执行**：

```powershell
& .\artifacts\icons-ready\host\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\icons-manual-profile
```

本轮没有进入 M2；先完成第 2 节的实际 UI 与插件运行验收，再决定后续阶段。

</details>

<a id="evidence-nav-density"></a>

<details>
<summary>E10 · 收紧导航按钮比例（2026-10-04）</summary>

用户对比截图指出侧栏图标按钮区域偏大。核对本地保存的 Watt 固定提交 MainView.axaml（侧栏 72、图标容器 28）及本项目模板，确认本项目侧栏 88、背景最小高度 76，并叠加图文间距和上下留白。参考的是比例，没有复制控件模板。

只调整 `TractorTool/Styles/ShellStyles.axaml` 和 `TractorTool/Views/MainWindow.axaml`：侧栏 88→72，背景最小高度 76→64、四周间距 4、圆角 8→6；呈现器最小高度 72，保留背景以外的留白；图标 32→28、图文间距 9→6、上下留白 10→8。标签字号 11→10、固定行高 14，单行省略，保留完整 Tooltip 与无障碍名称。主页与插件内容区的卡片、设置行和业务逻辑未改。

- 静态：AXAML、模板尺寸和本地文档链接核对通过；修改前快照为 `artifacts/nav-density-before-20261004-fda6cf/`，结果在 `artifacts/evidence/nav-density-static.json`。
- 构建：`dotnet build TractorTool/TractorTool.csproj -c Debug -m:1 -nr:false` 成功，0 警告/0 错误；日志为 `artifacts/evidence/nav-density-debug-build.txt`，默认 VS Code Debug 产物已更新。
- 自动化、集成、真实 Windows UI：均未执行；本次为可逆样式调整，没有新增镜像尺寸的单元测试，也未启动应用。截图仅说明用户看到旧比例偏大，不能作为新样式验收结果。
- 发布：本次未重新发布；E09 的 icons-ready 仍是旧尺寸。M1 继续待验收。

用户可重新启动 VS Code 调试，或用下列已构建程序隔离检查；此启动命令未代执行：

```powershell
& .\TractorTool\bin\Debug\net10.0\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\nav-manual-profile
```

</details>

<a id="evidence-page-layout"></a>

<details>
<summary>E11 · 窗口大小变化时的正文对齐（2026-10-04）</summary>

用户四张截图显示：页头固定左对齐，但主页/设置正文在宽窗口中移到中央，缩窄后又靠近左侧。源码原因是主页和插件页 MaxWidth=1000、设置页 MaxWidth=960、错误页 MaxWidth=800 与 Stretch 组合，而页头使用全宽容器。

移除上述整页宽度上限，四页复用 SharedStyles 中的 `pageScroll` 和 `pageBody`：内容横向 Stretch、禁止水平滚动、竖向滚动 Auto、统一右侧滚动条留白及底部间距。局部空状态文字仍限宽，主页插件卡片仍由 WrapPanel 换行。页头、状态栏、日志自身滚动和插件页面生命周期不变；没有修改 C#、插件协议、字体缩放或依赖。

核对了锁定 FluentAvalonia 提交的 [Frame 模板](https://github.com/amwx/FluentAvalonia/blob/215ee0481e8b0e6d396cbe2a0f33dc791c5646ab/src/FluentAvalonia/Styling/ControlThemes/FAControls/FrameStyles.axaml) 和 [设置行实现](https://github.com/amwx/FluentAvalonia/blob/215ee0481e8b0e6d396cbe2a0f33dc791c5646ab/src/FluentAvalonia/UI/Controls/SettingsExpander/FASettingsExpanderItem.cs)：Frame 已按父视口拉伸，设置行保留描述换行和小于 460 时操作区下移的库内行为，没有重写模板或新增窗口尺寸监听器。参考文本存于 `artifacts/reference-resize/`。

| 层级 | 本次结果 |
| --- | --- |
| 静态 | 核对四页布局、共享样式、原绑定保留和文档本地链接；证据 `artifacts/evidence/page-layout-static.json` |
| 构建 | `dotnet build TractorTool/TractorTool.csproj -c Debug -m:1 -nr:false` 通过，0 警告/0 错误；日志 `artifacts/evidence/page-layout-debug-build.txt` |
| 自动化 / 集成 / Windows UI | 均未执行，遵守用户自行测试的决定；没有为纯样式修正新增镜像参数的单元测试 |
| 发布 | 未重新发布；默认 Debug 已更新，icons-ready 仍是 E09 旧布局 |

修改前快照在 `artifacts/page-layout-before-20261004-d64602/`。以下仅供用户隔离验收，未执行：

```powershell
& .\TractorTool\bin\Debug\net10.0\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\layout-manual-profile
```

待验：720×540 最小窗口、普通宽度与宽窗口的标题/正文对齐，短窗口滚动到设置底部，长路径/错误文字换行，已安装卡片换行，以及系统缩放下的实际视觉表现。M1 仍未验收完成。

</details>

<a id="evidence-appearance-review"></a>

<details>
<summary>E12 · GPT-6.1-Sol Review 与外观即时生效（2026-10-04）</summary>

用户要求使用 GPT-6.1-Sol、相同思考深度审查后继续。三个子代理显式选用该模型，但当时未传 reasoning_effort；原记录将此直接表述为继承主代理深度并不可靠。用户后续截图确认 UI 子代理实际显示“轻度”，不能把本节记录当作高深度 Review 证据。全部为只读审查，没有运行程序。修改前快照：`artifacts/review-appearance-before-20261004-d6f901/`。

| 审查范围 | 结果与处理 |
| --- | --- |
| Storage/Logging、启动、App 退出、Windows/托盘 | 未发现确定阻断缺陷；数据迁移、磁盘日志保留等仍属于后续阶段 |
| 清单、兼容、独立装载、Contracts/UI 注册、插件生命周期 | 发现 Discover 重试未清除旧 DirectoryError；本次在每次扫描开始重置，扩展隔离文件阻塞用例覆盖修复后重扫 |
| 宿主 ViewModels/Views/Services 与 UI 控件 | 旧实现未发现新的确定缺陷；对新外观流程再做两轮复审，最终未发现新的确定问题，但不代表全仓库无缺陷 |

本次外观修改：主题、主题色、动画立即应用，移除“保存外观”和保存期间禁用选项；单个后台保存任务串行写入快照，期间只保留最后选择，旧写盘完成不重新应用旧主题。失败显示“已生效，但未保存”和重试；应用异常单独提示。瞬时空选择不产生配置，相同已应用值不重复写入，同步主题通知重入时应用最后有效选择。退出阻止新变更，等待整个队列；此前最后保存失败时最多再试一次，仍受 App 原有 10 秒退出期限约束。没有升级依赖或改动 SDK。

参考机制在上一轮已核对固定 Watt 提交：外观控件直接绑定设置，[设置属性](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client/Settings/Infrastructure/SettingsProperty.cs#L98-L122) 触发变更通知和自动保存，[应用](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/App.axaml.cs#L197-L204) 订阅主题色；本次使用现有 MVVM/ThemeService/JsonFileStore 实现，没有复制其设置框架。

| 验证层级 | 实际结果 |
| --- | --- |
| 静态 | 三组审查、外观复审及 XML/文档链接核对完成；新检查覆盖快速切换/旧写失败继续/重试/退出等待/临时空选择/同步重入/应用失败 |
| 构建 | 最终默认 Debug 解决方案、独立插件和集成检查均 0 警告/0 错误；win-x64 自包含发布成功 |
| 发布准备 | 宿主先发布，然后引用该 SDK 独立构建插件；复制到临时 Plugins 后，234 个原有宿主文件 SHA256 不变 |
| 自动化 / 集成 / Headless | 全部未执行；新增源码用例和编译不视为运行通过，没有运行 Verify-M1.ps1 |
| 真实 Windows UI / VS Code 预览 | 未启动或操作；主题即时效果、控件实际绑定、系统主题事件、动画和托盘由用户验收 |

失败注入用例在 JsonFileStore 验证回调中抛出 IOException，验证的是保存队列、状态和旧文件保留；不能代表已验证磁盘满、Flush 或 File.Replace 失败。同步主题重入通过 Application.PropertyChanged 注入，不代表已验证实际 ComboBox 模板重建。运行结果仍待补齐。

实际命令（仅编译/发布，未执行程序）：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/appearance-ready/host
dotnet build tests/IndependentPlugin/IndependentPlugin.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/appearance-ready/host -o H:/ProjH/TractorTool/artifacts/appearance-ready/plugin
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/appearance-ready/host -o H:/ProjH/TractorTool/artifacts/appearance-ready/checks
# 发布后再次正常 Debug 构建，恢复 VS Code 日常依赖资产
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
```

首次受限构建因 Avalonia BuildServices 无权写本机日志失败，并出现 NU1900 查询警告；正常权限重试构建成功但暂时保留还原缓存警告。随后发布与最终 Debug 还原构建正常，未关闭漏洞检查。集成工程初编译一处可空警告已用明确的页面非空检查修正。日志：`artifacts/evidence/appearance-debug-build.txt`（初始失败）、`appearance-debug-build-full.txt`（重试含警告）、`appearance-debug-final.txt`、`appearance-publish.txt`、`appearance-plugin-build.txt`、`appearance-integration-build.txt`、`appearance-integration-build-final.txt`。环境：.NET SDK 10.0.401，Windows，PowerShell，IDE 保持 VS Code。

独立插件复制到新目录的命令：

```powershell
$pluginTarget = 'H:\ProjH\TractorTool\artifacts\appearance-ready\host\Plugins\Test.Independent\1.0.0'
New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
Get-ChildItem -LiteralPath artifacts/appearance-ready/plugin | Copy-Item -Destination $pluginTarget -Recurse
```

逐文件哈希在 `artifacts/evidence/appearance-host-before.json` / `appearance-host-after.json`，结果 `appearance-host-hashes.json`；源码变更范围及文档核对 `appearance-static.json`。它们只证明文件和编译准备，没有声称新插件真实加载成功。

以下启动命令仅供用户隔离验收，本轮未执行：

```powershell
& .\artifacts\appearance-ready\host\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\appearance-manual-profile
```

下一步：用户检查实际主题/主题色/动画即时生效、快速更改后立即退出并重启保留、隔离写入失败提示及重试，再完成前述布局/图标/插件/托盘运行验收。M1 尚未完成，未进入 M2。

</details>

<a id="evidence-theme-transition"></a>

<details>
<summary>E13 · 移除常驻说明与深浅色过渡（2026-10-04）</summary>

用户截图指出“外观更改即时生效并自动保存”说明多余，且深浅色切换突然闪白。正常初始化、写入中和成功后均不显示保存文案；错误区域仅保存失败时出现，应用失败提示及重试保留。窗口背景/前景、标题区、侧栏、内容区、公共设置行、卡片及辅助文字加入原生 BrushTransition，220 ms、CubicEaseInOut；关闭“界面动画”会撤销过渡样式并取消在途过渡。主页工具按钮仅修改 toolCard 的背景/边框，不全局替换第三方按钮反馈。无截图遮罩、额外动画框架或依赖升级。

参考核对：固定 Watt 提交的 [App.Theme.cs](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/App.Theme.cs) 直接设置主题；[MainView.axaml.cs](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/Views/Pages/MainView.axaml.cs) 使用页面过渡，已核对的 Border/Window/TextBlock 样式没有整窗主题渐变。不能据此认定用户截图对应的其他版本没有此效果。本次按 [Avalonia 控件过渡机制](https://docs.avaloniaui.net/docs/graphics-animation/control-transitions) 实现；GPT-6.1-Sol 子代理核对 12.1.2 官方 Animatable/Selector 源码，确认过渡状态按控件隔离，撤销样式释放在途实例，设置行需要显式 `/template/` 选择器。

| 验证层级 | 本轮实际结果 |
| --- | --- |
| 静态 | 主题样式、关闭动画、错误区域绑定经子代理只读复审，未发现确定缺陷；XML、文档链接、资源及变更范围见 `artifacts/evidence/theme-transition-static.json` |
| 构建 | 最终默认 Debug 解决方案、独立插件、发布 SDK 集成检查均 0 警告/0 错误；win-x64 自包含发布成功 |
| 发布准备 | 新副本 `artifacts/theme-transition-ready/host` 中加入发布后独立构建的插件；234 个原有宿主文件 SHA256 不变 |
| 自动化 / 集成 / Headless | 未执行；新增窗口/导航/模板表面的动画开关检查，扩展保存失败区域可见性检查，仅编译 |
| 真实 Windows UI / VS Code 预览 | 未执行，由用户验收；没有将源码或编译结果写成视觉通过 |

范围限制：第三方控件内部、导航选中背景、Windows 标题栏按钮等没有全面增加主题颜色过渡；不是整窗所有像素交叉淡化。跟随系统时依靠同一动态资源变化触发过渡，实际系统事件、快速反向切换、关闭开关和深浅色对比度尚未运行核验。

用户截图指出旧 UI Review 显示轻度后，新建 `review_theme_high`，显式传入 `model="gpt-6.1-sol"`、`reasoning_effort="high"`、`fork_turns="1"`，重新只读审查本节修改及必要上下文。独立复核保存提示/失败/重试/退出、窗口 class 与设置行模板选择器、Avalonia 12.1.2 在途过渡取消和反转取当前值，未发现确定的新功能缺陷。检查源码只覆盖过渡配置随开关存在/撤销，不证明颜色时间序列；第三方文字前景、中途对比度和系统主题事件仍待实际验收。本次只更新约定和记录，未更改应用源码、重新构建或运行程序；不能用这次 UI 范围的高深度复审替代全仓库 Review。

实际命令（仅构建/发布和文件准备）：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/theme-transition-ready/host
dotnet build tests/IndependentPlugin/IndependentPlugin.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/theme-transition-ready/host -o H:/ProjH/TractorTool/artifacts/theme-transition-ready/plugin
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/theme-transition-ready/host -o H:/ProjH/TractorTool/artifacts/theme-transition-ready/checks
$pluginTarget = 'H:\ProjH\TractorTool\artifacts\theme-transition-ready\host\Plugins\Test.Independent\1.0.0'
New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
Get-ChildItem -LiteralPath artifacts/theme-transition-ready/plugin | Copy-Item -Destination $pluginTarget -Recurse
# 最后恢复 VS Code 默认 Debug 资产
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
```

集成检查初编译新增断言有一处 CS8602，改为检查可空 Property 后重新编译为 0 警告；初始与最终日志均保留。构建使用正常权限允许 Avalonia 写 BuildServices 日志，没有运行应用。证据文件位于 `artifacts/evidence/theme-transition-*`（debug-build/debug-final、publish、plugin-build、checks-build/checks-final 的 txt，host-before/host-hashes 与 static 的 json）。修改前快照：`artifacts/theme-transition-before-20261004-cb31bc/`。环境仍为 Windows / .NET SDK 10.0.401 / VS Code，未改 IDE 配置。

以下仅供用户启动验收，本轮未执行：

```powershell
& .\artifacts\theme-transition-ready\host\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\theme-transition-manual-profile
```

先切到设置页，来回选择深浅色，检查大面积背景和设置行是否柔和变化；关闭“界面动画”确认直接切换，再开启。检查正常状态无保存说明，重启仍保留最后设置；写盘错误及重试只在隔离数据中验证。M1 保持未验收完成，未进入 M2。

</details>

<a id="evidence-settings-cleanup"></a>

<details>
<summary>E14 · 设置预览与确定死代码清理（2026-10-04）</summary>

用户截图是 VS Code 预览：SettingsView 无设计时 DataContext，可见性绑定没有失败/空值回退，存在错误区域按默认 IsVisible 显示的源码问题。截图的 AXAML 也早于 E13（未含父级可见性判断），不能据截图认定当前运行实例发生了保存失败。补充 FallbackValue/TargetNullValue=false，父面板统一控制错误提示与重试按钮；同页其他错误说明也默认隐藏。机制依据：[Avalonia 绑定回退说明](https://docs.avaloniaui.net/docs/data-binding/introduction-to-data-binding#fallbackvalue-and-targetnullvalue)。实际预览未由代理刷新，结果待用户确认。

重试命令确有用途：自动写盘失败后恢复最后已应用的配置，不能因普通保存按钮取消就删除恢复路径。本次将 Status/HasSaveError 两个可写状态合并为 SaveError 及派生 bool，移除成功时冗余清空和子按钮重复可见性绑定；串行队列、重试次数和退出等待语义保留。

GPT-6.1-Sol + high 子代理按 C#/XAML/源生成器/反射入口核对，确认并清理三组残留：NavigationItem.IsSelected 及只写不读的赋值循环；IsPluginsSelected/IsSettingsSelected 及无消费者的通知；未参与当前页面创建的 ViewLocator 与 App 注册。IsLogsSelected 仍供日志刷新使用。公开 Contracts/UI 与共享资源未按“宿主无引用”误删。高深度最终复审未发现确定退化，但并非全仓库无缺陷的证明。

| 层级 | 实际结果 |
| --- | --- |
| 静态 | 上述引用清理、错误状态及可见性复审完成；文档链接/XML/变更范围见 `artifacts/evidence/settings-cleanup-static.json` |
| 构建 | 首次 Debug 通过，0 警告/0 错误；win-x64 自包含发布成功；最新集成检查编译 0 警告/0 错误 |
| 默认输出刷新 | 发布后恢复 Debug 时 TractorTool PID 17892 锁定 apphost EXE，报 MSB3026/MSB3027/MSB3021，10 警告/2 错误；保留用户进程，不声称最后一次默认构建成功 |
| 自动化 / 集成 / 真实 UI | 均未执行；只扩展并编译无 DataContext、错误显示时清空/恢复上下文及实际重试按钮命令绑定的检查 |

真实命令如下；最后的默认 Debug 命令因上述占用失败：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/settings-cleanup-build/host
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/settings-cleanup-build/host -o H:/ProjH/TractorTool/artifacts/settings-cleanup-build/checks
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
```

复审后补强“错误正显示时移除 DataContext”用例，并以相同命令再次编译检查，0 警告/0 错误。日志：`artifacts/evidence/settings-cleanup-debug.txt`、`settings-cleanup-publish.txt`、`settings-cleanup-checks.txt`、`settings-cleanup-checks-final.txt`、`settings-cleanup-debug-final.txt`。修改前快照：`artifacts/settings-cleanup-before-20261004-013830/`。未修改依赖版本、VS Code 配置或真实用户数据；无新增运行结果。

先停止调试/从托盘退出后重新构建、Restart Previewer。以下仅供用户隔离启动（本轮未执行，此发布未装入示例插件）：

```powershell
& .\artifacts\settings-cleanup-build\host\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\settings-cleanup-manual-profile
```

</details>

<a id="evidence-scroll-edge"></a>

<details>
<summary>E15 · 页面滚动条靠右（2026-10-04）</summary>

对照用户本轮参考截图：滚动条靠内容区右边，卡片留白在容器内部。此前 PagePresenter 左右各 28 外边距使滚动条也缩进 28。本次只对主页/设置/插件/错误四个宿主整页滚动页面启用 edgeScroll，取消页面容器右边距，将该 28 加到原正文右边距 8 上；正文位置和宽度保持，滚动条可到内容区右边。MainViewModel.HasPageScroll 随 CurrentPage 通知，ShellStyles 集中适配；日志和独立插件页面使用原边距。没有改公共 SDK、第三方滚动条模板或页面生命周期。

GPT-6.1-Sol + high 子代理只读复审未发现确定退化：正文样式沿内容逻辑树可达，优先级没有本地 Margin 遮挡；成功切页/错误页/Detach 更新 CurrentPage，取消导航保留原状态。实际像素位置与不同滚动条显示态未运行核验。

静态依据为用户截图、实际 AXAML 布局与已有 Watt 源码缓存；没有将截图对应到未经确认的参考版本。构建命令 `dotnet build TractorTool.slnx -c Debug -m:1 -nr:false` 通过，0 警告/0 错误，日志 `artifacts/evidence/scroll-edge-debug-build.txt`；E14 后续曾阻塞的默认输出已在本次成功刷新。XML/链接及变更核对见 `artifacts/evidence/scroll-edge-static.json`。修改前快照 `artifacts/scroll-edge-before-20261004-014805/`。

自动化、集成、真实 UI 均未运行；低风险布局调整没有新增仅重复尺寸常量的测试，也没有重新发布。现有 artifacts 发布副本不含本次改动。以下仅供用户隔离验收，本轮未启动：

```powershell
& .\TractorTool\bin\Debug\net10.0\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\scroll-edge-manual-profile
```

待验：短窗口中的设置/主页/插件页滚动条位置、正文与标题对齐、快速切页、窄窗与高 DPI；切回日志及独立插件页时保持其原有页面边距。M1 仍未完成运行验收。

</details>

## M2：已完成的实施轮次（阶段仍待验收）

<a id="evidence-m2"></a>

<details>
<summary>E16 · 本地 Git、插件管理与剪贴板插件（2026-10-04）</summary>

用户在 E15 后反馈“目前测试一圈没发现啥问题，可以继续开发”，据此推进 M2；没有收到逐项测试记录，未将历史缺证条目批量标为通过。随后用户要求先初始化本地 Git。已建立 `main`、提交 M1 基线 `fc18c20`（95 个文件），未配置远程。当时已经开始的 PluginFiles 新文件和清单 ValidateVersion 提取没有混入基线：用修改前快照恢复这一个文件的暂存版本，工作区改动保留。后续 M2 改动单独形成本地提交，以 `git log` 为准。快照 `artifacts/m2-before-20261004/`、构建产物和测试数据由现有 `.gitignore` 排除。

暂存检查最初报告原始 Windows SDK RTF 许可自带的行尾空白；未修改发行方许可格式，使用仅针对该文件的 `.gitattributes` binary 属性按原字节保存，重新检查其余文本差异。没有更改用户全局换行设置。

本轮代码：

- 插件页从本地 ZIP 安装/更新；启停、卸载、回退、取消待处理请求及错误反馈，全部重启生效。入口使用原生文件选择与现有主题确认框，不增加在线市场。
- Core 校验 ZIP 边界/链接/重名/大小、清单兼容性、共享 DLL 与入口元数据，暂存并记录摘要。活动版本选择、安装目录锁、与 dataRoot 绑定的请求、防覆盖更新、卸载保留数据及启动失败恢复明确分离。
- 更新数据快照在旧进程退出后的新启动制作；回退保留新版数据副本。管理状态使用原子 JSON 保存。固定启动、安装和自定义数据根锁一直保留到进程终止，避免退出超时后迟到写入与新进程重叠。
- 正式剪贴板插件使用真实 Windows 历史 API，UI 线程进入/刷新，只展示文本（每项最多 20,000 字符），其他类型/失效项占位，区分系统关闭、拒绝、空和失败。离页取消与迟到结果保护，无历史库、监听和正文日志。详细平台/依赖/许可见 [插件文档](Plugins/ClipboardHistory.md)。

两名子代理均显式使用 GPT-6.1-Sol + high，分别审查管理事务和剪贴板。确认并修复：首次接管旧插件的 ID 大小写漏洞、取消安装未清理暂存包、失败更新缺少可操作的取消入口、缺失清单时管理卡片重复/丢失恢复操作、长状态文字不换行；退出后继续排队的保护和管理异常日志也已补齐。最终静态复核未发现新确定 P1/P2，未宣称覆盖一切故障。

| 层级 | 实际结果与限制 |
| --- | --- |
| 静态 | 文档链接/片段、项目与 AXAML XML、脚本语法、插件包内容/许可及 Git 差异检查；报告 `artifacts/evidence/m2-static.json` |
| 构建 | 最终默认 Debug 解决方案 0 警告/0 错误；win-x64 自包含宿主发布成功；独立剪贴板插件、剪贴板模型/现有集成工程均 0 警告/0 错误 |
| 自动化 | 新增 15 项管理检查和 8 项剪贴板模型检查，仅编译，未执行。管理用例中的 Loaded 是显式状态夹具，未冒充 DLL 加载；模型读取器不调用系统 API |
| 发布文件检查 | 发布宿主后才单独构建剪贴板，复制到实际发布副本 Plugins/TractorTool.ClipboardHistory/1.0.0；234 个原有宿主文件 SHA256 均不变，无宿主/Core 静态引用或共享 SDK DLL 副本 |
| 集成 / Windows UI | 均未执行；未启动程序、未读取真实剪贴板、未改变系统历史开关、未触碰真实用户数据。首次真实 WinRT 加载/权限/焦点、管理变更和恢复仍待验 |

实际环境为 Windows、.NET SDK 10.0.401，既有 Avalonia 12.1.2 / MVVM 8.4.2 / FluentAvaloniaUI 3.1.0 不变；剪贴板单独采用 net10.0-windows10.0.19041.0，固定 WindowsSdkPackageVersion 10.0.19041.57。编译组件需要访问本机日志目录，构建使用已说明范围的提权执行；没有运行应用。第一次剪贴板构建因 ListBox 不支持 HorizontalContentAlignment 报 AVLN2000，改为 ListBoxItem 样式后重新构建成功；失败日志保留在 `m2-clipboard-build.txt`。

最终实际命令（从仓库根执行，输出目录在当时为空）：

```powershell
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/m2-ready/host
./scripts/Build-ClipboardPlugin.ps1 -HostSdkDir H:/ProjH/TractorTool/artifacts/m2-ready/host -Output H:/ProjH/TractorTool/artifacts/m2-ready/clipboard
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/m2-ready/host -o artifacts/m2-ready/integration-checks
dotnet build tests/TractorTool.ClipboardChecks/TractorTool.ClipboardChecks.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/m2-ready/host -p:PluginBuildDir=H:/ProjH/TractorTool/artifacts/m2-ready/clipboard/files -o artifacts/m2-ready/clipboard-checks
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
```

通过 PowerShell `Get-FileHash` 在独立构建前记录宿主文件，并在复制插件后比较原相对路径；没有用启动程序代替文件校验。证据：`m2-host-before-plugin.json`、`m2-ready-host-hashes.json`。ZIP 的 SHA256 为 `40AC3588BD4F019C06A131F01C0AE8ADE3B527F936EF77FFF8590F7D423D6DE5`，随包 `.sha256`；插件产品版本 1.0.0 来自清单，SDK 添加的 `+fc18c20…` 为编译时 HEAD，产物含当时未提交工作区，不能据此声称二进制恰好等于 M1 基线。

最终日志位于 `artifacts/evidence/`：`m2-ready-publish.txt`、`m2-ready-plugin-package.txt`、`m2-ready-integration.txt`、`m2-ready-clipboard-checks.txt`、`m2-ready-debug.txt`。早期 m2-sdk/m2-package 为中间产物，以 m2-ready 为当前副本。

以下仅供用户运行，**本轮未执行**：

```powershell
& .\artifacts\m2-ready\host\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\m2-manual-profile
# 只验证模型/管理逻辑，不调用 Windows 历史 API：
dotnet .\tests\TractorTool.Checks\bin\Debug\net10.0\TractorTool.Checks.dll
dotnet .\artifacts\m2-ready\clipboard-checks\TractorTool.ClipboardChecks.dll
```

用户验收重点：

1. 从默认主页/左侧进入剪贴板工具，检查真实列表/刷新、长文本、系统关闭/拒绝/空状态、切页后重新进入、浅深主题。仅使用可供验收的系统内容，不需清空历史。
2. 插件页禁用后当前窗口保持原功能；**从设置或托盘显式退出**（关闭窗口只隐藏），重启后没有该工具入口；再启用/重启恢复。
3. 卸载/重启后代码移除、数据保留；选择 `artifacts/m2-ready/clipboard/TractorTool.ClipboardHistory-1.0.0.zip` 重新安装，重启后出现。当前预置同版本时直接再安装应被拒绝覆盖。
4. 更新/数据回退需要另一个更高版本的隔离插件包与临时数据。源码检查已覆盖摘要变动、同版本/ID 冲突、备份时机、数据保留及移动后重放；运行结果仍未取得。真实 ACL、文件占用、磁盘/断电窗口尚未实测，不能将上述用例编译等同于事务验收。

M2 仍待运行出口；M3 的包/进程服务、视频真实链路、数据目录迁移和 CI 未借本轮提前实现。

</details>

<a id="evidence-clipboard-ux"></a>

<details open>
<summary>E17 · ZIP 拖放、启停开关与剪贴板重做（2026-10-04）</summary>

用户截图显示旧版历史列表存在非文本占位、缺少再次复制、选择与文本选区混杂、滚动区域不合理。按最新要求增加拖入安装、开关启停，并以“找回 → 预览 → 再次复制”为剪贴板主流程。本轮仍是 M2，不进入媒体包/视频/迁移阶段。

已实现：

- 插件面板接受一个本地 ZIP，合法拖入时显示目标提示；目录、多文件、非 ZIP、非本地来源不作为安装请求。复用文件选择入口的确认、校验、串行排队和退出保护。
- 启停改为 ToggleSwitch。点击和拖动滑块通过 IsCheckedChanged 排队；显示待重启目标，另列实际加载状态。未执行的启停可反向取消，写入失败恢复原值。
- 剪贴板左右分栏、紧凑历史行及各自边缘滚动；文本选区只在右侧。文本/图片等预览界限、原项复制和资源控制归 [插件设计](Plugins/ClipboardHistory.md)。
- 页面监听系统历史/开关事件，在 UI 线程合并刷新，窗口激活补刷，保留选择和已有预览。复制使用系统原历史项，不复制摘要或截断预览，不建立历史库。
- 独立插件从 1.0.0 升至 **1.1.0**，满足不可覆盖同版本的更新规则；宿主/Contracts/UI 版本及依赖未改。

GPT-6.1-Sol + high 子代理只读复审实现和回归源码。修复：刷新不重试相同项的失败预览；读取异常丢失排队事件；双击列表空白误复制；拖滑块绕过 Click/Command 只改外观；迟到预览检查未等待 VM 处理完成的误报。最终复核未发现新的确定 P1/P2，不等于运行通过或全仓库无缺陷。开关核对 [Avalonia 12.1.2 源码](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/ToggleSwitch.cs)；拖放、Windows API 和 Apple 布局参考见 [架构](ARCHITECTURE.md#24-安装更新禁用和卸载) / 插件设计。

| 层级 | 本轮实际结果 |
| --- | --- |
| 静态 | 文档链接/片段、XML、脚本语法、ZIP/许可/共享 DLL 和哈希核对，见 `artifacts/evidence/clipboard-ux-static.json` |
| 构建 | win-x64 自包含最终宿主发布成功；独立 1.1.0 插件、模型与 Headless 检查工程均 0 警告/0 错误。最后默认 Debug EXE 被用户运行中的进程占用，见下文 |
| 自动化 | 剪贴板模型 19 项：系统/复制结果、事件合并、异常补刷、选择保留、原项 Id、迟到预览/离页和错误脱敏；**仅编译，未执行** |
| 集成 | 新增 7 项管理 UI 检查：确认/非法路径/并发退出、开关两个方向及写入失败恢复；**仅编译，未执行**。InstallArchiveAsync 用例证明的是共享 VM 流程，不证明原生 Drop 路由 |
| 发布文件 | 宿主发布后独立打包，234 个原有宿主文件 SHA256 不变；宿主未预装插件，供用户拖入验收；**未加载插件 DLL** |
| Windows UI/API | **未执行**；前台访问、图片解码、多格式粘贴、事件/焦点返回、原生拖放、开关拖动和分栏尺寸均待用户验收；没有读取/清空/写入真实剪贴板 |

失败记录：初次 AutomationProperties.Name 的占位符 StringFormat 触发 AVLN2000（类型 0 无法解析），改为直接名称绑定及工具提示后发布成功。一次 Debug AXAML 编译遇到 `obj/Debug/net10.0/TractorTool.dll` 被其他进程占用（AVLN9999），随后默认重建曾成功。最终补正“失败状态仍读取管理记录的启用设置”后，默认重建又被用户运行中的 TractorTool PID 49588 占用 EXE，报 MSB3026/3027/3021（10 警告/2 错误）；未停止用户进程，不能写为最后一次 Debug 成功。最终隔离 Release 发布及检查编译均成功。环境仍为 VS Code / Windows / .NET SDK 10.0.401。

实际最终命令（输出目录当时为空；仅编译/发布/静态核对）：

```powershell
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/m2-clipboard-final/host
./scripts/Build-ClipboardPlugin.ps1 -HostSdkDir H:/ProjH/TractorTool/artifacts/m2-clipboard-final/host -Output H:/ProjH/TractorTool/artifacts/m2-clipboard-final/clipboard
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/m2-clipboard-final/host -o artifacts/m2-clipboard-final/integration-checks
dotnet build tests/TractorTool.ClipboardChecks/TractorTool.ClipboardChecks.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/m2-clipboard-final/host -p:PluginBuildDir=H:/ProjH/TractorTool/artifacts/m2-clipboard-final/clipboard/files -o artifacts/m2-clipboard-final/clipboard-checks
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
./artifacts/evidence/Inspect-ClipboardUx.ps1
```

中间 `artifacts/m2-clipboard-ux` / `m2-clipboard-ready` 不作为交付。最终日志为 `artifacts/evidence/clipboard-ux-*.txt`：publish-final、plugin-package-final、integration-reviewed、model-reviewed、debug-reviewed（最后一次占用失败）；中间记录仍保留。构建前宿主清单 `clipboard-ux-host-before-plugin-final.json`，核对报告 `clipboard-ux-static.json`。ZIP SHA256：`E258399B5FC57977FE033EBDA8D2350476B26B37B664FDF093423454E01460C4`，另附 `.sha256`。编译时 HEAD 为 `8daab60`，产物包含本轮未提交源码；Git 元数据不单独代表二进制来源，按包哈希和本轮提交追溯。

以下供用户验收，**代理未执行**：

```powershell
& .\artifacts\m2-clipboard-final\host\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\clipboard-ux-manual-profile
# 可选模型检查，只用替身，不读取真实剪贴板
dotnet .\artifacts\m2-clipboard-final\clipboard-checks\TractorTool.ClipboardChecks.dll
```

1. 将 `artifacts/m2-clipboard-final/clipboard/TractorTool.ClipboardHistory-1.1.0.zip` 拖入插件页，确认后从托盘或设置**显式退出再启动**；关闭窗口只隐藏。现有调试安装也需更新 ZIP，仅重建宿主不会替换已安装的 1.0.0。
2. 以临时文字/截图验收左右分栏和预览；复制后粘贴到适合的应用核对完整内容。长文本复制应完整，列表空白/滚动条双击不改剪贴板。
3. 在其他应用复制临时内容后返回，确认刷新并保持原选择；快速选择图片/文字、手动刷新和切页，检查无迟到预览覆盖。
4. 点击/拖动启停开关，检查待重启提示、反向取消、重启后的启停；多文件/目录/非 ZIP 拖入拒绝，确认取消不安装。
5. 补充关闭/空/拒绝/项目失效、最窄窗口/高 DPI/深浅色记录。先处理 M2 反馈，不自动进入 M3。

</details>

<a id="evidence-ui-fixes"></a>

<details open>
<summary>E18 · 拖放边界、卡片布局与空选择绑定修复（2026-10-04）</summary>

用户截图指出：拖放遮罩偏离正文边缘且内容透底；剪贴板 `SelectedEntry.TypeName/Timestamp` 在空选择时输出绑定错误；插件卡片右侧纵列撑高整体，卸载孤立在左下角。三处均有源码对应，不将黄色模块加载信息误记为异常。

- `PluginsView` 的 DropOverlay 直接绑定命名正文 PageBody 的有效 Margin，遵循 shell 的右边距与页面底部留白；移除整体 Opacity，提示保持不透明。覆盖层不命中测试，父页仍接收拖放；缩放/滚动后的原生坐标待用户验证。
- 卡片图标与标题顶对齐，版本/当前状态同行，右侧保留启停开关，打开/卸载/回退/取消集中在可换行的操作行；无可用操作不保留空行。重启生效及待变更语义保持。
- 剪贴板 VM 提供非空 SelectedTypeName/SelectedTimestamp 及选择变化通知；避免在隐藏区求值空对象链。新增模型用例覆盖初始空、选择、历史清空、再次选择及离页。插件升至 **1.1.1**，不覆盖已安装 1.1.0 DLL。
- 核查刷新为系统 HistoryChanged/HistoryEnabledChanged、首次进入、窗口激活和手动刷新，没有定时器或持续轮询；图片为当前选择按需缩放预览，复制仍恢复原历史项。业务细节只维护在 [插件文档](Plugins/ClipboardHistory.md#3-平台与内部设计)。

GPT-6.1-Sol + high 子代理只读复审最终差异、空值通知、有效边距与操作换行、事件合并和预览释放，未发现新的确定缺陷。该结论不替代运行验收。环境仍为 Windows / .NET SDK 10.0.401 / VS Code，依赖、SDK 和宿主产品版本未改。

| 层级 | 实际结果 |
| --- | --- |
| 静态 | 文档本地链接/锚点、AXAML/项目 XML、脚本语法、ZIP 内容/许可/哈希和差异核对，见 `artifacts/evidence/ui-fixes-static.json` |
| 构建 | 最终默认 Debug、独立 1.1.1、20 项模型用例工程、7 项管理 UI 检查工程均 0 警告/0 错误；win-x64 自包含 Release 发布成功 |
| 发布准备 | 全新空宿主 `artifacts/m2-ui-fixes/host`；发布后独立构建 ZIP，234 个宿主文件 SHA256 未变；未加载插件 |
| 自动化 / 集成 / Headless | 未执行；本次只新增并编译空选择标题/通知用例，没有用固定尺寸断言冒充视觉验证 |
| 真实 Windows UI / WinRT | 未执行；用户自行验收。没有启动应用、读写真实剪贴板、修改已有安装/profile 或终止用户进程 |

首次 Debug 失败为新增无障碍名称 StringFormat 以 `{0}` 开头被 AXAML 解析为类型（AVLN2000，0 警告/1 错误）；改为文字开头的格式后成功。初次/最终记录分别在 `ui-fixes-debug.txt` / `ui-fixes-debug-final.txt`，未隐藏失败。其余日志为 `ui-fixes-publish.txt`、`ui-fixes-plugin.txt`、`ui-fixes-clipboard-checks.txt`、`ui-fixes-integration-checks.txt`。均位于 `artifacts/evidence`。

真实执行的构建/文件核对命令（检查程序均未运行）：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/m2-ui-fixes/host
# 发布后、构建插件前：Get-ChildItem -Recurse -File + Get-FileHash 保存到 ui-fixes-host-before-plugin.json
./scripts/Build-ClipboardPlugin.ps1 -HostSdkDir H:/ProjH/TractorTool/artifacts/m2-ui-fixes/host -Output H:/ProjH/TractorTool/artifacts/m2-ui-fixes/clipboard
dotnet build tests/TractorTool.ClipboardChecks/TractorTool.ClipboardChecks.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/m2-ui-fixes/host -p:PluginBuildDir=H:/ProjH/TractorTool/artifacts/m2-ui-fixes/clipboard/files -o H:/ProjH/TractorTool/artifacts/m2-ui-fixes/clipboard-checks
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/m2-ui-fixes/host -o H:/ProjH/TractorTool/artifacts/m2-ui-fixes/integration-checks
./artifacts/evidence/Inspect-UiFixes.ps1
git diff --check
```

ZIP：`artifacts/m2-ui-fixes/clipboard/TractorTool.ClipboardHistory-1.1.1.zip`；SHA256：`D5BA8F986D3A91D95EC1795BECE10CCFD140E072B426E7ADF2414486138C2BE6`。先发布宿主再构建插件的哈希记录为 `ui-fixes-host-before-plugin.json`，结果在静态报告中；这证明构建/打包未改宿主，不证明运行加载成功。

以下命令仅供用户隔离启动，本轮未执行：

```powershell
& .\artifacts\m2-ui-fixes\host\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\ui-fixes-manual-profile
```

将 1.1.1 ZIP 拖入插件页，确认后**显式退出并重启**。调试宿主也需要安装该 ZIP，单独 F5 不替换已安装插件。依次检查空/已安装列表、长说明、异常/待重启状态、窄/宽窗口中的拖放边界和按钮；剪贴板空历史、首次选择、清空与离页不再出现上述两条错误。继续完成 E17 的多格式原项粘贴、事件/焦点刷新及启停重启闭环，不因本次源码修复勾选全部验收。

</details>

<a id="evidence-project-layout"></a>

<details open>
<summary>E19 · VS Code 解决方案与插件开发组织（2026-10-04）</summary>

用户截图提示正式剪贴板工程不属于当前解决方案。原因是此前把“独立发布后编译”过度施加在日常开发上：根 slnx 漏掉插件及相关检查；只在 DesignTimeBuild 下使用源码引用，普通编译硬需已发布 SDK。源码目录本身不需要搬迁。

对照 Watt 固定提交 `d04213147e77a8d73277fa8b86eeecfa444df071` 的 `WattToolkit.slnx`、Authenticator/GameTools csproj：插件列入同一解决方案的 Plugins 分组，并引用公共客户端/UI 工程。其 `src/README.md` 主体仍是 TODO/旧注释，结构判断以实际 slnx/csproj 为准。文件缓存在 `artifacts/reference-projects`，链接和采用边界见 [架构第 1 节](ARCHITECTURE.md#1-当前代码与目标结构)。网页正文获取失败后用正常网络权限下载公开固定源码，仅阅读，未执行。

修改范围：

- 根 slnx 收录全部 10 个工程，Plugins/tests 分组，保留所有物理路径和原 VS Code 启动配置。
- 正式插件、机制插件、剪贴板模型检查和集成检查统一按显式产物路径选择引用模式。日常源码模式可整体构建；发布模式使用 DLL，坏路径拒绝而不回退。ClipboardChecks 必须同时提供 HostSdkDir/PluginBuildDir，源码模式明确引用 UI/Contracts 以供检查程序使用。
- 正式打包和 Verify-M1 的独立阶段使用正常 restore + `-t:Rebuild`。源码输出不能作为发布包，不自动复制到运行安装。宿主 csproj、公开 API、业务代码、依赖和产品版本未改。

GPT-6.1-Sol + high 子代理复审引用方向、条件互斥、CopyLocal、缺参数失败及脚本；没有发现确定新缺陷。依次验证源码编译 → 发布 SDK 独立重建 → 源码编译恢复。默认开发输出与正式交付证据分开，未执行任何应用/检查程序。

| 层级 | 实际结果 |
| --- | --- |
| 工程静态 | slnx 覆盖全部 10 csproj；17 组普通/设计时/产物引用评估均符合预期；5 个无效/缺路径目标按预期报错，非运行测试失败；见 `project-layout-references.json` |
| 构建 | 前后两次默认 Debug 全方案均 0 警告/0 错误；Release 宿主发布、独立剪贴板/机制插件及产物模式模型/集成工程成功 |
| 文件与包 | 发布后独立重建未改变 234 个宿主文件；插件 ZIP/许可/哈希与共享 DLL 排除、文档链接/XML/脚本语法见 `project-layout-static.json` |
| 自动化 / 集成 / Headless | 未执行；MSBuild 参数校验目标的结果不等于应用测试通过，Verify-M1 脚本只改动/核对，没有运行 |
| VS Code / Windows UI | 未操作 IDE 或启动应用，编辑器重新载入后的提示、补全和预览仍由用户确认 |

实际执行命令（均在仓库根目录；日志在 `artifacts/evidence/project-layout-*.txt`）：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/m2-project-layout/host
# 发布后记录 Get-FileHash 到 project-layout-host-before-plugin.json
./scripts/Build-ClipboardPlugin.ps1 -HostSdkDir H:/ProjH/TractorTool/artifacts/m2-project-layout/host -Output H:/ProjH/TractorTool/artifacts/m2-project-layout/clipboard
dotnet build tests/IndependentPlugin/IndependentPlugin.csproj -t:Rebuild -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/m2-project-layout/host -o H:/ProjH/TractorTool/artifacts/m2-project-layout/independent
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -t:Rebuild -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/m2-project-layout/host -o H:/ProjH/TractorTool/artifacts/m2-project-layout/integration-checks
dotnet build tests/TractorTool.ClipboardChecks/TractorTool.ClipboardChecks.csproj -t:Rebuild -c Release -m:1 -nr:false -p:HostSdkDir=H:/ProjH/TractorTool/artifacts/m2-project-layout/host -p:PluginBuildDir=H:/ProjH/TractorTool/artifacts/m2-project-layout/clipboard/files -o H:/ProjH/TractorTool/artifacts/m2-project-layout/clipboard-checks
./artifacts/evidence/Inspect-ProjectLayout.ps1
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
./artifacts/evidence/Inspect-ProjectArtifacts.ps1
git diff --check
```

引用检查只执行 `dotnet msbuild -getItem:ProjectReference` 及 RequirePublishedSdk/RequireBuiltPlugin/RequirePublishedHost 校验目标，没有启动被测程序集。分别确认设计时与普通评估一致、产物模式无公共源码引用，以及检查的 Debug 输出具备 UI/Contracts/插件 DLL。结果不代表空白 NuGet 缓存的离线构建。

本轮只改变工程开发方式，不需要为已安装的 1.1.1 再发同版本更新。后续从 VS Code 重新载入唯一根 slnx；功能验收继续沿用 E18。没有进入 M3。

</details>

<a id="evidence-style-ownership"></a>

<details open>
<summary>E20 · 公共 UI、宿主与插件样式职责复核（2026-10-04）</summary>

用户询问宿主的 `Styles/ShellStyles.axaml` 是否偏离公共 UI 分层。对照产品第 3.2 节、架构第 1/6 节及实际 AXAML/项目引用后，保留当前代码归属：

- ThemeResources/SharedStyles 提供统一颜色、通用控件与外观；UI 工程只依赖 Contracts 与 UI 框架，没有反向引用宿主/Core/具体插件。
- ShellStyles 的内容是主导航模板适配和 PagePresenter 边距；App 负责装配。MainViewModel 只对四个内置整页滚动页面启用 edgeScroll，外部插件使用默认页面边距。
- 剪贴板的 ListBox.Styles 只调整当前列表项排版，表面颜色使用公共动态资源，没有向 Application 加装全局主题。

没有发现上述范围内需要迁移文件或改变工程依赖的确定问题。ShellStyles 当前在应用级挂载，`shell`/`rail` 等选择器靠私有标识限定用途，并非强隔离；现有插件没有复用这些标识。后续若需更强范围约束，应收窄选择器，不能把同名冲突风险解释为现在已经污染插件，也不能把样式文件移进 UI 当作作用域修复。

本轮只补充 [架构中的样式归属表](ARCHITECTURE.md#ui-style-ownership)、插件规范和交接摘要，不更改 AXAML、项目、SDK、功能、版本或目录。高思考深度子代理独立复核此范围；复审不代表全仓库无缺陷。

静态核对命令：

```powershell
rg -n 'Styles|StyleInclude|ThemeResources|SharedStyles|ShellStyles|HasPageScroll' TractorTool TractorTool.UI Plugins -g '*.cs' -g '*.axaml' -g '*.csproj'
./artifacts/evidence/Inspect-StyleDocs.ps1
git diff --check
```

文档本地链接/锚点与本次文档差异核对结果保存在 `artifacts/evidence/style-ownership-static.json`。本轮构建、自动化、集成和真实 Windows UI 均未执行；不把 E19 构建或历史测试当作本次新执行结果。下一步仍由用户完成 E18/E17 运行验收，不因本次架构复核自动进入 M3。

</details>

<a id="m1-manual-checklist"></a>

## M1 人工回归清单（归档时的步骤）

用户手动验收建议（以下为步骤，未因此视作已经执行）：

1. 确认默认进入“主页”，欢迎区和安装卡片与真实插件一致；从卡片进入工具后左侧选中项同步，返回主页仍可打开。无插件、加载中、目录受阻及禁用/失败插件的显示要区分。再打开“独立插件”“日志”“插件”“设置”，检查紧凑图文导航、选中/悬停/键盘焦点、设置行和空态；软件目录应指向本次实际测试的发布副本（开发运行则为构建输出），数据目录应指向隔离 profile。先从标题文字与空白区拖动、双击最大化/还原，确认系统按钮仍可用；再检查缩放、高 DPI 和长路径。
2. 在“设置”选择浅色、深色、跟随系统及主题色，确认立即生效、没有常驻保存按钮；连续切换后退出重启确认最后选择保留。检查彩色导航/卡片保持原色、操作符号可辨、宿主和插件共享主题。在测试副本移走示例插件的 Assets/icon.png 后重启，确认回退符号且页面仍可打开，再恢复文件。开启/关闭页面动画后快速连续切页，检查无叠页/闪烁/误点，重启确认动画设置保留。仅在隔离 profile 中制造不可写配置，确认已生效但未保存的提示及重试。
3. 在“设置”中的“独立插件设置”修改备注并保存；返回插件页、退出重开，确认备注保留。
4. 记录插件页常驻计数，点窗口关闭；从系统托盘点击图标或“显示 TractorTool”恢复，计数应继续增长；切页面也不停止。
5. 隐藏后重复执行同一启动命令，并尝试 profile 参数末尾有/无反斜杠，应恢复同一窗口；从托盘“退出”和“设置 → 关于 → 退出应用”分别检查进程结束及日志中的 StopAsync/DisposeAsync。更改外观后立即退出再重开，检查最后选择仍保留。
6. 退出当前实例后，启动命令追加 `--no-tray`，点击窗口关闭应保留可见窗口并提示从设置退出。该模式是禁用托盘的故障注入，不能当作真实 Explorer 崩溃验证。

所有插件代码实验都使用临时发布副本的 `Plugins`。软件目录不可写时报告实际失败路径，不写入数据目录作为替代；历史自动测试验证了目录被文件阻塞的分支，Windows ACL 拒写尚未实测。测试不会删除真实视频、封面、剪贴板内容或用户数据。

插件开发、清单填写、正式引用与打包流程统一见 [插件开发规范](PLUGIN_DEVELOPMENT.md)，这里不维护第二份教程。

<a id="m1-exit"></a>

## M1 阶段出口（原要求）

### M1：宿主基础出口

- 保留现有应用路径，必要的 Contracts/Core/UI/测试工程可还原和构建，实际环境与结果可追溯。
- 软件、插件、数据和固定启动配置解析正确；设置持久化、备份和日志可用。
- 默认主页的欢迎区/真实安装卡片、日志/插件/设置入口、统一主题、图标/控件/设置行、页面过渡完整；深浅色/跟随系统、主题色、动画设置和重启保留实测。
- 标题栏拖动/双击/系统按钮、长路径、窄窗口和高 DPI 可用；关闭到托盘、恢复、再次启动和显式退出有真实结果，托盘失败时窗口可找回。
- 清单/兼容性/独立 DLL 加载、页面及设置注册和失败隔离得到验证；发布宿主后单独构建测试插件，保留宿主二进制未改变的证据。
- 快速切页、导航取消/失败、后台常驻活动以及保存后立即退出回归通过。测试夹具不作为正式工具交付。

用户已基于实测允许进入 M2；但 A01/A02/A03/A05/A21 的未逐项记录部分不因此自动改为全部通过，历史限制继续有效。

<a id="early-docs"></a>

## 早期文档整理与图标调研

### 1.3 文档整理与前期图标调研（历史摘要）

仅调整文档归属：建立文档导航、通用插件开发规范、剪贴板和视频两个插件文档；架构移出专属业务，开发文档保留台账并将旧命令移入附录，上下文改为精简交接。R01–R17、A01–A22、已确认语义和历史执行记录保留。没有修改应用代码、项目配置或插件实现，没有执行构建和运行测试。

文档静态核对通过：9 份文档的 137 个本地链接（含 41 个片段链接）无缺失；插件需求/设计/媒体验收原文迁移核对通过；本轮文档范围外的 71 个工作区文件 SHA256 未变；6 段历史 PowerShell 命令完整保留。证据：`artifacts/evidence/docs-reorg-links.json`、`docs-reorg-content.json`、`docs-reorg-scope.json`。这只验证文档整理，不新增任何应用验收结论。

随后按用户截图补查 Watt 的图标实现：固定提交的主导航使用图片，设置操作使用字体符号；当时 TractorTool 的 ToolIcon 和注册元数据只接了单色符号。公共设计及固定源码链接集中在 [图标分工与扩展](#icon-reference)。参考文本保留于 `artifacts/reference-icons/`，通过 `Invoke-WebRequest` 获取固定提交源码，并用 `rg` 核对导航、SDK 和本地 NuGet XML 类型声明。该次仅调研和文档更新，没有构建或运行测试；后续彩色资源和协议接入结果见 [E09](#evidence-icons-review)，不再列为尚未实现。

<a id="ui-reference"></a>

## UI 参考实现依据（M1 调研）

核对 Watt Toolkit 的 `develop` 提交 `d04213147e77a8d73277fa8b86eeecfa444df071`。用户截图所属版本未知，以下描述所读源码，不声称截图与该提交完全一致。

| 部分 | 源码事实与可借鉴做法 |
| --- | --- |
| 导航与主窗口 | [MainView.axaml](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/Views/Pages/MainView.axaml) 使用 FluentAvalonia `NavigationView`、`Frame` 和自定义 `TitleBar`；侧栏宽 72 个逻辑单位，定制图标在上、文字在下的模板及选中条，分别绑定工具与固定底部入口 |
| 切换动画 | [MainView.axaml.cs](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/Views/Pages/MainView.axaml.cs) 在选中工具时指定 `FromBottom`；[NavigationService.cs](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/Services.Implementation/UI/Widgets/NavigationService.cs) 映射到现成的 Slide、Entrance、DrillIn 或关闭过渡，并交给 Frame |
| 主题 | [App.axaml](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/App.axaml) 加载 `FluentAvaloniaTheme` 与自定义主题；后者集中包含颜色、字体、图标、控件样式。不是只更换主窗口背景 |
| 设置行 | [Settings_UI.axaml](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/Views/Pages/Settings/Settings_UI.axaml) 复用 `OptionsDisplayItem` 和 `SettingsExpander`；[设置行模板](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/Styling/Controls/OptionsDisplayItem.axaml) 将图标、标题/说明、操作区分列，并统一展开与状态外观 |
| 主页 | [HomePage.axaml](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/Views/Pages/HomePage.axaml#L517-L570) 将主窗口 TabItems 显示为可换行的按钮卡片，点击调用导航命令；TractorTool 采用欢迎区与本地插件入口，不接入其中的资讯、广告、商城或账号内容 |
| 图标 | 主导航用 `ImageIconSource` 加载 `IconKey` 对应图片；仓库有 home/settings/plugin 等 `.ico` 资源。常规操作另用 SymbolIcon、FontIcon。彩色工具标识与单色操作图标职责不同，添加图标库不会自动生成整个界面 |

其锁定的 [DirectoryPackages 子模块](https://github.com/BeyondDimension/DirectoryPackages/blob/5759bfb7760d10ec09c45ea405646c84fda4872a/Directory.Packages.props) 声明 Avalonia `11.3.20`、FluentAvaloniaUI `2.5.1`；[UI 工程](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/BD.WTTS.Client.Avalonia.csproj) 还引用自己的 Avalonia 内部工程。TractorTool 参考其组合方式，不整体迁入这些内部工程、旧版本模板和业务功能。

<a id="icon-reference"></a>

## 图标参考调研（M1）

在第 6.1 节固定提交中，[导航模板](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/Views/Pages/MainView.axaml#L354-L360) 将 `IconKey` 经 `BitmapAssetValueConverter` 交给 `ImageIconSource`。仓库包含 home/settings/plugin 等 ICO；[加速插件](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Plugins.Accelerator/Plugins/Plugin.cs) 将自己的 `Resources.accelerator` 作为菜单图标。[通用设置页](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/Views/Pages/Settings/Settings_General.axaml) 的操作图标另用 `FontIcon` / `FontIconSource`，部分显式指定 Segoe Fluent Icons，其余使用主题字体。这说明彩色资源和单色符号分别承担识别工具、解释操作的职责；不能仅给所有线框图标套主题色来替代。

<a id="docs-archive"></a>

## 文档归档维护（2026-10-04）

按用户要求降低 Agent 日常阅读量：M1 E01–E15、M2 已完成轮次 E16–E20、M1 人工回归/出口及参考调研迁入本文件。DEVELOPMENT 保留当前状态、操作、待验、里程碑和 A01–A22；PROJECT_CONTEXT 精简交接；AGENTS/README 明确历史不默认读取。归档 M2 的旧轮次不代表阶段完成。

本次只改 Markdown。静态命令 `./artifacts/evidence/Inspect-DocsArchive.ps1` 核对 20 条记录与命令的迁移完整性、A01–A22 原文、本地链接/锚点、唯一 AGENTS 和文档外变更范围；另执行 `git diff --check`。结果保存 `artifacts/evidence/docs-archive-static.json`。没有执行构建、应用、自动化/集成或 Windows UI 测试，也没有提升任何验收状态。

静态核对通过：20 条记录（其中 29 段 PowerShell 命令）、5 份 M1 补充片段和 22 条验收行完整保留；12 份文档的 203 个本地链接、78 个锚点引用无缺失；变更仅为 Markdown，根 AGENTS 仍唯一。

<a id="evidence-card-layout"></a>

<details>
<summary>E21 · 卡片信息/操作分组及旧插件绑定诊断（2026-10-04）</summary>

基于 `17600b2` 干净工作区处理用户的新截图。只读核对 Debug 软件目录的 `Plugins/TractorTool.ClipboardHistory/1.1.0/plugin.json`，确认仍安装 1.1.0；截图同时显示旧 `SelectedEntry.TypeName` / `SelectedEntry.Timestamp` 绑定错误。源码 1.1.1 在 E18 已改为非空派生属性 `SelectedTypeName` / `SelectedTimestamp`，选择变化通知和对应模型用例均存在。这里没有重复修改插件，也没有覆盖用户已安装 DLL；重新提供独立 1.1.1 ZIP，需安装并显式退出重启后验证。目录清单本身不能证明现存进程的加载版本，诊断同时依据截图中的旧绑定路径。

插件卡片局部布局留在宿主 `PluginsView.axaml`：图标与名称/说明/版本/当前状态组成左侧信息组；右列上方为无可见文字的开关，下方为操作组。两组共享高度，移除整张卡片单独预留的操作行；右列最大 240 逻辑单位，额外按钮按 8 单位间距换行。错误和待重启信息仍在下方、与信息文字对齐。没有改动开关状态语义、命令、事务或共享控件主题。

排版依据为 Apple [Layout](https://developer.apple.com/design/human-interface-guidelines/layout) 的对齐、相关内容分组和尺寸适配，以及 [Toggles](https://developer.apple.com/design/human-interface-guidelines/toggles) 的上下文识别原则；开关继续保留可访问名称和重启生效提示。采用本项目现有 Avalonia 控件，不引入 Apple 平台控件/素材。按钮换行使用 Avalonia [WrapPanel](https://docs.avaloniaui.net/controls/layout/panels/wrappanel) 的 ItemSpacing / LineSpacing，本机 12.1.2 API 和编译均确认支持。

实际命令（仓库根目录，Windows / SDK 10.0.401）：

```powershell
git status --short
dotnet --version
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
# 首次构建因沙箱拒绝写 LocalAppData/AvaloniaUI/BuildServices/buildtasks.log 失败。
# 授权同一构建命令重试，仅允许构建任务日志写入，不启动程序。
New-Item -ItemType Directory -Path artifacts/m2-card-layout -Force | Out-Null
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false 2>&1 | Tee-Object artifacts/m2-card-layout/debug-build.log
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/m2-card-layout/host 2>&1 | Tee-Object artifacts/m2-card-layout/publish-host.log
$publishedRoot = (Resolve-Path artifacts/m2-card-layout/host).Path
Get-ChildItem -LiteralPath $publishedRoot -Recurse -File | ForEach-Object {
    [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($publishedRoot, $_.FullName); Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
} | ConvertTo-Json | Set-Content artifacts/m2-card-layout/host-before-plugin.json
./scripts/Build-ClipboardPlugin.ps1 -HostSdkDir H:/ProjH/TractorTool/artifacts/m2-card-layout/host -Output H:/ProjH/TractorTool/artifacts/m2-card-layout/clipboard 2>&1 | Tee-Object artifacts/m2-card-layout/build-clipboard.log
./artifacts/m2-card-layout/Inspect-Delivery.ps1
git diff --check
```

结果分层：

- 静态：新包清单 1.1.1，共 13 文件，不含共享 SDK DLL；插件单独 Rebuild 后 234 个原宿主文件 SHA256 不变。结果在 `artifacts/m2-card-layout/delivery-static.json`，检查脚本只读文件/ZIP，不执行插件。变更与文档链接另作静态核对。
- 构建：授权重试的 Debug 全方案和发布 SDK 模式的剪贴板独立构建均 0 警告、0 错误；Release win-x64 自包含宿主发布成功。隔离输出在 `artifacts/m2-card-layout`，没有复制进真实安装或启动应用。
- 复审：GPT-6.1-Sol / high 子代理只读检查本次 diff、最小窗口布局约束、额外操作、命令绑定和旧插件诊断，未发现确定缺陷；不代表整个项目无问题或 UI 已通过。
- 自动化/集成/真实 Windows UI：均未执行。检查工程仅随方案编译；高 DPI/宽窄窗口、开关与恢复操作、安装更新后空选择控制台错误消失，均待用户运行确认。

交付 ZIP：`artifacts/m2-card-layout/clipboard/TractorTool.ClipboardHistory-1.1.1.zip`，SHA256 `3ADF97493EE6636B1428896B9CFA993F97A1EB6E7628643CE29685718FD74201`。本轮插件业务未改变，继续使用 1.1.1；已经安装该版本时不应重复安装同版本。日常 F5 仍启动原应用目录，发布副本是另一个独立软件目录；二者各自拥有 Plugins。运行入口与待验清单维护在 [开发文档](DEVELOPMENT.md#workflow)。

</details>

<a id="evidence-shared-motion"></a>

<details>
<summary>E22 · 统一动效与管理卡片原位更新（2026-10-04）</summary>

### E22 · 统一动效与管理卡片原位更新（2026-10-04）

起点本地提交 `94d1ed3`，工作区干净。用户最新截图已显示剪贴板 1.1.1，反馈插件启停开关没有参考软件的动画，要求完善公共框架。范围仅宿主/公共 UI 和相关检查，不进入 M3、不修改已安装插件或真实数据、不运行程序。

只读对照来源：

- [Watt Toolkit 开关样式](https://github.com/BeyondDimension/SteamTools/blob/d04213147e77a8d73277fa8b86eeecfa444df071/src/BD.WTTS.Client.Avalonia/UI/Styling/Controls/ToggleSwitch.axaml)：共享样式组织控件外观与过渡。本次仅参考组织方式，不复制其 GPL 模板。
- [FluentAvalonia 3.1.0 开关主题](https://github.com/amwx/FluentAvalonia/blob/215ee0481e8b0e6d396cbe2a0f33dc791c5646ab/src/FluentAvalonia/Styling/ControlThemes/BasicControls/ToggleSwitchStyles.axaml)：当前依赖本来有 Canvas.Left 200 ms 以及表面/旋钮尺寸 167 ms 过渡；按钮、滑杆、展开箭头也有各自内置反馈。本次保留原模板和原生拖动处理。
- [FAUISettings](https://github.com/amwx/FluentAvalonia/blob/215ee0481e8b0e6d396cbe2a0f33dc791c5646ab/src/FluentAvalonia/Core/FAUISettings.cs)：应用动画标志不会自动撤销 AXAML Transitions。[Avalonia Animatable](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Base/Animation/Animatable.cs) 支持替换有效 Transitions 时移除在途属性过渡；[ToggleSwitch](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/ToggleSwitch.cs) 在 Loaded 安装滑块过渡，拖动时临时清空并恢复。
- [DynamicResourceExpression](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Markup/Avalonia.Markup.Xaml/Data/DynamicResourceExpression.cs) 从实际目标控件查资源，[Application](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/Application.cs) 先查 Resources 再查 Styles，因此应用的 false 不被 MotionStyles 默认 true 遮蔽。[Expander](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/Expander.cs) 已开始的 CrossFade 不随 ContentTransition=null 取消，这一边界保留。

修正内容：

1. 原 PluginsViewModel.Update 每次管理结果都 Clear 并重建卡片/过滤枚举，销毁正在过渡的开关。改为按记录目录保留可观察 PluginCard，快照原子替换后通知；集合只增删/移动实际变化项，同 ID 不同版本目录仍分开。失败后显式恢复本地开关，不依赖相同模型值必定触发绑定重放。
2. 新增 UI 内的 Motion/MotionStyles。全局控件及模板子元素通过资源读偏好；关闭时可逆覆盖普通 Transitions，不覆盖写死原生拖动的底层值；开启恢复原值。自有颜色过渡时长统一为 220 ms；页面仍走同一个 FA 应用标志；移除主窗口 themeTransitions 接入要求。新增窗口/页面不必重复配线。
3. 不复制完整模板、不引入依赖、轮询、通用动画引擎或全局时钟冻结。普通属性过渡与后续展开/切页受控；已开始的第三方展开/页面短时过渡仍由库结束，自定义业务动效需遵循偏好与自身取消。
4. 管理 UI 用例扩充至 8 项（原对象/控件保留、失败复位、筛选与重复 ID）；新增 2 项共享动效检查覆盖模板局部过渡、关闭/恢复、拖动下层值和新窗口。保留已有忙碌串行保护，不宣称焦点始终保持。

实际命令（仓库根 PowerShell，SDK 10.0.401）：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false 2>&1 | Tee-Object artifacts/m2-motion/build-debug.log
# 首轮失败：新增 MotionChecks 直接访问 ITransition.Property，2 个 CS0122；宿主/UI 库编译成功。
# 修为 OfType<DoubleTransition>() 后再次编译：
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false 2>&1 | Tee-Object artifacts/m2-motion/build-debug-fixed.log
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/m2-motion/host 2>&1 | Tee-Object artifacts/m2-motion/publish-host.log
# 复审要求用例先处理 Loaded；修正后最终编译检查工程及依赖：
dotnet build tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj -c Debug -m:1 -nr:false 2>&1 | Tee-Object artifacts/m2-motion/build-checks-final.log
./artifacts/m2-motion/Check-Docs.ps1
git diff --check
```

结果分层：

- 静态：GPT-6.1-Sol/high 子代理只读复审实现及精确依赖源码，指出 Expander 在途取消边界和检查用例 Loaded 时序，分别保留限制/修正；未发现其他确定缺陷不等于全仓无缺陷。文档相对链接及新增 E22 锚点检查结果在 `artifacts/m2-motion/docs-links.json`。
- 构建/发布：修正后的 Debug 全方案 0 警告/0 错误；最终检查工程及依赖编译 0 警告/0 错误；Release win-x64 自包含发布成功。首次失败日志保留。构建授权仅用于 Avalonia 构建日志等正常输出，不启动应用。
- 自动化/集成/Windows UI：**均未执行**，新增检查仅编译。动画视觉连续性、真实点击/拖动、快速反向/失败复位、偏好关闭恢复及新页面、深浅主题仍交用户验收；对象引用断言不能证明渲染流畅。
- 产物：隔离宿主 `artifacts/m2-motion/host`；已有剪贴板 1.1.1 ZIP 沿用 E21，本轮不重编正式插件、不声称取得新的独立加载或哈希不变证据。VS Code F5 使用已构建的原 Debug 宿主；隔离宿主自带空 Plugins，用户可在该副本安装既有 ZIP 测试。

当前操作与未关闭事项只在 [开发文档](DEVELOPMENT.md#workflow) 维护；本记录归档不代表 M2 或真实动效验收通过。

</details>

<a id="evidence-process-service"></a>

<details>
<summary>E23 · M2 复审与公共进程服务起步（2026-10-04）</summary>

### E23 · M2 复审与公共进程服务起步

起点为本地提交 `d1465dc`，工作区干净。用户要求 Review，无阻塞则继续开发；运行检查/UI 仍由用户负责。两位显式 GPT-6.1-Sol/high 子代理只读复审上一轮共享动效、稳定卡片/开关、M2 文件事务及剪贴板生命周期，未发现确定阻塞问题，没有把静态结论改写成 M2 验收通过。

本轮进入 M3 第一个增量：

- Contracts API 1.1 增加 `IProcessRunner`、请求/结果与 `PluginContext.Processes`，接受旧 API 1.0；保留原五参数构造/解构和 `AssemblyVersion=1.0.0.0`。剪贴板插件仍为 1.1.1，没有修改其安装目录。
- Core 负责参数快照、UTF-8 双流并发有界保留、非零退出码、超时/取消分类与清理；实例属于插件，注册请求和关闭新请求使用同一把锁。初始化失败和宿主停止都会开始取消进程，不依赖页面卸载或插件 StopAsync 是否响应。
- Windows 适配放在宿主 Platform，创建时用 Job 属性绑定整个进程树，限制继承句柄并关闭 stdin；正常结束、取消和失败都清理 Job，最后句柄关闭再兜底。Core/Contracts 未加入 Win32 依赖，没有 Worker/IPC 框架。非 Windows 调用明确不支持。
- 此增量不含下载包、包租约、管理页、实时进度或真实媒体处理；清单非空 dependencies 继续拒绝。没有 FFmpeg/GPU 结果、没有发布产物新哈希证明。

复审与修复记录：

1. 初稿只依赖 `.NET Process.Kill(entireProcessTree)`，根先退出会失去后代；改为 `CreateProcessW` 创建属性 `PROC_THREAD_ATTRIBUTE_JOB_LIST` 原子绑定，不能用启动后 Assign Job 冒充无竞争。
2. 初稿启动前取消检查在异常分类外，极短执行时限可能返回普通取消；已放入同一分类路径。
3. 正常结束时内部清理超时在第二次清理成功后可能被误报 Cancelled；两位复审均指出，现转为 IOException，并补故障用例。调用方取消、宿主停止、请求时限分别保留。
4. 最终自查将 OS 等待包装改为共享同一 SafeWaitHandle 引用计数；已登记等待保护实际进程句柄，释放同时结束未派发的退出 Task。

实现依据：[微软创建属性](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)、[Process.Kill 的后代等待限制](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0)。未复制参考软件模板或新增依赖，普通工具参数按参数向量传递，Windows 原生入口仅做 CRT 引用编码。

实际命令（仓库根目录；只编译/静态检查）：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false *> artifacts/m3-process/build-debug.log
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false *> artifacts/m3-process/build-debug-final.log
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false *> artifacts/m3-process/build-debug-reviewed.log
./artifacts/m3-process/Check-Docs.ps1
git diff --check
```

证据分层：

- 静态：两位 GPT-6.1-Sol/high 审查新进程实现与适配器，以上确定问题均修复。最后错误分类及 4 项失败/竞争用例复审未发现新确定缺陷。文档相对链接/锚点核对保存 `artifacts/m3-process/docs-links.json`。
- 构建：Debug 全方案 10 工程 0 警告/0 错误；日志保存在上述路径。本轮没有 Release 发布或独立插件重打包，不把 E22 产物视为含新服务。
- 自动化/集成：新增 17 项进程用例（4 项替身：读失败、清理取消、清理失败、启动/关闭竞争；13 项 Windows 真实子进程夹具：参数/双流/状态/取消/超时/关停/EOF/后代及相互隔离）和 1 项 API 兼容检查，**仅编译，未运行**。夹具使用当前检查程序和随机临时目录，不调用媒体工具或用户文件。没有宣称 native Job 在本机运行通过。
- Windows UI/剪贴板/媒体/GPU：**均未执行**。M2 闭环、API 1.0 旧 DLL 与新宿主实际装载、Windows 嵌套 Job/权限/后代清理仍待用户运行；包与媒体阶段未完成。

开发接续与阶段缺口见 [当前台账](DEVELOPMENT.md#current-status)，不因本记录归档关闭验收项。

</details>

<a id="evidence-package-service"></a>

## E24 · 外部包事务、租约与设置入口（2026-10-04）

起点提交 `7e40df1`，工作区干净。用户允许继续开发，仍由用户负责运行验收。本轮实现 M3 包服务与设置入口，没有开发视频业务或启动应用/检查程序/FFmpeg。

### 实现与复审

- Contracts API 1.2 增加只读状态/取租约的 Packages 能力，保留上下文原构造/解构和程序集身份，接受旧 1.0/1.1。清单声明固定 ID/版本，只支持宿主目录和 RID；依赖缺失仍可加载页面，任务取得租约明确失败。
- Core/Packages 集中固定目录、HTTPS 下载、SHA-256、安全 ZIP、工具版本探测、receipt、同版本请求合并、占用保护和版本共存。设置页显式下载/取消/重试/移除，展示路径、文件大小、来源/许可和使用者。进程/页面所有权继续分开，未引入 Worker/IPC 或新项目。
- 临时事务位于 Packages/.work，带精确归属标记；启动恢复不删除无标记目录。退出封闭新请求并取消/等待已有请求；插件停止后回收遗漏租约。
- GPT-6.1-Sol / high 子代理静态复审发现：单包路径异常穿透构造、HTTP 超时误报取消、Acquire 失败先释放占用导致旧状态覆盖新事务、清理过早删归属标记。均已修复并获复审确认。补充设置页实际目录/文件大小/来源信息并再次复核，无新确定缺陷；大小为安装时逻辑文件总量，不是实时物理磁盘占用。不将静态复审当运行通过。

### 固定来源与静态证据

[FFmpeg 官方下载页](https://ffmpeg.org/download.html)指向 Windows 构建提供方；本轮选 [Gyan 9.0.2 release](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2) 的 essentials ZIP，固定 win-x64、GPLv3，上游 [构建说明](https://www.gyan.dev/ffmpeg/builds/)及[版本校验文件](https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip.sha256)已核对。代码不使用 latest。

只下载到仓库 artifacts/m3-packages 后，用 Get-FileHash 和 .NET ZipArchive 读取文件目录，未执行 EXE、未向真实 DataRoot 安装：

- ZIP 大小 114,768,076 字节，49 个条目，展开文件共 329,221,735 字节。
- SHA-256：`60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`，与上游相同。
- ffmpeg/ffprobe 路径为 ffmpeg-9.0.2-essentials_build/bin 下对应 EXE；LICENSE 存在，首部为 GPL version 3，安装保留全文与 README。
- 证据：`artifacts/m3-packages/source-inspection.json` 和同目录 ZIP。初次受限网络请求 TLS 失败，获准正常网络权限后完成下载；未绕过证书校验。

### 构建与限制

实际在仓库根目录执行：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false --no-restore
```

初次、复审修正后和最终构建均成功，10 工程，0 警告/0 错误。日志分别为 artifacts/m3-packages/build-debug.log、build-reviewed.log、build-final.log。本轮未发布，旧 E22 Release 副本不含 M3 变更。

新增 18 项包事务用例（内存 ZIP/HTTP/进程替身）和依赖声明检查，覆盖多版本、安装/读取损坏、恶意 ZIP、哈希/版本错误、网络超时、取消/合并、占用、作用域、退出与遗留清理，**只编译未运行**。上一轮 17 项进程检查与 M2 用例仍未运行；没有真实 FFmpeg 版本探测、服务安装/取消、低磁盘/ACL/锁定、旧插件加载、视频导出或 GPU 证据。

文档核对：183 个仓库本地链接无缺失，E24 锚点唯一，记录在 artifacts/m3-packages/docs-links.json；Git 差异空白检查通过。

当前验收与下一步继续维护在 [开发台账](DEVELOPMENT.md#current-status)。接续为视频插件的真实探测/执行/进度，不能将本轮 ZIP 静态核对记为安装或转码已通过。

<a id="evidence-dependency-flow"></a>

## E25：依赖申请、引用清理与设置分类（2026-10-04）

用户确认：首次缺失依赖经确认下载、已装复用；首版软件统一维护可信源，插件卸载保留包，设置可清理未引用包。按图将软件设置和插件设置分组。补充要求是替换流程时清除旧命令/绑定，保持框架边界干净。

- Contracts API 1.3 新增 RequestAsync（默认接口实现兼容既有实现类），原 AcquireAsync 语义不变。Core 合并确认/安装，申请预留覆盖直到获取租约；宿主确认对话框统一排队，退出可取消。
- C# 固定源目录改成软件目录 PackageSources/default.json，构建/发布复制；无远程索引服务或自定义源功能。receipt v2 固化安装定义，源移除仍可复用和清理；修复使用当前源，旧 v1 可选升级失败保留有效 Ready 状态。
- 引用由清单与管理日志重建，保护活动、禁用、失败、待激活和回退版本；不可读暂停清理。管理锁内同步到包服务，Core 同锁检查引用、请求、租约和事务。卸载重启前后正确分离引用与包文件，清理前展示具体清单并重新检查保护条件。
- SettingsView 变为分类容器，正文拆通用/数据/依赖/关于，动态添加实际插件设置。宽窗口分栏、窄窗口分类选择，复用 PageHost 动画与边缘滚动。旧长页、返回设置命令、目录全量卡片入口已移除。嵌套页转发直接路由的离页取消，失败工厂不得清理仍显示的页面。
- GPT-6.1-Sol/high 分 Core/UI 静态复审，修复：DirectoryError 正常 null；单坏目录终止恢复；安装定义与当前源混用；旧 receipt 迁移写失败误报坏包；错误页导航取消后的过早清理；嵌套取消传递；旧控件定位及 Loaded 异步调度的测试适配。审查未运行程序。
- 包用例新增 8 项，Windows 合计 26 项（旧 receipt 占用仅 Windows）；加入设置嵌套导航回归，更新协议与未知源兼容检查。全部只编译，未执行。

构建过程与证据：首次沙箱构建因 Avalonia 用户缓存日志访问被拒失败；允许构建缓存访问后发现拆分 AXAML 子元素的重复 xmlns，移到根声明后修复；随后修正新增测试缺 using。最终命令为 `dotnet build .\TractorTool.slnx -c Debug --no-restore -m:1 -nr:false`，全方案 0 错误、2 个 NU1900（NuGet 漏洞数据源不可达）警告，日志 `artifacts/m3-dependency-flow/build-debug.log`。静态确认宿主输出含 PackageSources/default.json，文档本地链接及 diff 空白检查通过。

本轮未启动应用、检查程序、FFmpeg 或 UI 自动化，没有读写真实剪贴板/已安装插件/用户数据；未发布、未配置远程或推送。首次确认、来源失效后复用、卸载后清理、窗口分类布局/动画/退出、旧 DLL 兼容及真实网络/媒体行为仍待运行验收；编译和静态复审不替代这些证据。当前状态只维护 DEVELOPMENT.md。

<a id="evidence-settings-hierarchy"></a>

## E26：设置层级与视频读取入口（2026-10-04）

用户用 Windows 设置/PowerToys 截图澄清原图为包含关系，并确认理解：左侧软件/插件分组、通用与外部依赖两个软件大类、通用下三个子页及包详情、顶部面包屑返回。并指出没有实际依赖消费插件使功能无法验收。

- SettingsPage 加父关系，SelectedCategory 与当前子页分离；入口行进入子页、面包屑返回。保留 PageHost 动画、窄窗口及取消离页，替换旧四项平级入口/Pages 集合；外观页移至 SettingsAppearanceView，主页快捷入口直达。包列表只展示摘要，原详情移入 PackageDetailsView。
- 新增正式视频插件 0.1.0，API 1.3、win-x64、FFmpeg 9.0.2：单视频选择、RequestAsync 申请、真实 ffprobe 命令与 JSON 解析，取消、文件变化检查、错误与未知信息。实例共享模型使离页不取消，停止经 UI dispatcher 取消并等待。插件偏好是选择后自动读取，通过公开 Settings 自动保存，坏配置不覆盖。无导出/封面/GPU 或模拟结果。
- 阅读 [ffprobe 官方文档](https://ffmpeg.org/ffprobe.html)，使用 show_format/show_streams/of json；工具运行与返回内容本轮未实际执行验证。纯音频及只有附加封面不当视频，平均帧率与编码尺寸标签不冒充 VFR 时间轴/旋转后的尺寸。
- 两位 GPT-6.1-Sol/high 只读复审，设置层级未发现确定阻塞缺陷；视频退出工作线程访问模型/取消源的竞争已通过 Dispatcher 调度修复，复核关闭。新增层级回归及 5 组纯 JSON 断言，仅编译未运行。
- 本轮没有改 Core 包服务/进程事务，没有创建通用 Worker/IPC；没有运行应用/检查程序/ffprobe/FFmpeg，没有安装插件或写真实用户数据。

实际构建/静态证据：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
./scripts/Publish-Host.ps1 -Output H:\ProjH\TractorTool\artifacts\settings-hierarchy\host
./scripts/Build-VideoPlugin.ps1 -HostSdkDir H:\ProjH\TractorTool\artifacts\settings-hierarchy\host -Output H:\ProjH\TractorTool\artifacts\settings-hierarchy\video
```

首次受限构建因 Avalonia 用户缓存日志权限失败；授权构建缓存访问后通过。修复退出调度时多 await 层次造成 CS0815，按本地 Avalonia Func<Task> 重载自动展开规则改为一次 await 后编译通过。最终 Debug 共 12 工程，0 错误/0 警告；Release 宿主发布、只引用发布 DLL 的视频插件独立构建同样成功。235 个原宿主文件哈希全部未变。日志及哈希在 artifacts/settings-hierarchy/{build-debug.log,publish-host.log,build-video.log,host-hash-check.json}；插件 ZIP 与 SHA256 在 video 子目录。

本地 Markdown 链接、JSON/ZIP 结构与共享 SDK 排除、Git diff 空白检查通过。运行验收仍由用户执行；真实网络下载、ffprobe 媒体内容、设置交互、卸载保留/清理、退出并发，以及先前 M2/进程/包测试仍未取得运行证据。当前状态集中在 DEVELOPMENT，不把信息读取入口当视频导出验收完成。

<a id="evidence-dependency-feedback"></a>

## E27：依赖拒绝/缺失路径异常与全局计数修正（2026-10-04）

用户测试截图显示：缺失依赖选择“先不安装”后调试器在共享准备任务上中断；清理和下次启动出现 Packages/.work 及 FFmpeg 路径缺失异常；全局插件计数占用每页底部空间。

- 原拒绝流程抛 OCE，视频模型虽捕获，跨优化插件调试仍可能中断。新增 API 1.4 `TryRequestAsync`，拒绝返回 null、保留输入、结束忙碌，调用 token 取消及实际失败仍抛异常。Core 共享准备结果和申请预留、Scope 租约回收继续复用；旧 1.3 `RequestAsync` 签名/拒绝语义保留，程序集身份不变。新宿主仍接受旧清单。
- `PackageFiles.CheckPath` 原来对每个缺失组件调用 File.GetAttributes 再捕获，导致 first-chance 异常日志。改用 FileInfo.Attributes 的 -1 缺失值并继续向上检查重解析点，权限等错误仍传播。核对 [.NET 10 属性文档](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.attributes?view=net-10.0)及[运行时实现](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystemInfo.Windows.cs)。没有关闭调试器异常设置，也没有吞掉未知错误。
- 主窗口移除正常计数及“加载中”页脚；主页与插件管理已有摘要继续保留。条件 Notice 只在实际初始化/呈现失败、托盘不可用时占位，取消导航不留下常驻提示。
- 视频插件升至 0.1.1 / API 1.4，使用新申请接口；独立生成 ZIP，不覆盖已安装 0.1.0。宿主设置安装入口也使用新流程。
- 包回归从 Windows 26 项增至 29 项，新增旧协议拒绝兼容、共享拒绝、安装/清理/重启正常路径无缺失文件 first-chance 异常；已有拒绝/重试/取消共享用例改测新接口，补协议版本检查。全部仅编译未运行。
- GPT-6.1-Sol/high 只读复审契约、共享确认、取消/租约及路径防护，未发现确定缺陷；实际并发、旧 DLL 加载及真实 UI 仍未验证。

实际构建与打包：

```powershell
dotnet build TractorTool.slnx -c Debug --no-restore -m:1 -nr:false
./scripts/Publish-Host.ps1 -Output H:\ProjH\TractorTool\artifacts\dependency-feedback\host
./scripts/Build-VideoPlugin.ps1 -HostSdkDir H:\ProjH\TractorTool\artifacts\dependency-feedback\host -Output H:\ProjH\TractorTool\artifacts\dependency-feedback\video
```

首次 Debug 构建指出 App 初始化错误处理仍引用旧 Status 属性（CS1061），改为 Notice 后全方案 12 工程编译通过，0 警告/0 错误。Release 宿主发布及插件独立构建成功，235 个宿主文件哈希未变。日志为 artifacts/dependency-feedback/{build-debug-first.log,build-debug.log,publish-host.log,build-video.log,host-hash-check.json}。ZIP 为 video/TractorTool.VideoExport-0.1.1.zip，SHA256 为 `7A44BD7C8ABABD14BF177CAE0422B3522F0AC2CC3B852FFC91059E87973C2290`。

静态核对 186 个本地文档链接、ZIP 9 个条目、清单版本/API、无共享 DLL 副本及 diff 空白均通过（static-checks.json）。独立打包后正常 restore 恢复源码 SDK 依赖图；首次受限 restore 无诊断退出 1，授权缓存访问后还原成功，证据在 restore-development.log。

未启动应用、检查程序、UI 自动化或 FFmpeg，没有改用户实际依赖、已安装插件或数据。用户截图是问题定位证据，源码修正/构建不等于实机问题已验收；新宿主与新 ZIP 需一同使用，修复复测和已有阶段缺口仍留在 DEVELOPMENT。

<a id="evidence-python-plan"></a>

## E28：Python / pip 依赖扩展边界（2026-10-04）

用户询问未来能否使用 pip 安装源，并确认将其列入后续计划。本轮核对现有包契约、本地源与分层后，只修正产品需求、架构、开发计划及交接快照；首版便携包范围与 M3 媒体工作不变。

方案保留宿主统一申请/确认/引用/清理，由 Core 在实际接入 Python 时区分安装流程，增加解释器和隔离环境的管理。重点记录版本冲突、间接依赖保护、完整环境复用条件，以及 venv 在数据根迁移时需重建的限制。官方依据留在[架构方案](ARCHITECTURE.md#python-dependencies)，尚未决定环境共享粒度、锁定格式或源码分发包支持策略。

5 份改动文档的 165 处本地链接/锚点与文档范围 diff 空白检查通过。不构建、不运行应用或测试，不安装 Python/pip 依赖；本轮不产生功能通过结论。已有 `.vscode/launch.json` 工作区修改保持原样，不纳入文档提交。

<a id="evidence-video-export"></a>

## E29：框架边界复审与视频单次导出（2026-10-04）

用户强调框架精干、业务归插件，要求再次 Review 后继续。两路 GPT-6.1-Sol/high 静态复审现有公共层和插件生命周期，未发现确定阻塞；保留有实际用途的进程实例分工与旧 API 兼容入口，不把它们误当死代码删除。随后按 M3 接入单次真实导出，没有提前实现 Python/AI 或通用 Worker/IPC。

- 公共改动限定为 API 1.5 的 ProcessRequest 可选 stdout 分块回调：保留原构造/解构和程序集身份，worker 顺序回调，捕获限制不截断通知，回调失败仍清理进程。宿主不解析 FFmpeg 参数或进度，旧 API 1.0–1.4 清单继续兼容。
- 视频 0.2.0 自己实现结构化探测、容器/编码参数、码率估算、可选首帧封面、进度解析、GPU 短片编码检查和完整导出、取消、输出探测与无覆盖提交。MP4/MKV/MOV 的 H.264/AAC 为当前实现范围；多预设和公共有限任务仍在 M4。
- 文件操作限定为用户指定目录中的独有事务目录和指定临时文件；输入不覆盖，封面快照，源文件变更检查，失败保留其他文件。包租约覆盖整个导出；切页/隐藏继续，停止时取消并等待。
- 新代码复审发现透明封面可能透出原首帧、高位深识别遗漏，以及新增接受 API 1.5 检查与旧拒绝 1.5 检查相冲突。分别改为封面先铺黑合成不透明画面、明确 8 位 SDR 格式/色彩白名单、未知协议拒绝用例升为 1.6。两位复核未发现其他确定阻塞，均未运行程序。
- 新增 stdout 到达时机/超出捕获上限与回调异常清理检查，进程检查合计 19 项（6 个替身、13 个 Windows）。视频检查合计 12 组，覆盖 JSON/旋转/SAR、输入拒绝、实际流映射与参数、分块进度、输出元数据拒绝、隔离无覆盖提交与取消清理；29 项 Windows 包用例保留。以上全部只编译，未执行。

实际构建与独立打包：

```powershell
dotnet build TractorTool.slnx -c Debug --disable-build-servers -m:1 -nr:false
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/video-export/host
./scripts/Build-VideoPlugin.ps1 -HostSdkDir H:/ProjH/TractorTool/artifacts/video-export/host -Output H:/ProjH/TractorTool/artifacts/video-export/video
# 正式 SDK 模式打包后，恢复日常源码依赖图并作最终构建
dotnet build TractorTool.slnx -c Debug --disable-build-servers -m:1 -nr:false
```

首次受限构建因 Avalonia BuildServices 用户日志权限失败，并出现 NU1900 数据源不可达；使用正常构建缓存权限后编译成功，中间两次 Debug 日志各保留 4 个 NU1900 警告。最后一次正常源码 restore/build **12 工程 0 警告/0 错误**，Release 宿主发布和只引用发布 SDK 的插件独立构建成功。日志位于 artifacts/video-export/{build-debug.log,build-debug-final.log,publish-host.log,build-video.log,build-source-final.log}，早期受限失败仅保留工具输出。

独立构建前后 **235 个宿主文件哈希未变**，记录为 host-before.json、host-hash-check.json。ZIP 为 video/TractorTool.VideoExport-0.2.0.zip，含 9 项、无共享 SDK/FFmpeg 副本，SHA256 为 `C6F334EA16027DA0A58E1BFFF74B28769EC693997A8212794D4F334DE2B4DFA0`。新插件要求 API 1.5，测试时必须搭配本轮新宿主；未覆盖用户已安装目录。已有用户 `.vscode/launch.json` 改动保持原样并排除提交。

实现时核对 [FFmpeg 参数文档](https://ffmpeg.org/ffmpeg.html)及[滤镜文档](https://ffmpeg.org/ffmpeg-filters.html)；使用实际 stdout 进度，提交前最多 99%，输出探测只是元数据检查，不证明画面/音画正确。封面在第 0 帧覆盖、VFR 保留时间戳、8 帧 GPU 检查均是待验证的实现，不记为真实结果。

静态核对 10 份 Markdown 的 234 处本地链接/锚点、修改的 AXAML XML、ZIP 9 项及版本/API/共享 DLL 排除通过；本轮 diff 空白检查通过，排除用户已有的 launch.json 空白改动。结果保存在 artifacts/video-export/static-checks.json。

本轮未启动应用、检查程序、FFmpeg 或 UI，没有读写真实剪贴板/安装插件/修改用户数据。CFR/VFR、旋转、透明封面首帧和后续画面、时长/音画同步、不同容器及实际 GPU 链、取消/退出仍待用户实测；M3 尚未关闭，M2 和旧依赖/设置运行缺口继续留在 DEVELOPMENT。

<a id="evidence-video-workspace"></a>

## E30：视频双功能工作区与多预设串行（2026-10-04）

用户提供草图：右上切换功能，导出页左侧输入/参数、右侧预设/进度/导出。用户进一步明确“放视频”指拖入文件识别，信息页应类似 MediaInfo 提取信息，而不是视频播放器或首帧预览。按最终更正实现，无媒体播放依赖或预览任务。

- 视频插件 0.3.0 / Contracts 1.5 / UI 1.3，拆为 VideoView 导航、ExportWorkspaceView、MediaInfoView、共用 VideoInputView，替换旧单列设置行/摘要 Expander。复用 PageHost 切换动效，宽窗口左右、窄窗口上下，区域滚动独立，右侧列表底部保留导出按钮。
- 左侧拖放一次一个本地文件，和文件选择走相同模型入口；图片区仅接指定图片扩展名。信息模式拖入即识别，右侧只读文本支持全选/复制；ffprobe show_format/show_streams/show_chapters/show_programs 的字段全部保留，按容器/媒体轨/章节/节目和嵌套层级格式化，不枚举逐帧/逐包内容。
- 左侧编辑器新增/修改卡片；右侧勾选、复制、删除，名称不解析成参数。VideoSettings 使用新增可选 Presets 字段兼容旧偏好，保存稳定 ID、名称、参数、勾选，不保存输入/输出路径；写失败可重试，坏配置不覆盖。编辑已有预设清除旧输出状态。
- 插件实例持有单批次和每项取消源，快照勾选项后串行执行，失败继续，取消等待项跳过，取消整批终止当前/后续；运行中不允许编辑输入/预设。包租约覆盖整批，封面只读共享锁防止版本之间变化，单项输出仍用原事务与无覆盖提交。没有扩展公共媒体任务协议；公共有限任务及中断恢复仍待接续。
- 宿主只新增可选 UsesCustomHeader（默认 false），让插件提供标题与局部导航；导航成功后才提交隐藏状态，错误页默认恢复标题，设置容器不受影响。UI API 1.3 接受旧 1.0–1.2，保留旧构造/签名与程序集身份；新插件需配套新宿主。
- 静态复核修正预检部分失败遗留卡片等待态、编辑后旧结果状态，以及批次封面变化窗口。增加 UI 协议兼容、完整报告、旧偏好/参数无路径持久化、串行/取消等待项/失败继续回归；视频检查共 15 组，仅编译未运行。本轮没有再调用子代理，E29 的模型复审结果不套用本轮代码。

构建/打包在 artifacts/video-workspace：初次 Debug 成功但有 AVLN5001 Watermark 过时警告，改用 PlaceholderText 后 build-final.log 和独立打包后的 build-source-final.log 均为 **12 工程 0 错误/0 警告**。实际命令：

```powershell
dotnet build TractorTool.slnx -c Debug --disable-build-servers -m:1 -nr:false
./scripts/Publish-Host.ps1 -Output H:/ProjH/TractorTool/artifacts/video-workspace/host
./scripts/Build-VideoPlugin.ps1 -HostSdkDir H:/ProjH/TractorTool/artifacts/video-workspace/host -Output H:/ProjH/TractorTool/artifacts/video-workspace/video
dotnet build TractorTool.slnx -c Debug --disable-build-servers -m:1 -nr:false
```

Release 宿主发布和插件独立构建通过（publish-host.log、build-video.log），独立构建前后 **235 个宿主文件 SHA256 未变**（host-before.json、host-hash-check.json）。video/TractorTool.VideoExport-0.3.0.zip 的 SHA256 为 `E80E625CFBA71A92027CE2C1211A457D8434F1A246CE6163B4D1DE9370997120`；未安装到用户 Plugins。正常源码 restore/build 已恢复开发依赖图。

静态检查通过：10 份 Markdown 的 235 处本地链接/锚点、7 个相关 AXAML 的 XML、ZIP 9 项及版本/API/共享二进制排除，以及本轮 diff 空白检查（排除用户已有 launch.json 改动）。结果保存在 artifacts/video-workspace/static-checks.json。

本轮核对 [ffprobe 官方信息输出选项](https://ffmpeg.org/ffprobe.html)。Apple Layout 页面只返回需 JavaScript 的提示，未将其当作新读取的设计证据；布局依据用户草图及仓库既有对齐/分组/适配约定。没有运行应用、自动化检查、FFmpeg 或 Windows UI，也没有读取真实视频或剪贴板；拖放路由、缩放布局、复制、持久化/串行/取消及真实媒体结果仍由用户验收。已有 `.vscode/launch.json` 用户空白修改保留、不提交。

<a id="evidence-video-rate-control"></a>

## E31：视频码率模式、GPU 偏好与紧凑布局（2026-10-04）

用户反馈 E30 的卡片/报告排版松散，要求导出主要参数为格式、VBR 一次/两次/CBR、视频/音频码率，GPU 加速移到插件设置；确认不支持时自动使用 CPU 并显示原因。澄清 VBR/CBR 是码率而非帧率。业务修改均在视频插件，不修改宿主/SDK/Core、不引入新依赖。

- 0.4.0 以原生 TabStrip 切换功能；输入区压缩，文件名省略并悬浮显示完整路径；未输入路径、未开始进度及空错误不占卡片空间，预设卡片按可用宽度排列。输出位置/导出按钮集中右下，尺寸及首帧替换折叠在画面选项；信息报告的边框/工具条和外边距统一。
- 输出格式由插件配套：MP4/MKV/MOV/TS → H.264/AAC，WebM → VP9/Opus。文件选择不再过滤输入扩展名；ffprobe/FFmpeg 负责识别，导出仍保留明确的 SDR/位深/隔行限制，未承诺所有格式。码率模式不添加 -r，不等同 CFR/VFR。
- VBR 两次在独有事务目录写统计文件，完整分析后再第二遍编码；第一遍 null muxer 不写视频，第二遍映射音轨，两遍共享滤镜/首帧规则，进度分段，取消不启动下一遍，清理已知统计产物。CBR 配置 VBV 与后端码率控制，x264 filler 避开 MP4 的 nal-hrd=cbr 限制。
- GPU 开关默认关闭、自动保存；批次开始快照。开启时先检查 H.264 GPU 后端，失败/不支持用 CPU；已通过短检查但完整导出失败也最多重试一次 CPU。卡片保留使用方式/回退原因，取消不回退。VBR 两次为真正文件级两遍，使用 CPU；WebM 暂用软件 VP9，不把 NVENC 内部 multipass 当整段两遍。
- 旧 schema 1、预设 ID/名称/参数/勾选保留；缺少 RateControl 默认 VBR 一次，旧 Encoder 字段只被读取器忽略、下次保存不再输出，没有遗留旧选择器或 VM Encoder/Encoders。宿主 JsonFileStore 严格检查 schema，因此没有为了字段调整升高 schema 后拒绝旧设置。
- 回归共 22 组，新增旧字段转换、格式/模式参数组合、分析统计复用/取消清理、GPU 不可用与完整失败回退、VBR 两次绕过 GPU；仅编译，未执行。本轮由主代理静态核对，没有调用新的模型子代理复审。

实际构建/打包命令：

```powershell
dotnet build TractorTool.slnx -c Debug --disable-build-servers -m:1 -nr:false
./scripts/Build-VideoPlugin.ps1 -HostSdkDir H:/ProjH/TractorTool/artifacts/video-workspace/host -Output H:/ProjH/TractorTool/artifacts/video-rate-control/video
dotnet build TractorTool.slnx -c Debug --disable-build-servers -m:1 -nr:false
```

日志在 artifacts/video-rate-control：build-debug.log 首次成功但有 1 个 CS8604（passLog 可空）警告，补显式空值检查后 build-final.log 为 0 警告/0 错误；build-video.log 独立 Release 构建通过；恢复正常源码依赖后的 build-source-final.log 为 **12 工程 0 警告/0 错误**。复用的 E30 宿主 **235 个文件哈希未变**（host-before.json、host-hash-check.json），本轮没有重新发布或修改宿主。

新包为 video/TractorTool.VideoExport-0.4.0.zip，SHA256 为 `982C0AE5E0B01D5D887F91D6783B91FA6733861D9BD4BBD5E156E6D7228357E4`，API 1.5 / UI 1.3 需求不变；用户安装后显式退出重启。本轮不安装插件、不覆盖用户目录，保留已有 .vscode/launch.json 改动。

静态检查通过：10 份 Markdown 的 236 处本地链接/锚点、5 个插件 AXAML 的 XML、ZIP 9 项及版本/API/共享二进制排除，以及本轮 diff 空白检查（排除用户已有 launch.json 改动）。结果保存在 artifacts/video-rate-control/static-checks.json。

依据：[FFmpeg 两次编码选项](https://ffmpeg.org/ffmpeg.html#Video-Options)、[FFmpeg 编码器文档](https://ffmpeg.org/ffmpeg-codecs.html)、[NVENC 逐帧 multipass](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/)、[FFmpeg libvpx 码率映射源码](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/libvpxenc.c)及 [x264 filler 参数](https://github.com/mirror/x264/blob/master/common/base.c)。Apple Layout 页面仍仅返回 JavaScript 提示；未将未读取的正文当作验证依据。

没有运行应用、检查程序、FFmpeg 或 Windows UI。新布局实际视觉、帧率/首帧/音画同步、VBR/CBR 实际码率与大小、两遍结果、WebM/TS 播放、GPU 及回退都仍待用户测试；不以编译或参数检查关闭媒体/GPU 验收。
