# TractorTool 开发上下文

更新日期：2026-10-04。当前为 **M3：公共服务已接入，视频插件已提供媒体读取入口，转码/GPU 待接续**；M2 运行验收仍未关闭。唯一状态台账见 [DEVELOPMENT](DEVELOPMENT.md#current-status)，过程按 E 编号查历史。

## 1. 当前边界

- IDE 为 VS Code，根 slnx 共 12 工程。宿主不静态引用具体插件；日常源码 SDK 与正式 HostSdkDir 引用互斥，开发 bin 不自动安装。
- 用户接手运行测试；未经新指示不启动应用/检查程序/UI 自动化/FFmpeg、不读写真实剪贴板。必要编译、独立打包与静态检查允许。
- 子代理显式 GPT-6.1-Sol + high。本地 Git 保存实质改动，不配置远程或推送。
- 插件在真实 AppRoot/Plugins；数据/工具包在 DataRoot。M5 数据根迁移尚无 UI，不能把测试 profile 当迁移功能。
- 公共 UI / 宿主 ShellStyles / 插件局部布局的职责保持不变；普通动效使用 Motion/MotionStyles 和现有控件主题，不逐页面复制动画。
- 用户已纠正：设置图表达包含关系。左侧软件组只有通用设置/外部依赖；通用下有外观/应用数据/关于，包列表进入详情；顶部面包屑可返回、左侧保持父类。插件组按真实注册产生入口。保持旧流程替换后的代码干净。

## 2. 接续要点

- M2 ZIP/开关/卸载/更新/回退重启生效，保留插件数据；管理卡片原位更新。剪贴板 1.1.1 是系统历史左列表/右预览、复制原项，事件/激活补刷，无轮询/自建库；本轮未改已安装目录。
- 进程服务按插件生命周期持有；Core 负责参数、双流、取消、退出；Windows 创建时绑定 Job。非 Windows 适配器与实时输出回调仍无，17 项用例只编译。
- API 1.3 RequestAsync：已有包复用、缺失由宿主首次确认，合并同包确认/安装；申请预留到取得租约。原 Acquire 不下载。插件不能自建下载器或持有租约外路径启动进程。
- 可信源是随宿主分发的 PackageSources/default.json，目前 Gyan FFmpeg 9.0.2 essentials/win-x64；无远程刷新/自定义源。receipt v2 保留独立安装定义，v1 可读、升级失败只警告。
- PluginPackageReferences 从清单/管理日志重建引用，含禁用、失败、待激活及回退版本；不可确认则禁止清理。插件卸载不删除包，重启生效后才解除引用，清理同时检查声明/申请/租约/事务。
- SettingsPage.Parent 表达层级；SelectedPage、SelectedCategory 和 Breadcrumbs 分离。SettingsView 仍只负责页呈现/生命周期，复用 PageHost 动画、取消和错误处理；窄窗口只折叠大类列表。主页外观快捷入口直接到外观子页。
- 视频插件 0.1.0 已有独立工程/清单/ZIP，声明 FFmpeg 9.0.2，在选择或手动读取时申请包并运行 ffprobe，解析 JSON，支持取消、异常和文件变化检测。实例持有模型，切页/隐藏不取消；StopAsync 经 UI dispatcher 取消并等待读取/设置保存。未写任何转码、封面/GPU 或模拟输出。
- 视频专属设置“选择视频后读取信息”自动保存，坏配置保留，写失败可重试；源码版本和包版本为 0.1.0。以后更新已安装包必须提高版本，不能覆盖同版本。
- E26 两位 GPT-6.1-Sol/high 静态复审，退出跨线程问题已修复复核；新增层级回归及 5 组 JSON 检查仅编译。Debug 全方案 0 错误/0 警告，Release 宿主发布及视频独立构建通过；235 个宿主文件哈希未变。没有启动应用、测试程序或 ffprobe。

## 3. 入口与产物

| 工作 | 入口 |
| --- | --- |
| 设置层级 | [SettingsViewModel](../TractorTool/ViewModels/SettingsViewModel.cs)、[SettingsView](../TractorTool/Views/SettingsView.axaml)、[PackageDetailsView](../TractorTool/Views/PackageDetailsView.axaml) |
| 视频读取/设置 | [VideoPlugin](../Plugins/TractorTool.Plugins.VideoExport/VideoPlugin.cs)、[VideoViewModel](../Plugins/TractorTool.Plugins.VideoExport/VideoViewModel.cs)、[MediaProbe](../Plugins/TractorTool.Plugins.VideoExport/MediaProbe.cs) |
| 依赖/进程 | [PackageManagement](../TractorTool.Core/Packages/PackageManagement.cs)、[PluginPackageReferences](../TractorTool.Core/Plugins/PluginPackageReferences.cs)、[ProcessRunner](../TractorTool.Core/Processes/ProcessRunner.cs) |
| 打包/检查 | [Build-VideoPlugin](../scripts/Build-VideoPlugin.ps1)、[VideoChecks](../tests/TractorTool.VideoChecks/Program.cs)、[NavigationChecks](../tests/TractorTool.IntegrationChecks/NavigationChecks.cs) |

当前可供用户测试：artifacts/settings-hierarchy/host；插件 ZIP 为 artifacts/settings-hierarchy/video/TractorTool.VideoExport-0.1.0.zip。不自动安装；用户可拖 ZIP 后显式退出重启。旧 E22 副本不含 M3。构建/发布/文件检查证据在 artifacts/settings-hierarchy，完整步骤只见 DEVELOPMENT。

下一步实现插件内真实 FFmpeg 导出、进度和首帧/GPU 验证，然后 M4 多预设/有限任务。当前信息读取也尚缺运行证据，不能因为能打包就判定完成验收。源码开发应正常 restore/build，避免沿用正式打包模式的依赖图。

后续替换本快照，详细过程归 [E26](PROJECT_HISTORY.md#evidence-settings-hierarchy)。
