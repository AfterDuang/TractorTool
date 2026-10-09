<p align="center">
  <img src="TractorTool/Assets/tractortool.png" width="96" height="96" alt="TractorTool 图标">
</p>

<h1 align="center">TractorTool</h1>

<p align="center">插件驱动的桌面工具箱，让工具各司其职。</p>
<p align="center">Windows 优先 · .NET 10 · Avalonia · MVVM</p>

[框架能力](#框架能力) · [开始使用](#开始使用) · [从源码开发](#从源码开发) · [框架结构](#设计与结构) · [插件开发](Docs/PLUGIN_DEVELOPMENT.md) · [项目文档](Docs/README.md)

TractorTool 将工具业务放在独立插件中，由精干的宿主统一提供导航、主题、设置、日志、后台任务和外部依赖管理。新增符合现有协议的工具，可以独立编译和安装，无需改动宿主。

> **开发中，尚未发布正式 Release。** Windows x64 ZIP 和安装器已可本地构建，当前进度与验证结果见[开发与验收](Docs/DEVELOPMENT.md#current-status)。

## 框架能力

| 能力 | 使用方式 |
| --- | --- |
| 插件管理 | 拖入或选择本地 ZIP 安装、更新，支持启停和卸载；变更在退出并重启后生效 |
| 外部依赖 | 从 GitHub Release 按需下载、校验和复用；统一管理已安装包与依赖来源 |
| 后台任务 | 切换页面、隐藏到托盘后继续；在“日志 → 任务进度”查看状态与取消 |
| 运行记录 | INFO / WARNING / ERROR 分级高亮，搜索筛选、底部跟随、未读错误提醒 |
| 外观与设置 | 浅色、深色、跟随系统和主题色；软件设置与插件设置分开组织 |
| 数据管理 | 可自定义数据目录，重启时迁移并保留旧数据；插件程序留在软件目录 |

插件面向可信开发者，运行于宿主进程内；当前不提供不可信插件沙箱或在线插件商店。各工具的使用与业务说明见[插件文档目录](Docs/Plugins)。

## 开始使用

当前目标平台是 **Windows x64**。发布包采用自包含布局，使用时无需另装 .NET SDK；Linux、macOS 暂不提供发布包。

本地构建会生成以下文件，正式下载渠道将在 Release 流程接入后提供：

| 文件 | 用途 |
| --- | --- |
| `TractorTool-<版本>-win-x64-setup.exe` | 用户级安装版，默认安装到 `%LOCALAPPDATA%\Programs\TractorTool` |
| `TractorTool-<版本>-win-x64.zip` | 解压到可写的新目录，运行其中的 `TractorTool.exe` |
| `TractorTool.<插件名>-<版本>.zip` | 单独安装或更新插件 |
| `SHA256SUMS` | 核对发布文件的 SHA256；不等同于发布者签名 |

**关闭窗口会隐藏到托盘。** 要重启、安装升级或卸载，请从托盘菜单或“设置 → 通用设置 → 关于软件”显式退出。

安装版升级保留已有插件和管理状态，卸载保留插件及应用数据。ZIP 应解压到新目录，不直接覆盖旧安装；安装器不接管自行解压的目录。

### 数据放在哪里

| 内容 | 默认位置 |
| --- | --- |
| 插件程序及私有依赖 | `<软件目录>/Plugins/<PluginId>/<Version>` |
| 配置、插件数据、日志和下载的依赖 | `%LOCALAPPDATA%\TractorTool` |

ZIP 版也使用上述数据目录。数据迁移不移动插件程序；卸载插件不删除下载的依赖包，可在“设置 → 外部依赖”清理未引用且未在使用的包。

## 从源码开发

使用 Windows、VS Code 和仓库 [global.json](global.json) 指定的 .NET SDK，配合 C# 与 Avalonia 扩展。运行分发脚本还需要 **PowerShell 7.4 或更新版本**。

1. 用 VS Code 打开仓库根目录，加载 `TractorTool.slnx`。
2. 完成依赖还原和构建。
3. 在“运行和调试”中选择已有的 **Avalonia: TractorTool**，按 **F5**。

在仓库根目录也可使用以下命令构建：

```powershell
dotnet restore ./TractorTool.slnx
dotnet build ./TractorTool.slnx -c Debug -m:1 -nr:false
```

普通构建和 F5 **不会自动安装插件**。插件打包与安装见[插件开发规范](Docs/PLUGIN_DEVELOPMENT.md#6-开发构建与安装验证)，完整分发使用下方入口。

### 构建分发包

在 VS Code 中点击 **终端 → 运行任务 → TractorTool：本地发布**，选择 **ZIP 压缩包** 或 **ZIP＋Windows 安装包**。

每次输出到新的 `artifacts/local-publish-日期时间-随机后缀/distribution`，终端显示完整路径。安装包模式会下载并校验 Inno Setup 构建工具；任务只构建和校验，不安装、启动或上传。

命令行方式仍可用：从仓库根目录执行；输出目录必须尚不存在，重复构建请换一个新目录：

```powershell
# 完整分发与校验文件
./scripts/Build-Distribution.ps1 -Output ./artifacts/local-distribution

# 只校验产物，不启动软件
./scripts/Verify-Distribution.ps1 -AssetsDir ./artifacts/local-distribution/assets
```

`assets` 存放打包产物，`layout` 存放运行目录。本地与 [GitHub Actions](.github/workflows/distribution.yml) 共用脚本；参数和安装器说明见 [scripts 使用说明](scripts/README.md)。

随附插件由 [packaging/plugins.json](packaging/plugins.json) 统一选择，增删只改工程路径列表；各插件 ID 与版本从自己的 `plugin.json` 读取，产物名单记录在 `release.json.plugins`。

### GitHub Release 草稿

仓库上传 GitHub 后，推送与 `Directory.Build.props` 版本一致的标签（例如 `v0.1.0`），工作流会构建、校验并创建 **Release 草稿**。普通提交、PR 和手动构建只保留 Actions 产物。标签、产物版本与源码提交须一致，工作区须干净；预发布标签生成 prerelease 草稿。

草稿不会自动公开。维护者核对附件、发布说明、本项目授权和实际运行验收后，再到 GitHub 发布。已有同标签 Release 不覆盖；上传失败或任务中断可能留下不完整草稿，先在 Releases 核对，仅删除本次失败的草稿，再重跑 `draft-release` job。不要删除正式 Release 来绕过保护。当前配置尚未在 GitHub 实跑，也未自动配置远程或推送。

测试时可用 `--profile-root <绝对路径>` 隔离配置和数据；默认 F5 未使用该参数。方法与验收要求见[开发入口](Docs/DEVELOPMENT.md#workflow)。

## 设计与结构

**框架负责连接、管理和统一交互，插件负责业务。** 公共能力按真实需求演进，不为尚未出现的场景预建复杂平台。

下面列出当前软件框架的主要源码目录与入口，省略构建生成文件和单个插件的业务细节：

```text
TractorTool/
├─ TractorTool/                         宿主应用：组装服务与桌面交互
│  ├─ Program.cs                       进程入口、启动参数与 Avalonia 初始化
│  ├─ App.axaml / App.axaml.cs         应用资源、启动/迁移、服务组装与退出
│  ├─ Views/                          主窗口、主页、插件、设置、日志与任务界面
│  ├─ ViewModels/                     导航、页面状态和用户操作协调
│  ├─ Services/                       托盘、主题、页面注册、图标缓存与宿主对话框
│  ├─ Platform/                       Windows 进程、安装互斥及窗口恢复等适配
│  ├─ Styles/                         宿主窗口与导航样式
│  └─ Assets/                         应用与内置导航图标
├─ TractorTool.Contracts/              与界面无关的公开插件协议
│  ├─ PluginContracts.cs              生命周期、路径、设置与日志契约
│  ├─ Packages/                       外部依赖申请、状态和租约接口
│  ├─ Processes/                      工具进程请求与结果接口
│  └─ Tasks/                          有限任务、进度与取消接口
├─ TractorTool.Core/                   宿主公共服务实现
│  ├─ Plugins/                        发现、装载隔离、生命周期与安装/更新管理
│  ├─ Packages/                       GitHub 来源解析、下载校验、安装与引用保护
│  ├─ Processes/                      进程执行协调、输出与取消处理
│  ├─ Tasks/                          公共任务状态、持久化摘要与取消
│  ├─ Storage/                        数据路径、JSON 存储、实例锁与数据迁移
│  └─ Logging/                        日志事件、内存记录与文件滚动保留
├─ TractorTool.UI/                     宿主与插件共用的 Avalonia UI SDK
│  ├─ PluginUi.cs                     页面/设置注册、图标及页面生命周期接口
│  ├─ PluginTray.cs                   插件托盘菜单接口
│  ├─ Controls/                       PageHost、SettingRow、ToolIcon
│  ├─ Styles/                         共享控件样式、主题资源与动效样式
│  └─ Motion.cs                       统一动效偏好与控件适配
├─ Plugins/                            独立工具插件源码，不属于框架业务
├─ tests/                              核心、插件、UI 检查与独立装载夹具
├─ scripts/                            构建、发布、插件打包与产物校验
├─ packaging/                          安装器、分发说明与许可资料
├─ .github/workflows/                  调用统一脚本的 CI 与 Release 草稿流程
├─ Docs/                               需求、架构、接入规范与开发验收文档
├─ TractorTool.slnx                    解决方案与工程分组
├─ Directory.Build.props               公共构建属性与默认产品版本
└─ global.json                         固定开发和 CI 使用的 .NET SDK
```

项目依赖方向为：宿主应用使用 Core 和 UI，Core 与 UI 依赖 Contracts；插件只引用公开 Contracts/UI 和自己的私有依赖。宿主不静态引用具体插件，Core 不依赖 Avalonia 或工具业务。页面、有限任务和常驻服务分别管理生命周期。

### 阅读与修改入口

先从 [Program.cs](TractorTool/Program.cs) → [App.axaml.cs](TractorTool/App.axaml.cs) 阅读启动、迁移与服务组装，再看 [MainViewModel](TractorTool/ViewModels/MainViewModel.cs) 和 [MainWindow](TractorTool/Views/MainWindow.axaml) 如何组织导航与页面。按关注的功能继续定位：

| 关注点 | 主要入口 |
| --- | --- |
| 插件加载与生命周期 | [PluginHost](TractorTool.Core/Plugins/PluginHost.cs)、[PluginLoadContext](TractorTool.Core/Plugins/PluginLoadContext.cs)；安装/更新事务见 [PluginManager](TractorTool.Core/Plugins/PluginManager.cs) |
| 外部依赖与工具进程 | [ExternalPackageService](TractorTool.Core/Packages/ExternalPackageService.cs)、[ProcessRunner](TractorTool.Core/Processes/ProcessRunner.cs)；Windows 执行适配见 [WindowsProcessSession](TractorTool/Platform/WindowsProcessSession.cs) |
| 日志与任务 | [AppLog](TractorTool.Core/Logging/AppLog.cs)、[BackgroundTaskService](TractorTool.Core/Tasks/BackgroundTaskService.cs)；界面入口见 [LogsViewModel](TractorTool/ViewModels/LogsViewModel.cs) |
| 配置与数据迁移 | [AppPaths](TractorTool.Core/Storage/AppPaths.cs)、[JsonFileStore](TractorTool.Core/Storage/JsonFileStore.cs)、[DataRootMigration](TractorTool.Core/Storage/DataRootMigration.cs) |
| 共享 UI 与宿主外观 | [公共控件](TractorTool.UI/Controls)、[共享样式](TractorTool.UI/Styles/SharedStyles.axaml)；宿主导航样式见 [ShellStyles](TractorTool/Styles/ShellStyles.axaml) |

详细公共设计见 [软件架构](Docs/ARCHITECTURE.md)，新增工具的接入与内部组织见 [插件开发规范](Docs/PLUGIN_DEVELOPMENT.md)。上图是源码布局，安装后的插件与用户数据位置见前文[数据放在哪里](#数据放在哪里)；`bin/`、`obj/`、`artifacts/` 为生成产物，不是新的框架层。

## 项目文档

- [文档导航](Docs/README.md)：按需要查阅，避免重复阅读全部上下文。
- [产品需求](Docs/TractorTool.md)与[软件架构](Docs/ARCHITECTURE.md)：产品范围和模块边界。
- [插件开发规范](Docs/PLUGIN_DEVELOPMENT.md)：协议、清单、共享样式和独立打包。
- [开发与验收](Docs/DEVELOPMENT.md)：当前状态、验证证据与未关闭事项。
- [开发上下文](Docs/PROJECT_CONTEXT.md)：接续开发的简短快照。

反馈问题时，请附软件/插件版本、Windows 版本、复现步骤和相关日志；分享前移除日志或截图中的个人路径及敏感内容。功能建议请说明使用场景；新增工具优先考虑独立插件。

## 许可与致谢

本项目目前尚未附带自身代码的 `LICENSE`，授权方式将在正式发布前确定。

第三方组件、图标和外部工具的来源及许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)；分发包和各插件也携带各自的许可材料。第三方声明不替代本项目自身的授权。
