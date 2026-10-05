# 外部依赖测试插件

此夹具只通过公开 Contracts/UI 申请 MediaInfo CLI 26.05 并执行 `--Version`，不读取真实媒体，不在初始化时下载，不捆绑工具。
宿主管理包版本 `26.5.0` 映射上游发布版本 `26.05`。
MediaInfo 的包源定义在测试数据 `../PackageSources/mediainfo.json`；宿主生产源不因此改变。
私有依赖 CommunityToolkit.Mvvm 8.4.2 的许可由通用 Build-Plugin 脚本收集到 Licenses。
Avalonia 和 TractorTool SDK 由宿主共享，不随插件分发。
