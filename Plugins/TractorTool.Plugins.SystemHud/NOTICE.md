# 桌面状态 HUD

功能方向参考 [GlacierGlimmer/zmd-charge-plus](https://github.com/GlacierGlimmer/zmd-charge-plus)，按 TractorTool 公开 SDK 独立实现；未复制其源码、图片、字体或动画资产。
基础采集使用 Windows API 与 .NET 计数器；硬件传感器通过未修改的 LibreHardwareMonitorLib 0.9.6 提供。插件不附带或自动安装 PawnIO 驱动，不自动提权。
私有运行依赖及许可原文见 Licenses 和下面的来源表。MPL 覆盖的库未修改，其对应源代码可从固定提交链接取得；TractorTool 自己的实现未复制这些库的源码。
Avalonia 和 TractorTool SDK 由宿主共享，不随插件分发。

| 组件 | 版本 | 许可 / 源码 |
| --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | MIT；打包脚本复制 NuGet 许可与第三方声明 |
| LibreHardwareMonitorLib | 0.9.6 | [MPL-2.0 源码](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701)；另附 LibreHardwareMonitor-ThirdParty.txt |
| DiskInfoToolkit | 1.1.2 | [MPL-2.0 源码](https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea) |
| BlackSharp.Core | 1.0.7 | [MPL-2.0 源码](https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0) |
| RAMSPDToolkit-NDD | 1.4.2 | [MPL-2.0 源码](https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce) |
| HidSharp | 2.6.4 | 原始 NuGet LICENSE.txt，含许可选择及第三方声明 |
| Mono.Posix.NETStandard | 1.0.0 | [NuGet 声明的许可来源](https://go.microsoft.com/fwlink/?linkid=869050)；原文快照 Mono.Posix.NETStandard.txt |
| System.IO.Ports / runtime.native.System.IO.Ports 及平台资产 | 10.0.3 | [MIT 源码](https://github.com/dotnet/dotnet/tree/c2435c3e0f46de784341ac3ed62863ce77e117b4) |
| System.Management / System.CodeDom | 10.0.2 | [MIT 源码](https://github.com/dotnet/dotnet/tree/44525024595742ebe09023abe709df51de65009b) |
| System.IO.FileSystem.AccessControl | 5.0.0 | [MIT 源码](https://github.com/dotnet/runtime/tree/cf258a14b70ad9069470a108f13765e0e5988f51)；仅在解析后的运行资产需要时随包 |

Licenses/sources.json 记录补充许可原文的下载地址（2026-10-07）；有 NuGet 源码提交的项均固定到对应版本。未篡改上游二进制或签名，未承诺所有硬件均支持读数。
