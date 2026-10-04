# TractorTool 开发上下文

更新日期：2026-10-04。**M5 日志磁盘保留和数据目录迁移已实现；本轮按用户要求双代理审查迁移并修正两项 P2。** 当前验收以 [DEVELOPMENT](DEVELOPMENT.md#current-status) 为准，本轮证据见 [E38](PROJECT_HISTORY.md#evidence-migration-review)。未逐项记录的 M1–M4 运行缺口继续保留。

## 1. 当前边界

- 用户接手运行测试；未经新指示不启动应用、检查程序、FFmpeg、UI 自动化或操作真实剪贴板/媒体。必要编译、独立打包和静态核对允许。“看着没啥问题”是 E35 反馈，不视作媒体/GPU 或所有故障分支通过。
- VS Code / 根 slnx 12 工程；宿主不引用具体插件，源码 SDK 与 HostSdkDir 发布 DLL 引用互斥，开发 bin 不自动安装。本轮两位子代理均显式 GPT-6.1-Sol + high，分别审查存储事务和启动/退出链，修正后复审均无新增发现。
- 框架只提供公共连接、管理、生命周期及交互，媒体/AI 业务留插件。插件在实际 AppRoot/Plugins；数据/工具包在 DataRoot；不建通用 Worker/IPC/Python 平台。
- 实质改动保存本地 Git，不配置远程或推送；保留用户已有 .vscode/launch.json 空白修改。M5 迁移/日志保留运行验证、可靠性回归和 CI/安装器尚未完成。

## 2. 接续要点

- Contracts API **1.7** / UI API **1.3** 未变，旧构造、默认接口实现和程序集身份继续兼容。日志保留和迁移都在 Core 内实现，不扩展 SDK 或新建业务框架。
- 数据迁移入口为设置 → 通用设置 → 应用数据，只接受本机空的专用目录。确认后在固定 bootstrap 记录绑定 AppRoot 的请求，显式退出、下次服务/插件加载前执行；不迁移插件代码。复制 Config/Data/Packages/Logs，跳过 Cache/Packages/.work/bootstrap，SHA256 核对、无覆盖提交，保留原目录。
- 启动持有固定 profile、当前/目标 DataRoot 和插件安装目录租约，后台复制、事件进度，无轮询。全部复制校验后原子切换 dataRoot，保留事务直至插件回退状态/快照元数据重绑定完成。提交前失败回原根并提示设置重试/取消；提交后收尾失败停止加载，重启幂等收尾，不能回旧根继续写。存盘报错会重读实际指针，避免错误处理反向覆盖。
- 有待重启插件变更/待加载结果时不安排迁移；安排后新增变更则保留旧根先处理插件，随后手动重试。关闭启动进度窗口停止复制/快照校验并退出，提交前后均保留请求；提交后取消保留新指针，下次先收尾。快照摘要格式不变，遍历和每 64 KiB 读取前检查取消。重试在归属/内容校验后回收专属 scratch，再查实际空间；不删除已复制目标文件。原根继续运行导致已复制内容变化时，取消后选择新空目录；无自动清理旧目录入口。
- 当前包 receipt 持久化相对工具路径；用户输入/输出路径不改。新根回退快照会更新本安装引用的 DataRoot，旧快照不动。未来 Python venv 需专门重建，不由此次复制自动保证可搬迁。
- AppLog 单文件 10 MiB，UTC 跨日/大小滚动，最近 14 天、最多 20 文件/100 MiB；首次写入/滚动时只清理顶层精确命名普通日志，失败 WARNING 后继续、下次滚动重试。单条磁盘最多 64 KiB 并标注截断，内存最近 300 条仍保留原文。规则在应用数据页展示，无定时扫描或可配置策略体系。
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
| 应用数据/迁移起点 | [DataRootMigration](../TractorTool.Core/Storage/DataRootMigration.cs)、[AppPaths](../TractorTool.Core/Storage/AppPaths.cs)、[App](../TractorTool/App.axaml.cs)、[DataMigrationViewModel](../TractorTool/ViewModels/DataMigrationViewModel.cs)、[迁移设计](ARCHITECTURE.md#storage) |
| 待重启与依赖 | [PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[ExternalPackageService](../TractorTool.Core/Packages/ExternalPackageService.cs) |

本轮宿主 artifacts/m5-migration-review/host；视频仍为 artifacts/preset-task-review/video/TractorTool.VideoExport-0.5.2.zip，不需要重复安装。Debug 12 工程和 Release 宿主发布通过；迁移 9 组核心检查、6 组插件管理关联检查仅编译未运行，证据详见 E38。新增夹具覆盖两个阶段取消/再启动，扩展旧摘要固定值及残留 scratch 回收；近满盘/块内取消仍需运行证据。未启动应用或改动实际数据根。

下一项推进 **M5 本地/CI 共用的正式分发流程**，完善压缩包、许可与校验产物，再落实安装器。用户负责隔离运行验收，重点仍有迁移的跨卷/ACL/空间不足/中断恢复、实际回退与包复用；媒体/GPU、任务/生命周期及旧取消异常等缺口保持，不随文档归档消失。
