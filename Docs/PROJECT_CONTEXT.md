# TractorTool 开发上下文

更新日期：2026-10-05。本页只保留接续所需信息；完整进度与未关闭项以 [开发验收台账](DEVELOPMENT.md#current-status) 为准。

## 1. 当前位置

**处于 M5 可靠性与发布阶段，M1–M4 的功能实现已转维护，运行验收尚未完成。** 标题栏布局最近改动为 [E53](PROJECT_HISTORY.md#evidence-titlebar-branding)：标题栏保留 32 DIP，图标缩至 16、软件名改为 13/常规字重，移除说明并收紧间距；只调整主窗口 AXAML，宿主 Debug 构建通过，真实 UI 未运行。E49 设置折叠及标题栏原生交互仍待验。最新插件改动 [E52](PROJECT_HISTORY.md#evidence-video-parameter-heading)：视频 0.5.4 标题/卡片对齐，独立构建及包静态核对通过。剪贴板 1.1.2 留白、视频布局及标题栏比例仍由用户验证。

最新修复为 [E55](PROJECT_HISTORY.md#evidence-navigation-memory-fix)：移除公共 PageHost 内部 FAFrame 的窗口事件持有路径，改用原生 Border/Transitions，保留同步取消及异常恢复。宿主/集成检查 Release 构建通过；本次用户单独授权的隔离导航检查 10/10 通过，动画开/关各 40 轮、320 个旧对象全部可回收。旧发布 UI DLL 对照仍保留 124/160 个对象。子代理 Review 结论及精确证据见 E55。真实发布版长期切页/图片内存趋势待用户重新打包复测，未操作当前进程、剪贴板或用户数据；不把这次授权延伸为常态自动测试。

[E51](PROJECT_HISTORY.md#evidence-docs-current-state) 仅整理文档、核对源码/现存产物元数据，未改变功能或验收结论。当前版本、构建证据、运行缺口及产物快照集中在 [开发台账](DEVELOPMENT.md#current-status)，不从历史包或文件存在推定新版已通过。

- 用户接手测试；未经新指示不运行应用、检查程序、FFmpeg 或 UI 自动化，不访问真实配置/媒体/剪贴板。开发可做必要构建和静态核对，文档任务只核对文档。
- 保留用户 `.vscode/launch.json` 空白修改及 `.gitignore` 新增的 Docs/ 规则，不纳入提交；Docs 已被 Git 跟踪，忽略规则不会自动移除它们。使用子代理时显式 `gpt-6.1-sol` / `high`。

## 2. 下次改动必须保留的边界

- 框架只负责公共连接、管理、生命周期和交互，业务留插件，不预建 Python/AI/Worker 平台。Contracts API 1.7 / UI API 1.4；保留原有公开方法/注册 ABI，PageHost 取消事件从 1.4 提供，依赖旧 FAFrame 事件者需按插件规范迁移。宿主不引用具体插件，正式插件独立使用发布 SDK，源码编译不自动更新已装 DLL。
- 启动只创建一次 MainWindow，实例恢复不重新初始化；准备完成前不创建业务写入服务/插件。进度事件不能迟到覆盖主页；启动中关闭取消并退出，进入业务界面后关闭到托盘。
- 迁移保持 profile/数据根/插件安装目录互斥，提交前失败保留旧指针，切换后收尾失败停止加载并在下次恢复；保留原根、恢复记录和插件代码。重试先核对目标归属/内容、专属 scratch 与空间，详见 [存储架构](ARCHITECTURE.md#storage)。
- 分发只写新目录，ZIP 解压不等于升级；安装器仅使用已校验且 installationProtocol=1 的 ZIP。升级保留整个已有 Plugins，卸载保留插件及数据，首次安装残留只能同包恢复。WindowsMaintenance 与安装器的命名门/存活信号须一致，维护不强杀任务、不轮询；当前互斥限同一 Windows 会话，不能删除启动保护却保留协议标记。
- PowerShell 7.4+、SDK 10.0.401、Inno Setup 6.7.3；编译器哈希/签名及安装器来源成对校验不能省略，依赖升级须复核许可。CI 只上传产物，未授予发布写权限。
- 未关闭：真实媒体/GPU、任务/退出、依赖/管理/剪贴板，以及同窗启动、迁移跨卷/近满盘/ACL/块内及部分收尾取消、安装保留/恢复、跨会话维护。旧取消调试输出触发不明；Windows Job 终止仍有 20ms/最多 3 秒轮询，启用时警告一次。逐项状态只看 [A01–A23](DEVELOPMENT.md#acceptance)，不因总体反馈或 Review 自动关闭。

## 3. 入口与下一步

| 工作 | 入口 |
| --- | --- |
| 设置与标题栏 | [SettingsView](../TractorTool/Views/SettingsView.axaml)、[ShellStyles](../TractorTool/Styles/ShellStyles.axaml)、[MainWindow](../TractorTool/Views/MainWindow.axaml) |
| 启动与维护保护 | [Program](../TractorTool/Program.cs)、[WindowsMaintenance](../TractorTool/Platform/WindowsMaintenance.cs)、[App](../TractorTool/App.axaml.cs) |
| 迁移与回退 | [DataRootMigration](../TractorTool.Core/Storage/DataRootMigration.cs)、[PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[DataMigrationViewModel](../TractorTool/ViewModels/DataMigrationViewModel.cs) |
| 分发与安装 | [Build-Distribution](../scripts/Build-Distribution.ps1)、[Build-Installer](../scripts/Build-Installer.ps1)、[安装脚本](../packaging/TractorTool.iss)、[Verify-Distribution](../scripts/Verify-Distribution.ps1)、[CI](../.github/workflows/distribution.yml) |

本地发布：VS Code“终端 → 运行任务 → TractorTool：本地发布”，可选择 ZIP 或安装包；菜单交互尚未实测。脚本职责见 [scripts/README](../scripts/README.md)，现存目录及元数据见 [本地产物](DEVELOPMENT.md#local-artifacts)，操作与回归方法见 [开发入口](DEVELOPMENT.md#workflow)。

下一步由用户重新发布后复测 **E55 导航修复** 的真实内存曲线及切页视觉效果；随后继续 **M5 GitHub Release 发布流程**。CI 已接入但未在 GitHub 执行，未配置远程、推送或实际发布；其余运行缺口保持待验。不得添加定时强制 GC 或反射清空第三方私有事件来掩盖资源持有。
