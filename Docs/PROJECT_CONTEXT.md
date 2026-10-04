# TractorTool 开发上下文

更新日期：2026-10-04。**本轮修正日志已读、底部跟随/布局和插件重复安装冲突；M2/M3/M4 运行验收待用户测试，M5 未开始。** 当前台账见 [DEVELOPMENT](DEVELOPMENT.md#current-status)，本轮详情见 [E35](PROJECT_HISTORY.md#evidence-log-follow)，预设命名/公共任务审查按需查 E34。

## 1. 当前边界

- 用户最新要求：“已读”清除当前所有错误红点；日志旧在上新在下、底部跟随，改善留白和详情布局；待重启变更的重复安装不要抛出异常。已按具体分支处理，旧取消异常触发仍不确定，不声称全部根治，不改调试器过滤。
- 用户接手运行测试；未经新指示不启动应用、检查程序、FFmpeg、UI 自动化或操作真实剪贴板/媒体。必要编译、独立打包和静态核对允许。
- VS Code / 根 slnx 12 工程；宿主不引用具体插件，源码 SDK 与 HostSdkDir 发布 DLL 引用互斥，开发 bin 不自动安装。若使用子代理，显式 GPT-6.1-Sol + high。
- 框架只提供公共连接、管理、生命周期及交互，媒体/AI 业务留插件。插件在实际 AppRoot/Plugins；数据/工具包在 DataRoot；不建通用 Worker/IPC/Python 平台。
- 实质改动保存本地 Git，不配置远程或推送；保留用户已有 .vscode/launch.json 空白修改。M5 数据迁移、磁盘日志保留和 CI/安装器未开始。

## 2. 接续要点

- Contracts API **1.7** 接受旧 1.0–1.6，原构造/解构/程序集身份不变；UI API 1.3。IAppLog 增加带 AppLogLevel 的默认接口重载，旧 Write 无异常为 INFO、有异常为 ERROR；新重载消费方须声明 API 1.7。
- 日志内“运行记录 / 任务进度”页签共用已有模型，无标题栏任务按钮。记录 INFO 绿、WARNING 黄、ERROR 红；旧在上新在下，底部自动跟随、向上阅读不拉回，禁用选择项自动滚动；详情按需展开、统一左右留白。内存 300 条，磁盘保留详细记录，滚动跟随不使用定时器。
- 角标是本次启动未读 ERROR 记录数。“已读”在日志锁内确认当前全部错误，包含暂停/筛选隐藏记录；不删除日志，点击后新错误重新提醒。进页不自动清零、内存淘汰不扣数；删去旧显示快照水位接口与命令。事件驱动、UI 合并调度，无日志/任务轮询。
- 插件安装/变更的待重启、重复/较旧版本等冲突返回原因，不主动抛异常；在同一管理锁内判断，保留原请求并清理本次暂存，页面和 ERROR 日志仍告知拒绝。实际文件/包损坏及 I/O 故障继续走原异常处理。
- 仅记录插件发现/生命周期/管理、依赖安装与清理、任务开始/阶段/每 10% 节点/取消/终态和实际错误。正常导航、隐藏/恢复无记录。正常取消记 INFO，GPU 回退及启动恢复中断记 WARNING，意外中断/清理失败保留 ERROR。
- ProcessRunner/任务包装直接观察已取消 Task，减少同一取消反复抛出；公开取消语义不变。**仅 Windows 进程树终止确认仍有 20ms、每次最多 3 秒轮询**，实际进入时每次启动只警告一次，后续 Job 事件化优化仍未完成。正常进程退出为 OS 通知。
- API 1.6 Tasks 的 QueueBatchAsync 一次持久化整批，插件逐项 RunAsync 决定顺序；正常回调返回保留成功结果，迟到取消不覆盖。日志任务进度汇总所有插件，按 PluginId 打开所属插件第一个普通页面；按钮写“打开＋插件名”，不可用写“查看插件”并进入管理。初始化后刷新恢复摘要的名称/入口；批次取消同时限定插件 ID。摘要 DataRoot/Data/tasks.json 保留活动项和最近 100 条终态；重启记 Interrupted，无自动续转码/清理用户输出；坏文件保留并阻止新任务，可重试保存。
- 视频 **0.5.2 / API 1.7 / UI 1.3**，FFmpeg 9.0.2。输出名取不可变预设快照，格式为“原文件名_预设名”，重名依次 `_2`、`_3`，非法字符清洗和长名截短，不覆盖且名称不参与参数解析。多格式/VBR 一次/真正两次/CBR、GPU 自动回退、多预设串行不变。信息提取为 MediaInfo 风格文本，无播放/首帧预览；实际编码器/回退/失败写日志。
- 媒体/依赖/参数预检仍在入队前，预检失败或拒绝安装不建公共任务，切页/隐藏不会取消。输入/预设/GPU 批次快照、源身份验证、封面只读锁、隔离临时输出和无覆盖提交继续有效；输出/首帧/VFR/音画同步/色彩/GPU 的实测缺口见插件设计和台账。
- 剪贴板 1.1.1 读取 Windows 历史，系统事件/激活补刷、选中图片按需预览，不自建库/轮询。M2 管理重启生效；依赖缺失先确认、拒绝正常返回 null，禁用仍保护依赖，卸载不联动删包。
- 软件设置按通用设置/外部依赖分类，通用含外观/应用数据/关于；插件设置按注册生成，保留面包屑层级。Python/pip 与本地 AI 仍为未来方向。

## 3. 入口与交接

| 工作 | 入口 |
| --- | --- |
| 日志与角标 | [AppLog](../TractorTool.Core/Logging/AppLog.cs)、[LogsViewModel](../TractorTool/ViewModels/LogsViewModel.cs)、[LogEntriesView](../TractorTool/Views/LogEntriesView.axaml)、[MainViewModel](../TractorTool/ViewModels/MainViewModel.cs) |
| 公共任务与取消 | [BackgroundTaskService](../TractorTool.Core/Tasks/BackgroundTaskService.cs)、[TasksViewModel](../TractorTool/ViewModels/TasksViewModel.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs)、[WindowsProcessSession](../TractorTool/Platform/WindowsProcessSession.cs) |
| 插件与检查 | [视频设计](Plugins/VideoExport.md)、[插件规范](PLUGIN_DEVELOPMENT.md)、[LogChecks](../tests/TractorTool.Checks/LogChecks.cs)、[Headless 检查](../tests/TractorTool.IntegrationChecks/Program.cs) |

宿主更新在 artifacts/log-follow/host-final；插件未改，沿用 artifacts/preset-task-review/video/TractorTool.VideoExport-0.5.2.zip，不用重复安装同版。本轮 Debug 12 工程、Release 宿主发布及静态核对记录见 E35；新增冲突/滚动检查并修订已读用例，均仅编译未运行。上轮双代理只审 E34 范围，不自动套用于本轮。用户已有 launch.json 空白修改保留。

下一步由用户按 DEVELOPMENT 核对全部已读、底部跟随/上翻/暂停/长列表淘汰、详情/窄窗口，以及重复安装的提示和 ERROR 记录。命名、公共任务、旧取消异常及媒体/GPU/进程/依赖/剪贴板等运行缺口继续保留，不随归档移除。
