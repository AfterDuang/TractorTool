# 原生运行库夹具

仅用于隔离测试，不随正式分发自动打包。需要支持 API 1.8 的宿主；现有工具包夹具仍见 [PackageProbePlugin](../PackageProbePlugin/ProbePlugin.cs)。

`plugin.json` 声明真实官方 SDL 3.2.8 Windows x64 ZIP。`ProbePlugin` 按需申请、先登记进程期占用，再通过 `SdlVersionProbe` 用绝对路径加载并读取版本；普通租约结束后仍保留 DLL 句柄及安装资产，直到进程退出。初始化、切页和隐藏窗口不触发下载。

用 [Build-Plugin](../../scripts/Build-Plugin.ps1) 和本轮发布的宿主 SDK 构建独立 ZIP。用户运行时使用隔离的宿主目录及 `--profile-root` 数据目录，按[插件验收流程](../../Docs/PLUGIN_DEVELOPMENT.md#6-开发构建与安装验证)安装后重启。

本轮已生成的待测目录为 `artifacts/native-runtime-20261127-final`。在仓库根目录的 PowerShell 执行以下命令可使用独立数据目录启动（尚未代为执行）：

```powershell
& ./artifacts/native-runtime-20261127-final/host/TractorTool.exe --profile-root H:/ProjH/TractorTool/artifacts/native-runtime-20261127-final/profile
```

在插件页安装 `artifacts/native-runtime-20261127-final/plugin/Test.NativeLibraryProbe-1.0.0.zip`，显式退出后仍用上述命令启动。该宿主目录只用于此次验收，插件安装不会写入日常软件目录。

1. 点击“申请运行库并读取版本”，拒绝安装后应可重试；允许后应显示 `SDL 3.2.8 版本检查通过`。
2. 在设置的 Packages 查看类型、GitHub 来源、版本、位置 `Packages/DLL/SDL3/3.2.8/win-x64` 和退出前保护说明。重复点击应复用同一安装。
3. 切换页面、隐藏到托盘再返回，结果保留；已登记的保护不因普通租约释放而消失。
4. 显式退出并重启后，占用应清空；插件仍引用此包时不可清理。卸载夹具并重启后，可在 Packages 清理运行库。
5. 完整校验的故障检查仅限隔离数据：退出宿主后修改运行库中的 `README.md`，重启并重新申请应报校验失败，可经宿主修复；不要修改或删除用户实际安装的数据。

上述是待执行方法，编译成功和已核对官方归档不代表 DLL 加载、网络或界面运行验收通过。
