# TractorTool 开发上下文

更新日期：2026-10-07。唯一当前台账为 [DEVELOPMENT](DEVELOPMENT.md#current-status)，逐轮证据归 [PROJECT_HISTORY](PROJECT_HISTORY.md)。

## 1. 当前工作

宿主仍处于 M5 发布准备；本轮按用户确认新增桌面状态 HUD 独立插件，第一步实现和打包见 [E65](PROJECT_HISTORY.md#evidence-system-hud)。使用 .NET 10 / Avalonia 12.1.2，原生事件优先，必要采样按需启停，权限不足明确管理员提示；不补导航接口，复用托盘“显示 TractorTool”。不改公共接口，不复制参考程序的应用/托盘/自启动壳。

三步范围：基础 HUD → 多方案/轮播/变量模板/颜色规则/编辑预览 → GPU/温度/传感器/探测/HTTP/JSON/DeepSeek。当前只实现第一步；业务设计和验收方法见 [SystemHud](Plugins/SystemHud.md)。主窗口关闭或切页不停止 HUD；HUD 自身隐藏/暂停停止性能采样，显式退出注销通知、关闭窗口并等待在途读取。

电源用 Windows 通知；CPU/内存/网速每秒按当前可见指标读取，使用原生 API/.NET，不引入 WMI 或硬件驱动库。实际开始性能采样时只写一次优化 Warning。大于 64 逻辑处理器注明处理器组范围，网速注明接口合计，未知/重置不伪装成有效零值。

插件与 HudChecks 构建 0 警告/0 错误，独立 ZIP 的 9 个文件与输出一致、无共享 SDK DLL，发布 SDK 的 244 文件哈希不变。纯逻辑检查仅编译；A25 电源/穿透/DPI/退出等真实行为由用户验证。ZIP 路径唯一维护在 [本地产物](DEVELOPMENT.md#local-artifacts)，未替用户安装；HUD 暂未并入首版完整分发。

## 2. 保留的产品决定

- E62 用户已测试依赖安装/拒绝重试/修复清理、视频/GPU、插件升级、数据迁移，反馈暂未发现问题；不重复要求常规测试，不外推未说明的专项/硬件矩阵。
- E63 UI API 1.5 托盘注册支持一级插件子菜单、普通/勾选动作和状态句柄；宿主保留显示/退出。资源随插件生命周期，回调从 UI 线程开始，停止封闭入口并等待动作结束。A24 仍待真实运行证据。
- E64 Test.Independent 1.0.1 删除了 200ms 心跳和 tick 日志。源码构建不会替换已安装 1.0.0，需 ZIP 更新后显式退出重启。
- E60 外部依赖仅 Packages/依赖来源；默认 GitHub，可加公开 GitHub Enterprise API。插件声明精确仓库/tag/ZIP/hash/许可/工具，宿主负责确认、下载、事务、租约。无自有源，pip/AI/Worker 平台不留空入口。
- receipt v1/损坏记录保留可修复状态；身份/hash 冲突不暗中替换，引用不完整禁清理。来源配置在 DataRoot/Config/dependency-sources.json，旧缓存不读取、不删真实数据；查询仅实际申请时触发。
- E61 标签流程只创建 Release 草稿，不覆盖已有 Release。远程未配置，CI/Release 未实跑，公开发布由用户决定。
- E55 页面持有路径已修，历史隔离检查通过，实际长期内存曲线待复测。旧取消调试输出触发不明，Job 终止仍有 20ms/最多 3 秒轮询并警告一次。

## 3. 工作约定

- 用户接手运行测试，未经新指示不启动软件、检查程序、FFmpeg、UI 自动化，不接管真实配置/媒体/剪贴板/用户进程。构建、静态和 ZIP 校验不能冒充运行验收。
- 保留 .vscode/launch.json 空白修改、.gitignore 的 Docs/ 规则，不纳入提交。已跟踪文档用 git add -u -- Docs；新文档指定文件 git add -f。使用子代理时显式 gpt-6.1-sol / high；本地提交不代表远程推送授权。
- 框架精简，业务留插件，只依赖 Contracts/UI。VS Code、SDK 10.0.401，不更换 IDE 配置，不启用 AOT/未经验证裁剪。
- 数据根与软件目录分开；迁移保持互斥及恢复记录，不迁移插件代码、不删除旧根。分发只写新目录，ZIP 不覆盖升级，安装器保留插件和用户数据。跨会话维护仍待验。
- 日志只记录重要事件，正常取消不记 Error；任务在日志页，标题栏不放独立任务按钮。

## 4. 入口与下一步

| 工作 | 入口 |
| --- | --- |
| HUD 生命周期/设置 | [HudPlugin](../Plugins/TractorTool.Plugins.SystemHud/HudPlugin.cs)、[HudModel](../Plugins/TractorTool.Plugins.SystemHud/HudModel.cs) |
| Windows 采集/窗口 | [WindowsMetrics](../Plugins/TractorTool.Plugins.SystemHud/WindowsMetrics.cs)、[HudWindow](../Plugins/TractorTool.Plugins.SystemHud/HudWindow.axaml.cs) |
| HUD 规则/纯逻辑检查 | [插件文档](Plugins/SystemHud.md)、[HudChecks](../tests/TractorTool.HudChecks/Program.cs) |
| 托盘公共接口 | [PluginTray](../TractorTool.UI/PluginTray.cs)、[接入规范](PLUGIN_DEVELOPMENT.md#tray-menu) |
| GitHub 依赖 | [GitHubPackage](../TractorTool.Core/Packages/GitHubPackage.cs)、[PackageSources](../TractorTool.Core/Packages/PackageSources.cs) |
| 分发与维护 | [脚本导航](../scripts/README.md)、[Build-Distribution](../scripts/Build-Distribution.ps1) |

下一步按三步方案继续 HUD 第二步；第一步 A25 与 A24 运行证据由用户补充。宿主 M5 的远程发布、媒体/硬件细项、长期内存、迁移故障、跨会话及无 SDK 机器缺口继续看 [验收台账](DEVELOPMENT.md#acceptance)，不因新增插件而视为已关闭。
