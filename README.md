<p align="center">
  <img src="TractorTool/Assets/tractortool.png" width="96" height="96" alt="TractorTool 图标">
</p>

<h1 align="center">TractorTool</h1>

<p align="center">插件驱动的桌面工具箱，让工具各司其职。</p>
<p align="center">Windows 优先 · .NET 10 · Avalonia · MVVM</p>

[主要功能](#主要功能) · [开始使用](#开始使用) · [从源码开发](#从源码开发) · [插件开发](#插件源码组织) · [项目文档](Docs/README.md)

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

| 目录 | 职责 |
| --- | --- |
| `TractorTool/` | 应用入口、窗口、导航、托盘及宿主设置 |
| `TractorTool.Contracts/` | 插件与宿主之间的公开协议 |
| `TractorTool.Core/` | 插件装载、任务、依赖及存储管理 |
| `TractorTool.UI/` | 页面注册接口、共享控件、样式与主题 |
| `Plugins/` | 独立工具插件的源代码 |
| `tests/` | 核心、插件及 UI 检查和机制夹具 |
| `scripts/`、`packaging/` | 构建、打包、校验和安装器资源 |

宿主不静态引用具体插件，插件通过 Contracts / UI 接入。页面、有限任务和常驻服务的生命周期分别管理，切换页面不代表取消任务。

### 插件源码组织

插件内部按实际职责组织。下面是**源码布局示例**，并非必须创建的模板；小插件可以从入口、页面和模型几个文件开始，业务增长后再拆目录。

```text
TractorTool.Plugins.Example/
├─ ExamplePlugin.cs              入口：初始化、页面/托盘注册和停止清理
├─ Configuration/               配置模型、保存格式与校验
├─ Processing/                  业务处理与外部接口适配，按业务命名
├─ ViewModels/                  页面状态、命令和操作协调
├─ Views/                       AXAML、同名后台代码及显示转换
├─ Assets/                      图标等静态资源（需要时）
├─ Licenses/                    第三方许可原文（使用第三方依赖时）
├─ NOTICE.md                    依赖及素材来源说明
├─ README.md                    职责目录、阅读顺序和开发入口
├─ plugin.json                  插件身份、入口、兼容性与外部依赖声明
└─ TractorTool.Plugins.Example.csproj
```

业务目录使用具体名称，例如 HUD 的 `Sampling/`；不要求所有插件都有采集、凭据或常驻服务。源码目录与安装目录、用户数据目录分别管理，`bin/`、`obj/` 是构建输出，不能直接作为正式插件包。

从 `plugin.json` 和入口类读起，再跟到模型、业务实现和页面。最小接入参考 [IndependentPlugin](tests/IndependentPlugin/ProbePlugin.cs)，较完整的职责划分参考 [SystemHud 源码导航](Plugins/TractorTool.Plugins.SystemHud/README.md)。拆分原则见 [插件源码组织规范](Docs/PLUGIN_DEVELOPMENT.md#source-organization)，实际交付使用 [独立构建与打包流程](Docs/PLUGIN_DEVELOPMENT.md#6-开发构建与安装验证)。

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
