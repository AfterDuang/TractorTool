# 剪贴板历史插件

通过 Windows 系统 API 读取历史；不记录、自建或上传历史数据。支持文本与图片预览、复制原项；不可读取的格式显示原因。

构建使用 Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 提供的 C#/WinRT 投影和运行时，以及 CommunityToolkit.Mvvm 8.4.2（MIT）。Avalonia 与 TractorTool 公开 SDK 由宿主提供，不随插件重复分发。

包内 Licenses/CommunityToolkit.Mvvm.md 和 ThirdPartyNotices.txt 来自实际还原的 MVVM 包；CsWinRT.txt 来自 [Microsoft C#/WinRT](https://github.com/microsoft/CsWinRT/blob/master/LICENSE)，WindowsSDK.rtf 来自该 SDK NuGet 元数据声明的 [Windows SDK 许可](https://aka.ms/WinSDKLicenseURL)。来源文本于 2026-10-04 获取。这里不将整个 Windows SDK 简化称为 MIT。
