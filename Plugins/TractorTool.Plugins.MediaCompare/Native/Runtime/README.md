# 运行库准备材料

`scripts/Prepare-GStreamerRuntime.ps1` 在构建期准备插件私有的 GStreamer 1.28.9 MSVC x64 运行库。默认下载缓存位于仓库 `artifacts/media-compare-runtime/downloads`，可交付目录为 `artifacts/media-compare-runtime/runtime`；项目将其复制到插件输出的 `Native/GStreamer`。二进制不进入 Git。

```powershell
pwsh -File Plugins/TractorTool.Plugins.MediaCompare/scripts/Prepare-GStreamerRuntime.ps1
```

脚本需要 Windows、PowerShell 7.4+。支持 `-OutputDirectory`、`-CacheDirectory`，均须位于当前工作区。首次下载约 102 MiB，后续构建核对输入指纹及完整文件 SHA256 后复用产物。输入变更或文件损坏时重新准备，旧的受管理产物保留为 `.replaced-<随机值>`，不覆盖未知目录。下载缓存的校验失败会停止构建。

`runtime-sources.json` 固定 5 个官方 wheel 的 URL、SHA256、媒体模块和许可目录；脚本将 wheel 作为 ZIP 读取，不需要或附带 Python，不执行安装器、不写注册表、不改 PATH。DLL 交付集合以生成的清单为准。3 个 MSVC DLL 来自官方 `gstreamer_ext_runtime` wheel，与安装包内 VC Redistributable 均为 14.44.35211。

`licenses/` 的原文文件来自同版本官方安装包 `app/share/licenses/` 及其中 VC Redistributable 的英文 `license.rtf`；另补 Good、Ugly 与 libav 包装层 1.28.9 源码中的 COPYING。安装包 URL/SHA256 记录在 `runtime-sources.json` 的 `licenseSource`；每份原文哈希及额外来源在 `license-manifest.json`。普通构建直接使用这些入库文本，不下载完整安装器或额外解包工具。局部 `.gitattributes` 禁止 Git 改写许可证换行，避免破坏来源校验。许可材料、声明和来源/校验清单随 `Native/GStreamer` 一起复制，项目无需重复添加一套许可内容。

`PeImports.cs.txt` 只供准备脚本编译调用，通过 `PEReader` 解析普通及延迟导入，不加载 DLL；使用 `.txt` 避免成为插件业务程序集的一部分。脚本从所选媒体模块递归计算 `bin/` 导入闭包，强制 x64，未知非系统依赖使构建失败。不携带整个 SDK、可执行工具、GStreamer 外部扫描器或 Python 模块。D3D11 的系统动态加载依赖（如 Windows 自带 `d3dcompiler_47.dll`）不打包。

`runtime-manifest.json` 记录每个交付文件的大小/哈希与 DLL 导入，`payloadBytes` 不包括清单自身；控制台统计完整目录实际大小。运行库预算为 240 MiB，为插件程序集留下余量。宿主安装器限制 ZIP 及展开总量为 256 MiB；通用打包脚本不会代替安装入口检查该上限，交付时仍须核对最终插件包体积。

这一步只证明来源、布局、依赖闭包和体积。真实解码、图像序列、同步播放、声音切换和 GPU 渲染由用户在 Windows 上验收。
