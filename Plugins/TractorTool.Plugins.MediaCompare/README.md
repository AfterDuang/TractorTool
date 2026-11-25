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
