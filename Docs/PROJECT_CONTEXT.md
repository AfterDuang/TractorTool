# TractorTool 开发上下文

更新日期：2026-10-04。用户允许复审后继续；**M3 已接入公共进程与外部包服务，真实媒体链路待接续，M2 运行验收仍未关闭**。当前状态和待验清单只见 [DEVELOPMENT](DEVELOPMENT.md#current-status)，历史按 E 编号查阅。

## 1. 当前边界

- IDE 为 **VS Code**，根 slnx 共 10 工程；宿主不静态引用具体插件。插件日常引用源码 SDK，正式打包使用显式 HostSdkDir，开发 bin 不自动安装。
- 用户接手运行测试；未经新指示不启动应用/检查程序/UI 自动化，不运行 FFmpeg、不读写真实剪贴板。允许必要编译、发布、静态核对。
- 子代理显式 **GPT-6.1-Sol + high**，静态复审不替代运行证据。本地 Git 保存实质改动，不配置远程或推送。
- 插件代码在实际 AppRoot/Plugins；数据在 DataRoot。数据根迁移 UI 属 M5；已有启动指针和测试 profile 不是迁移功能。M4 视频 UI/有限任务、M5 尚未开始。
- 样式分工仍为公共 UI / 宿主 ShellStyles / 插件局部布局。普通控件动效由 Motion/MotionStyles 与当前 FluentAvalonia 主题处理，不逐页面复制动画。
- 最新确认已落代码：插件申请依赖、宿主从统一维护的可信源解析；已有包复用，缺失首次确认下载。卸载插件保留包，独立设置页清理未引用包；软件/插件设置分类导航。保持框架干净，替换旧流程时同时移除旧命令/绑定，不能两套入口并存。

## 2. 接续要点

- M2 ZIP/开关/卸载/更新/回退重启生效，事务保留插件数据；卡片原位更新。剪贴板 1.1.1 左历史右预览、复制原项，系统事件与激活补刷，无轮询/自建历史库；未修改安装目录。
- 进程服务按插件生命周期持有；Core 处理参数/双流/取消/退出，Windows 创建时 Job 绑定和整组后代清理。非 Windows 适配器、实时进度尚无。17 项进程用例仅编译未运行。
- Contracts API **1.3** 接受 1.0/1.1/1.2，新增 `Packages.RequestAsync`，原 Acquire 保持只获取已装版本；五参数上下文和程序集身份保留，UI API 仍 1.2。同包确认/下载合并，申请预留覆盖确认到取得租约，单个等待取消不影响其他插件。
- 软件目录 `PackageSources/default.json` 是随宿主分发的可信索引，源码在 Core/Packages/default.json，当前提供 **Gyan FFmpeg 9.0.2 essentials / win-x64**；不再写死 C# 目录。尚无远程索引刷新/自定义源。receipt v2 保存独立安装定义；源下架不丢已装包，修复使用当前可信源。旧 v1 可读且对应源存在时升级，升级写失败只警告，不禁用有效工具。
- `PluginPackageReferences` 从持久清单/管理日志重建引用，含禁用、失败、待激活、可回退版本。提交前暂停清理、管理锁内发布新引用；不可读即禁止清理。Core 同时检查引用/申请预留/租约/事务。卸载重启生效前保留旧引用，包本身不联动删除。
- 设置分为通用/数据/外部依赖/关于与实际注册的插件页；宽窗口分栏、窄窗口分类选择。嵌套 PageHost 复用动画，桥接插件取消离页；旧整页布局、返回设置命令与目录直出卡片已删除。
- E25 GPT-6.1-Sol/high 分别复审 Core/UI 并修复边界问题；Debug 全方案编译通过，2 个 NU1900 为漏洞数据源不可达。包用例 Windows 共 26 项与设置导航回归只编译未运行。网络安装、确认/清理、分类/窄窗口、旧 DLL、ACL/磁盘故障仍需用户验证。
- 构建证据 `artifacts/m3-dependency-flow/build-debug.log`；本轮未运行应用/检查程序/FFmpeg、未发布或替换已装插件。E22 发布副本仍不含 M3。临时包事务的精确归属标记和安全路径策略沿用 E24。

## 3. 代码入口

| 工作 | 入口 |
| --- | --- |
| 包契约 / 事务 / 引用 | [IExternalPackageService](../TractorTool.Contracts/Packages/IExternalPackageService.cs)、[ExternalPackageService](../TractorTool.Core/Packages/ExternalPackageService.cs)、[PackageManagement](../TractorTool.Core/Packages/PackageManagement.cs)、[PluginPackageReferences](../TractorTool.Core/Plugins/PluginPackageReferences.cs) |
| 设置 / 确认 / 用例 | [ExternalToolsViewModel](../TractorTool/ViewModels/ExternalToolsViewModel.cs)、[SettingsView](../TractorTool/Views/SettingsView.axaml)、[HostDialogs](../TractorTool/Services/HostDialogs.cs)、[PackageChecks](../tests/TractorTool.Checks/PackageChecks.cs) |
| 进程 / 平台 / 用例 | [IProcessRunner](../TractorTool.Contracts/Processes/IProcessRunner.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs)、[ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) |
| 组装 / 插件 / 文档 | [App](../TractorTool/App.axaml.cs)、[PluginHost](../TractorTool.Core/Plugins/PluginHost.cs)、[插件规范](PLUGIN_DEVELOPMENT.md)、[视频插件设计](Plugins/VideoExport.md) |

下一步接视频插件的真实 ffprobe 探测、FFmpeg 执行与进度；业务留在插件，不加入 Worker/IPC 平台。首帧替换、VFR、同步、多格式和实际 GPU 编码仍必须取得真实输出证据。未经新指示继续只做必要构建与静态核对，运行由用户负责。

后续替换本摘要；详细过程归 [E25](PROJECT_HISTORY.md#evidence-dependency-flow)，不要逐轮追加。
