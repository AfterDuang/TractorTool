# 视频导出插件

使用 ffprobe 提取媒体信息，使用 FFmpeg 按多个预设串行导出；支持码率模式、首帧替换与 GPU 加速偏好，硬件不支持时回退 CPU 并说明原因。
FFmpeg 由宿主包管理器按需下载，不包含在插件 ZIP 内。
私有依赖 CommunityToolkit.Mvvm 8.4.2 的许可见 Licenses。
Avalonia 和 TractorTool SDK 由宿主共享，不随插件分发。
