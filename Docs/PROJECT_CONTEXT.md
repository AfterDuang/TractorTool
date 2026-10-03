# TractorTool 开发上下文

更新日期：2026-10-04。当前收敛 **M2 插件管理与剪贴板 1.1.1**，未进入 M3。本文只提供接续摘要；完整当前状态、运行入口和待验清单见 [DEVELOPMENT](DEVELOPMENT.md#current-status)。M1 过程及已完成轮次迁入 [项目历史](PROJECT_HISTORY.md)，默认不读，按 E 编号查阅。

## 1. 当前边界

- IDE 为 **VS Code**；依赖和平台基线见开发文档。用户已接手运行测试，未经新指示不启动应用/检查程序/UI 自动化，不读写真实剪贴板；允许必要构建、发布和静态核对。
- 子代理按用户要求显式 **GPT-6.1-Sol + high**；复审必须说明范围，不能用“没发现问题”代替运行证据。
- 本地 Git `main`，不配置远程或推送。用户允许从 M1 推进 M2，但逐项验收缺口继续保留。M3 包/进程、M4 视频/有限任务、M5 数据迁移尚未开始。
- 插件代码在实际 `AppRoot/Plugins/<Id>/<Version>`；数据默认 LocalAppData/TractorTool。测试用绝对路径 `--profile-root` 隔离，自定义指针不等于已实现迁移 UI。
- 公共 UI、宿主 ShellStyles、插件局部布局分工见 [架构约定](ARCHITECTURE.md#ui-style-ownership)。不为目录整齐搬迁工程，宿主不得引用具体插件。

## 2. 本阶段接续

- 根 slnx 包含全部 10 工程。日常源码引用公开 Contracts/UI；正式打包显式 HostSdkDir，只用发布 DLL 并 Rebuild，坏路径不回退。模式切换正常还原，开发 bin 不自动安装。
- 插件管理重启生效，开关表示待生效目标；更新快照在旧进程退出后、新 DLL 加载前制作。失败请求保留恢复信息，卸载保留用户数据，软件目录不可写不改装到数据目录。
- 剪贴板 1.1.1 已修复空选择标题绑定；左历史/右预览、复制原项、系统事件与窗口激活补刷，无轮询/自建历史库；图片按选择生成预览，离页取消解绑。资源界限见 [插件文档](Plugins/ClipboardHistory.md)。
- 最近 [E21](PROJECT_HISTORY.md#evidence-card-layout) 调整管理卡片：信息在左，开关/按钮在右共享高度，恢复操作可换行；Debug 全方案、Release 发布及独立 1.1.1 打包成功，234 个宿主文件未变。GPT-6.1-Sol/high 只读复审未发现确定缺陷；实际布局仍待验。
- 最新截图的空值绑定来自旧版路径；已只读核实 Debug 安装清单仍为 1.1.0。验收副本为 `artifacts/m2-card-layout/host` 与同级 1.1.1 ZIP，隔离命令见 [当前操作](DEVELOPMENT.md#workflow)。需安装升级并显式退出重启；只构建宿主不会更新插件，已装 1.1.1 不重装同版本。未替用户修改安装目录。
- 15 项管理、20 项剪贴板模型、7 项管理 UI 用例仅编译未运行；1.1.1 修复及 M2 完整系统行为仍待验。旧 M1 的 33/21 项成功不覆盖新代码。

## 3. 代码入口

| 工作 | 入口 |
| --- | --- |
| 管理事务与装载 | [PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[PluginFiles](../TractorTool.Core/Plugins/PluginFiles.cs)、[PluginHost](../TractorTool.Core/Plugins/PluginHost.cs) |
| 管理 UI / 剪贴板 | [PluginsView](../TractorTool/Views/PluginsView.axaml)、[PluginsViewModel](../TractorTool/ViewModels/PluginsViewModel.cs)、[ClipboardHistoryViewModel](../Plugins/TractorTool.Plugins.ClipboardHistory/ClipboardHistoryViewModel.cs) |
| 启动 / 导航 / 主题 | [App](../TractorTool/App.axaml.cs)、[MainViewModel](../TractorTool/ViewModels/MainViewModel.cs)、[SettingsViewModel](../TractorTool/ViewModels/SettingsViewModel.cs) |
| 插件规范与构建 | [PLUGIN_DEVELOPMENT](PLUGIN_DEVELOPMENT.md)、[Build-ClipboardPlugin](../scripts/Build-ClipboardPlugin.ps1) |

下一步：由用户按 [当前待验清单](DEVELOPMENT.md#workflow) 核验拖放边界、开关/重启、卡片、空选择日志、分栏/图片/原项粘贴及事件刷新；处理反馈后再讨论 M3。遇到失败保留日志和原状态，不删除真实数据。

后续替换本文摘要，当前结论写开发台账，详细过程归历史；不要逐轮向本文追加。
