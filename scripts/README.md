# 构建与维护脚本

日常发布不用逐个运行这些脚本。在 VS Code 点击 **终端 → 运行任务 → TractorTool：本地发布**，选择 ZIP 或 ZIP＋Windows 安装包即可。菜单配置见 [tasks.json](../.vscode/tasks.json)，具体操作和运行环境见 [项目首页](../README.md#构建分发包)。

这些脚本在开发电脑上构建或维护项目；本地“发布”是生成文件，不会自动上传 GitHub。

## 每个脚本做什么

| 脚本 | 用途与结果 | 什么时候使用 |
| --- | --- | --- |
| [Build-Distribution.ps1](Build-Distribution.ps1) | **完整分发总入口**：发布宿主、独立编译两个正式插件、收集许可、打 ZIP、生成版本与 SHA256 信息，最后校验；提供 CompilerPath 时同时生成安装包 | 日常发布；VS Code 菜单和 CI 都调用它 |
| [Build-Installer.ps1](Build-Installer.ps1) | 把**已有且校验通过的分发 ZIP**编译成 setup.exe，附带 installer.json 来源信息及 SHA256SUMS；不运行生成的安装器 | 完整发布时自动调用；也可对已有 ZIP 单独生成安装包，无需重编宿主 |
| [Build-Plugin.ps1](Build-Plugin.ps1) | 指定一个插件工程，引用已发布宿主的公开 SDK 独立构建，收集私有许可，输出插件 ZIP 和 .sha256；检查没有夹带宿主共享 DLL | 只更新某个插件时；完整分发也会调用 |
| [Export-DependencyNotices.ps1](Export-DependencyNotices.ps1) | 根据实际还原/发布依赖，收集第三方许可原文、NuGet 元数据和 dependencies.json；材料缺失则失败 | 完整分发自动调用，平时不用单独运行 |
| [Generate-AppIcon.ps1](Generate-AppIcon.ps1) | 把 Artwork 中的图标母版导出为 PNG 和多尺寸 ICO，默认重写 TractorTool/Assets 中对应资源 | 更换应用图标后手动运行；普通构建直接使用已入库的图标，详见 [图标说明](../Artwork/README.md) |
| [Get-InnoCompiler.ps1](Get-InnoCompiler.ps1) | 下载固定版本的 Inno Setup，校验 SHA256 和签名，以便携方式展开编译器，返回 ISCC.exe 路径 | 需要安装包时准备构建工具；VS Code 安装包选项和 CI 自动调用，ZIP 模式不需要 |
| [Publish-Host.ps1](Publish-Host.ps1) | **只发布宿主**，默认生成 Release / win-x64 自包含目录，含公开 SDK；不编译或装入正式插件，也不生成 ZIP/安装包 | 完整分发内部调用；单独准备插件开发用的宿主 SDK 时也可使用 |
| [Verify-Distribution.ps1](Verify-Distribution.ps1) | **只检查产物文件**：SHA256、ZIP 路径和内容、清单、版本、整包与独立插件一致性，以及安装包来源；不加载插件或启动应用 | 打包末尾自动调用，也可对已有 assets 再校验；通过不代表软件功能正常 |
| [Verify-PluginHost.ps1](Verify-PluginHost.ps1) | **会编译并运行检查程序**：核心检查、独立机制插件构建/加载、Headless 集成检查、宿主文件哈希比对；生成隔离证据目录 | 验证宿主与插件机制时使用，不属于日常打包步骤；不代替真实 Windows UI、视频或 GPU 验收 |

## 它们如何配合

完整分发由 Build-Distribution 串联 Publish-Host、Export-DependencyNotices、Build-Plugin 和 Verify-Distribution；选择安装包时，菜单/CI 先用 Get-InnoCompiler 准备工具，再由 Build-Distribution 调用 Build-Installer。无需手动依次执行所有脚本。

Generate-AppIcon 和 Verify-PluginHost 是独立的维护、验证入口，不在这条打包流程里。

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
- **Project / HostSdkDir**：要打包的插件 `.csproj` / 已发布宿主目录。HostSdkDir 中须有公开 SDK DLL，不是仓库源码目录。
- **AssetsDir / CompilerPath**：已有分发的 `assets` 目录 / Inno Setup 的 `ISCC.exe` 文件路径。
- **ExpectedTag / ExpectedCommit**：Verify-Distribution 的可选 Release 身份校验；两项须同时提供，要求标签等于 `v` 加构建版本、源码提交相同、工作区干净，且包含安装器。普通本地包校验不要求这两项。
- 分发使用 PowerShell 7.4+ 和 [global.json](../global.json) 指定的 .NET SDK。宿主版本默认来自 [Directory.Build.props](../Directory.Build.props)，插件版本来自各自 plugin.json；菜单不用每次填写版本。

命令示例集中在 [开发入口](../Docs/DEVELOPMENT.md#workflow) 与 [独立插件打包](../Docs/PLUGIN_DEVELOPMENT.md#6-开发构建与安装验证)。本页只解释脚本，当前实现和验收结果统一看 [开发台账](../Docs/DEVELOPMENT.md#current-status)。

[GitHub 工作流](../.github/workflows/distribution.yml) 的构建 job 只有读取权限；仅 `v*` 标签 push 才进入独立的 Release 写入 job，下载同一次构建产物并调用 Verify-Distribution 后，用 GitHub CLI 创建草稿。使用内置 GITHUB_TOKEN，无需额外发布脚本或 PAT；现有草稿/Release 不自动覆盖。操作及失败恢复见 [Release 草稿](../README.md#github-release-草稿)。

[ReleaseMetadataChecks.ps1](../tests/ReleaseMetadataChecks.ps1) 是只读静态回归入口：对已完成的干净分发目录验证正常身份，并断言错误标签、错误/缺失提交被拒绝。它不启动软件或安装器，当前执行状态见开发台账。
