# TractorTool 开发与验收

更新日期：2026-11-27。本文是**唯一的当前实现和验收台账**。设计见 [架构](ARCHITECTURE.md)，简短交接见 [开发上下文](PROJECT_CONTEXT.md)，旧命令、实施过程与逐轮证据见 [项目历史](PROJECT_HISTORY.md)。

入口：[当前实现](#current-status) · [开发操作](#workflow) · [本地产物](#local-artifacts) · [验收台账](#acceptance) · [下一步](#next)。

<a id="current-status"></a>

## 1. 当前状态

**1.1.0 分发包含 Windows x64 宿主和四个 1.1.0 插件。** 产品和插件分别定版，沿用 Avalonia 12.1.6。插件私有扩展、外部工具和发布验证见 [E137](PROJECT_HISTORY.md#evidence-release-110)。正式标签经构建和校验后自动公开附件。

源码新增独立 [AB 审片 0.1.7](#media-compare) 开发版；未改变上述已发布版本。后续整包构建会按现行规则自动发现该插件，不新增名单或框架依赖。

仍待真实 Windows 环境验收的项目包括跨会话安装、断电恢复、大目录迁移、设备采样和媒体导出。切页末段闪动继续使用官方渲染后端，等待上游修复。详见[下一步](#next)及[精度对照](PROJECT_HISTORY.md#evidence-page-flicker-precision)。

下表概括当前实现；验证证据单列，详细命令与过程归[项目历史](PROJECT_HISTORY.md#记录索引)，不把历史构建或常规使用反馈扩写为新代码的专项验收。

| 范围 | 当前实现 |
| --- | --- |
| 工程与版本 | VS Code，项目清单见 [TractorTool.slnx](../TractorTool.slnx)。SDK 见 [global.json](../global.json)，框架 NuGet 版本见 [Directory.Packages.props](../Directory.Packages.props)，插件业务私有库版本见所属插件的工程文件。宿主产品版本见 [Directory.Build.props](../Directory.Build.props)，插件版本取自各自的 `plugin.json`。分发脚本要求 PowerShell 7.4+ |
| 系统数据HUD | 硬件按类型显示图标/示例，CPU 访问条件明确提示，重新检测后清读数缓存；E93 独立采样和 E95 精简保持。E96 离屏回归通过。E94 两项 P2 仍待修复，CPU 温度实读/原生交互/DPI/高频性能待验，托盘方案列表暂缓 |
| 公共接口 | Dev 为 Contracts API 1.10 / UI API 1.6；增加 Node 环境申请，保留 Python 及子进程环境覆盖、DLL 入口和进程期保护。旧接口签名及 API 1.0–1.9 继续支持，能力按声明限制。UI 1.6 支持插件私有 Avalonia 扩展 |
| 插件与公共 UI | 独立装载、页面/设置注册、常驻生命周期；ZIP 管理与启停/卸载/回退允许重启生效，预期状态冲突正常返回原因。共享主题/动效、层级设置及面包屑；软件/插件设置分组可独立折叠，标题区 32 DIP，仅保留小图标和常规字重软件名，专属布局留在各页面 |
| 插件托盘 | 按插件名分组，注册普通/勾选动作并更新启用/文字状态；重复点击保护、统一日志、停止取消及解绑；切页/隐藏继续存在。只提供公共菜单接入，不实现悬浮球业务或任意深度菜单树 |
| 剪贴板 | Fluent 彩色剪贴板图标共用于侧栏、主页和插件管理，Avalonia 保持 12.1.6；系统历史、左列表/右预览、复制原项、事件与激活刷新；WinRT 操作完成后检查取消，避免原生 Cancel 中断离页清理。无自建历史库或定时轮询 |
| 文件夹历史 | 文件树状态角标与稳定快照编号。历史行支持选区恢复、另存、提交为最新，内容对比覆盖纯文本、.docx 和 .xlsx |
| 依赖与进程 | GitHub Release 精确附件解析；默认 GitHub/可添加公开 Enterprise API 来源；Packages 显示已安装包、首次下载及修复进度，列出来源、许可和引用插件。首次确认、hash/ZIP/版本校验、离线复用、引用/租约保护及旧记录恢复。Contracts ABI 不变，Core 无 FFmpeg 专属目录。公共进程支持参数向量、双流、超时/取消和 Windows Job |
| 视频 | 导航/设置使用与 HUD 同系列的 Fluent 彩色 Video 图标；MediaInfo 风格报告，无播放/预览；五种容器、VBR 一次/两次/CBR、多预设串行、GPU 偏好及自动 CPU 回退。输出“原文件名_预设名”，冲突追加数字 |
| 公共任务 | 整批持久化登记、逐项执行、单项/整批取消；日志汇总全部插件任务并按 PluginId 打开所属工具。Data/tasks.json 保留活动项和最近 100 条终态，重启标中断、不自动续跑；坏摘要保留并阻止新执行，可重试保存 |
| 日志 | INFO 绿/WARNING 黄/ERROR 红；筛选、复制、暂停、折叠详情，旧上新下、底部跟随，上翻不拉回。“已读”确认点击时全部错误，新错误再提醒。内存 300 条，普通导航不记录 |
| M5 日志保留 | 10 MiB 分段，UTC 跨日/大小滚动，14 天、最多 20 文件/100 MiB；只清理顶层宿主日志，失败警告后继续。单条磁盘记录 64 KiB，完整原文保留在内存窗口 |
| M5 数据迁移 | 设置安排/重试/取消；启动先复制 Config/Data/Packages/Logs、hash 校验、持久化源清理清单及原子切换，再重绑回退记录并清理旧 Config/Data/Packages/Logs/Cache；未知根项/bootstrap/锁保留。确认自定义目录删除且无迁移时恢复默认初始化，不重建自定义根。启动/迁移/失败共用唯一主窗口 |
| M5 分发 | 随包工程从 Plugins 一级子目录自动识别，ID/版本读取 plugin.json。生成宿主 ZIP、独立插件 ZIP、SHA256SUMS、版本/源码和文件清单，按实际依赖收集宿主许可。本地与 CI 共用打包脚本。标签经独立校验后公开 Release，普通 CI 上传构建产物 |
| M5 安装器 | Inno Setup 6.7.3，当前用户可写目录；从已校验 ZIP 编译，首次附带插件、升级保留现有 Plugins，卸载保留插件及应用数据。首个文件写入前登记安装事务，同包可在原目录恢复。 |
| 插件打包边界 | Build-Plugin 支持无 MVVM 的插件；仅随包包含 MVVM DLL 时复制对应许可，缺失仍失败；保留插件自备 Licenses。其他私有库许可由作者维护，不声称全部自动收集；宿主验证也复用该打包入口 |
| DLL 运行库 | Dev 支持 GitHub 原生 DLL ZIP，分类目录、完整载荷摘要、Windows PE 架构检查、进程期占用与管理页提示；宿主不加载 DLL，插件登记占用后自行加载。旧工具路径与 schema 1/2 收据保留。实现证据及待验边界见 [E147](PROJECT_HISTORY.md#evidence-native-library-packages) |
| Python 环境 | Dev 新增固定 uv/CPython、项目/锁快照、单索引 wheel 安装、最终代次、完整文件复核及受管调用；默认 PyPI/兼容索引进入来源页，Packages 管理整体环境。迁移在新根重建，构建与运行状态见 [A31/A33](#dependency-extension-plan) |
| Node 环境 | Dev 新增固定 Node/pnpm、原始锁快照、单 registry 冻结安装、受管调用、npm 来源和 Node 分类；Python/Node 共用事务及目标重建，不放宽目录链接策略，见 A32/A33 |
| 未实现的扩展 | 私有源认证、其他安装类型、源码构建/安装脚本、机器级安装、非 Windows 进程适配器及 AI 扩展仍未实现，不再有自有源部署任务 |

### 验证证据入口

框架隔离回归见 [E135](PROJECT_HISTORY.md#evidence-release-100) / [E137](PROJECT_HISTORY.md#evidence-release-110)，官方 Avalonia 升级见 [E117](PROJECT_HISTORY.md#evidence-avalonia-1216-upgrade)；命令、数量、失败过程和产物哈希只保存在对应历史记录。各项当前结论及未覆盖范围统一见[验收台账](#acceptance)，历史通过不自动覆盖新代码。

用户负责真实 Windows 窗口、安装和硬件验收。切页闪动仍未修复，E94 HUD 两项 P2 及 E99 进程清理测试缺口继续留在[下一步](#next)；归档不代表关闭问题。

<a id="dependency-extension-plan"></a>

### 外部依赖扩展

`Dev` 已完成 DLL、uv/Python 和 pnpm/Node 三类接入。2026-11-27 用户反馈本轮 Node 测试没有问题，授权整理文档并本地提交；**未推送、未发布**，正式版 1.1.0 不变。设计见[架构 7.3](ARCHITECTURE.md#dependency-extension)，选型与实施证据见 [E146](PROJECT_HISTORY.md#evidence-dependency-extension-plan)、[E147](PROJECT_HISTORY.md#evidence-native-library-packages)、[E148](PROJECT_HISTORY.md#evidence-python-environments)、[E150](PROJECT_HISTORY.md#evidence-node-environments)。

| 顺序 | 实施范围与阶段出口 | 当前状态 |
| --- | --- | --- |
| 1. 独立原生运行库 | 扩展声明、完整文件校验、收据兼容及进程期保护；以真实消费者验证申请、加载、占用阻止修复/删除和重启后维护 | 已实现，构建通过，SOL/high 最终静态复审无新阻断；用户测试暂未发现问题并同意提交，专项缺口见 A30 |
| 2. uv / Python | 固定工具链、按锁安装、插件隔离环境及逐进程环境配置；用真实 PyPI 依赖验证首次准备、调用、拒绝重试与离线复用 | 已实现；构建/独立打包和静态复核完成，CLI 互斥已修正；用户反馈当前版本暂未遇问题并允许提交，专项缺口见 A31/A33 |
| 3. pnpm / Node | 固定 Node/pnpm、按锁安装、hoisted+copy 无链接布局和禁用安装钩子；真实 npm 夹具用于调用、取消及环境维护验收 | 已实现；构建、静态复核及独立打包见 E150；用户反馈测试没有问题并允许提交，专项缺口见 A32/A33 |

两类语言环境共用确认、事务、租约、缓存维护和迁移保护，保留旧 Python 记录格式与公开接口；当前仅 Windows x64，工具链版本、清单及限制统一见 [Python](PLUGIN_DEVELOPMENT.md#python-environments) / [Node](PLUGIN_DEVELOPMENT.md#node-environments)。新分类与旧包重名保护、最终路径重建和故障恢复仍须按 A33 补齐专项运行证据。

真实来源夹具及运行步骤：[DLL](../tests/NativeLibraryProbePlugin/README.md)、[Python](../tests/PythonEnvironmentProbePlugin/README.md)、[Node](../tests/NodeEnvironmentProbePlugin/README.md)，均不随正式分发；当前交付入口见[开发测试产物](#development-artifacts)。用户反馈未逐项列出场景，新增自动检查仅编译，NU1900 表示在线漏洞数据查询未完成；不将本轮反馈扩写为 A30–A33 全项通过。

<a id="media-compare"></a>

### AB 审片开发版

- 实现：0.1.7 优化拖动定位调度与定位后续播复用，保留现有媒体呈现、设备锁和音画配对；未改框架或运行库。实施及复审过程见 [E145](PROJECT_HISTORY.md#evidence-media-compare-scrub-optimization)。
- 验证：构建、静态 Review、独立打包及文件哈希核对通过；NU1900 导致漏洞扫描未完成。按用户要求未运行本轮检查或测量时延；旧版运行证据不外推为 0.1.7 通过。
- 限制：未知帧时长且无固定帧率，或真正长空窗内的非零准确定位/续播仍可能被拒绝；没有邻帧索引。稀疏 PTS 连续播放呈现停帧，不代表已验证素材连续性。预览为未校准 SDR 8 位、同 GPU；没有 HDR/Log、长 GOP 代理/邻域缓存或跨 GPU 支持。容器音频尾段可能没有视频帧；无标记素材不能推定色彩准确。
- 产物见[开发测试产物](#development-artifacts)，构建日志与包核对记录见 [E145](PROJECT_HISTORY.md#evidence-media-compare-scrub-optimization)。安装后需显式退出重启，无需另装运行库。
- 接续：0.1.7 已提交并推送 `e025a04`；此前 0.1.6 已推送 `1448d7c`。用户手感反馈“也还行”，不等于连续正倒拖、松手后续播、暂停再播放及 A/B 音画对应逐项通过，也不能宣称达到 PotPlayer/PR 流畅度。实际扬声器、Explorer 拖放、可见 UI、驱动资源回收及长 GOP 性能仍待验。方法见[插件文档](Plugins/MediaCompare.md)。运行库统一申请仍须交架构侧。

<a id="workflow"></a>

## 2. 开发与验收入口

使用根 `TractorTool.slnx` 和现有 **Avalonia: TractorTool** 启动项。新增属性后若预览器提示属性不存在，先确认构建结果；构建已通过时，按 `Ctrl+Shift+P` 执行 **Avalonia: Restart Previewer**。尚未构建时可选 **Avalonia: Rebuild Project and Restart Previewer**；仍有旧提示再执行 **Developer: Reload Window**。上述 Avalonia 命令已核对本机扩展12.4.3。不能将预览器旧诊断当作编译失败，也不通过移除绑定或改 IDE 配置掩盖问题。

外部依赖入口为“设置 → 软件设置 → 外部依赖 → Packages / 依赖来源”。默认 GitHub/PyPI/npm 配合相应插件声明使用，无需部署索引；具体规则见 [GitHub 依赖](PLUGIN_DEVELOPMENT.md#package-source-maintenance) / [Python](PLUGIN_DEVELOPMENT.md#python-environments) / [Node](PLUGIN_DEVELOPMENT.md#node-environments)。未接入的类型不提供空配置入口。

Packages 在面包屑下按“外部工具、DLL 运行库、Python 环境、Node 环境”切换；搜索、统计与批量清理范围保持一致，确认期间切类不更换已列出的清理对象。缓存按钮仅作用于当前语言分类。实现与用户反馈见 [E149](PROJECT_HISTORY.md#evidence-package-categories) / [E150](PROJECT_HISTORY.md#evidence-node-environments)，验证状态统一见 A30–A33。

**交互式本地发布：终端 → 运行任务 → TractorTool：本地发布 → 选择 ZIP 或 ZIP＋Windows 安装包。** 配置见 [tasks.json](../.vscode/tasks.json)，自动使用 `artifacts/local-publish-日期时间-随机后缀/distribution/assets` 保存最终产物，不覆盖旧目录。需 PowerShell 7.4+（`pwsh` 在 PATH 中）；安装包模式会下载并验证固定便携编译器。版本沿用工程默认，流程复用本地/CI 脚本，不自动安装、启动或上传，也不改变 F5 启动项。详细操作见 [项目首页](../README.md#构建分发包)。

以下是操作入口，**不是本轮执行记录**；运行部分由用户决定。仓库根目录：

```powershell
dotnet restore .\TractorTool.slnx
dotnet build .\TractorTool.slnx -c Debug -m:1 -nr:false
# 隔离启动；默认 F5 没有隔离参数
dotnet run --project .\TractorTool\TractorTool.csproj -- --profile-root H:\ProjH\TractorTool\artifacts\manual-profile
# 发布到新空目录，正式插件另行打包
.\scripts\Publish-Host.ps1 -Output H:\ProjH\TractorTool\artifacts\manual-publish
# PowerShell 7.4+：完整分发，目标必须尚不存在；版本默认来自 Directory.Build.props
.\scripts\Build-Distribution.ps1 -Output H:\ProjH\TractorTool\artifacts\manual-distribution
# 如需同时生成安装器，先在新目录准备固定版本便携编译器，再输出到另一新目录
$compiler = .\scripts\Get-InnoCompiler.ps1 -Output H:\ProjH\TractorTool\artifacts\manual-inno
.\scripts\Build-Distribution.ps1 -Output H:\ProjH\TractorTool\artifacts\manual-installer -CompilerPath $compiler
# 仅核对产物文件，不运行应用
.\scripts\Verify-Distribution.ps1 -AssetsDir H:\ProjH\TractorTool\artifacts\manual-distribution\assets
```

`--profile-root` 要求绝对路径，隔离启动指针及默认数据；不带参数访问真实 `%LOCALAPPDATA%\TractorTool`，该参数不等于迁移。插件仍在所启动宿主的 AppRoot/Plugins，不随 profile 切换。**迁移/故障测试使用独立发布副本、隔离 profile 与新的空目标，不修改真实 bootstrap 或用户文件。**

当前启动使用官方 Avalonia 默认渲染；已撤回的诊断开关、候选包与旧命令不可用于当前验证，历史材料见 [E114–E116](PROJECT_HISTORY.md#evidence-page-flicker-precision)。切换构建前需从托盘显式退出旧进程，关闭窗口仅隐藏。

SDK 版本由根 global.json 与 CI 共用。ZIP 只解压到新目录，不覆盖旧安装；setup 默认安装到 `%LOCALAPPDATA%\Programs\TractorTool`，仅接管其管理的安装或新空目录。用户接手验证时显式选择独立测试安装位置，安装后用隔离 profile 启动，不直接覆盖日常环境。相同已装插件版本无需重装；日常源码构建不会替换已装 DLL。独立打包见 [插件规范](PLUGIN_DEVELOPMENT.md#6-开发构建与安装验证)，变更后显式退出重启，关闭窗口仅隐藏。

<a id="local-artifacts"></a>

### 本地产物与正式发布

#### 正式版 1.1.0

[1.1.0 发布页](https://github.com/AfterDuang/TractorTool/releases/tag/v1.1.0) 提供 Windows x64 整包、同源安装器及四个独立插件 ZIP。`release.json` 记录源码提交及各插件身份，配合 `SHA256SUMS` 和 [Verify-Distribution](../scripts/Verify-Distribution.ps1) 可核对附件。

开发版升级、同版本与较高版本插件的处理步骤见[开始使用](../README.md#开始使用)。验证新版本时，将整包解压到新目录，用独立的 `--profile-root` 启动。安装器升级会保留原 Plugins。

<a id="avalonia-1216-upgrade"></a>
<a id="video-icon-update"></a>
<a id="clipboard-icon-update"></a>
<a id="folder-history-first"></a>

此前独立开发包的证据见 [Avalonia 12.1.6 升级](PROJECT_HISTORY.md#evidence-avalonia-1216-upgrade)、[视频图标](PROJECT_HISTORY.md#evidence-video-color-icon)、[剪贴板图标](PROJECT_HISTORY.md#evidence-clipboard-color-icon)和[文件夹历史预览](PROJECT_HISTORY.md#evidence-folder-history-preview-cancellation)。真实 Windows、媒体和硬件场景按下方台账分别验收。

历史产物路径及停用状态归[旧开发产物快照](PROJECT_HISTORY.md#historical-artifacts)与各轮 E 记录，不作为当前源码的运行入口。

<a id="development-artifacts"></a>

#### Dev 开发测试产物

| 产物 | 用途与证据 |
| --- | --- |
| [Node 接入宿主](../artifacts/node-environment-20261127-reviewed/host) | DLL/Python/Node 当前开发基线，Contracts API 1.10；构建、打包与静态复核见 E150。用户反馈测试没有问题，专项缺口见 A30–A33 |
| [Node 测试插件](../artifacts/node-environment-20261127-reviewed/plugin/Test.NodeEnvironmentProbe-1.0.0.zip) | 配合上述宿主，通过“准备环境并执行”调用真实 npm 依赖；[验收方法](../tests/NodeEnvironmentProbePlugin/README.md)，不进入正式分发 |
| [AB 审片 0.1.7](../artifacts/media-compare-0.1.7-final/TractorTool.MediaCompare-0.1.7.zip) | 插件独立开发包；能力及运行缺口见[当前状态](#media-compare)，证据见 E145 |

宿主使用独立 `--profile-root`；安装插件后显式退出重启。DLL/Python 的旧交付包与命令保留在 E147/E148，重建时使用当前源码和宿主 SDK。生成产物不入库，路径仅在对应本地工作区可用。

需要运行或完整分发时按[开发入口](#workflow)从当前源码生成新目录，独立安装插件后显式退出重启；不要拼用历史副本，也不要把机制夹具当正式业务插件。托盘验证方法见[接入规范](PLUGIN_DEVELOPMENT.md#tray-menu)。

### 当前人工回归重点

下表是回归方法索引，不表示全部仍未执行；E62 已记录用户本机测试反馈，最新状态只记入第 4 节。完整插件步骤链接到对应业务文档，避免重复维护。

| 范围 | 本轮及后续需要核对 |
| --- | --- |
| 依赖源与第二工具 | E60 GitHub 源、Packages、真实 FFmpeg/ripgrep 下载声明均已实现及编译；E62 已反馈安装、拒绝重试、修复清理测试暂未发现问题；离线、取消及故障覆盖按实际测试范围补证。直接使用真实 GitHub，不要求用户搭建索引或虚拟源 |
| 内存与页面释放 | E55 公共导航持有路径已修，合成页面弱引用检查通过。下一步由用户重新发布并重复相同切页/图片预览动作，比较多轮托管堆与进程内存；本轮未重新测量实际发布进程，不能用工作集暂时下降或定时强制 GC 代替验证。隔离检查授权不扩展为接管真实 UI、剪贴板或采集转储 |
| 安装/升级/卸载 | 隔离目录初装附带 release.json.plugins 所列插件；同版重装及升级保留自装/已更新/已卸载插件和 .management，卸载保留 Plugins 及默认/自定义数据。检查运行/托盘时拒绝维护、维护中再次启动、首次安装取消/ACL 失败及同包恢复；拒绝目录链接、非安装版目录、数字版本降级。当前互斥限同一 Windows 会话，跨会话待验证，不声称覆盖 |
| 启动/恢复 | 普通启动无额外小窗；迁移状态居中，完成原位进入主页；深浅主题首帧、缩放/窄窗口、启动中关闭、错误退出、最小化/再次启动恢复及托盘恢复。标题栏下无额外空带，普通/最大化窗口、DPI 切换、拖动/双击及原生按钮可用。仅迁移时显示迁移信息，不强制最大化 |
| 数据迁移 | 用隔离 profile/空目标检查新根配置、预设、包、任务及日志可用，旧五类业务目录按清单清理而未知根项/定位记录/锁与插件代码保留；更新/回退及包复用只写新根。检查取消/重试、跨卷、近满盘、ACL/链接、提交前后与清理中断、源文件变化、旧 marker 缺清单及安排迁移后新增插件变更。删除隔离自定义目录后回默认并记录 Warning，可再次迁移；磁盘离线、ACL 和未完迁移不得重置 |
| 日志 | 级别/筛选/复制/详情、窄窗口留白、底部跟随/上翻/拖动/回底、暂停恢复及 300 条淘汰；“已读”包含隐藏项，之后新错再提醒。隔离验证过期/数量/容量清理、10 MiB/UTC 跨日、64 KiB 截断及占用失败重试；不清普通文件、子目录或链接，不改系统时间/真实日志制造故障 |
| 管理与设置 | ZIP 拖放/选择、宽窄卡片/开关、安装/更新/禁用/卸载/回退/取消和数据保留；待重启/同版/旧版冲突保留旧请求、清理本次暂存、ERROR 无人为堆栈，真实 I/O 错误仍可见。左侧软件/插件分组独立折叠、键盘操作、收起保留右侧页和选择，无插件设置时整组隐藏，窄窗仍可切换分类；面包屑、动画/主题即时与持久化；统计仅主页/插件页，其他页面无占位 |
| 依赖 | 缺失首次确认、先不安装后可重试、已装复用、两插件共享、切页/隐藏继续；禁用仍有引用，卸载重启后包保留，再主动清理。空列表/包详情、取消/损坏/权限/空间恢复及再次启动无缺失路径异常。未知源明确失败，不伪装已安装 |
| 任务 | 整批/单项取消、失败后余项、迟到取消不抹成功结果；切页/托盘继续。按所属插件打开工具，无可用页面转管理，同批 ID 跨插件隔离；显式退出确认/系统退出/异常后中断摘要/坏摘要重试，不自动续转码或删旧输出。预检失败/拒绝依赖不建公共任务 |
| 真实插件 | [剪贴板方法](Plugins/ClipboardHistory.md#5-验收方法)：系统异常/选择/复制/事件与激活刷新、无迟到预览；不清真实历史。[视频方法](Plugins/VideoExport.md#6-验收方法)：信息报告、预设/命名/持久化、串行多格式、首帧/后续帧、CFR/VFR/音画/色彩、GPU 及 CPU 回退，用临时样本检查真实输出 |

<a id="automated-checks"></a>

### 自动检查入口

以下断言程序通过各自的入口运行，具体结果见对应验收项。

| 入口 | 覆盖及边界 |
| --- | --- |
| `dotnet run --project tests/TractorTool.FolderHistoryChecks -c Release -- --office` | Word、Excel 样本、原有纯文本比较及模拟快照提取；[E130](PROJECT_HISTORY.md#evidence-folder-history-office) 已通过 |
| [DistributionPluginsChecks.ps1](../tests/DistributionPluginsChecks.ps1) | 使用临时工程与 ZIP 验证插件发现、清单身份、共享程序集及外部工具归属。CI 在构建安装包前运行 |
| `dotnet run --project tests/TractorTool.Checks -c Release` | 核心存储、管理、日志、依赖、任务与进程；Windows 部分包含真实子进程夹具，包使用内存 ZIP/虚拟 HTTP，不启动 FFmpeg |
| `dotnet run --project tests/TractorTool.ClipboardChecks` | 剪贴板模型/替身，不证明 WinRT 实际系统行为 |
| `dotnet run --project tests/TractorTool.VideoChecks` | 媒体数据、参数、进度与隔离输出替身，不证明真实转码或 GPU |
| [Verify-PluginHost.ps1](../scripts/Verify-PluginHost.ps1) | 构建、核心检查、发布、独立机制插件、Headless 和哈希；证据输出到 artifacts/plugin-host-*，不能代替正式插件/原生拖放/托盘验收 |
| `dotnet run --project tests/TractorTool.IntegrationChecks -c Release -- <宿主目录> <隔离证据目录> --navigation-only` | 仅隔离导航/页面回收与协议版本边界，不启动真实桌面生命周期或读取剪贴板；已有完整检查入口仍默认执行全量 |

<a id="milestones"></a>

## 3. 实施里程碑

| 阶段 | 交付边界 | 当前进度与未关闭出口 |
| --- | --- | --- |
| M0 | 需求、架构、工作约定 | 已建立，持续同步用户更正 |
| M1 | 宿主、公共 UI/服务、独立插件协议 | 实现转维护；历史部分通过，当前 UI/兼容/托盘回归仍需证据 |
| M2 | 管理闭环与真实剪贴板 | 代码/独立构建已有；E62 插件升级用户测试暂未发现问题，ACL/占用/回退及 Windows 历史专项仍待验 |
| M3 | 公共依赖/进程与媒体链路 | E62 依赖与视频/GPU 本机流程用户测试暂未发现问题；严格首帧/同步及具体硬件覆盖未逐项说明 |
| M4 | 多预设串行与公共有限任务 | 实现补齐；需同源多个正确可播放输出、切页/隐藏继续、取消/退出/中断及持久化证据 |
| M5 | 可靠性与正式发布 | 日志保留、迁移/同窗启动、ZIP 与用户级安装器已实现；1.1.0 已经标签流程构建、校验并公开发布。隔离恢复回归见 E135，插件隔离及分发校验见 E137；真实安装、专项故障恢复、跨会话及无 SDK 机器验收仍未完成 |

外部依赖三类扩展已在 `Dev` 实现，进入专项可靠性验收；当前反馈与缺口见[外部依赖扩展](#dependency-extension-plan)。本地 AI 等业务仍归插件，实现与构建不代表故障场景已经验证。

<a id="acceptance"></a>

## 4. 验收台账

下表是整版产品验收，不以 M1 子项通过代替完整条目通过。无界面、真实 UI、媒体和硬件证据分别记录。

**E62 用户本机测试反馈**：当轮列出的依赖安装、拒绝后重试、修复/清理、视频/GPU、插件升级及数据迁移已测试，暂未发现问题。未提供具体构建哈希、GPU 型号、逐帧/音画测量或故障注入明细，因此下表保留这些专项覆盖的缺口，不将用户反馈扩写成自动检查或全硬件矩阵通过。该反馈不覆盖 E104 后的新迁移/重置语义。未单列的媒体细项沿用各自状态，不重复要求常规使用回归。

| 编号 | 验收场景 | 通过条件 | 证据层级 | 状态 |
| --- | --- | --- | --- | --- |
| A01 | 主框架与主题 | 顶部主页和三个底部入口可达，欢迎/插件卡片与实际安装一致，布局/图标/控件/切页反馈统一，宿主和插件主题一致，重启保留 | UI | E117 官方 12.1.6 已构建，用户常规使用反馈暂未发现升级问题；E137 离屏呈现器清空与失败恢复的页头绑定检查通过，真实退出及页头同步仍待验。E116 用户确认高精度候选消闪、普通精度仍闪；候选已清理且不再采用定制后端，闪动仍未关闭。保留原动效及文字/透明度设置；快速切页/取消、GPU/高 DPI、主题首帧与动画关闭/恢复仍待验 |
| A02 | 独立插件 | 宿主发布后单独构建并安装到软件目录 Plugins，导航和设置出现，宿主二进制未改变 | 集成、UI | E117 正式插件独立构建与兼容声明核对通过，用户常规反馈暂未发现升级问题。E137 独立机制插件的离屏装载、私有依赖隔离、旧 UI API 兼容及共享副本拒绝通过，234 个宿主文件哈希未变；全部正式插件的真实安装/UI 仍未逐项验收 |
| A03 | 兼容性与禁用 | 不兼容、禁用插件只读清单不执行入口；加载错误可定位 | 集成 | E135 离屏集成验证独立插件装载、兼容性拒绝、禁用及图标回退。日常安装的升级及原生交互需真机验证。[E135](PROJECT_HISTORY.md#evidence-release-100) |
| A04 | 管理操作重启生效 | 安装/更新/禁用/卸载正确生效，失败可恢复，数据保留；目录不可写不误报成功或改装别处 | 集成、UI | E62 插件升级用户测试暂未发现问题。E137 隔离检查覆盖待重启请求、状态冲突、更新快照/回退、禁用状态保留、移码后恢复及卸载保留数据；真实安装目录 ACL、占用和现场中断恢复仍待专项验收 |
| A05 | 页面与任务分离 | 导出时切页、关闭到托盘、重新显示，任务连续且状态正确 | UI、进程 | E55 隔离导航回收、取消/错误恢复、动画及嵌套 Settings 检查通过；真实发布版长期切页/图片内存曲线待用户复测。当前真实视频任务、任务页和托盘仍待验，不能据页面回收通过认定后台业务已验收 |
| A06 | 默认与自定义数据根目录 | 迁移后可用并安全清旧业务数据，插件代码不移动；自定义目录删除后默认初始化，访问故障/未完迁移不重置 | 集成、UI | E137 隔离检查通过默认重置及失败重试、父路径/占位保护、移码恢复、连续迁移快照与旧目标复用、目标损坏/源变化保护、清理中断恢复及旧清单兼容。真实 ACL、离线卷、跨卷/近满盘、大目录、块内取消及提交两侧实际断电仍待验证；删除后重新迁移的完整 UI 流程未单独验收 |
| A07 | 外部包事务及依赖管理 | GitHub 精确版本/附件、首次确认、离线复用、多版本、声明冲突、已装来源展示；引用/使用中不可删，卸载保留包；新工具不改宿主二进制 | 集成、UI、网络 | E137 隔离检查通过坏/旧收据恢复、hash 与 Windows 别名保护、修复失败/取消及提交前后中断恢复；离屏设置页验证首次下载可见并可取消。E62 用户反馈安装、拒绝重试、修复/清理暂未发现问题；真实 GitHub/ripgrep 独立插件专项覆盖未逐项报告。E117 FFmpeg 移动目录被拒后重试成功，根因未定 |
| A08 | [系统剪贴板历史](Plugins/ClipboardHistory.md#5-验收方法) | 系统历史分栏预览、复制原项、事件/手动刷新及明确异常，无自建历史库 | 集成、UI | E103 真实日志确认离页取消 WinRT 失败，1.1.3 已修正并独立编译/打包；新增迟到失败用例仅编译，用户需验证读取中切页、快速选择和重返页面。E46 底部留白及 Windows 完整行为仍待验收 |
| A09 | [媒体探测及视频验收](Plugins/VideoExport.md#6-验收方法) | JSON 正确解析，含中文路径、无音轨、未知时长和损坏文件处理 | 集成 | 0.5.2 完整报告、预设命名及任务接入已独立打包；媒体/参数/进度/隔离输出检查只编译；E62 视频常规流程用户测试无问题，中文路径/损坏文件等专项未逐项报告 |
| A10 | 输入限制 | 一次只允许一个源视频和至多一张封面，不出现多源队列 | UI | 未验证 |
| A11 | 预设与持久化 | 多张独立参数卡片可保存复用，源文件/封面不固定绑定到预设 | 集成、UI | E52 视频 0.5.4 左右标题对齐及固定输出栏已独立编译/打包，宽窄窗口、独立滚动、长路径及选择/取消待用户验证；预设增删改/复制、勾选与原子保存已编译，旧偏好兼容与实际重启仍待验 |
| A12 | 名称与大小预估 | 改名不改变参数，码率单位正确，仅显示预计大小，无目标 MB 控制 | 单元、UI | 每卡独立参数/名称与码率预估已编译未运行 |
| A13 | 多版本串行 | N 张选中卡片对应 N 个结果，任何时刻最多一个转码任务运行 | 集成、进程 | 插件实例持有单批次串行执行，失败后继续；隔离替身用例只编译未运行 |
| A14 | 参数快照 | 入队后编辑预设不影响已提交任务，源文件变化可检测 | 集成 | 提交时快照所选预设及源身份；批次持有封面只读共享锁、单项复制封面；运行中不允许编辑，未运行验收 |
| A15 | 首帧替换 | 各输出首帧为裁剪封面、后续对应原画面，时长与音画不被额外移动 | 媒体、UI | 未验证 |
| A16 | 不加封面版本 | 同次导出的未启用封面版本保留自己的原始首帧 | 媒体 | 未验证 |
| A17 | 多格式 | 至少多个实际容器/编码组合可播放，信息与预设一致 | 媒体 | 未验证 |
| A18 | GPU | 真实硬件完成所选处理链，记录实际编码器；不可用时不假报成功 | 硬件、媒体 | E62 用户反馈视频/GPU 已测试，暂未发现问题；未提供 GPU 型号/实际编码器明细，不外推其他硬件或故障回退覆盖 |
| A19 | 取消与失败 | 单项取消/整批取消正确，无遗留进程，失败不冒充成功，余项策略一致 | 集成、进程 | 已修整批登记和迟到取消竞争；E99 公共进程改用 Job 通知及自然 EOF，新增回归只编译。StopTree 成功但 reader 不给 EOF 的 P2 回归未补；单项/整批取消、等待项跳过、失败继续与原生清理未运行验收 |
| A20 | 输出安全 | 不覆盖源文件和已有文件，只清理任务自身临时产物 | 集成 | 单次事务目录、无覆盖提交和指定临时文件清理已编译；隔离输出检查未运行 |
| A21 | 退出和恢复 | 明确退出能停止任务，重启标记中断，无无限等待或重复执行 | UI、进程 | E137 隔离检查通过并发退出、重启标记中断、坏摘要保护及保存重试；离屏集成通过保存等待、迁移选择器重复退出、在途激活和插件生命周期清理。真实 Windows 显式退出、托盘/系统会话及正式业务任务的中断恢复仍待验收 |
| A22 | 发布验证 | 本地与 CI 使用同一脚本，安装/压缩包加载插件，校验信息正确 | 发布、UI | 1.1.0 校验范围见 [E137](PROJECT_HISTORY.md#evidence-release-110)。真实 Windows 整包启动、断电后重装、无 SDK 机器及跨会话待验 |
| A23 | 日志与任务交互 | 分级/筛选/折叠详情、任务进度；全部已读保留日志，新错误再次提醒；底部跟随且不打断上翻；磁盘保留有界且不误删其他文件 | 集成、UI | E35 用户反馈未发现问题，未逐项报告边界；E36 日志保留检查只编译未运行。E99 已改进程树通知等待和读取清理，仅编译，旧取消输出触发及新原生行为仍待验证 |
| A24 | 插件托盘扩展 | 插件分组、勾选/禁用/文字更新、动作失败隔离、切页/隐藏保留、停止解绑；原显示/退出入口正常 | 集成、Windows UI | E63 子代理复审和构建通过；自动化检查只编译未运行。E63/E64 历史副本已不存在，E99 独立夹具未安装/加载；真实托盘由用户在当前隔离宿主中验证 |
| A25 | 桌面状态 HUD 第一步 | 框架依赖解析、主页面/设置分工、失败状态；电源/无电池、五项指标、按需采样、穿透/焦点/DPI/托盘、独立保存、退出清理 | 集成、Windows UI/硬件 | E65 用户发现启动失败，E66/E67 修正后 E68 用户反馈“看着没问题”并允许继续；这是基本使用反馈，专项硬件/退出/加载隔离检查仍未逐项运行，不标整项通过 |
| A26 | HUD 方案与预览 | 旧配置继承、方案增删复制/保存/撤销、轮播停启、模板及规则验证、左侧草稿示例预览、提醒打断/到期恢复（E95 已删除桌面草稿预览）、保存失败不丢数据 | 纯逻辑、Windows UI | E86 选色保存/旧色值兼容、轮播独立保存/失败回退、定时循环/详情暂停/长条恢复与草稿隔离已在内存离屏检查通过；默认逻辑套件、原生窗口/磁盘保存/重启、提醒/真实采样专项仍待验证 |
| A27 | HUD 首版数据源与页面 | 原方案兼容、传感器权限/缺失/释放、网络限时/取消/频率、余额凭据隔离、编辑可达性、三态内容/单双击/拖动、形变/动画偏好/透明命中、跨屏/DPI/穿透 | 逻辑、HTTP、Windows UI/硬件 | E96 硬件类型/编辑示例/缺失前置条件的离屏与纯数据检查、0.4.17 包通过，保留独立采样等原回归。本机未发现 PawnIO 注册/服务，温度实读恢复未验；真实 5Hz 硬件耗用/网络/桌面交互未测，托盘方案列表及原生窗口/DPI/DPAPI 等缺口保留 |
| A28 | [文件夹版本历史](Plugins/FolderHistory.md#7-验收方法) | 文件状态、文本与 Office 内容比较、选区恢复与发布、原件保留、中断撤销、自动保护与清理 | 逻辑、真实 CLI、Windows UI | [0.6.2](PROJECT_HISTORY.md#evidence-folder-history-preview-cancellation) 快速切换、迟到错误、引擎取消、模拟事务与离屏工作区回归通过。[0.6.1 全插件审查](PROJECT_HISTORY.md#evidence-folder-history-full-review) 记录其余覆盖。真实 CLI 与正式窗口的当前版本验收、高 DPI、故障及性能场景待验 |
| A29 | [AB 审片](Plugins/MediaCompare.md#验收方法) | 双素材时间对应、声音选择、拖放/清空、联动视图、序列、原生与图形资源、独立安装 | 纯托管、真实媒体、Windows GPU/UI | 0.1.7 构建、SOL 静态复核及独立打包通过；按用户要求未运行测试，性能与定位后续播由用户验证。打包 NU1900 漏洞扫描未完成。0.1.6 的 44/66 项检查为历史证据；真实 UI/声音、资源回收与长 GOP 性能待验，未知时长和长空窗非零定位受限，见[当前状态](#media-compare) |
| A30 | 独立原生运行库 | 完整发布物校验与离线复核；加载前登记进程期占用，部分加载失败仍保护；退出前不可修复/删除，旧工具声明及收据兼容 | 集成、真实 DLL、UI | 已实现，构建/Review 见 E147；2026-11-27 用户反馈目前测试没问题并同意提交。未逐项确认覆盖范围，22 项自动检查、故障及兼容专项仍不记为通过 |
| A31 | uv / Python | 无全局 Python 前提下自动准备固定工具链，按锁安装并真实调用；拒绝/取消/断网重试、环境隔离、来源约束及清理正确 | 集成、真实网络/进程、UI | `--frozen` / `--no-sources` 互斥已修正；2026-11-27 用户对当前版本反馈暂未遇问题并允许提交，见 E148/E149。未逐项报告测试场景，自动检查仅编译，拒绝/取消/离线/损坏及兼容专项不标全部通过 |
| A32 | pnpm / Node | 无全局 Node 前提下自动准备固定工具链，按锁安装并真实调用；安装脚本/钩子不绕过策略，链接不越界，取消无残留进程 | 集成、真实网络/进程、UI | 已实现，构建/静态复核及独立打包见 E150；2026-11-27 用户反馈测试没有问题并允许提交。未逐项报告场景，Agent 自动检查仅编译，取消/离线/安装策略及故障覆盖不标全项通过 |
| A33 | 扩展数据与兼容 | uv/Node/DLL 分类及旧同名目录保护；多版本与回退引用、占用保护、缓存独立清理；新根重建验证后切换，失败/中断保留原数据；不修改插件代码和业务数据 | 集成、故障恢复、Windows UI | 三类目录、共享环境事务、原锁快照、目标重建及独立缓存清理已实现；本轮补混合迁移和目标污染保护。构建/静态审查不等于迁移、中断/离线/未知内容保护运行通过，旧 A06/A07 证据不覆盖新能力 |


## 5. 验证与发布要求

- 静态、构建、自动化/集成、真实 UI/系统/硬件分开报告；历史通过不自动覆盖新版本，未执行或阻塞不能标通过。
- 独立插件和故障夹具使用隔离目录。媒体使用短合成样本和真实 FFmpeg，检查首帧/后续画面、时长与音画，不以退出码、文件存在或模拟进度代替。
- 本地与 CI 复用发布脚本，首版 win-x64 自包含，禁用 Native AOT/未经验证裁剪；升级保留 AppRoot/Plugins 和用户数据，包位于 DataRoot/Packages。
- 分发须落实压缩包/安装器、SHA256、第三方及 FFmpeg 来源许可；签名、最低系统、无 SDK 机器和设备支持单独取得证据，不照搬参考软件结论。不发布用户数据、缓存、令牌或本机工具副本。

<a id="next"></a>

## 6. 下一步与维护

`Dev` 的[三类外部依赖接入](#dependency-extension-plan) 已获用户测试反馈并允许保存；推送与发版另行授权。后续重点是 A30–A33 的专项可靠性证据：首次拒绝后重试、离线复用、缓存清理后执行、取消与进程回收、混合数据迁移和中断恢复。用户未逐项确认的场景继续保留，以下既有未关闭问题不随本轮提交关闭。

E117 用户常规使用反馈暂未发现 Avalonia 升级问题。FFmpeg 安装末尾拒绝移动目录曾偶发一次、重试成功，未作代码修复；若再现，先捕获源/目标路径与所有相关进程的文件访问记录定位原因。以下专项运行缺口保持开放。

框架恢复路径的自动化证据见 [E135](PROJECT_HISTORY.md#evidence-release-100)。仍待在独立 Windows 环境验证真实 ACL、离线卷、跨卷中断和大目录校验耗时；目标冲突时保留数据。原生剪贴板和正式安装运行需单独验证。

1. **闪动等待官方修复，退出绑定与页头同步另行验收**：按 [E114–E116](PROJECT_HISTORY.md#evidence-page-flicker-precision) 归档原因与证据，不再编译自有 Avalonia/Skia、替换官方 DLL 或维护定制 NuGet；保持默认渲染和 500ms、18 DIP、0→1 淡入。后续有针对同类混色问题的官方修复或受支持配置时，核对变更再用官方包复验原切页、快速/嵌套导航、主题/DPI、透明窗口及性能；本记录不代表上游已确认、接单或承诺版本，也未代用户创建 Issue。E113 真实退出与 E112 页头同步仍待用户验证，不与闪动诊断合并标通过。
2. **补框架 P2 回归**：复用 [ProcessChecks](../tests/TractorTool.Checks/ProcessChecks.cs) 的 EofReader，让 FakeSession.Stop 成功但不结束读取，限时断言读取被取消、session 释放、任务 Faulted/IOException 并记录清理错误。这是尚未补齐的测试，不是已复现的生产故障，本轮未处理。
3. **补框架运行证据**：正常取消/超时、根先退出而子进程残留、显式退出，以及 E98 自动发现故障回归和当前完整分发的运行验收。E99 原生通知丢失/取消唤醒/截止查证/API 失败缺确定性覆盖，MVVM 两分支也只有一次性打包证据。旧截图取消异常触发仍未知，不调整调试器设置或把正常取消改成成功。运行由用户负责，未经新指示不执行。
4. **专项可靠性与发布**：E55 长期内存、迁移近满盘/ACL/中断、跨 Windows 会话、无 SDK 机器及媒体/硬件细项按实际目标补证，保留 E62 已有反馈。标签工作流负责构建、校验和发布；真实安装和失败恢复需单独验证。
5. **既有插件问题暂缓**：[E94](PROJECT_HISTORY.md#evidence-hud-full-review) 两项 P2（硬件开关保存后缓存未失效、非硬件 SensorId=null 导致初始化失败）未修复，E96 只在重新检测后清缓存。A26/A27 原生/硬件/性能缺口及托盘动态方案列表继续保留。
6. **文件夹历史集中验收**：使用正式包 和临时样本，在真实 Windows 宿主及 Duplicati 下核对快速切换文件或快照后的对比内容及日志、导航与状态角标、选区恢复与提交。其他恢复及性能场景见 A28。

<a id="evidence"></a>

历史入口：[证据索引](PROJECT_HISTORY.md#记录索引)。按模块或 E 编号查阅，不要求默认通读。
