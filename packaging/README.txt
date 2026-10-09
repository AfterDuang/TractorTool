TractorTool · Windows x64

将整个目录解压到可写的位置，运行 TractorTool.exe；无需另装 .NET。
此开发版本附带独立插件，具体插件及版本见 release.json 的 plugins 列表，尚待完整运行验收。

插件代码位于软件目录的 Plugins/<PluginId>/<Version>，管理变更重启生效。
应用数据默认位于 %LOCALAPPDATA%\TractorTool，可在设置中安排迁移。
压缩包不使用便携数据模式，也不包含用户配置、媒体或 FFmpeg。
首次需要外部依赖时由宿主确认后下载；卸载插件保留已安装依赖。

请先退出旧版本并解压到新的空目录，不要直接覆盖正在运行的安装。
现有自行安装的插件不会自动复制到新目录；保留原安装以便返回。
测试时使用 --profile-root <绝对空目录> 隔离数据，避免影响日常配置。

使用 setup.exe 安装版时，默认安装到当前用户 Programs/TractorTool。
后续运行安装器升级宿主会保留 Plugins 及管理状态，不更新或恢复已有插件。
卸载保留插件、配置、下载包及自定义数据，不自动启动软件或结束任务。
安装器只接受新空目录或自己管理的安装，不能直接接管 ZIP 解压目录。

release.json：版本、源码提交及工作区是否有未提交改动。
files.json：包内文件的大小和 SHA256（不包含清单自身）。
Licenses/dependencies.json、各插件 Licenses 和 THIRD-PARTY-NOTICES.md：第三方许可材料。
分发目录 SHA256SUMS 用于检查 ZIP 完整性，不等于发布者签名或运行验收。
