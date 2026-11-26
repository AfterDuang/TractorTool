# Node 环境夹具

仅用于隔离验收，不随正式分发自动打包。需要 Contracts API 1.10、Avalonia 12.1.6 的 Windows x64 宿主。

`plugin.json` 声明 Node 24.22.3 和 `Node/package.json` / `Node/pnpm-lock.yaml` 的 SHA256。固定 pnpm 为 10.34.9，唯一 npm 依赖为 `is-number@7.0.0`，锁中 SHA512 来自 npm 官方该版本元数据。插件不含安装脚本、自有下载器或 node_modules。

点击“准备环境并执行”才调用 `TryRequestNodeAsync("worker")`。同意后由宿主准备环境，插件通过租约运行 Node，用 `createRequire(<lease.Directory>/package.json)` 显式导入环境依赖，不依赖系统 `NODE_PATH`。检查 Node 和包版本、数字判断及 stdout/stderr 中文输出。初始化和切页不触发下载。

使用 [Build-Plugin](../../scripts/Build-Plugin.ps1) 和当前已发布的开发宿主 SDK 打包。用户在独立宿主目录中以 `--profile-root` 指向隔离数据目录，安装 ZIP 后显式退出并重启。修改两份输入后须重新计算清单 SHA256；Git 换行固定为 LF。

用户验收：首次拒绝后重试、同意安装与真实执行；Packages 的 Node 分类、版本/来源/引用与 `Packages/Node` 目录；断网复用；取消后重试；清缓存后仍可执行；有引用时禁止清理，卸载并重启后可清理；迁移到空目标后重建可用。故障及取消覆盖结果须单独记录，构建不代表运行通过。
