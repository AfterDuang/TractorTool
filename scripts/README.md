# 构建与维护脚本

日常发布不用逐个运行这些脚本。在 VS Code 点击 **终端 → 运行任务 → TractorTool：本地发布**，选择 ZIP 或 ZIP＋Windows 安装包即可。菜单配置见 [tasks.json](../.vscode/tasks.json)，具体操作和运行环境见 [项目首页](../README.md#构建分发包)。

这些脚本在开发电脑上构建或维护项目；本地“发布”是生成文件，不会自动上传 GitHub。

## 每个脚本做什么

| 脚本 | 用途与结果 | 什么时候使用 |
| --- | --- | --- |
| [Build-Distribution.ps1](Build-Distribution.ps1) | **完整分发总入口**：自动识别插件，发布宿主与独立插件、收集许可、打 ZIP、生成版本及 SHA256 信息并校验；提供 CompilerPath 时同时生成安装包 | 日常发布；VS Code 菜单和 CI 都调用它 |
| [Get-DistributionPlugins.ps1](Get-DistributionPlugins.ps1) | 扫描 Plugins 一级子目录的 plugin.json 与同目录工程，校验路径和身份，不编译或加载 DLL | 完整分发的前置校验，在创建输出前完成 |
| [Build-Installer.ps1](Build-Installer.ps1) | 把**已有且校验通过的分发 ZIP**编译成 setup.exe，附带 installer.json 来源信息及 SHA256SUMS；不运行生成的安装器 | 完整发布时自动调用；也可对已有 ZIP 单独生成安装包，无需重编宿主 |
| [Build-Plugin.ps1](Build-Plugin.ps1) | 引用发布 SDK 独立构建插件，复制随包 MVVM 许可与插件自备 Licenses，输出 ZIP/.sha256 并拒绝共享 DLL 副本 | 不要求使用 MVVM；完整分发及宿主验证也调用 |
| [Export-DependencyNotices.ps1](Export-DependencyNotices.ps1) | 根据宿主实际还原/发布依赖，收集第三方许可原文、NuGet 元数据和 dependencies.json；材料缺失则失败 | 完整分发自动调用，平时不用单独运行 |
| [Generate-AppIcon.ps1](Generate-AppIcon.ps1) | 把 Artwork 中的图标母版导出为 PNG 和多尺寸 ICO，默认重写 TractorTool/Assets 中对应资源 | 更换应用图标后手动运行；普通构建直接使用已入库的图标，详见 [图标说明](../Artwork/README.md) |
| [Get-InnoCompiler.ps1](Get-InnoCompiler.ps1) | 下载固定版本的 Inno Setup，校验 SHA256 和签名，以便携方式展开编译器，返回 ISCC.exe 路径 | 需要安装包时准备构建工具；VS Code 安装包选项和 CI 自动调用，ZIP 模式不需要 |
| [Publish-Host.ps1](Publish-Host.ps1) | **只发布宿主**，默认生成 Release / win-x64 自包含目录，含公开 SDK；不编译或装入正式插件，也不生成 ZIP/安装包 | 完整分发内部调用；单独准备插件开发用的宿主 SDK 时也可使用 |
| [Verify-Distribution.ps1](Verify-Distribution.ps1) | **只检查产物文件**：SHA256、ZIP 路径和内容、清单、版本、整包与独立插件一致性，以及安装包来源；不加载插件或启动应用 | 打包末尾自动调用，也可对已有 assets 再校验；通过不代表软件功能正常 |
| [Verify-PluginHost.ps1](Verify-PluginHost.ps1) | **会编译并运行检查程序**：核心检查、独立机制插件构建/加载、Headless 集成检查、宿主文件哈希比对；生成隔离证据目录 | 验证宿主与插件机制时使用，不属于日常打包步骤；不代替真实 Windows UI、视频或 GPU 验收 |

## 它们如何配合

完整分发由 Build-Distribution 串联 Publish-Host、Export-DependencyNotices、Build-Plugin 和 Verify-Distribution；选择安装包时，菜单/CI 先用 Get-InnoCompiler 准备工具，再由 Build-Distribution 调用 Build-Installer。无需手动依次执行所有脚本。

Generate-AppIcon 和 Verify-PluginHost 是独立的维护、验证入口，不在这条打包流程里。

## 随包插件自动识别

发布扫描 `Plugins` 的一级子目录，带 `plugin.json` 且同目录恰有一个 `.csproj` 即为随包插件，按目录名排序；工程和目录名称不限，ID 与版本读取插件清单。增删插件工程即可改变随包范围，无需额外名单。无清单的辅助目录不参与；不递归扫描 `bin/obj`、嵌套工程或仓库 `tests`。没有插件时只分发宿主。实验/测试插件应留在 `tests`，放入上述正式目录会自动随包发布。

构建开始前拒绝清单同目录缺少工程或存在多个工程、链接路径、重复 ID 和无效身份；独立构建前后身份必须一致。`release.json.plugins` 保存本次实际组包名单，ZIP 校验和安装器使用该快照，不回读当前源码目录；校验同时拒绝重复记录与整包内未登记的插件文件。宿主哈希、共享 SDK 副本检查和安装器保留已有插件的规则不变。

## 输出文件在哪里

Build-Distribution 的输出目录结构如下；VS Code 菜单会自动使用新的 `artifacts/local-publish-日期时间-随机后缀/distribution`：

```text
分发输出目录/
├─ assets/    最终产物：宿主 ZIP、插件 ZIP、版本信息、校验文件；可选 setup.exe
├─ layout/    含正式插件的完整宿主目录，也是宿主 ZIP 的组包来源
└─ work/      插件构建和安装器编译的中间文件
```

要分发时看 **assets**。单独运行 Build-Plugin，则在指定输出目录得到 `files/`、插件 ZIP 与 `.sha256`。Verify-PluginHost 的日志、隔离宿主和前后哈希位于 `artifacts/plugin-host-*`。安装包模式下载的编译工具在本次 `local-publish-*` 的 `inno/`，不进入发布包。

## 参数与边界

- **Output**：输出位置；Build-Distribution、Build-Installer、Get-InnoCompiler 要求目录尚不存在，Publish-Host、Build-Plugin 要求新目录或空目录，不用已有安装目录作为构建输出。
- **Project / HostSdkDir**：要打包的插件 `.csproj` / 已发布宿主目录。目标目录包含公开 SDK DLL 和宿主发布时生成的 `TractorTool.deps.json`。
- **AssetsDir / CompilerPath**：已有分发的 `assets` 目录 / Inno Setup 的 `ISCC.exe` 文件路径。
- **ExpectedTag / ExpectedCommit**：Verify-Distribution 的可选 Release 身份校验；两项须同时提供，要求标签等于 `v` 加构建版本、源码提交相同、工作区干净，且包含安装器。普通本地包校验不要求这两项。
- 分发使用 PowerShell 7.4+ 和 [global.json](../global.json) 指定的 .NET SDK。宿主版本默认来自 [Directory.Build.props](../Directory.Build.props)，插件版本来自各自 plugin.json；菜单不用每次填写版本。

### 插件打包的现有适用范围

Build-Plugin 从目标宿主的 `TractorTool.deps.json` 读取 Avalonia 版本，核对清单及实际 NuGet 还原版本。1.1.0 SDK 在 `TractorTool.runtimeconfig.json` 中记录本次构建提供的程序集，打包按该清单排除共享副本。以旧 SDK 为目标时沿用原有的 `Avalonia.*` 限制。仓外插件可使用自己的 NuGet 配置。

Build-Plugin 不要求 CommunityToolkit.Mvvm。仅在输出实际包含该 DLL 时，从唯一还原版本复制许可和第三方通知；元数据或原文缺失仍拒绝打包。插件自备 `Licenses/` 始终保留，其他私有 NuGet/原生库的许可由插件作者维护，脚本不声称自动收集全部许可。宿主依赖许可由 Export-DependencyNotices 负责。Verify-PluginHost 使用不依赖 MVVM 的 IndependentPlugin 走同一个打包入口，再验证宿主装载。

`HostSdkDir` 只切换公开 SDK 的引用来源，插件私有项目与 NuGet 依赖仍正常构建。`files/`、宿主完整 `layout/` 和 `assets/` 是不同产物层级，不能把日常源码模式的 bin 当作独立插件包；依赖归属见[插件规范](../Docs/PLUGIN_DEVELOPMENT.md#dependency-kinds)。

命令示例集中在 [开发入口](../Docs/DEVELOPMENT.md#workflow) 与 [独立插件打包](../Docs/PLUGIN_DEVELOPMENT.md#6-开发构建与安装验证)。本页只解释脚本，当前实现和验收结果统一看 [开发台账](../Docs/DEVELOPMENT.md#current-status)。

[GitHub 工作流](../.github/workflows/distribution.yml) 的构建作业使用只读权限。`v*` 标签 push 触发独立的发布作业，以内置 GITHUB_TOKEN 下载本次构建的附件，调用 Verify-Distribution 核对标签和源码提交，再通过 GitHub CLI 上传附件并公开 Release。操作及失败恢复见 [GitHub Release](../README.md#github-release)。

[ReleaseMetadataChecks.ps1](../tests/ReleaseMetadataChecks.ps1) 是只读静态回归入口：对已完成的干净分发目录验证正常身份，并断言错误标签、错误/缺失提交被拒绝。它不启动软件或安装器，当前执行状态见开发台账。

[DistributionPluginsChecks.ps1](../tests/DistributionPluginsChecks.ps1) 使用临时工程元数据和 ZIP 验证插件发现、清单身份和包内容，包括多个插件共享外部工具声明、无关插件的同名私有文件、改名归档和共享程序集副本。该检查在 CI 的分发构建之前运行。
