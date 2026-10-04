# TractorTool 开发上下文

更新日期：2026-10-05。本页只保留接续所需信息；完整进度与未关闭项以 [开发验收台账](DEVELOPMENT.md#current-status) 为准。

## 1. 当前位置

**处于 M5 可靠性与发布阶段。** 最新一轮 [E43](PROJECT_HISTORY.md#evidence-installer) 实现 Windows 用户级安装器及升级/卸载保留逻辑。两位 gpt-6.1-sol / high 子代理复审后，完整 ZIP/安装器编译及静态校验通过；没有执行实际安装、升级、卸载或应用。CI 已接入相同流程但未执行，打包不等于运行通过。

- M1–M4 功能实现已转维护；真实媒体/GPU、任务/退出、依赖/管理/剪贴板的逐项缺口仍在台账。M5 的运行恢复、安装保留、Release 发布、签名及无 SDK 机器验收未完成。
- 用户接手测试；未经新指示不运行应用、检查程序、FFmpeg 或 UI 自动化，不访问真实配置/媒体/剪贴板。允许必要构建/发布和静态核对。保留 `.vscode/launch.json` 用户已有空白修改，不纳入提交。
- 框架仅负责公共连接、管理、生命周期和交互；业务留插件，不预建 Python/AI/Worker 平台。插件代码在实际 AppRoot/Plugins，数据与工具包在 DataRoot。使用子代理时显式 `gpt-6.1-sol` / `high`。

## 2. 下次改动必须保留的边界

- 启动只创建一次 MainWindow，首次 Opened 即解绑；实例恢复不重新初始化。迁移前只读外观，业务写入服务/插件在准备完成后创建；进度由事件更新，迟到回调不能覆盖主页。启动中关闭取消并退出，进入业务界面后关闭仍到托盘。
- 迁移持有 profile/数据根/插件安装目录锁，提交前失败保留旧指针；原子切换后收尾失败必须停止加载，下次先完成恢复。原根保留、插件代码不动。取消保留恢复记录；重试先确认目标归属/内容，再回收专属 scratch 和检查空间；快照摘要遍历及读取可取消。详细规则只维护在 [存储架构](ARCHITECTURE.md#storage)。
- Contracts API 1.7 / UI API 1.3，剪贴板 1.1.1、视频 0.5.2 未变。源码编译不自动安装插件，相同已装版本无需重装；正式打包用发布 SDK，宿主不引用具体插件。
- Build-Distribution 只写新目录；ZIP 解压不等于升级。安装器只从已校验且 installationProtocol=1 的 ZIP 快照取文件，默认用户级安装。升级保留整个已有 Plugins，卸载保留插件和数据；首次插件安装残留标记只能同包恢复，应用在恢复前不启动。
- WindowsMaintenance 与安装器的命名门/存活信号必须一致，维护不强杀任务、不轮询；当前限同一 Windows 会话，跨会话安全尚无保证。不可只删启动保护却继续标 installationProtocol=1。
- PowerShell 7.4+、global.json/CI 共用 SDK 10.0.401、固定 Inno Setup 6.7.3；准备编译器时校验哈希与签名。安装器与来源元数据成对校验；CI 只上传产物，未授予发布写权限。依赖许可升级时重新核对。
- 未解决：同窗启动原生表现、迁移跨卷/近满盘/ACL/块内与部分收尾取消；旧取消调试输出触发不明；Windows Job 终止确认仍有 20ms/最多 3 秒轮询，实际启用警告一次。这些不因 E35 总体反馈或本轮 Review 关闭。

## 3. 入口与下一步

| 工作 | 入口 |
| --- | --- |
| 启动与维护保护 | [Program](../TractorTool/Program.cs)、[WindowsMaintenance](../TractorTool/Platform/WindowsMaintenance.cs)、[App](../TractorTool/App.axaml.cs) |
| 迁移与回退 | [DataRootMigration](../TractorTool.Core/Storage/DataRootMigration.cs)、[PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[DataMigrationViewModel](../TractorTool/ViewModels/DataMigrationViewModel.cs) |
| 分发与安装 | [Build-Distribution](../scripts/Build-Distribution.ps1)、[Build-Installer](../scripts/Build-Installer.ps1)、[安装脚本](../packaging/TractorTool.iss)、[Verify-Distribution](../scripts/Verify-Distribution.ps1)、[CI](../.github/workflows/distribution.yml) |

当前产物在 `artifacts/m5-installer/delivery/assets`，宿主布局为同级 `layout/TractorTool-0.1.0-win-x64`。包内基线 `af47817`、sourceDirty=true，不冒充干净提交产物；操作与人工回归方法见 [开发入口](DEVELOPMENT.md#workflow)。历史 artifacts 部分已被外部清理，不保证旧路径存在。

下一项开发是 **M5 GitHub Release 发布流程**，复用已有 ZIP/安装器与校验。用户并行验证隔离安装、升级/卸载保留、首次失败同包恢复、维护并发和前述运行缺口。没有配置远程、推送或实际发布；公开发布前仍需确认本项目授权和签名安排。
