# Python 环境夹具

仅用于隔离验收，不随正式分发自动打包。需要 API 1.9、Avalonia 12.1.6 的 Windows x64 宿主。

`plugin.json` 声明 Python 3.13.18 及 `Python/pyproject.toml` / `Python/uv.lock` 的实际 SHA256。锁由固定 uv 0.13.10 生成，依赖为真实 PyPI `idna==3.11` wheel。插件没有自有下载器、源码构建或安装脚本，也不携带 Python、uv 或已安装环境。

`ProbePlugin` 仅在点击“准备环境并执行”后调用 `TryRequestPythonAsync("worker")`。用户同意后由宿主准备环境；插件通过租约的 `RunAsync` 执行解释器，检查 idna 实际版本、中文域名编码及 stdout/stderr 中文结果。UTF-8 运行参数由宿主注入，插件没有重复设置。初始化、切页和隐藏窗口均不发起下载。

使用 [Build-Plugin](../../scripts/Build-Plugin.ps1) 和本轮发布的宿主 SDK 打包。用户在独立宿主目录中用 `--profile-root` 指向新的隔离数据目录，再安装此 ZIP 并显式退出重启；不在日常数据目录做破坏性检查。步骤见[插件验收](../../Docs/PLUGIN_DEVELOPMENT.md#6-开发构建与安装验证)。

本轮现成产物为[修正 uv 参数后的宿主](../../artifacts/python-environment-20261127-cli-fix/host)和[原测试 ZIP](../../artifacts/python-environment-20261127-final/plugin/Test.PythonEnvironmentProbe-1.0.0.zip)。原 `python-environment-20261127-final/host` 存在安装参数互斥问题，不再用于验收。在仓库根目录的 PowerShell 中启动（仅供用户执行，Agent 未启动）：

```powershell
& .\artifacts\python-environment-20261127-cli-fix\host\TractorTool.exe --profile-root H:\ProjH\TractorTool\artifacts\python-environment-20261127-cli-fix\profile
```

每次重启沿用这个参数。安装 ZIP 后，显式退出托盘程序再启动；首次下载需要访问官方 GitHub 与 PyPI。

待执行场景：

1. 首次点击后拒绝准备，再次点击仍可申请；同意后显示 `idna 3.11：测试 → xn--0zwm56d`。
2. Packages 显示整体 Python 环境、插件引用、Python/uv 版本、PyPI 来源及 `Packages/uv` 存储位置；无独立 Python/uv 全局安装要求。
3. 断网后重复点击仍能复用已安装环境；切页、隐藏窗口不取消已开始的操作。显式取消可重试。
4. 保持夹具安装时环境不能清理；卸载夹具并重启后，未使用环境允许清理。清理不删除插件业务数据。
5. 仅针对隔离数据迁移到另一空目录，重启后再次调用；成功说明目标解释器与环境重建可用。中断/离线恢复另按框架验收台账验证。
6. Packages 的“清理 Python 下载缓存”完成后，已装环境仍能离线调用；环境准备期间不允许并发清理缓存。

修改项目或锁后必须重新计算清单摘要。两文件的 Git 换行固定为 LF，避免不同系统检出改变摘要。构建成功和锁生成成功不等于上述运行场景通过。
