# TractorTool 开发上下文

更新日期：2026-10-04。当前收敛 M2：按用户截图增加 ZIP 拖入安装、启停滑块，并重做剪贴板分栏预览、原项复制与事件刷新（插件 1.1.0）。构建/打包完成，新功能运行验收待用户执行；未进入 M3。

本文只保留交接摘要；完整状态、命令、证据和限制见 [DEVELOPMENT.md](DEVELOPMENT.md#current-status) / [E17](DEVELOPMENT.md#evidence-clipboard-ux)。文档分工见 [README.md](README.md)，工作约定见唯一根 [AGENTS.md](../AGENTS.md)。

## 1. 当前约定

- Windows 优先，.NET 10、Avalonia、MVVM；开发 IDE 为 **VS Code**，保留现有 `.vscode` 工作流，不转向 Visual Studio。
- 用户接手运行测试；未经新指示不启动应用、核心/集成/Headless 检查或 UI 自动化，不读取真实剪贴板。构建、发布和静态核对与运行证据分别记录。
- 子代理显式指定 **GPT-6.1-Sol + high**，本轮复审了实现与回归源码，修复滑块拖动不触发命令、双击空白误复制等问题。没有发现新确定问题不等于全仓库无缺陷。
- 本地 Git 分支 `main`，M1 基线 `fc18c20`；后续变更形成独立本地提交，查看 `git log` 获取最新版本。不自动建立远程或推送；artifacts/bin/obj 不入库。
- 插件代码固定在实际 `AppRoot/Plugins/<Id>/<Version>`，不是源码 `Plugins` 或终端当前目录；数据默认 LocalAppData/TractorTool，使用绝对路径 `--profile-root` 隔离测试。
- 数据根指针已有，自定义目录的完整 UI/迁移仍在 M5；修改数据目录不迁移插件代码。M3 外部包与进程服务、M4 视频和有限任务尚未开始。
- 用户允许进入 M2 的反馈不补造 M1 的逐项测试证据；本轮运行验收未完成，不直接推进 M3。

## 2. 代码快照

- 保留原 TractorTool 应用目录及 Contracts/Core/UI/tests；Avalonia 12.1.2、MVVM 8.4.2、FluentAvaloniaUI 3.1.0 未升级。公开 Contracts API 1.0、UI API 1.2（接受旧 1.0/1.1），本轮未改变 SDK。
- M1 已有路径、JSON 原子保存/备份、日志、实例恢复/托盘、独立 DLL 加载、页面/设置注册、常驻活动；默认主页、紧凑导航、共享样式/图标/设置行/动效与整页靠右滚动条沿用。
- 外观选择即生效并自动保存；正常无保存按钮/说明，失败才出现重试区域，无 DataContext 默认隐藏。保存串行合并快照并参与退出等待，220 ms 主要表面颜色过渡遵循动画开关。
- M2 管理状态位于 `PluginRoot/.management/state.json`；ZIP 暂存及活动版本选择和启用状态与用户外观配置分开。目录锁持有到进程终止；另有自定义数据根锁，避免其他安装/profile 并发写同一数据。
- 安装/更新/启停/卸载/回退均重启生效，不覆盖已加载 DLL。更新前在新进程中备份旧插件最后保存的数据；快照位于 `DataRoot/Data/.plugin-backups`，回退保留新版数据，卸载保留用户数据。
- 拖入只接受单个本地 ZIP，复用选择文件的确认/管理流程。启停用 IsCheckedChanged 覆盖点击及拖动；开关表示待生效目标，未执行的启停可反向取消，失败复位。
- 失败请求保留恢复日志；失败安装/更新允许取消后重试。共享 DLL 副本、非法 ZIP 路径、同版本覆盖、ID 大小写变更和跨数据根应用变更均拒绝。设计及限制见 [公共架构](ARCHITECTURE.md#24-安装更新禁用和卸载)。
- 剪贴板插件位于源码 `Plugins/TractorTool.Plugins.ClipboardHistory`，只正式引用已发布 Contracts/UI。1.1.0 提供左历史/右预览、文本/图片、系统原项复制、事件合并刷新、焦点补刷及选择保留；离页取消/解绑，不建历史库或记录正文。格式与资源界限归 [剪贴板文档](Plugins/ClipboardHistory.md)。
- 核心 15 项管理检查沿用；剪贴板模型扩展为 19 项，管理 UI 新增 7 项，只编译未执行。替身不能证明 Windows API，VM 安装检查不能证明原生 Drop。历史 33/21 项成功不覆盖本轮代码。

## 3. 关键入口

| 工作 | 入口 |
| --- | --- |
| 插件装载与管理事务 | [PluginHost](../TractorTool.Core/Plugins/PluginHost.cs)、[PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[PluginFiles](../TractorTool.Core/Plugins/PluginFiles.cs) |
| 启动/锁/托盘/退出 | [App.axaml.cs](../TractorTool/App.axaml.cs)、[Program.cs](../TractorTool/Program.cs) |
| 管理页面、排队与错误 | [PluginsViewModel](../TractorTool/ViewModels/PluginsViewModel.cs)、[PluginsView](../TractorTool/Views/PluginsView.axaml) |
| 导航/主页/主题 | [MainViewModel](../TractorTool/ViewModels/MainViewModel.cs)、[HomeViewModel](../TractorTool/ViewModels/HomeViewModel.cs)、[SettingsViewModel](../TractorTool/ViewModels/SettingsViewModel.cs)、[共享样式](../TractorTool.UI/Styles/SharedStyles.axaml) |
| 通用 SDK 和插件交付 | [插件开发规范](PLUGIN_DEVELOPMENT.md)、[PluginUi](../TractorTool.UI/PluginUi.cs)、[Build-ClipboardPlugin.ps1](../scripts/Build-ClipboardPlugin.ps1) |
| 正式插件业务 | [剪贴板历史](Plugins/ClipboardHistory.md)、[视频导出](Plugins/VideoExport.md) |
| 回归源码 | [管理检查](../tests/TractorTool.Checks/PluginManagementChecks.cs)、[剪贴板模型检查](../tests/TractorTool.ClipboardChecks/Program.cs) |

## 4. 接续

最新隔离发布为 `artifacts/m2-clipboard-final/host`，未预装插件；将同级 `clipboard/TractorTool.ClipboardHistory-1.1.0.zip` 拖入安装后显式退出重启。先发布宿主、再独立构建插件，234 个宿主文件哈希不变；未实际加载。最终 Release/独立插件与两个检查工程均成功；最后默认 Debug 被用户进程 PID 49588 占用 EXE，未终止它，退出调试/托盘后再构建。旧 m2-ready 和调试安装中的 1.0.0 需要更新 ZIP，单独重建宿主不会更新插件 DLL。

下一步按 [E17 的隔离命令和待验清单](DEVELOPMENT.md#evidence-clipboard-ux) 由用户验证原生拖放、启停拖动、分栏预览、再次复制后粘贴、事件与焦点刷新及管理重启闭环；失败时定位日志和原状态，不删除真实数据。运行测试仍待用户指示，本阶段收敛后再讨论 M3。

实质变更先更新开发台账，再替换本文摘要；需求、公共设计、接入规范和业务方案分别维护，本文不追加完整流水账。
