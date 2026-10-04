# TractorTool 开发上下文

更新日期：2026-10-04。用户允许复审后继续；**M3 已接入公共进程与外部包服务，真实媒体链路待接续，M2 运行验收仍未关闭**。当前状态和待验清单只见 [DEVELOPMENT](DEVELOPMENT.md#current-status)，历史按 E 编号查阅。

## 1. 当前边界

- IDE 为 **VS Code**，根 slnx 共 10 工程；宿主不静态引用具体插件。插件日常引用源码 SDK，正式打包使用显式 HostSdkDir，开发 bin 不自动安装。
- 用户接手运行测试；未经新指示不启动应用/检查程序/UI 自动化，不运行 FFmpeg、不读写真实剪贴板。允许必要编译、发布、静态核对。
- 子代理显式 **GPT-6.1-Sol + high**，静态复审不替代运行证据。本地 Git 保存实质改动，不配置远程或推送。
- 插件代码在实际 AppRoot/Plugins；数据在 DataRoot。数据根迁移 UI 属 M5；已有启动指针和测试 profile 不是迁移功能。M4 视频 UI/有限任务、M5 尚未开始。
- 样式分工仍为公共 UI / 宿主 ShellStyles / 插件局部布局。普通控件动效由 Motion/MotionStyles 与当前 FluentAvalonia 主题处理，不逐页面复制动画。

## 2. 接续要点

- M2 ZIP/开关/卸载/更新/回退重启生效，事务保留插件数据；卡片原位更新。剪贴板 1.1.1 左历史右预览、复制原项，系统事件与激活补刷，无轮询/自建历史库；未修改安装目录。
- 进程服务按插件生命周期持有；Core 处理参数/双流/取消/退出，Windows 创建时 Job 绑定和整组后代清理。非 Windows 适配器、实时进度尚无。17 项进程用例仅编译未运行。
- Contracts API **1.2** 接受 1.0/1.1，保留五参数上下文构造/解构及程序集身份；UI API 仍 1.2。Packages 服务只供插件查询/取租约，安装/取消/移除由宿主设置显式触发。
- 固定目录提供 **Gyan FFmpeg 9.0.2 essentials / win-x64**，内置版本 URL、SHA-256、工具路径与许可。下载校验、安全 ZIP、工具版本检查、receipt、原子提交；取得租约重新校验工具哈希，占用禁止移除。初始化不自动下载，支持但未安装的依赖仍允许插件页面加载。
- Packages/.work 临时事务带归属标记，启动只清理自身遗留，标记最后删除。设置页显示进度、错误、使用者、来源/许可、目录及文件大小。包服务统一退出取消；插件清理完成后回收遗漏租约。
- E24 复审修复单包异常隔离、网络超时分类、Acquire 状态竞争及清理标记顺序；构建及 18 项包事务替身用例已编译。实际网络安装/版本探测、取消/退出、ACL/磁盘故障及旧 DLL 兼容仍需用户验证。旧测试通过不覆盖此轮。
- artifacts/m3-packages 为构建/静态证据；其中固定 ZIP 只下载并核对哈希/条目/许可，没有运行或安装。旧 artifacts/m2-motion/host 是 E22 UI 副本，不含 M3；本轮未发布或替换已装插件。

## 3. 代码入口

| 工作 | 入口 |
| --- | --- |
| 包契约 / 事务 / 目录 | [IExternalPackageService](../TractorTool.Contracts/Packages/IExternalPackageService.cs)、[ExternalPackageService](../TractorTool.Core/Packages/ExternalPackageService.cs)、[PackageCatalog](../TractorTool.Core/Packages/PackageCatalog.cs)、[PackageScope](../TractorTool.Core/Packages/PackageScope.cs) |
| 设置入口 / 包用例 | [ExternalToolsViewModel](../TractorTool/ViewModels/ExternalToolsViewModel.cs)、[SettingsView](../TractorTool/Views/SettingsView.axaml)、[PackageChecks](../tests/TractorTool.Checks/PackageChecks.cs) |
| 进程 / 平台 / 用例 | [IProcessRunner](../TractorTool.Contracts/Processes/IProcessRunner.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs)、[ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) |
| 组装 / 插件 / 文档 | [App](../TractorTool/App.axaml.cs)、[PluginHost](../TractorTool.Core/Plugins/PluginHost.cs)、[插件规范](PLUGIN_DEVELOPMENT.md)、[视频插件设计](Plugins/VideoExport.md) |

下一步接视频插件的真实 ffprobe 探测、单次 FFmpeg 执行与进度，复用包/进程服务；业务留在插件，不加入 Worker/IPC 平台。首帧替换、VFR、同步、多格式和实际 GPU 编码仍必须取得真实输出证据，不能以安装成功或列出编码器替代。未经新指示继续只做必要构建与静态核对，运行由用户负责。

后续替换本摘要；详细过程归 [E24](PROJECT_HISTORY.md#evidence-package-service)，不要逐轮追加。
