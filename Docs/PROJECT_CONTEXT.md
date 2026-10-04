# TractorTool 开发上下文

更新日期：2026-10-04。用户允许复审后继续；**M3 公共进程服务已开始，M2 运行验收仍待完成**。本文只提供接续摘要；当前状态、运行入口和待验清单见 [DEVELOPMENT](DEVELOPMENT.md#current-status)。历史默认不读，按 E 编号查阅。

## 1. 当前边界

- IDE 为 **VS Code**；依赖和平台基线见开发文档。用户已接手运行测试，未经新指示不启动应用/检查程序/UI 自动化，不读写真实剪贴板；允许必要构建、发布和静态核对。
- 子代理按用户要求显式 **GPT-6.1-Sol + high**；复审必须说明范围，不能用“没发现问题”代替运行证据。
- 本地 Git `main`，不配置远程或推送。M3 当前只有进程服务增量；包下载/租约/管理页、真实媒体链路待接续，M4 视频 UI/有限任务、M5 数据迁移未开始。开发推进不等于运行验收通过。
- 插件代码在实际 `AppRoot/Plugins/<Id>/<Version>`；数据默认 LocalAppData/TractorTool。测试用绝对路径 `--profile-root` 隔离，自定义指针不等于已实现迁移 UI。
- 公共 UI、宿主 ShellStyles、插件局部布局分工见 [架构约定](ARCHITECTURE.md#ui-style-ownership)。不为目录整齐搬迁工程，宿主不得引用具体插件。

## 2. 本阶段接续

- 根 slnx 包含全部 10 工程。日常源码引用公开 Contracts/UI；正式打包显式 HostSdkDir，只用发布 DLL 并 Rebuild，坏路径不回退。模式切换正常还原，开发 bin 不自动安装。
- 插件管理重启生效，开关表示待生效目标；更新快照在旧进程退出后、新 DLL 加载前制作。失败请求保留恢复信息，卸载保留用户数据，软件目录不可写不改装到数据目录。
- 剪贴板 1.1.1 已修复空选择标题绑定；左历史/右预览、复制原项、系统事件与窗口激活补刷，无轮询/自建历史库；图片按选择生成预览，离页取消解绑。资源界限见 [插件文档](Plugins/ClipboardHistory.md)。
- E22 的 Motion/MotionStyles 统一普通控件/模板动效与公共颜色过渡；管理卡片原位更新保持开关实例。第三方已开始的短时过渡不保证即时取消。[E23](PROJECT_HISTORY.md#evidence-process-service) 两位高强度子代理复审动效、管理事务和剪贴板生命周期，未发现确定阻塞，运行缺口保留。
- M3 新增 `PluginContext.Processes`，Contracts API 1.1 接受旧 1.0，保留旧构造/解构及程序集身份。Core 管理输出/超时/关停，Windows 适配用创建时 Job 绑定处理父先退出及后代清理；非 Windows 明确不支持，包 dependencies 仍拒绝。内部清理取消不得误报用户取消，失败必须报告。
- 本轮 Debug 全方案 0 警告/0 错误；新增 17 项进程检查（4 替身、13 Windows 子进程）及 1 项 API 兼容检查，仅编译未运行；旧插件实际兼容和原生 Job 行为待验。M2 的 15/20/8/2 项管理/剪贴板/管理 UI/动效用例也仍未运行。
- `artifacts/m3-process` 保存本轮构建/静态日志，未发布宿主；`artifacts/m2-motion/host` 是旧 E22 UI 副本，不含进程服务。未修改已安装插件或数据，剪贴板仍为 1.1.1，无需重打同版本包。

## 3. 代码入口

| 工作 | 入口 |
| --- | --- |
| 管理事务与装载 | [PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[PluginFiles](../TractorTool.Core/Plugins/PluginFiles.cs)、[PluginHost](../TractorTool.Core/Plugins/PluginHost.cs) |
| 进程服务 / 平台 / 用例 | [公开契约](../TractorTool.Contracts/Processes/IProcessRunner.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs)、[ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) |
| 管理 UI / 剪贴板 | [PluginsView](../TractorTool/Views/PluginsView.axaml)、[PluginsViewModel](../TractorTool/ViewModels/PluginsViewModel.cs)、[ClipboardHistoryViewModel](../Plugins/TractorTool.Plugins.ClipboardHistory/ClipboardHistoryViewModel.cs) |
| 启动 / 导航 / 主题 | [App](../TractorTool/App.axaml.cs)、[MainViewModel](../TractorTool/ViewModels/MainViewModel.cs)、[SettingsViewModel](../TractorTool/ViewModels/SettingsViewModel.cs)、[Motion](../TractorTool.UI/Motion.cs)、[MotionStyles](../TractorTool.UI/Styles/MotionStyles.axaml) |
| 插件规范与构建 | [PLUGIN_DEVELOPMENT](PLUGIN_DEVELOPMENT.md)、[Build-ClipboardPlugin](../scripts/Build-ClipboardPlugin.ps1) |

下一步开发 M3 固定版本外部包服务与设置管理入口，再接 ffprobe/FFmpeg 的真实消费方和进度。运行仍由用户按 [当前清单](DEVELOPMENT.md#workflow) 验证 M2、进程夹具和旧 SDK 兼容；不据编译结果越过媒体/GPU 验收。遇到失败保留日志和原状态，不删除真实数据。

后续替换本文摘要，当前结论写开发台账，详细过程归历史；不要逐轮向本文追加。
