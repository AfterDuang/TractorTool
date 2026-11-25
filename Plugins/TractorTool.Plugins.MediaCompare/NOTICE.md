# 第三方组件

- GStreamer 1.28.9：官方 Windows x64 分发，来源与 SHA256 固定在本插件的运行库准备脚本中；原文许可与文件清单随运行库保留。
- Vortice.Windows 3.8.3（Direct3D11、D3DCompiler 与实际传递依赖）：MIT，https://github.com/amerkoleci/Vortice.Windows 。
- Vortice.Mathematics 2.1.0、SharpGen.Runtime/COM 2.4.2-beta：MIT，原文在 `Licenses`。
- CommunityToolkit.Mvvm：插件私有 NuGet 依赖，构建脚本按实际版本收集原文许可。
- Avalonia、TractorTool.Contracts、TractorTool.UI：由兼容宿主提供，插件不重复分发。

本插件不安装系统编解码器，不修改系统 PATH；运行库准备工具不随插件交付。

导航图标使用 Microsoft Fluent System Icons 的 Image 彩色图标，表示视频、图片及序列帧的画面对比，与视频导出的摄像机图标区分。固定来源：[883bef1 的原始 SVG](https://github.com/microsoft/fluentui-system-icons/blob/883bef1e12d37343d0b2a64a1af43d0209de1128/assets/Image/SVG/ic_fluent_image_32_color.svg)。仅栅格化为透明 256×256 PNG，保留原始轮廓和配色；MIT 许可随包附于 `Assets/LICENSE-FluentSystemIcons.txt`。

NuGet 私有库许可原文按包元数据中的源码提交取得：

- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows/blob/9e609cb9439c9872aa1b339f177e40ec96f77239/LICENSE)
- [Vortice.Mathematics](https://github.com/amerkoleci/Vortice.Mathematics/blob/fa05ec6dcba48f3f7331791da6dc7f3d866b2ad6/LICENSE)
- [SharpGenTools](https://github.com/SharpGenTools/SharpGenTools/blob/6990bcafe124a4c22515ad19cee5a081da8db67b/LICENSE.txt)
