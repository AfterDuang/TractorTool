# TractorTool 开发上下文

更新日期：2026-10-04。**本轮完成预设命名、公共任务入口澄清及双子代理审查；M2/M3/M4 运行验收待用户测试，M5 未开始。** 当前台账见 [DEVELOPMENT](DEVELOPMENT.md#current-status)，本轮详情见 [E34](PROJECT_HISTORY.md#evidence-preset-task-review)，日志/任务实现按需查 E33/E32。

## 1. 当前边界

- 用户最新要求：输出为原文件名加下划线和预设名；任务进度必须公用，修改后由子代理检查无用/重复代码。已完成相关静态复核；旧截图取消异常触发仍不确定，不能声称已根治，不改 VS Code 异常过滤。
- 用户接手运行测试；未经新指示不启动应用、检查程序、FFmpeg、UI 自动化或操作真实剪贴板/媒体。必要编译、独立打包和静态核对允许。
- VS Code / 根 slnx 12 工程；宿主不引用具体插件，源码 SDK 与 HostSdkDir 发布 DLL 引用互斥，开发 bin 不自动安装。若使用子代理，显式 GPT-6.1-Sol + high。
- 框架只提供公共连接、管理、生命周期及交互，媒体/AI 业务留插件。插件在实际 AppRoot/Plugins；数据/工具包在 DataRoot；不建通用 Worker/IPC/Python 平台。
- 实质改动保存本地 Git，不配置远程或推送；保留用户已有 .vscode/launch.json 空白修改。M5 数据迁移、磁盘日志保留和 CI/安装器未开始。

## 2. 接续要点

- Contracts API **1.7** 接受旧 1.0–1.6，原构造/解构/程序集身份不变；UI API 1.3。IAppLog 增加带 AppLogLevel 的默认接口重载，旧 Write 无异常为 INFO、有异常为 ERROR；新重载消费方须声明 API 1.7。
- **删除标题栏任务按钮**，日志内“运行记录 / 任务进度”页签共用已有模型。记录 INFO 绿、WARNING 黄、ERROR 红；级别/关键词筛选、复制、选中详情、暂停。内存 300 条，磁盘文件保留详细记录。
- 角标是本次启动未读 ERROR 记录数，日志图标左上角显示具体数字。手动标记已读到显示快照水位（含被筛选隐藏项），暂停后新错误不被确认；进页不自动清零、内存淘汰不扣数。事件驱动、UI 合并调度，无日志/任务轮询。
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

产物为 artifacts/preset-task-review/host 与 video/TractorTool.VideoExport-0.5.2.zip；安装 ZIP 后显式退出重启，不自动更新已装 DLL。Debug 12 工程 0 警告/0 错误，Release 发布、发布 SDK 独立插件构建及恢复源码引用后编译通过，235 个宿主文件未变。两位 GPT-6.1-Sol/high 子代理未发现相关范围的阻塞/明显死代码或大量重复，修正了一处检查只断言导航 Key 的精度问题。新增 3 组命名、2 组跨插件任务/宿主路由检查仅编译；VideoChecks 共 26 组。初始化恢复链、实际并发重名和长名仍无运行证据。

下一步由用户按 DEVELOPMENT 核对命名/冲突和所属工具入口，以及日志/角标/任务状态、取消异常触发；既有媒体、GPU、进程、依赖、旧 DLL、剪贴板与生命周期回归仍待验，未关闭项不随归档移除。
