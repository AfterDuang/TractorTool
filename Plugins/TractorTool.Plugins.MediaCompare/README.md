# AB 审片源码

从 [plugin.json](plugin.json) 与 [ComparePlugin.cs](ComparePlugin.cs) 开始读。

| 修改内容 | 入口 |
| --- | --- |
| 播放、定位、帧配对 | [Media](Media) |
| 原生 ABI 与加载 | [Native/Interop](Native/Interop) |
| GPU 资源与缩放 | [Rendering](Rendering)、[预览控件](Views/ComparePreview.cs) |
| 操作状态与界面 | [ViewModels](ViewModels)、[Views](Views) |

业务设计与验收方法见 [MediaCompare](../../Docs/Plugins/MediaCompare.md)；当前状态只见[开发台账](../../Docs/DEVELOPMENT.md#media-compare)。依赖与发布遵循[插件开发规范](../../Docs/PLUGIN_DEVELOPMENT.md)。

常规构建自动准备并复制私有运行库，来源和离线复用见 [运行库材料](Native/Runtime/README.md)。纯编译可传 `-p:SkipMediaCompareRuntime=true`，此时输出不构成可安装插件。正式 ZIP 使用仓库 `Build-Plugin.ps1` 和已发布的 `HostSdkDir`；不要从源码引用的 Debug 输出手工压包，避免混入共享框架 DLL。

## 独立播放诊断

[检查工程](../../tests/TractorTool.MediaCompareChecks) 默认只运行纯托管检查。定位播放阻塞时，获得运行授权后可显式启用真实 Win32/GPU 路径；不启动宿主或读取用户设置。

```powershell
# 仓库根目录：先编译；运行库和合成素材须已准备好。
dotnet build tests/TractorTool.MediaCompareChecks/TractorTool.MediaCompareChecks.csproj -c Release -p:SkipMediaCompareRuntime=true
```

```text
TractorTool.MediaCompareChecks.exe --gpu-playback <runtimeRoot> <newScratchDirectory> <syntheticA> <syntheticB>
```

可执行文件位于 `tests/TractorTool.MediaCompareChecks/bin/Release/net10.0`。四个参数均为绝对路径：`runtimeRoot` 是含 `bin` 的 GStreamer 私有运行库根，`newScratchDirectory` 必须不存在，后两项为自行生成、至少 8 秒的 SDR 视频；建议 12 秒固定/可变帧率组合。独立窗口在屏幕外且不激活；日志、运行库缓存只写入本次临时目录。标准输出重定向文件放在该目录之外，以免提前创建目录而被拒绝。

启动者需保存诊断 PID 并外部监控 `diagnostic.log`；原生阻塞时先采集线程/转储，再仅终止此进程，不能依赖内部超时取消原生调用。退出成功不是可见 UI、音画正确性或完整 GPU 清理验收，详细判据见[验收方法](../../Docs/Plugins/MediaCompare.md#验收方法)；本轮结果只记录在开发台账。
