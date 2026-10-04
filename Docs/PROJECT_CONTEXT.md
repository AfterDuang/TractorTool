# TractorTool 开发上下文

更新日期：2026-10-04。**用户对 E35 日志修正反馈未发现问题并允许继续；进入 M5，已补日志磁盘保留。** 当前验收以 [DEVELOPMENT](DEVELOPMENT.md#current-status) 为准，本轮证据见 [E36](PROJECT_HISTORY.md#evidence-log-retention)。未逐项记录的 M1–M4 运行缺口继续保留。

## 1. 当前边界

- 用户接手运行测试；未经新指示不启动应用、检查程序、FFmpeg、UI 自动化或操作真实剪贴板/媒体。必要编译、独立打包和静态核对允许。“看着没啥问题”是 E35 反馈，不视作媒体/GPU 或所有故障分支通过。
- VS Code / 根 slnx 12 工程；宿主不引用具体插件，源码 SDK 与 HostSdkDir 发布 DLL 引用互斥，开发 bin 不自动安装。若使用子代理，显式 GPT-6.1-Sol + high；本轮无新子代理审查。
- 框架只提供公共连接、管理、生命周期及交互，媒体/AI 业务留插件。插件在实际 AppRoot/Plugins；数据/工具包在 DataRoot；不建通用 Worker/IPC/Python 平台。
- 实质改动保存本地 Git，不配置远程或推送；保留用户已有 .vscode/launch.json 空白修改。M5 数据迁移、可靠性运行验证和 CI/安装器尚未完成。

## 2. 接续要点

- Contracts API **1.7** / UI API **1.3** 未变，旧构造、默认接口实现和程序集身份继续兼容。日志文件保留是 Core 内部实现，不扩展 SDK 或新增日志框架。
- AppLog 统一写锁内调用 LogFileWriter：10 MiB 单文件，UTC 跨日或容量滚动；最近 14 天、最多 20 文件/100 MiB，保留当前段并预留其容量。首次写入/滚动检查，不逐条扫描或定时轮询。只处理顶层精确命名的普通宿主日志，跳过未知文件、子目录、链接；路径祖先链接拒绝写入/清理。占用/权限阻止清理时 WARNING 一次，继续写入、后续滚动重试，此时不宣称已达磁盘上限。
- 单条磁盘记录最多 64 KiB，完整 UTF-8 截断并标注，原文仍在最近 300 条内存窗口；不会补写被截断或先前写入失败的内容。设置 → 通用设置 → 应用数据复用策略常量显示规则和打开目录，不增加可配置策略体系。
- 日志统一承载“运行记录 / 任务进度”，INFO 绿/WARNING 黄/ERROR 红，旧上新下、底部跟随、上翻不拉回，详情默认折叠。点击“已读”锁内确认当前全部 ERROR（含暂停/筛选隐藏项），不删记录；之后新错误提醒。磁盘清理不改变角标、内存或已读状态。
- 待重启、重复/较旧版本等插件管理冲突，在管理锁内正常返回原因；保留原请求并清理本次暂存，页面和 ERROR 说明拒绝，无人为异常堆栈。实际 I/O/损坏包故障仍正常捕获，未更改调试器过滤。此前取消调试异常触发仍不确定，不能据 E35 反馈宣称全部根治。
- 普通导航、隐藏/恢复不写日志；任务只记录阶段及每 10% 节点。正常取消 INFO、GPU 回退/恢复中断 WARNING、真实故障 ERROR。Windows Job 终止确认仍有 20ms/最多 3 秒轮询，实际启用每次启动警告一次，后续事件化待处理。
- 公共任务按 PluginId/BatchId 隔离；整批持久化登记后由插件逐项 RunAsync，宿主按插件关联打开其页面，无可用页面进入管理。Data/tasks.json 保留活动项与最近 100 条终态，重启标为 Interrupted，不自动续转码或清理用户输出。损坏文件保留并阻止执行，页面可重试保存；迟到取消不抹掉已成功结果。
- 视频 **0.5.2 / API 1.7 / UI 1.3**，FFmpeg 9.0.2。输出“原文件名_预设名”，清洗非法字符、截短长名，同名追加数字且不覆盖；名称不解释参数。多格式/VBR 一次/真正两次/CBR、多预设串行、GPU 设置及自动回退不变。信息页为拖入识别和 MediaInfo 风格文本，无播放/首帧预览。
- 媒体/依赖/参数预检在入队前，失败或拒绝安装不建公共任务；切页/隐藏不取消。批次参数快照、源身份验证、封面只读锁及隔离输出有效。首帧/VFR/音画同步/色彩/GPU 等实际证据仍待验。
- 剪贴板 1.1.1 使用系统历史/事件/激活补刷，无自建库/轮询。依赖缺失先确认、拒绝返回 null，禁用仍保护，卸载不联动删包。设置保持软件/插件分类与面包屑。Python/pip 和本地 AI 仍为未来计划。

## 3. 入口与下一步

| 工作 | 入口 |
| --- | --- |
| 日志与磁盘保留 | [AppLog](../TractorTool.Core/Logging/AppLog.cs)、[LogFileWriter](../TractorTool.Core/Logging/LogFileWriter.cs)、[LogChecks](../tests/TractorTool.Checks/LogChecks.cs) |
| 应用数据/迁移起点 | [AppPaths](../TractorTool.Core/Storage/AppPaths.cs)、[InstanceLease](../TractorTool.Core/Storage/InstanceLease.cs)、[App](../TractorTool/App.axaml.cs)、[SettingsViewModel](../TractorTool/ViewModels/SettingsViewModel.cs)、[迁移设计](ARCHITECTURE.md#storage) |
| 待重启与依赖 | [PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[ExternalPackageService](../TractorTool.Core/Packages/ExternalPackageService.cs) |

本轮宿主 artifacts/m5-log-retention/host；视频仍为 artifacts/preset-task-review/video/TractorTool.VideoExport-0.5.2.zip，不需要重复安装。LogChecks 新增 6 组磁盘保留用例，只编译未运行，证据详见 E36；未删除实际用户日志或操作安装目录。

下一项推进 **M5 数据目录迁移**：检查固定 bootstrap、数据根/安装目录独占、管理请求绑定 DataRoot 与包路径语义；在下次启动、插件加载前复制并校验，成功后原子切换，旧数据保留，失败仍可恢复。不得在线直接改路径或移动插件代码。之后再做可靠性验收与统一发布/CI/安装器，现有运行缺口不随文档归档消失。
