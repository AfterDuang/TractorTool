# 视频导出插件

使用 ffprobe 提取媒体信息，使用 FFmpeg 按多个预设串行导出；支持码率模式、首帧替换与 GPU 加速偏好，硬件不支持时回退 CPU 并说明原因。
FFmpeg 由宿主包管理器按需下载，不包含在插件 ZIP 内。
私有依赖 CommunityToolkit.Mvvm 8.4.2 的许可见 Licenses。
Avalonia 和 TractorTool SDK 由宿主共享，不随插件分发。

导航及设置图标使用 Microsoft Fluent System Icons 的 Video 彩色摄像机，与系统数据 HUD 图标同源。固定来源：[883bef1 的原始 SVG](https://github.com/microsoft/fluentui-system-icons/blob/883bef1e12d37343d0b2a64a1af43d0209de1128/assets/Video/SVG/ic_fluent_video_32_color.svg)。仅栅格化为透明 256×256 PNG，保留原始图形和配色；MIT 许可随包附于 `Assets/LICENSE-FluentSystemIcons.txt`。

## 外部工具来源

本插件清单固定 Gyan 的 FFmpeg / ffprobe 9.0.2 essentials Windows x64 构建，通过 GitHub Release 附件按需取得。上游将该构建标为 GPLv3；下载包内 LICENSE 与 README.txt 保留原样。

- Release / 对应源码：[GyanD/codexffmpeg 9.0.2](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2)
- 附件：`ffmpeg-9.0.2-essentials_build.zip`
- 构建和许可说明：[Gyan FFmpeg builds](https://www.gyan.dev/ffmpeg/builds/)
- SHA256：`60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`

版本、附件和校验值以随包 `plugin.json` 为准。
