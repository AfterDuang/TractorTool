# 运行库声明与开发材料

API 1.11 宿主从 `plugin.json` 列明的五个官方 GStreamer 1.28.9 Windows x64 wheel 下载、逐份校验，并按明确映射组成一个 `TractorTool.MediaCompare.GStreamer` DLL 运行库。使用默认 PyPI 来源，无需自有包源、镜像或 Release 附件，也不安装 Python 或执行脚本。旧单 ZIP 候选使用独立的 `GStreamer.MediaCompare` 标识，宿主不自动迁移或删除旧安装。当前实现与验收见[开发台账](../../../../Docs/DEVELOPMENT.md#media-compare)。

插件代码 ZIP 不包含 GStreamer DLL。完整许可材料 `licenses/`、`license-manifest.json`、`RUNTIME-NOTICE.md` 随插件保留；宿主单独记录 DLL 安装目录的全部文件摘要。消费者通过租约访问 `bin/`、`lib/gstreamer-1.0/`，声明入口是 `libraries.GStreamer = bin/gstreamer-1.0-0.dll`，不推导数据路径或读取旧插件内副本。

`plugin.json` 是运行时安装声明；`runtime-sources.json` 固定相同的上游 URL/SHA256、媒体模块与系统导入，用于开发阶段复核选择。`Prepare-GStreamerRuntime.ps1` 与 `PeImports.cs.txt` 仅用于经授权的独立诊断和版本升级时验证 DLL 导入闭包，不参与普通构建、宿主安装或发布。旧 `Build-GStreamerPackage.ps1` 已删除；不再生成或上传自维护运行库 ZIP。

当前映射选择 73 个既有 DLL，合计来自五份归档；重复出现的同名 DLL 先核对字节摘要一致，再只声明一份，宿主不按归档顺序覆盖。升级时须重新验证所选模块、完整传递 DLL、ABI、路径和许可，更新固定包身份与声明；不能只改下载地址。

`licenses/` 原文来自同版本官方安装包 `app/share/licenses/` 与其中 VC Redistributable 的英文 `license.rtf`，另补 Good、Ugly、libav 包装层 1.28.9 源码的 COPYING。`license-manifest.json` 记录逐份路径/摘要及上游来源；局部 `.gitattributes` 禁止改写原文换行。三个 MSVC DLL 来自官方 `gstreamer_ext_runtime` wheel，保持原有 14.44.35211 基线。许可材料不假定由五个 wheel 完整提供，不要求用户下载或执行安装器。

静态映射和文件摘要一致只证明所选字节未变。首次真实下载、拒绝/取消、离线复用、加载与进程期保护、修复，以及媒体和 GPU 效果仍由用户在 Windows 上验收。
