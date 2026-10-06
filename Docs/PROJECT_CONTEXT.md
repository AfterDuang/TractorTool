# TractorTool 开发上下文

更新日期：2026-10-06。本页是接续快照；唯一当前状态台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

处于 M5 发布准备阶段，最新 [E63](PROJECT_HISTORY.md#evidence-plugin-tray) 按用户授权实现插件托盘菜单扩展。子代理复审及构建完成，新功能运行由用户接手；不开发悬浮球业务。[E62](PROJECT_HISTORY.md#evidence-user-local-validation) 用户已反馈依赖安装/拒绝重试/修复清理、视频/GPU、插件升级与数据迁移测试暂未发现问题，不重复要求这些常规测试，也不外推未报告的故障/硬件细项。E60 GitHub 依赖决定及 E61 Release 草稿流程保持有效。

后续 [E64](PROJECT_HISTORY.md#evidence-quiet-probe) 按用户反馈清理测试插件刷屏：Test.Independent 1.0.1 删除 200ms 定时器及 tick 日志，常驻服务只等待停止信号，托盘菜单不变。仅独立构建插件和受影响集成检查，0 警告/0 错误，检查未执行；Verify-PluginHost 改读输出清单版本，语法通过。新 ZIP/更新步骤见 [本地产物](DEVELOPMENT.md#local-artifacts)，未替用户安装或运行；E63 旧包仍需手工升级，不重打宿主。

- 新可选 UI API 1.5：`IPluginTrayModule.RegisterTrayMenu` → 每插件一级子菜单 → 普通/勾选动作与可更新状态句柄。宿主保留显示/退出；显式推送状态，无轮询；不扩展任意菜单树或悬浮窗平台。
- 菜单注册资源归 PluginHost 生命周期持有，Core 只接 `IAsyncDisposable`；切页/隐藏保留，失败回滚、停止封闭入口并取消/解绑，等待动作结束再释放插件。回调在 UI 线程开始，插件自行异步处理耗时工作，有限任务复用现有 Tasks；意外取消记 Error，正常停止取消记 Info。

- 外部依赖分为 **Packages**（已安装包及修复进度）与 **依赖来源**。默认 GitHub，额外来源只支持公开 GitHub Enterprise API 地址；pip 等安装能力接入后才开放，不做空入口。
- 插件在 `dependencies[].downloads` 声明 RID、仓库、精确标签/ZIP、SHA256、许可、工具路径和版本检查。宿主负责解析/首次确认/下载/事务/租约，无 FFmpeg 专属目录。PackageReference / Contracts API 1.7 不变；UI 1.5 保留 UI 1.0–1.4 的旧接口 ABI，未知更高版本拒绝加载。
- 视频源码清单升至 0.5.5，FFmpeg 9.0.2 改用 GyanD/codexffmpeg Release；独立 Test.PackageProbe 1.1.0 改用 ripgrep 14.1.1 真实 Release，只调用 --version，不搜索用户文件。
- 已装 receipt v2 可离线复用；旧 v1 等插件声明补资料，SHA/工具不符拒绝复用。损坏旧包保留可修复状态。相同身份的声明冲突要明确失败，不能暗中换工具。已装清理继续检查禁用/待重启/回退引用与使用租约。
- GitHub API 查询仅实际申请/修复时触发；共享查询、限时限量、精确附件/hash 校验，退出取消并等待；无轮询。来源配置在 DataRoot/Config/dependency-sources.json，旧源文件/缓存不再读取，真实用户数据不删除。
- E61 保留损坏/缺失 receipt 的目录身份及可读 hash，匹配声明到达后恢复为可管理的 Failed 状态，仍经用户同意才能修复；旧 hash 不符拒绝替换。Windows 大小写别名申请被拒绝，引用状态不完整时禁清理。来源保存完成不会抹掉用户后续输入。
- 现有 distribution.yml 增加 v* 标签 push → 独立写权限 job → 同次构建附件校验 → Release 草稿。校验 tag/version/commit/dirty 及安装器，不覆盖已有 Release；公开发布由维护者确认。上传失败/中断可能留下部分草稿，人工核对删除本次失败草稿后仅重跑发布 job。

E63 全 13 工程 Release 隔离构建通过，0 警告/0 错误。子代理 gpt-6.1-sol / high 指出意外取消分类问题，修复后再审无剩余确定问题。托盘状态/回调/资源及独立 DLL 检查只编译未运行。已生成新自包含宿主与独立编译的 Test.Independent 菜单夹具，234 个原宿主文件哈希不变；路径及用户启动命令只在 [本地产物](DEVELOPMENT.md#local-artifacts) 维护。未启动应用/检查程序、替换日常安装或打正式分发包；CI/草稿未执行。

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
| 托盘公开接口与实现 | [PluginTray](../TractorTool.UI/PluginTray.cs)、[TrayService](../TractorTool/Services/TrayService.cs)、[PluginTrayMenu](../TractorTool/Services/PluginTrayMenu.cs)、[插件接入](PLUGIN_DEVELOPMENT.md#tray-menu) |
| GitHub 解析/来源设置 | [PackageSources](../TractorTool.Core/Packages/PackageSources.cs)、[GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs) |
| 安装/旧记录/租约 | [ExternalPackageService](../TractorTool.Core/Packages/ExternalPackageService.cs)、[PackageManagement](../TractorTool.Core/Packages/PackageManagement.cs)、[PackageDefinitions](../TractorTool.Core/Packages/PackageDefinitions.cs) |
| 设置与来源 UI | [SettingsViewModel](../TractorTool/ViewModels/SettingsViewModel.cs)、[ExternalToolsViewModel](../TractorTool/ViewModels/ExternalToolsViewModel.cs)、[DependencySourcesView](../TractorTool/Views/DependencySourcesView.axaml) |
| 测试插件 | [PackageProbePlugin](../tests/PackageProbePlugin/ProbePlugin.cs)、[GitHub 接入规范](PLUGIN_DEVELOPMENT.md#package-source-maintenance) |
| 分发与维护 | [脚本导航](../scripts/README.md)、[Build-Distribution](../scripts/Build-Distribution.ps1)、[安装脚本](../packaging/TractorTool.iss) |

下一步用户验证新增 A24 托盘交互；随后确定 GitHub 仓库及项目自身授权，具备远程目标与上传授权后实跑普通 CI 和标签草稿。目前 git remote 为空，没有推送或远程 CI 证据。E62 是用户本机运行反馈，不代表 GitHub 流程或新增托盘扩展已验收；不因缺少代理运行记录而否定用户反馈。

未关闭的专项覆盖只看 [A01–A24](DEVELOPMENT.md#acceptance)：新托盘、未说明的媒体细项、硬件矩阵、长期内存曲线、迁移近满盘/ACL/中断、跨会话及无 SDK 机器。旧取消调试输出触发不明，Job 终止仍有 20ms/最多 3 秒轮询并警告一次。运行测试仍由用户接手，本轮不扩展代理测试权限。
