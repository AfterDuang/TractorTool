# 文件夹版本历史插件

插件使用 Duplicati CLI 保存并提取本地未加密历史。Avalonia 和 TractorTool SDK 由宿主共享，Duplicati 由宿主包管理器按需下载。

## Office 文档读取

插件随附 Microsoft Open XML SDK（`DocumentFormat.OpenXml` 和 `DocumentFormat.OpenXml.Framework` 3.5.1）以及 `System.IO.Packaging` 10.0.2，用于在本机读取 Word 和 Excel 快照内容。三者均使用 MIT 许可证。[Open XML SDK](https://www.nuget.org/packages/DocumentFormat.OpenXml/3.5.1) · [System.IO.Packaging](https://www.nuget.org/packages/System.IO.Packaging/10.0.2) · [随包许可](Licenses/OpenXml-Packaging-MIT.txt) · [第三方声明](Licenses/System.IO.Packaging-ThirdParty.txt)

## 导航图标

彩色文件夹图标来自 Microsoft Fluent System Icons 的 Document Folder。插件随附透明 PNG 和 [MIT 许可](Assets/LICENSE-FluentSystemIcons.txt)。[固定版本的原始 SVG](https://github.com/microsoft/fluentui-system-icons/blob/883bef1e12d37343d0b2a64a1af43d0209de1128/assets/Document%20Folder/SVG/ic_fluent_document_folder_24_color.svg)

## 外部工具来源

- Duplicati 项目许可证为 MIT。附件中原有的第三方许可保留。 [Duplicati 2.4.0.1](https://github.com/duplicati/duplicati/releases/tag/v2.4.0.1_stable_2026-10-07)
- 插件使用 Windows x64 附件 `duplicati-2.4.0.1_stable_2026-10-07-win-x64-gui.zip` 内的 `Duplicati.CommandLine.exe`。
- 宿主包标识 `Duplicati.CLI` 的 `1.0.1` 是插件下载配置的版本，实际引擎版本为上游 `2.4.0.1`。
- 官方附件 SHA256 为 `9a9cf4c57922f3029a0559e11c7de28f02b1ca14220667252bebb364e6266999`，由宿主安装时校验。 [官方发布附件](https://github.com/duplicati/duplicati/releases/expanded_assets/v2.4.0.1_stable_2026-10-07)

历史文件和索引存放在用户目录内。上游 CLI 默认可能发送使用统计，包括启动事件。有关发送内容及关闭方法见 [Duplicati 使用统计](https://docs.duplicati.com/security-and-secrets/telemetry-collection)。
