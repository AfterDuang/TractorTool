# 外部依赖测试插件

本插件只通过公开 Contracts/UI 申请 ripgrep 14.1.1 并执行 `--version`，不搜索用户文件，不在初始化时下载，不捆绑工具。
GitHub 仓库：<https://github.com/BurntSushi/ripgrep>；固定 Release `14.1.1`，Windows x64 MSVC ZIP。
仓库、标签、附件、SHA256、许可和工具路径在本插件 `plugin.json` 的 `dependencies[].downloads` 声明，宿主没有该工具的专属目录。
ripgrep 使用 MIT OR Unlicense；下载的官方归档携带 LICENSE-MIT 和 UNLICENSE。
私有依赖 CommunityToolkit.Mvvm 8.4.2 的许可由 Build-Plugin 脚本收集到 Licenses。
Avalonia 和 TractorTool SDK 由宿主共享，不随插件分发。
