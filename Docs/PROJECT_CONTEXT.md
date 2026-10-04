# TractorTool 开发上下文

更新日期：2026-10-04。本页只保留接续所需信息；完整进度与未关闭项以 [开发验收台账](DEVELOPMENT.md#current-status) 为准。

## 1. 当前位置

**处于 M5 可靠性与发布阶段。** 最新源码提交 `2fe0bd6` 将启动、迁移和失败提示放在唯一主窗口，完成后原位进入主页。本轮仅复核代码、整理文档，未发现新增可确认的代码问题；见 [E40](PROJECT_HISTORY.md#evidence-doc-review)。最近编译/发布证据仍为 [E39](PROJECT_HISTORY.md#evidence-startup-shell)，不能把本轮静态 Review 当作运行通过。

- M1–M4 功能实现已转维护；真实媒体/GPU、任务/退出、依赖/管理/剪贴板的逐项缺口仍在台账。M5 日志保留与迁移已有实现，运行验收及正式分发尚未完成。
- 用户接手测试；未经新指示不运行应用、检查程序、FFmpeg 或 UI 自动化，不访问真实配置/媒体/剪贴板。允许必要构建/发布和静态核对。保留 `.vscode/launch.json` 用户已有空白修改，不纳入提交。
- 框架仅负责公共连接、管理、生命周期和交互；业务留插件，不预建 Python/AI/Worker 平台。插件代码在实际 AppRoot/Plugins，数据与工具包在 DataRoot。使用子代理时显式 `gpt-6.1-sol` / `high`。

## 2. 下次改动必须保留的边界

- 启动只创建一次 MainWindow，首次 Opened 即解绑；实例恢复不重新初始化。迁移前只读外观，业务写入服务/插件在准备完成后创建；进度由事件更新，迟到回调不能覆盖主页。启动中关闭取消并退出，进入业务界面后关闭仍到托盘。
- 迁移持有 profile/数据根/插件安装目录锁，提交前失败保留旧指针；原子切换后收尾失败必须停止加载，下次先完成恢复。原根保留、插件代码不动。取消保留恢复记录；重试先确认目标归属/内容，再回收专属 scratch 和检查空间；快照摘要遍历及读取可取消。详细规则只维护在 [存储架构](ARCHITECTURE.md#storage)。
- Contracts API 1.7 / UI API 1.3，剪贴板 1.1.1、视频 0.5.2 未变。源码编译不自动安装插件，相同已装版本无需重装；正式打包用发布 SDK，宿主不引用具体插件。
- 未解决：同窗启动原生表现、迁移跨卷/近满盘/ACL/块内与部分收尾取消；旧取消调试输出触发不明；Windows Job 终止确认仍有 20ms/最多 3 秒轮询，实际启用警告一次。这些不因 E35 总体反馈或本轮 Review 关闭。

## 3. 入口与下一步

| 工作 | 入口 |
| --- | --- |
| 启动呈现与生命周期 | [App](../TractorTool/App.axaml.cs)、[MainWindow](../TractorTool/Views/MainWindow.axaml)、[MainViewModel](../TractorTool/ViewModels/MainViewModel.cs) |
| 迁移与回退 | [DataRootMigration](../TractorTool.Core/Storage/DataRootMigration.cs)、[PluginManager](../TractorTool.Core/Plugins/PluginManager.cs)、[DataMigrationViewModel](../TractorTool/ViewModels/DataMigrationViewModel.cs) |
| 日志保留/正式分发 | [LogFileWriter](../TractorTool.Core/Logging/LogFileWriter.cs)、[Publish-Host](../scripts/Publish-Host.ps1)、[第三方声明](../THIRD-PARTY-NOTICES.md) |

当前宿主副本为 `artifacts/m5-startup-shell/host`；产物、隔离启动、插件打包和人工回归入口统一见 [开发操作](DEVELOPMENT.md#workflow)，不使用历史旧副本验证当前修正。

下一项开发是 **M5 本地/CI 共用正式分发流程**：压缩包、许可与校验产物，然后安装器。本轮未开始该开发，也未新建远程或推送；用户继续负责隔离运行验收。
