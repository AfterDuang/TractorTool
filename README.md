<p align="center">
  <img src="TractorTool/Assets/tractortool.png" width="96" height="96" alt="TractorTool 图标">
</p>

<h1 align="center">TractorTool</h1>

<p align="center">插件驱动的桌面工具箱，让工具各司其职。</p>
<p align="center">Windows 优先 · .NET 10 · Avalonia · MVVM</p>

[主要功能](#主要功能) · [开始使用](#开始使用) · [从源码开发](#从源码开发) · [框架结构](#设计与结构) · [插件开发](Docs/PLUGIN_DEVELOPMENT.md) · [项目文档](Docs/README.md)

TractorTool 将工具业务放在独立插件中，由精干的宿主统一提供导航、主题、设置、日志、后台任务和外部依赖管理。新增符合现有协议的工具，可以独立编译和安装，无需改动宿主。

> **开发中，尚未发布正式 Release。** Windows x64 ZIP 和安装器已可本地构建；安装升级、真实媒体与 GPU 等仍有待验收项。功能实现与实际验证结果请以[开发与验收](Docs/DEVELOPMENT.md#current-status)为准。

## 主要功能

### 剪贴板历史

读取 Windows 自带的剪贴板历史，浏览文本与图片，预览并再次复制原内容。使用系统已有记录，不自行监听并建立另一套历史库；需要在 Windows 中开启剪贴板历史。

### 视频工具

- **媒体信息**：拖入一个视频，提取 MediaInfo 风格的格式化报告，支持复制信息。
- **多版本导出**：为同一个视频保存多张预设，选中后依次导出 MP4、MKV、MOV、WebM 或 TS。
- **导出参数**：视频/音频码率、输出尺寸，以及 VBR 一次、VBR 两次或 CBR 码率模式。
- **首帧替换**：可选一张图片，等比缩放、居中裁剪后替换第一帧。
- **GPU 偏好**：在插件设置中启用加速；硬件或模式不支持时回退 CPU 并说明原因。

输出按 `原文件名_预设名` 命名，重名时追加数字，不覆盖已有文件。媒体处理使用 FFmpeg / ffprobe，由宿主按需安装；输入与编码能力取决于实际工具和硬件。

### 桌面状态 HUD（独立预览插件）

电源变化提醒与多方案桌面状态，支持时钟、CPU、内存、网速、磁盘、硬件传感器、ICMP/TCP、HTTP/JSON 和 DeepSeek 余额。主页面编辑显示方案、轮播、文字颜色和窗口位置，插件设置管理长期偏好、硬件启用及加密凭据。需使用包含框架程序集加载修复的宿主并单独安装插件；新增硬件/网络及界面行为待用户运行验收，尚未并入完整分发。架构、使用方法和首版边界见 [HUD 插件文档](Docs/Plugins/SystemHud.md)。

### 统一的使用体验

| 能力 | 使用方式 |
| --- | --- |
| 插件管理 | 拖入或选择本地 ZIP 安装、更新，支持启停和卸载；变更在退出并重启后生效 |
| 外部依赖 | 默认从 GitHub Release 按需下载，首次确认、校验与已装复用；设置分为 Packages（已安装包）和依赖来源。支持添加公开 GitHub Enterprise 来源；[插件声明](Docs/PLUGIN_DEVELOPMENT.md#package-source-maintenance) |
| 后台任务 | 切换页面、隐藏到托盘后继续；在“日志 → 任务进度”查看状态与取消 |
| 运行记录 | INFO / WARNING / ERROR 分级高亮，搜索筛选、底部跟随、未读错误提醒 |
| 外观与设置 | 浅色、深色、跟随系统和主题色；软件设置与插件设置分开组织 |
| 数据管理 | 可自定义数据目录，重启时迁移并保留旧数据；插件程序留在软件目录 |

插件面向可信开发者，运行于宿主进程内；当前不提供不可信插件沙箱或在线插件商店。

## 开始使用

当前目标平台是 **Windows x64**。发布包采用自包含布局，使用时无需另装 .NET SDK；Linux、macOS 暂不提供发布包。

本地构建会生成以下文件，正式下载渠道将在 Release 流程接入后提供：

| 文件 | 用途 |
| --- | --- |
| `TractorTool-<版本>-win-x64-setup.exe` | 用户级安装版，默认安装到 `%LOCALAPPDATA%\Programs\TractorTool` |
| `TractorTool-<版本>-win-x64.zip` | 解压到可写的新目录，运行其中的 `TractorTool.exe` |
| `TractorTool.<插件名>-<版本>.zip` | 单独安装或更新插件 |
| `SHA256SUMS` | 核对发布文件的 SHA256；不等同于发布者签名 |

完整分发包含剪贴板历史和视频导出两个插件。首次需要 FFmpeg 时会询问是否下载，不要求自行配置系统 `PATH`。

**关闭窗口会隐藏到托盘。** 要重启、安装升级或卸载，请从托盘菜单或“设置 → 通用设置 → 关于软件”显式退出。

安装版升级保留已有插件和管理状态，卸载保留插件及应用数据；这些保留与恢复流程仍需运行验收。ZIP 应解压到新目录，不直接覆盖旧安装；安装器不接管自行解压的目录。

### 数据放在哪里

| 内容 | 默认位置 |
| --- | --- |
| 插件程序及私有依赖 | `<软件目录>/Plugins/<PluginId>/<Version>` |
| 配置、插件数据、日志和下载的依赖 | `%LOCALAPPDATA%\TractorTool` |
| 导出的视频 | 用户选择的输出目录 |

ZIP 版也使用上述数据目录。设置中迁移数据不会移动插件程序或导出的视频。卸载插件不会删除下载的依赖包；可在“设置 → 外部依赖”主动清理未被引用、未在使用的包。

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

普通构建和 F5 **不会自动安装插件**。插件打包与安装步骤见[插件开发规范](Docs/PLUGIN_DEVELOPMENT.md#6-开发构建与安装验证)。若要获得包含两个插件的完整布局，可使用下面的分发入口。

### 构建分发包

在 VS Code 中点击 **终端 → 运行任务 → TractorTool：本地发布**，选择 **ZIP 压缩包** 或 **ZIP＋Windows 安装包**，即可查看构建输出，无需手写命令。也可按 `Ctrl+Shift+P` 搜索 `Tasks: Run Task` 进入。入口使用 VS Code 自带的 [Tasks](https://code.visualstudio.com/docs/debugtest/tasks) 与选择框。

每次自动生成 `artifacts/local-publish-日期时间-随机后缀/distribution`，其中 `assets` 是最终产物；成功时终端显示完整路径。安装包选项会下载并校验固定版本的 Inno Setup 便携构建工具。需要本机已安装 PowerShell 7.4+ 且 `pwsh` 可用，PowerShell 扩展本身不替代运行时。任务只构建、打包和校验，不启动软件或安装器、不上传 GitHub，F5 仍用于调试。

命令行方式仍可用：从仓库根目录执行；输出目录必须尚不存在，重复构建请换一个新目录：

```powershell
# 宿主 ZIP、两个独立插件 ZIP、版本信息和校验文件
./scripts/Build-Distribution.ps1 -Output ./artifacts/local-distribution

# 只校验产物，不启动软件
./scripts/Verify-Distribution.ps1 -AssetsDir ./artifacts/local-distribution/assets
```

`assets` 存放打包产物，`layout` 存放含两个插件的完整宿主目录。本地与 [GitHub Actions](.github/workflows/distribution.yml) 使用同一组脚本；CI 配置尚待在 GitHub 实跑。

各脚本的用途、调用关系和输出目录见 [scripts 使用说明](scripts/README.md)。

### GitHub Release 草稿

仓库上传 GitHub 后，推送与 `Directory.Build.props` 版本一致的标签（例如 `v0.1.0`），同一工作流会构建并校验 ZIP、安装器及两个插件，然后创建 **Release 草稿**。普通提交、PR 和手动构建只保留 Actions 产物。标签、产物版本、源码提交必须一致，构建工作区须干净；带预发布后缀的标签会标记为 prerelease。

草稿不会自动公开。维护者核对附件、发布说明、本项目授权和实际运行验收后，再到 GitHub 发布。已有同标签 Release 不覆盖；上传失败或任务中断可能留下不完整草稿，先在 Releases 核对，仅删除本次失败的草稿，再重跑 `draft-release` job。不要删除正式 Release 来绕过保护。当前配置尚未在 GitHub 实跑，也未自动配置远程或推送。

<details>
<summary>同时生成 Windows 安装器</summary>

以下命令在新的构建目录下载并校验固定版本的 Inno Setup 便携编译工具，再生成 ZIP 与安装器；不会运行生成的安装器。

```powershell
$compiler = ./scripts/Get-InnoCompiler.ps1 -Output ./artifacts/local-inno
./scripts/Build-Distribution.ps1 -Output ./artifacts/local-installer -CompilerPath $compiler
```

</details>

运行测试时可通过 `--profile-root <绝对路径>` 隔离配置和数据；默认 F5 未使用该参数。插件仍从所运行软件的 `Plugins` 目录加载。完整的隔离运行、检查程序及人工验收方法见[开发入口](Docs/DEVELOPMENT.md#workflow)。

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

项目依赖方向为：宿主应用使用 Core 和 UI，Core 与 UI 依赖 Contracts；插件只引用公开 Contracts/UI 和自己的私有依赖。宿主不静态引用具体插件，Core 不依赖 Avalonia，也不包含视频处理、硬件采集等工具业务。页面、有限任务和常驻服务分别管理生命周期，切换页面不代表取消任务。

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

## 后续方向

近期工作集中在 GitHub 依赖链路、安装升级恢复、真实媒体/GPU 和任务生命周期验收，以及 GitHub Release 发布流程。独立 ripgrep 测试插件见[插件规范](Docs/PLUGIN_DEVELOPMENT.md#package-source-maintenance)。Python / pip 与本地 AI 支持属于[后续计划](Docs/ARCHITECTURE.md#python-dependencies)，尚未实现；业务仍由插件承担。

## 许可与致谢

本项目目前尚未附带自身代码的 `LICENSE`，授权方式将在正式发布前确定。

第三方组件、图标和外部工具的来源及许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)；分发包和各插件也携带各自的许可材料。第三方声明不替代本项目自身的授权。
