# TractorTool 开发上下文

更新日期：2026-10-04。**M4 实现、编译及独立包已补齐，M2/M3/M4 运行验收待用户最终测试。** 当前状态只见 [DEVELOPMENT](DEVELOPMENT.md#current-status)，本轮详情归 [E32](PROJECT_HISTORY.md#evidence-m4-tasks)，旧码率/布局详情留在 E31。

## 1. 当前边界

- 用户授权先 Review、解决问题后推进完 M4，最后自己测试；未经新指示不启动应用、检查程序、FFmpeg、UI 自动化或操作真实剪贴板。必要编译、独立打包和静态核对允许。
- IDE 为 VS Code；根 slnx 共 12 工程。宿主不静态引用具体插件，源码 SDK 与 HostSdkDir 发布 DLL 引用互斥；开发 bin 不自动安装。子代理显式 GPT-6.1-Sol + high。
- 框架提供插件连接、资源管理、生命周期及统一交互；媒体与未来 AI 策略归插件。没有新增通用 Worker/IPC、脚本或 Python/AI 平台。
- 插件在实际 AppRoot/Plugins，数据和工具包在 DataRoot。M5 数据迁移、CI/安装器未开始；profile 仅隔离测试。实质改动保存本地 Git，不配置远程或推送；保留用户已有 .vscode/launch.json 修改。

## 2. 接续要点

- Contracts API 1.6 接受旧 1.0–1.5，保留原上下文构造/解构和程序集身份；UI API 1.3 不变。API 1.5 stdout 分块回调仍由 worker 顺序发送，Core 不解析媒体进度；Windows 创建时绑定 Job，非 Windows 进程适配器仍无。
- API 1.6 新增 PluginContext.Tasks：Core BackgroundTaskService/Scope，QueueBatchAsync 一次登记和持久化整批，调用方逐项 RunAsync 决定串行。回调正常返回保留已提交结果，迟到取消不改为已取消；整批登记避免取消遗漏后续版本。
- 任务摘要为 DataRoot/Data/tasks.json，保留活动项和最近 100 条终态；重启非终态转 Interrupted，无业务参数、不自动重试或续转码、不删除崩溃遗留输出。坏文件保留并阻止新任务执行，任务页可重试记录保存。
- 标题栏“任务”提供稳定状态入口、进度、单项/整批取消、结果和返回工具，不增底部导航或插件页脚。显式退出有活跃公共任务时确认，关机跳过确认；任务/插件/包清理共用外层 10 秒等待。
- 视频 0.5.0 / API 1.6 / UI 1.3，FFmpeg 9.0.2；单视频/可选单封面、多预设串行，MP4/MKV/MOV/TS + H.264/AAC、WebM + VP9/Opus，VBR 一次/真正两次/CBR。GPU 默认关闭，失败/不支持自动回退 CPU 并解释，取消不回退；实际编码器写诊断日志。
- 媒体读取、依赖准备和参数检查仍为入队前预检，插件页显示状态/取消；预检成功后一次入队，失败或拒绝安装不建公共任务。预检中切页/隐藏仍由插件实例续跑。
- 输入/参数/GPU 在申请依赖前固定，源文件核对大小/修改时间，封面整批只读共享锁且单项临时复制；输出校验后无覆盖提交，取消仅清理自己的产物。运行中锁定输入/预设编辑，失败继续余项。
- 导出限制为可识别 8 位像素格式、逐行和直角旋转。已补 color_space/color_range，拒绝明确 HDR/广色域/高位深/未支持范围；无色彩标记的 8 位输入按普通 SDR 假设。FFmpeg 自动颜色协商仍需 BT.709、full-range、彩色/透明封面、首帧/后续帧与同步实测，不能声称色彩验收通过。
- 预设名称/参数/勾选和偏好自动保存，输入/输出路径不入偏好；旧 schema 1 缺 RateControl 默认 vbr，旧 Encoder 忽略并在保存时移除，坏配置保留、写失败可重试。
- 双功能原生页签、紧凑输入/主要参数、折叠画面选项、右侧预设/输出；信息页为完整可复制 MediaInfo 风格文本，没有播放/预览。宽窄窗口与滚动效果待用户验收；公共 Motion/PageHost 与主题边界不变。
- M2 管理操作重启生效、数据保留；剪贴板 1.1.1 读取系统历史/事件及激活补刷，不自建库、不轮询，运行缺口保留。API 1.4 TryRequestAsync 拒绝正常返回 null，包引用/租约/同包确认合并不变；禁用仍保护依赖，卸载不联动删包。
- 软件设置仅通用设置/外部依赖两大类，通用含外观/应用数据/关于，顶部面包屑保留父层级；插件设置按注册生成。Python/pip 与本地 AI 仍是后续方向。

## 3. 入口、证据与下一步

| 工作 | 入口 |
| --- | --- |
| 公共任务 | [任务契约](../TractorTool.Contracts/Tasks/BackgroundTasks.cs)、[任务服务](../TractorTool.Core/Tasks/BackgroundTaskService.cs)、[TasksViewModel](../TractorTool/ViewModels/TasksViewModel.cs)、[TasksView](../TractorTool/Views/TasksView.axaml)、[App](../TractorTool/App.axaml.cs) |
| 视频处理 | [VideoViewModel](../Plugins/TractorTool.Plugins.VideoExport/VideoViewModel.cs)、[MediaProbe](../Plugins/TractorTool.Plugins.VideoExport/MediaProbe.cs)、[ExportPlan](../Plugins/TractorTool.Plugins.VideoExport/ExportPlan.cs)、[VideoExporter](../Plugins/TractorTool.Plugins.VideoExport/VideoExporter.cs) |
| 发布与检查 | [Publish-Host](../scripts/Publish-Host.ps1)、[Build-VideoPlugin](../scripts/Build-VideoPlugin.ps1)、[TaskChecks](../tests/TractorTool.Checks/TaskChecks.cs)、[VideoChecks](../tests/TractorTool.VideoChecks/Program.cs) |

artifacts/m4-complete/host 为新宿主，video/TractorTool.VideoExport-0.5.0.zip 为独立包；必须配 API 1.6 宿主，E30/E31 旧宿主不能复用。独立插件构建前后 235 个宿主文件未变；Debug 12 工程、Release 宿主发布和发布 SDK 独立视频构建通过，源码依赖图已恢复。13 项任务、23 组视频及既有进程/包检查仅编译，程序和真实集成未运行；文档链接、AXAML、ZIP 清单/内容及变更空白核对通过，详情见 E32。

下一步由用户按 DEVELOPMENT 验证任务/退出/中断摘要、预设/串行/取消、真实媒体/首帧/VFR/同步/色彩/GPU，再按反馈修复。M2、依赖/设置、旧 DLL 和进程回归缺口保留，不能把本轮编译当作阶段验收通过。
