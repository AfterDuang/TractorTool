# TractorTool 开发上下文

更新日期：2026-10-05。本页是接续快照；唯一当前状态台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

处于 M5 可靠性与发布阶段，M1–M4 功能已实现但运行验收没有全部完成。最新 [E60](PROJECT_HISTORY.md#evidence-github-dependencies) 按用户决定取消自有包源，直接接 GitHub Release；覆盖之前 E56/E58/E59 的索引、缓存、刷新和目录浏览方向，不再安排自有源部署或虚拟源测试。

- 外部依赖分为 **Packages**（已安装包及修复进度）与 **依赖来源**。默认 GitHub，额外来源只支持公开 GitHub Enterprise API 地址；pip 等安装能力接入后才开放，不做空入口。
- 插件在 `dependencies[].downloads` 声明 RID、仓库、精确标签/ZIP、SHA256、许可、工具路径和版本检查。宿主负责解析/首次确认/下载/事务/租约，无 FFmpeg 专属目录。公开 PackageReference / Contracts API 1.7 / UI API 1.4 ABI 不变。
- 视频源码清单升至 0.5.5，FFmpeg 9.0.2 改用 GyanD/codexffmpeg Release；独立 Test.PackageProbe 1.1.0 改用 ripgrep 14.1.1 真实 Release，只调用 --version，不搜索用户文件。
- 已装 receipt v2 可离线复用；旧 v1 等插件声明补资料，SHA/工具不符拒绝复用。损坏旧包保留可修复状态。相同身份的声明冲突要明确失败，不能暗中换工具。已装清理继续检查禁用/待重启/回退引用与使用租约。
- GitHub API 查询仅实际申请/修复时触发；共享查询、限时限量、精确附件/hash 校验，退出取消并等待；无轮询。来源配置在 DataRoot/Config/dependency-sources.json，旧源文件/缓存不再读取，真实用户数据不删除。

E60 Release 全 13 工程、隔离 Debug 宿主构建通过。子代理 Core/UI Review 问题已修，检查代码只编译未运行；官方 API/校验文件/ZIP 内容只读核对不代表真实安装通过。详细产物与最后证据见台账；E56/E58/E59 旧产物不包含本轮代码。

## 2. 必须保留的约定

- 用户接手测试，未经新指示不启动应用、检查程序、FFmpeg、UI 自动化，不接管真实配置、媒体、剪贴板或用户进程。可做必要构建/静态核对。新源码构建不自动替换已安装插件，插件更新需 ZIP 和显式退出重启。
- 保留 `.vscode/launch.json` 空白修改、`.gitignore` 的 Docs/ 规则，不纳入提交。Docs 已跟踪，可 `git add -u -- Docs`。使用子代理显式 gpt-6.1-sol / high；本地提交不等于远程推送授权。
- 框架精简，只承担公共连接、管理、生命周期和交互，业务留插件；不预建 pip/AI/Worker 平台，不静态引用插件。
- 唯一 MainWindow 承担启动/迁移/错误；初始化后关闭到托盘，切页/隐藏不取消任务。日志承载重要运行记录与任务进度，不记录导航；正常取消不记 Error。
- 迁移保持 profile/数据根/插件目录互斥；提交前保留旧指针，提交后失败暂停插件并待恢复；不迁移插件代码、不删除旧根。实例/维护保护和恢复记录不能省略。
- 分发用现有脚本，只写新目录；ZIP 不覆盖升级。安装器保留现有 Plugins、用户数据及管理状态，首次安装残留只允许同包恢复。Windows 维护互斥当前限同一会话，跨会话待验。
- VS Code + PowerShell 7.4+、SDK 10.0.401、Inno Setup 6.7.3；不更换 IDE，不启用 Native AOT/未经验证的裁剪，不自动配置 Git 远程或推送。

## 3. 代码入口与下一步

| 工作 | 入口 |
| --- | --- |
| GitHub 解析/来源设置 | [PackageSources](../TractorTool.Core/Packages/PackageSources.cs)、[GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs) |
| 安装/旧记录/租约 | [ExternalPackageService](../TractorTool.Core/Packages/ExternalPackageService.cs)、[PackageManagement](../TractorTool.Core/Packages/PackageManagement.cs)、[PackageDefinitions](../TractorTool.Core/Packages/PackageDefinitions.cs) |
| 设置与来源 UI | [SettingsViewModel](../TractorTool/ViewModels/SettingsViewModel.cs)、[ExternalToolsViewModel](../TractorTool/ViewModels/ExternalToolsViewModel.cs)、[DependencySourcesView](../TractorTool/Views/DependencySourcesView.axaml) |
| 测试插件 | [PackageProbePlugin](../tests/PackageProbePlugin/ProbePlugin.cs)、[GitHub 接入规范](PLUGIN_DEVELOPMENT.md#package-source-maintenance) |
| 分发与维护 | [脚本导航](../scripts/README.md)、[Build-Distribution](../scripts/Build-Distribution.ps1)、[安装脚本](../packaging/TractorTool.iss) |

下一步由用户验证 E60 的真实 GitHub 安装、来源操作及 Packages 信息。后续继续 M5 GitHub Release 流程，但 CI 尚未在 GitHub 执行，未配置远程/推送。E55 公共导航页面回收的长期真实切页/图片内存曲线仍待复测；此前单独授权的 10/10 隔离检查不扩展为常态测试权限。

未关闭：真实媒体/GPU/任务/退出/依赖/插件管理/剪贴板；两插件布局、设置折叠/标题栏、同窗启动；迁移跨卷/近满盘/ACL/取消及部分收尾恢复；安装保留/恢复和跨会话维护。旧取消调试输出触发不明，Job 终止仍有 20ms/最多 3 秒轮询并警告一次。逐项只看 [A01–A23](DEVELOPMENT.md#acceptance)，不能因 Review/编译通过自动关闭。
