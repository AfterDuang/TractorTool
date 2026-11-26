# DLL 运行库测试插件

本插件通过公开 Contracts/UI 申请 SDL 3.2.8 的 Windows x64 运行库，只调用 `SDL_GetVersion`，不调用 `SDL_Init`，不访问媒体或设备。

- 上游：[libsdl-org/SDL release-3.2.8](https://github.com/libsdl-org/SDL/releases/tag/release-3.2.8)。
- 官方附件：`SDL3-3.2.8-win32-x64.zip`，SHA256、根 DLL 路径和来源固定在 `plugin.json`。
- SDL 许可为 Zlib；宿主下载的官方 ZIP 保留 `LICENSE.txt`，本插件 ZIP 不捆绑 SDL DLL。
- 私有依赖 CommunityToolkit.Mvvm 的许可由 Build-Plugin 脚本收集。Avalonia 和 TractorTool SDK 由宿主共享。
- 版本函数说明：[SDL_GetVersion](https://wiki.libsdl.org/SDL3/SDL_GetVersion)；版本数值按固定标签的 [SDL_version.h](https://github.com/libsdl-org/SDL/blob/release-3.2.8/include/SDL3/SDL_version.h) 解读。
