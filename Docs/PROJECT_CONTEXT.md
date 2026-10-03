# TractorTool 开发上下文

更新日期：2026-10-04。最新将设置等宿主整页滚动条移到内容区右侧，正文留白和插件页原布局保留；默认 Debug 解决方案构建 0 警告/0 错误，之前占用不再阻塞本次输出刷新。未运行应用或测试、未重新发布。当前仍是 M1，未验收完成，M2 未开始。

本文是接续摘要；完整代码状态、命令、证据和结果只维护在 [DEVELOPMENT.md](DEVELOPMENT.md#current-status)，文档职责见 [README.md](README.md)，工作约定见唯一根 [AGENTS.md](../AGENTS.md)。

## 1. 用户决定与当前边界

- Windows 优先，.NET 10、Avalonia、MVVM。开发 IDE 为 **VS Code**，保留现有 .vscode 配置，不转向 Visual Studio/VS MCP。
- 用户已接手运行测试；未经新指示不启动应用、核心/集成/Headless 检查或 UI 自动化。必要源码核对和构建须单独说明范围。
- 子代理必须显式使用 GPT-6.1-Sol + high（高）。用户截图确认此前 UI Review 实际显示“轻度”，不能把未传 reasoning_effort 当作已验证的高深度审查。
- 只完成 M1，不自动进入后续阶段。M0 是文档整理，公共 UI 属于 M1；演示插件不能代替两个真实首版插件。
- 左下角仅日志/插件/设置，显式退出在托盘和设置的关于区域；普通关闭隐藏到托盘。外观选择即生效并自动保存，无常驻保存按钮或说明，写入失败时提供重试。深浅色切换需柔和过渡，遵循动画开关。
- 左侧顶部固定主页并作为启动默认页，显示欢迎区和真实已安装插件卡片；卡片复用工具导航，不增加广告、资讯或商城。
- 设置等整页滚动页面的滚动条靠内容区最右侧，正文在容器内留白并与标题对齐。
- 本轮确认按范围维护文档：宿主架构、通用插件规范、每个插件独立业务文档；不再把视频/剪贴板内部方案写进软件架构。

## 2. 继续工作需要知道的代码事实

- 保留 TractorTool 应用目录及 Contracts/Core/UI/tests。Avalonia 12.1.2、MVVM 8.4.2、FluentAvaloniaUI 3.1.0；没有初始化 Git。
- M1 已有路径、设置/备份、日志、托盘/实例恢复、清单与独立加载、导航和设置注册、常驻服务。公共 UI 已接入窗口/紧凑导航/共享设置行/图标/页面过渡。
- 最近代码修正包括标题栏非客户区角色、导航提交与失败处理、路径边界和恢复规范化、有界退出与插件资源释放顺序、产品版本同源和多功能工具图标。
- 外观由 SettingsViewModel 在 UI 线程即时应用；单个保存任务串行写入不可变快照，合并期间的最新选择，退出等待整个队列，最后保存失败时退出额外重试一次。忽略临时空选择、保护同步主题通知重入，应用失败与写盘失败分别提示；沿用 JSON 原子替换/备份，没有新依赖。
- SaveError 是保存错误的唯一状态，HasSaveError 为派生属性；错误面板无 DataContext 时默认隐藏，真实写盘失败仍有重试。已删除旧 ViewLocator、NavigationItem.IsSelected 及无消费者的两个选中属性，宿主仍显式创建页面、插件仍用注册工厂。
- 颜色过渡使用原生 BrushTransition，220 ms；Window 的 themeTransitions class 绑定动画开关，撤销样式会取消在途过渡。主要宿主表面与公共卡片/设置行参与；未全面覆盖第三方内部控件和系统按钮，不宣称整窗所有像素都渐变。
- MainViewModel 缓存整次初始化任务；插件页加载/空安装/目录错误/搜索无结果分别呈现。彩色导航及主页卡片与单色操作符号分工，宿主统一缓存图片，在窗口关闭后释放，不在 Detach/隐藏时释放。
- 导航侧栏已从 88 收紧为 72 逻辑单位，图标 28、按钮背景最小高度 64；标签单行省略，保留完整悬停提示。只改宿主模板，未缩小设置行或主页卡片。
- 主页/设置/插件/错误页整页 MaxWidth 已移除，复用共享 pageScroll/pageBody 与页头左对齐并随视口伸缩；局部空状态文字限宽和卡片换行保留，日志使用自己的文本滚动区域。
- HasPageScroll 仅识别上述四个宿主整页滚动 View，通过 edgeScroll class 将 PagePresenter 的右侧 28 外边距移到正文内部（8 + 28 = 36）；滚动条靠右，正文位置保持。日志/外部插件沿用默认边距，公共 UI SDK 未改。
- 最近绑定修正：插件页字符串 `!!` 改为两个 bool 属性及通知，caption 覆盖 SelectableTextBlock。ApplicationVersion 在源码和默认 DLL 中存在；VS Code 预览报缺失推断与旧程序集/元数据有关，刷新后结果尚未确认。
- 正式剪贴板/视频插件、插件管理事务、包/进程服务、有限任务队列、数据迁移、CI/安装器尚未实现。具体状态见开发文档，不据设计创建大批空接口。
- 插件代码固定在实际 AppRoot/Plugins；数据默认 LocalAppData/TractorTool，测试用绝对路径 profile 隔离；数据迁移不改变代码位置。

## 3. 入口

| 要处理什么 | 先看哪里 |
| --- | --- |
| 宿主装载、生命周期、路径与公共 UI | [软件架构](ARCHITECTURE.md) |
| 开发插件、SDK/清单/打包 | [插件开发规范](PLUGIN_DEVELOPMENT.md) |
| 真实系统剪贴板 / 视频业务 | [剪贴板插件](Plugins/ClipboardHistory.md)、[视频插件](Plugins/VideoExport.md) |
| 启动组装、托盘与退出 | [App.axaml.cs](../TractorTool/App.axaml.cs)、[Program.cs](../TractorTool/Program.cs) |
| 标题栏与导航 | [MainWindow.axaml](../TractorTool/Views/MainWindow.axaml)、[MainViewModel.cs](../TractorTool/ViewModels/MainViewModel.cs)、[公共控件](../TractorTool.UI/Controls) |
| 欢迎区与已安装插件卡片 | [HomeView.axaml](../TractorTool/Views/HomeView.axaml)、[HomeViewModel.cs](../TractorTool/ViewModels/HomeViewModel.cs)；数据复用插件页的安装记录，不能提前实例化页面 |
| 设置、版本与主题 | [SettingsViewModel.cs](../TractorTool/ViewModels/SettingsViewModel.cs)、[AppInfo.cs](../TractorTool/Services/AppInfo.cs)、[共享样式](../TractorTool.UI/Styles/SharedStyles.axaml) |
| 图片加载与公共图标 | [NavigationIconStore.cs](../TractorTool/Services/NavigationIconStore.cs)、[ToolIcon.cs](../TractorTool.UI/Controls/ToolIcon.cs)、[素材与许可来源](../Artwork/README.md) |
| 核心存储和装载实现 | [Storage](../TractorTool.Core/Storage)、[Plugins](../TractorTool.Core/Plugins) |
| 发布、隔离启动与检查命令 | [开发操作](DEVELOPMENT.md#workflow)、[Publish-Host.ps1](../scripts/Publish-Host.ps1)、[Verify-M1.ps1](../scripts/Verify-M1.ps1) |

## 4. 最新证据与下一步

已用现有 FluentAvalonia 接入 Microsoft Fluent Emoji 的 3D PNG，许可随发布；没有新增运行库或复制 Watt 素材。UI API 1.2 的可选 IconPath 指向插件版本包内 PNG，旧签名/枚举值/程序集身份保留，接受旧 1.0/1.1 声明；缺图和解码失败回退符号。公共设计见 [图标设计](ARCHITECTURE.md#icon-design)，插件字段及限制见 [开发规范](PLUGIN_DEVELOPMENT.md#5-统一-ui-与设置)。

最新 E15 已成功构建到默认 Debug，0 警告/0 错误；E14 的输出占用不再阻塞本次构建。`settings-cleanup-build/host` 仍为 E14，带独立插件的 theme-transition-ready 仍为 E13，均没有 E15 布局。新旧回归均未运行，历史 33/21 项通过不覆盖新代码。详见 [当前证据](DEVELOPMENT.md#current-status)、[E15](DEVELOPMENT.md#evidence-scroll-edge)。

用户下一步用 E15 默认 Debug 隔离命令检查页面滚动条靠右、正文对齐、窄窗及切回日志/插件页；刷新预览核验 E14 错误区域，再继续之前的主题/持久化/插件/托盘验收。默认 VS Code 启动没有隔离参数。完整发布回归按需运行 Verify-M1.ps1；未经新指示不代用户运行，没有新运行证据前不将 M1、A01/A02 标为完成或进入 M2。

实质性变更后先更新开发台账，再替换本文的交接摘要；需求、设计和插件规则分别更新对应文档，不在本文继续追加逐轮流水账。
