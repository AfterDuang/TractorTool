# MediaCompare 原生媒体运行库

本目录是 GStreamer 1.28.9 官方 MSVC x64 二进制的未修改子集，仅供本插件使用。GStreamer、FFmpeg、GLib 等以动态 DLL 链接，未静态合并进插件。许可证原文及上游声明在 `licenses/`，每个交付文件的 SHA256、导入闭包和来源记录在 `runtime-manifest.json` / `runtime-sources.json`。

- GStreamer 及插件源代码：https://gstreamer.freedesktop.org/src/ （1.28.9）
- 官方构建配方、依赖版本、补丁和编译方法：https://gitlab.freedesktop.org/gstreamer/cerbero/-/tree/1.28.9
- 官方二进制来源：https://gstreamer.freedesktop.org/data/pkg/windows/1.28.9/msvc/
- 许可说明：https://gstreamer.freedesktop.org/documentation/frequently-asked-questions/licensing.html
- FFmpeg 源码与构建配置由同版本 Cerbero `recipes/ffmpeg.recipe` 指定；本包使用官方 LGPL 版 FFmpeg，未选择 GStreamer 的 GPL 编解码插件。
- `gstlibav.dll` 包装层的原文许可来自 [gst-libav 1.28.9 COPYING](https://github.com/GStreamer/gstreamer/blob/1.28.9/subprojects/gst-libav/COPYING)，不以 FFmpeg 许可替代；MP4/Matroska/图片等 Good 模块对应 [gst-plugins-good 1.28.9 COPYING](https://github.com/GStreamer/gstreamer/blob/1.28.9/subprojects/gst-plugins-good/COPYING)，分别存于 `licenses/gst-libav-1.0` 和 `licenses/gst-plugins-good-1.0`。
- `FLAC-8.dll` 对应 `licenses/flac/COPYING.Xiph`；`flac/bins/` 为上游原文中另外列出的命令行工具通知，本插件不分发这些命令行工具。
- Visual C++ 运行库 14.44.35211 从 GStreamer 官方 `gstreamer_ext_runtime` wheel 提取。`licenses/msvc-runtime/license.rtf` 原样来自同版官方安装包内 `vc_redist.x64.exe` 的英文许可，来源与 SHA256 已记录。适用 Microsoft 条款，不能以 GStreamer 的 LGPL 许可替代。

支持替换这些动态库，但替换者须维持 GStreamer 1.x / 当前组件的二进制 ABI 和目录结构，并承担兼容性验证。插件不修改系统 PATH，不安装系统编解码器，不附带 Python、开发头文件、静态库、测试程序或全局 GStreamer 插件扫描器。原生媒体能力依赖具体插件、系统和显卡；文件清单核验不等于真实格式或硬件验收。
