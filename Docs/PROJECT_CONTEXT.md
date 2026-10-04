# TractorTool 开发上下文

更新日期：2026-10-04。当前为 **M3：视频单次导出已实现并打包，真实媒体/GPU 验收待用户执行**；M2 运行验收仍未关闭。唯一状态台账见 [DEVELOPMENT](DEVELOPMENT.md#current-status)，过程按 E 编号查历史。

## 1. 当前边界

- 框架只提供插件连接、统一交互及依赖/进程/生命周期管理；媒体和未来 AI 业务归插件。公共扩展必须有实际消费者，不提前搭空接口、通用 Worker/IPC 或脚本平台。
- IDE 为 VS Code，根 slnx 共 12 工程。宿主不静态引用具体插件；日常源码 SDK 与正式 HostSdkDir 引用互斥，开发 bin 不自动安装。
- 用户接手运行测试；未经新指示不启动应用/检查程序/UI 自动化/FFmpeg、不读写真实剪贴板。必要编译、独立打包与静态检查允许。
- 子代理显式 GPT-6.1-Sol + high。本地 Git 保存实质改动，不配置远程或推送。
- 插件在真实 AppRoot/Plugins；数据/工具包在 DataRoot。M5 数据根迁移尚无 UI，不能把测试 profile 当迁移功能。
- 公共 UI / 宿主 ShellStyles / 插件局部布局的职责保持不变；普通动效使用 Motion/MotionStyles 和现有控件主题，不逐页面复制动画。
- 用户已纠正：设置图表达包含关系。左侧软件组只有通用设置/外部依赖；通用下有外观/应用数据/关于，包列表进入详情；顶部面包屑可返回、左侧保持父类。插件组按真实注册产生入口。保持旧流程替换后的代码干净。
- Python / pip 与本地 AI 支撑列入[后续计划](ARCHITECTURE.md#python-dependencies)，尚未排期；未来统一管理解释器/隔离环境，原生工具不强行经过 Python。当前没有 Python 安装或 AI 专属框架实现。

## 2. 接续要点

- M2 ZIP/开关/卸载/更新/回退重启生效，保留插件数据；管理卡片原位更新。剪贴板 1.1.1 是系统历史左列表/右预览、复制原项，事件/激活补刷，无轮询/自建库；本轮未改已安装目录。
- Contracts 当前 API 1.5，兼容旧 1.0–1.4。ProcessRequest 可选 stdout 分块回调在 worker 顺序执行，不受捕获上限限制；回调应快速返回，异常进入进程清理。Core 不解析媒体进度；Windows 创建时绑定 Job，非 Windows 适配器仍无。
- API 1.4 TryRequestAsync：已有包复用、缺失首次确认，拒绝正常返回 null；token 取消仍抛 OCE，同包共享确认/安装，预留到取得租约。原 Acquire 不下载，旧 RequestAsync 保留 1.3 拒绝异常语义兼容 DLL。插件不能自建下载器或持有租约外路径启动进程。
- 可信源是随宿主分发的 PackageSources/default.json，目前 Gyan FFmpeg 9.0.2 essentials/win-x64；无远程刷新/自定义源。receipt v2 保留独立安装定义，v1 可读、升级失败只警告。
- PluginPackageReferences 从清单/管理日志重建引用，含禁用、失败、待激活及回退版本；不可确认则禁止清理。插件卸载不删除包，重启生效后才解除引用，清理同时检查声明/申请/租约/事务。
- SettingsPage.Parent 表达层级；SelectedPage、SelectedCategory 和 Breadcrumbs 分离。SettingsView 仍只负责页呈现/生命周期，复用 PageHost 动画、取消和错误处理；窄窗口只折叠大类列表。主页外观快捷入口直接到外观子页。
- 视频插件 0.2.0 / API 1.5 声明 FFmpeg 9.0.2，现有单视频、单组参数导出：MP4/MKV/MOV + H.264/AAC，CPU/NVIDIA/Intel/AMD 选择，可选首帧封面，真实 stdout 进度、取消与无覆盖提交。GPU 先实际编码 8 帧检查所选链，再完整导出，不静默切回 CPU。
- 当前仅支持已识别的 8 位 SDR、逐行、直角旋转素材；高位深/HDR/广色域/隔行等明确拒绝。封面先铺黑合成不透明画面再覆盖第 0 帧。首帧/后续画面、VFR 时间轴、音画同步及硬件效果仍待真实验收。
- 导出快照参数、检查源文件大小/修改时间、复制封面到独有事务目录；输出探测后无覆盖提交，只清理自己创建的指定文件，进度提交前最多 99%。切页/隐藏不取消，停止经 UI dispatcher 取消并等待；生命周期和包租约覆盖整个操作。
- 视频偏好自动保存，坏配置保留、写失败可重试；导出参数当前为会话参数，M4 多预设/持久化/有限任务与中断恢复尚未实现。拒绝安装正常保留输入、退出忙碌，可再次申请；没有模拟结果。
- E27 修正拒绝安装的异常控制流，以及 File.GetAttributes 对正常缺失路径抛出再捕获造成的调试噪声。改用 FileInfo.Attributes 的 -1 缺失值，仍向上检查重解析点，权限等其他错误保留。未改插件/依赖卸载分离及清理保护。
- 全局计数及正常页脚空白移除，统计仍在主页/插件管理摘要；Notice 仅用于实际初始化/呈现失败、托盘不可用提示。
- 本轮两路 GPT-6.1-Sol/high 复审修正透明封面透底、高位深识别缺漏和协议检查版本冲突，复核无剩余确定阻塞。最终 Debug 12 工程 0 错误/0 警告，Release 宿主/视频独立构建通过，235 个宿主文件哈希未变。19 项进程、12 组视频、29 项 Windows 包检查均只编译；没有运行程序、FFmpeg 或 UI，不据此关闭 M3。

## 3. 入口与产物

| 工作 | 入口 |
| --- | --- |
| 设置层级 | [SettingsViewModel](../TractorTool/ViewModels/SettingsViewModel.cs)、[SettingsView](../TractorTool/Views/SettingsView.axaml)、[PackageDetailsView](../TractorTool/Views/PackageDetailsView.axaml) |
| 视频读取/导出 | [VideoViewModel](../Plugins/TractorTool.Plugins.VideoExport/VideoViewModel.cs)、[MediaProbe](../Plugins/TractorTool.Plugins.VideoExport/MediaProbe.cs)、[ExportPlan](../Plugins/TractorTool.Plugins.VideoExport/ExportPlan.cs)、[VideoExporter](../Plugins/TractorTool.Plugins.VideoExport/VideoExporter.cs)、[ExportProgress](../Plugins/TractorTool.Plugins.VideoExport/ExportProgress.cs) |
| 依赖/进程 | [PackageManagement](../TractorTool.Core/Packages/PackageManagement.cs)、[PluginPackageReferences](../TractorTool.Core/Plugins/PluginPackageReferences.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs) |
| 打包/检查 | [Build-VideoPlugin](../scripts/Build-VideoPlugin.ps1)、[VideoChecks](../tests/TractorTool.VideoChecks/Program.cs)、[NavigationChecks](../tests/TractorTool.IntegrationChecks/NavigationChecks.cs) |

当前可供用户测试：artifacts/video-export/host；插件 ZIP 为 artifacts/video-export/video/TractorTool.VideoExport-0.2.0.zip。需搭配 API 1.5 新宿主，拖 ZIP 后显式退出重启，不自动安装；源码编译不会替换旧 DLL。构建/发布/文件检查证据在 artifacts/video-export，完整步骤只见 DEVELOPMENT。已有用户 `.vscode/launch.json` 修改保持原样，不纳入本轮提交。

下一步按用户测试反馈收敛真实输出、首帧/VFR、音画同步、GPU 与取消，再接 M4 多预设/有限任务；保留 M2、依赖/设置、旧 DLL 及进程等未验收项。独立打包后已正常 restore/build 恢复源码依赖图。

后续替换本快照，详细过程归 [E29](PROJECT_HISTORY.md#evidence-video-export)。
