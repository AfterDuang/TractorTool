# 文件夹版本历史插件

插件使用 Duplicati CLI 保存并提取本地未加密历史。Avalonia 和 TractorTool SDK 由宿主共享，Duplicati 由宿主包管理器按需下载。

## 外部工具来源

- Duplicati 项目许可证为 MIT。附件中原有的第三方许可保留。 [Duplicati 2.4.0.1](https://github.com/duplicati/duplicati/releases/tag/v2.4.0.1_stable_2026-10-07)
- 插件使用 Windows x64 附件 `duplicati-2.4.0.1_stable_2026-10-07-win-x64-gui.zip` 内的 `Duplicati.CommandLine.exe`。
- 宿主包标识 `Duplicati.CLI` 的 `1.0.0` 是插件下载配置的版本，实际引擎版本为上游 `2.4.0.1`。
- 已核对发布方列出的 SHA256 `9a9cf4c57922f3029a0559e11c7de28f02b1ca14220667252bebb364e6266999`。实际安装和进程探测仍待在 Windows 验证。 [官方发布附件](https://github.com/duplicati/duplicati/releases/expanded_assets/v2.4.0.1_stable_2026-10-07)

历史文件和索引存放在用户目录内。上游 CLI 默认可能发送使用统计，包括启动事件。有关发送内容及关闭方法见 [Duplicati 使用统计](https://docs.duplicati.com/security-and-secrets/telemetry-collection)。
