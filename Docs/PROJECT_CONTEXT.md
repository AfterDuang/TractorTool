# TractorTool 开发上下文

更新日期：2026-10-11。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

**用户最新决定：先解决软件框架，插件问题暂缓。** 当前仍在 M5 可靠性与发布准备，Windows 通知、pip/Python 和托盘动态方案列表不在本轮范围。

E99 已完成框架打包与进程清理调整：Build-Plugin 支持无 MVVM 插件，仅在实际随包包含 MVVM DLL 时复制对应许可；Verify-PluginHost 复用同一打包入口。Windows Job 清理用临时完成端口等待，移除 20ms 轮询及关联提醒；通知不保证送达，截止时仍查证活动进程数，保留三秒上限。确认进程退出后自然排空管道，失败/超时才取消读取。旧截图的取消异常触发不明，未声称全部消失。

宿主与核心检查工程编译成功；使用本轮宿主构建输出的公开 SDK 独立打包两种机制夹具，无 MVVM/有 MVVM 的 ZIP 分别 10/9 文件哈希一致，87 个 SDK 输出文件未变。宿主及插件构建有 NU1900 网络警告；核心工程 0 警告。新增回归仅编译，未运行应用、检查程序或完整自包含分发。产物位于 artifacts/e99-framework；详细命令和证据见 [E99](PROJECT_HISTORY.md#evidence-framework-packaging-process)。

E98 发布自动发现保持：仅扫描 Plugins 一级子目录中带 plugin.json 和唯一同目录工程的插件，ID/版本读取清单；没有 packaging/plugins.json 名单。校验与安装器使用 release.json.plugins 快照。自动发现回归尚未执行，不能继承旧整包验证结果。

## 2. 保留的决定与未关闭项

- E62 用户已测试依赖安装/拒绝重试/修复清理、视频/GPU、插件升级、数据迁移，反馈暂未发现问题；不重复要求常规测试，不外推未说明的专项/硬件矩阵。
- E60 外部依赖仅 Packages/依赖来源；默认 GitHub，可加公开 GitHub Enterprise API。插件声明精确仓库/tag/ZIP/hash/许可/工具，宿主负责确认、下载、事务、租约。无自有源、pip/AI/Worker 空入口。receipt v1/损坏记录可修复；身份/hash 冲突不暗中替换，引用不完整禁清理。
- E63 UI API 1.5 托盘接口支持注册期 Add、状态 Update 和生命周期清理，未开放分隔线/运行期增删；动态方案列表按用户决定暂缓，A24 原生运行证据仍缺。
- E94 HUD 两项 P2 **未修复、暂缓**：硬件启用开关保存后旧缓存未失效；非硬件方案 SensorId=null 会导致初始化失败。0.4.17 只在重新检测后清缓存，不等于修复保存入口。硬件/原生/DPI/性能缺口见 [A26/A27](DEVELOPMENT.md#acceptance) 和 [插件文档](Plugins/SystemHud.md)，E92 一分钟旧版性能观察不能外推。
- E55 页面持有路径已修，长期内存曲线待复测；迁移近满盘/ACL/中断、跨 Windows 会话、无 SDK 机器等专项仍未补齐。E99 通知等待的原生运行和通知丢失极端路径未验证。
- E61 标签流程只创建 Release 草稿，不覆盖已有 Release；尚未配置远程或实跑 CI/Release，公开发布由用户决定。

## 3. 工作约定

- 用户接手运行测试，未经新指示不启动软件、检查程序、FFmpeg、UI 自动化，不操作真实配置/媒体/剪贴板/用户进程。此前 HUD 离屏授权不外推为本轮框架测试授权。构建、静态和 ZIP 校验不等于运行验收。
- 保留用户 .vscode/launch.json、.gitignore 和 Directory.Build.props 的现有修改，不纳入本轮提交。props 当前用户版本为 1.0.0，产品版本继续以文件为准；文档不维护第二个默认值。被忽略但已跟踪的 Docs 用 git add -u 指定文件暂存。
- 框架保持精干，业务留插件；Contracts API 1.7 / UI API 1.5 不变。插件开发若需要更改框架，先交架构侧分析，不凭“继续”扩接口。VS Code、SDK 10.0.401，不更换 IDE、不启用 AOT/未经验证裁剪。
- 数据根与软件目录分开；迁移保持互斥及恢复记录，保留旧根，不迁移插件代码。分发写新目录，安装器保留既有插件和用户数据。日志只记重要事件，正常取消不记 Error；任务在日志页。
- 有实质进展更新唯一台账并替换本快照，详细证据归历史；本地提交不代表远程授权。使用子代理时显式 gpt-6.1-sol / high。

## 4. 入口与下一步

| 工作 | 入口 |
| --- | --- |
| 进程清理与回归 | [WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) |
| 独立打包 | [Build-Plugin](../scripts/Build-Plugin.ps1)、[Verify-PluginHost](../scripts/Verify-PluginHost.ps1)、[脚本边界](../scripts/README.md) |
| 装载与公共接口 | [PluginLoadContext](../TractorTool.Core/Plugins/PluginLoadContext.cs)、[接入规范](PLUGIN_DEVELOPMENT.md) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 分发与维护 | [Build-Distribution](../scripts/Build-Distribution.ps1)、[验收与下一步](DEVELOPMENT.md#next) |

框架下一步以运行证据收口：取消/超时、根进程先退与子进程残留、显式退出及迁移故障；不自动启动检查。GitHub 发布等待远程目标与授权。插件两项 P2 和托盘方案列表继续暂缓，不因本轮框架修复而关闭。
