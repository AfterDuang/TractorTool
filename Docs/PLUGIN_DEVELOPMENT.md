# 插件开发规范

本文是所有 TractorTool 插件共同遵守的接入规范；具体业务需求和内部设计写在 [Docs/Plugins](Plugins)，当前进度与验收状态只写 [开发与验收](DEVELOPMENT.md)，历史执行详情归 [项目历史](PROJECT_HISTORY.md)。装载实现和公共服务的设计依据见 [软件架构](ARCHITECTURE.md)。

## 1. SDK 能力与依赖边界

当前公共代码以 [PluginContracts.cs](../TractorTool.Contracts/PluginContracts.cs) 和 [PluginUi.cs](../TractorTool.UI/PluginUi.cs) 为准：

| 能力 | 当前公开入口 | 边界 |
| --- | --- | --- |
| 插件实例 | `IPlugin`、`PluginContext` | 初始化、停止、释放；上下文提供路径、设置、日志和停止令牌 |
| 页面与设置页 | `IPluginUiModule`、`IPluginPageRegistry` | 工厂注册 Avalonia Control，不要求继承宿主 VM |
| 常驻活动 | `IPluginBackgroundService` | 按插件生命周期运行，与页面显示独立 |
| 页面离开 | `IPluginPageLifetime` | 只清理页面订阅和显示资源 |
| 路径、设置、日志 | `IAppPaths`、`IPluginSettingsStore`、`IVersionedSettings`、`IAppLog` | 按稳定 PluginId 使用宿主提供的服务 |
| 工具进程 | `IProcessRunner`、`ProcessRequest`、`ProcessResult` | API 1.1；上下文 Processes 按插件生命周期提供，当前 Windows 10+ 适配；见下文 |
| 外部工具 | `IExternalPackageService`、`IPackageLease` | API 1.2 查询/取得已装版本，API 1.4 TryRequestAsync 申请缺失包；确认、下载与安装统一由宿主处理 |
| 有限任务 | 后续阶段设计，当前尚无公开接口 | 不伪造已存在的 API，不引用 Core 内部类绕过缺失能力 |

插件只依赖公开 Contracts/UI 及自己的私有依赖，不引用 TractorTool 应用工程或 TractorTool.Core。Contracts 不依赖 Avalonia/Windows；UI 负责 Avalonia 页面和共享控件。平台专属能力放在对应插件或适配层。

宿主不得通过静态项目引用绑定具体插件。首版两个正式插件也按普通插件独立构建和加载；[独立测试插件](../tests/IndependentPlugin/ProbePlugin.cs) 只验证机制，不替代正式业务。

## 2. 插件清单与兼容性

每个版本目录根部包含 `plugin.json`。当前格式以 [清单校验器](../TractorTool.Core/Plugins/PluginManifest.cs) 和 [真实示例清单](../tests/IndependentPlugin/plugin.json) 为准。

| 字段 | 当前要求 |
| --- | --- |
| `schemaVersion` | 1 |
| `id` | 稳定 PluginId，通过路径标识校验；必须与插件目录名称一致，不能使用可变显示名称 |
| `name` | 非空显示名称 |
| `version` | 三段数字版本，与版本目录一致；当前不接受预发布后缀 |
| `entryAssembly` | 包内 DLL 相对路径，禁止绝对路径、越界及路径中的链接/重解析点 |
| `entryType` | 非空入口类型全名；入口是可实例化的 IPlugin，提供无参构造函数 |
| `apiVersion` | 当前 1.4，仍接受 1.0/1.1/1.2/1.3；Processes 从 1.1 提供，Packages 查询/租约从 1.2 提供，RequestAsync 要求 1.3，TryRequestAsync 要求 1.4 |
| `uiApiVersion` | 当前为 1.2；宿主仍接受旧 1.0、1.1 页面注册 |
| `avaloniaVersion` | 精确匹配 12.1.2 |
| `supportedPlatforms` | 非空 OS/RID 列表，与宿主 OS 或 RID 匹配后才加载 |
| `dependencies` | 可省略/空；否则为 `[{"id":"FFmpeg","version":"9.0.2"}]`，最多 32 项、不重复；清单有效但源未提供时页面仍可加载，申请时明确报告不可用 |
| `description`、`author` | 可选展示信息 |

图标枚举/说明元数据从 UI API 1.1 提供；使用 `IconPath` 或控件 `ImageSource` 的插件声明 1.2。保留 PluginPageOptions 的原两参数构造函数和旧注册签名，不要求旧插件为缺省符号重新构建；旧 DLL 的实际加载仍需兼容回归。应用产品版本不是插件 API 版本，也不是插件自身版本；不能把三者一起自动升版。

管理页已提供本地 ZIP 安装/更新、启用/禁用、卸载与可用时回退，重启生效。活动版本由安装目录的管理记录明确指定；未纳入管理的多个同 ID 候选仍报告冲突，不自动选最高版本。旧 `disabledPlugins` 只用于未管理插件，接管后的状态以管理记录为准。禁用、不兼容、损坏的插件只显示清单状态，不为了获取名称而执行入口。

## 3. 运行目录与打包

```text
<AppRoot>/Plugins/<PluginId>/<Version>/
  plugin.json
  Entry.dll
  Entry.deps.json              # 按依赖解析需要随包提供
  私有依赖、资源、许可文件

<DataRoot>/Data/Plugins/<PluginId>/   # 插件配置和持久数据
<DataRoot>/Packages/<PackageId>/<Version>/<RID>/  # 外部工具，由宿主管理
```

AppRoot 是实际软件启动目录，不是源码仓库或终端当前目录。DataRoot 默认位于 %LOCALAPPDATA%/TractorTool；修改数据根目录不改变插件代码位置。软件目录不可写时明确报错，不改装到数据目录。完整目录和启动指针规则见 [架构：目录与迁移](ARCHITECTURE.md#storage)。

三类依赖分别处理：

- **宿主共享 SDK**：TractorTool.Contracts、TractorTool.UI、Avalonia 及 Avalonia.*、FluentAvalonia、MicroCom.Runtime 由宿主提供，插件包不携带另一套 DLL。固定已验证版本；复制共享副本或程序集身份不匹配会导致拒绝加载。
- **插件私有依赖**：随插件提供 DLL、必要的 deps.json、资源和与目标平台匹配的原生库；不能依赖开发机 NuGet 缓存、工程相对路径或软件目录外的链接。依赖可由自己的项目或包构建，但不能成为宿主到具体插件的静态引用。
- **外部可执行工具**：由宿主包服务提供版本化路径和占用保护，不混入插件代码目录。包来源及版本由宿主维护，插件不自行下载或搜索系统 PATH。

插件代码是可信的进程内代码；AssemblyLoadContext 不是安全沙箱。不承诺热卸载、不可信代码隔离或任意插件故障都不影响宿主。

ZIP 必须直接以 `plugin.json` 为根，不再套一层插件目录。当前限制：压缩包和展开总量各不超过 256 MiB、最多 5000 项、相对名称不超过 240 字符/32 层、清单不超过 128 KiB。禁止路径越界、链接/重解析点、大小写重名、Windows 保留名、ADS、尾部点/空格及共享 SDK DLL 副本。入口须为托管程序集，不能引用宿主或 Core；类型和实际私有依赖在真实加载时继续校验。

安装后版本不可原位覆盖；更新包保持完全一致的稳定 ID（含大小写），版本号提高。新版本可能读写数据格式时，必须提供自身 schema 校验/迁移；宿主会在重启更新前备份插件数据，回退时恢复。卸载默认保留数据，不能把代码目录当数据目录。待处理变更与原数据根绑定，变更完成前不要切换 profile 或手动修改管理状态。恢复流程见 [架构](ARCHITECTURE.md#24-安装更新禁用和卸载)。

## 4. 初始化、页面与生命周期

1. 入口实现 `IPlugin.InitializeAsync(PluginContext, CancellationToken)`、`StopAsync`、`DisposeAsync`。构造函数和注册函数保持轻量，I/O 与等待采用异步方式，不同步卡住 UI 线程。
2. 实现 `IPluginUiModule.RegisterPages`，调用 `AddNavigationPage` / `AddSettingsPage` 注册稳定页面 ID、标题和工厂；可通过 `PluginPageOptions` 提供图标和说明。具体调用形状见 [示例入口](../tests/IndependentPlugin/ProbePlugin.cs)。
3. 宿主先初始化再暂存页面注册，整次成功后提交；注册失败不会留下半份导航。页面工厂返回未挂到其他父控件的新页面，并设置自己的 DataContext。
4. 页面离开使用 `IPluginPageLifetime.OnNavigatedFrom` 解除订阅、释放显示资源。页面、有限任务、常驻服务分别拥有生命周期；不得在离页/隐藏窗口时取消后台业务。
5. 可选常驻活动实现 `IPluginBackgroundService.RunAsync`，响应停止令牌。清理必须兼容初始化部分失败和退出竞争，不能留下未观察的后台异常。
6. 停止并等待插件拥有的工作结束后再释放资源。宿主有有界退出等待，不能把它当作安全强杀线程或保证磁盘写入成功的机制；同步卡死的可信插件仍可能影响进程。

有限任务服务的后续接入遵循相同所有权规则，具体业务的并发和失败策略写在各插件文档；不把业务队列参数塞进通用协议。

### 工具进程（API 1.1）

通过 `context.Processes` 调用 [RunAsync](../TractorTool.Contracts/Processes/IProcessRunner.cs)，只传绝对工具路径和逐项参数，不拼 shell 字符串、不引用 Core 或宿主平台类。上下文保留原构造函数，因此手工测试上下文可能没有此服务；正式新宿主注入实例，非 Windows 调用明确不支持。服务不是可信沙箱，插件仍须校验业务输入和工具来源。

执行等待与页面无关，传入业务取消令牌；宿主退出/插件加载失败时服务封闭新调用并取消已有调用。根进程结束时其后代也会结束，不得用此接口启动应独立常驻的软件。返回非零退出码时由插件展示业务失败；调用取消为 OperationCanceledException，执行超时为 TimeoutException，清理失败为 IOException，不能把清理异常显示成“已安全取消”。

输出为 UTF-8，stdout/stderr 各自有长度上限和截断标记；超限内容仍被读取以免管道阻塞。解析 ffprobe JSON 前必须检查截断，必要时显式增大上限。当前没有实时进度回调，后续媒体链路再接入；包安装及版本校验没有完成前，不宣称依赖已可用。具体进程所有权及平台限制见 [架构 4.2](ARCHITECTURE.md#42-进程服务)。

### 外部工具接入（API 1.4）

插件在用户使用功能时调用 `TryRequestAsync`，由宿主复用已有包，或解析可信源并确认下载、安装。用户选择“先不安装”正常返回 `null`，插件保留输入并结束本次操作，不作为异常；调用方 token 取消仍抛出 `OperationCanceledException`，未提供的版本/平台抛出 `NotSupportedException`。旧 API 1.3 `RequestAsync` 的签名与拒绝时抛取消异常的语义保持兼容，新插件使用 1.4。失败应在功能页显示可处理状态。插件不自建下载器。安装包独立于插件保留，宿主设置统一清理未引用包。

清单只声明包 ID 与固定版本，不传入下载 URL、哈希或安装脚本。不在初始化中取得长期租约或自动下载。源缺失不使插件整体不兼容；需要依赖的操作不可继续，但其他页面仍可用。软件维护的索引为 `PackageSources/default.json`，当前提供 FFmpeg 9.0.2 / win-x64，尚无远程索引刷新和自定义源。

通过 `context.Packages` 查询 [PackageStatus](../TractorTool.Contracts/Packages/IExternalPackageService.cs)；仅在执行任务时取得租约：

```csharp
var packages = context.Packages ?? throw new NotSupportedException("需要支持 API 1.4 的宿主。");
using var lease = await packages.TryRequestAsync(new("FFmpeg", "9.0.2"), cancellationToken);
if (lease is null) return; // 用户暂不安装；保留已选择的输入，允许稍后重试。
var result = await context.Processes!.RunAsync(
    new(lease.Tools["ffprobe"], ["-v", "error", "-show_format", "-show_streams", "-of", "json", inputPath]),
    cancellationToken);
// 检查退出码、截断状态，再解析 JSON；退出 using 之后才释放该版本占用。
```

不能缓存租约外的工具路径再启动进程，不能在进程/输出读取仍活跃时释放租约。页面切换不应释放后台任务拥有的租约。宿主按插件生命周期收回遗漏租约，但这不替代正确的 using/取消逻辑。并发申请合并确认与安装；取消自己的等待不取消其他插件的下载。原 `AcquireAsync` 保持只获取已安装包的语义。禁用插件仍保留依赖引用，卸载重启后才解除，包文件不会联动删除。存储/事务边界见 [包管理](ARCHITECTURE.md#41-包管理)。

## 5. 统一 UI 与设置

- 页面采用 Avalonia/MVVM，复用宿主 UI SDK 的 `SettingRow`、`ToolIcon` 和 [共享样式](../TractorTool.UI/Styles/SharedStyles.axaml)；业务布局放插件自己的 View。
- 样式归属遵循 [公共 UI / 宿主 / 页面边界](ARCHITECTURE.md#ui-style-ownership)。页面可以有局部 `Styles` 调整列表密度等布局，不向 Application 注入全局规则，不引用 `TractorTool/Styles/ShellStyles.axaml` 或依赖 `shell`、`rail`、`PagePresenter` 等宿主私有标识。
- 颜色使用主题动态资源，支持深色、浅色、跟随系统和主题色，不再安装第二套全局主题。工具身份可用彩色图片并保留原色；设置和工具栏操作使用共享单色符号，随主题调整前景色。
- 切页由宿主 PageHost 统一呈现和过渡，页面不得绕过导航所有权控制宿主窗口。
- 普通控件直接使用当前 FluentAvalonia 主题；共享 MotionStyles 自动将“界面动画”偏好应用到控件及模板子元素，不为每个开关另写动画、定时器或复制模板。公共表面复用 card / themeSurface / SettingRow 等样式，主题过渡时长只有 UI SDK 一个来源；不要依赖已移除的主窗口 themeTransitions class。
- 自定义 Animation/Composition 读取已有共享库的 `FluentAvalonia.Core.FAUISettings.AreAnimationsEnabled()`，自行取消其拥有的动效；普通属性 Transitions 和 PageHost 已由公共层处理。Motion.Configure 是宿主组装入口，插件不调用它或覆盖 AppMotionEnabled 全局偏好；不通过关闭全局时钟影响进度指示。第三方已开始的页面/展开过渡可能短时继续，详见 [统一 UI](ARCHITECTURE.md#ui)。
- 有交互状态的列表使用稳定模型与可观察集合原位更新，避免每次刷新 Clear/替换整份 ItemsSource，造成动画中断、选择或焦点丢失；失败回退要覆盖“模型值未变、控件已操作”的分支。
- 后台变化经 UI 调度更新状态；列表、日志和订阅须有边界。加载、空、失败状态明确，长路径与较窄窗口可用。
- 保持绑定类型正确。字符串是否为空使用显式布尔属性并通知变化，不用 `!!字符串` 强制转换；不能用关闭编译绑定掩盖路径错误。
- 配置模型实现 `IVersionedSettings`，通过 `context.Settings.Load<T>(name)` / `Save<T>(name, value)` 读写；该存储已绑定插件 ID。名称变化不能导致数据换目录。
- 模型变更须兼顾已有配置、版本拒绝/迁移与回退，不在读取失败时覆盖原文件。插件专属设置由插件自己解释；宿主不知道码率等业务字段。
- 日志通过上下文记录可定位的插件/操作信息，不输出剪贴板正文、完整媒体内容或其他敏感数据。

### 可选插件图标（UI API 1.2）

普通页面和设置页面均可提供图标元数据，例如：

```csharp
registry.AddNavigationPage("main", "我的工具", () => new MyToolView(),
    new PluginPageOptions(ToolIconKind.Tools, "工具说明")
    {
        IconPath = "Assets/icon.png"
    });
```

将 PNG 实际复制到 `<AppRoot>/Plugins/<PluginId>/<Version>/Assets/icon.png`，随包附素材许可。项目可用 `<None Update="Assets/**" CopyToOutputDirectory="PreserveNewest" />` 复制资源。此字段不接受 avares URI、网络地址、绝对路径、`.`/`..` 路径段或越出版本包的链接；大小不超过 2 MiB，宽高分别在 1–1024 像素之间，建议透明背景、正方形 256×256。

省略或图片无效时使用 `Icon` 指定的符号，宿主记录加载失败且仍允许打开页面。宿主负责加载、缓存和释放，页面注册时不必创建 Bitmap；不要释放从宿主共享的图片。直接给公共控件设置自行创建的 ImageSource 时，仍由创建者管理该图片的生命周期，控件只负责显示。

## 6. 开发、构建与安装验证

使用 VS Code 和现有 Avalonia/.NET 工作流，打开根 [TractorTool.slnx](../TractorTool.slnx)。正式插件在 Plugins 分组，检查和机制夹具在 tests 分组；新增插件工程也应加入解决方案，不能靠排除工程维持独立性。宿主项目仍禁止引用具体插件。

| 用途 | 引用与构建方式 |
| --- | --- |
| 日常编辑/构建 | 不传 `HostSdkDir`；插件 `ProjectReference` 公开 Contracts/UI，普通解决方案还原、构建和设计时分析使用同一依赖图；无需预先发布宿主 |
| 独立插件交付 | 显式传入有效 `HostSdkDir`；插件只引用已发布 Contracts/UI DLL，`Private=false`；通过打包脚本正常 restore 并 `-t:Rebuild`，校验包内没有共享 SDK 副本 |
| 检查源码开发 | 检查工程默认引用被测项目；这只方便编辑和编译，不能作为独立安装证明 |
| 发布产物检查 | IntegrationChecks 显式传 `HostSdkDir`；ClipboardChecks 同时传 `HostSdkDir` 和 `PluginBuildDir`，只引用指定产物。缺少任意路径或 DLL 明确失败 |

参考 [IndependentPlugin.csproj](../tests/IndependentPlugin/IndependentPlugin.csproj) 的互斥条件。模式取决于是否显式提供产物路径，不随 Debug/Release 或 `DesignTimeBuild` 偷换。指定坏路径不会回退源码；切换模式时不要使用 `--no-restore`，条件依赖变化需要重新还原。正式脚本强制重新编译，避免沿用日常源码构建的 DLL。

在 VS Code 中正常开发可直接构建解决方案：

```powershell
dotnet build TractorTool.slnx -c Debug -m:1 -nr:false
```

插件开发输出保留在该工程的 bin 下，不自动复制进调试宿主的 Plugins，不绕过安装状态覆盖现有版本。源码模式可能包含公共项目的传递依赖，不能把整个 bin 目录当正式插件包。需要安装时使用下方独立打包流程；宿主和插件版本各自维护。

验证顺序：

1. 先发布宿主到新的空目录并记录原有文件 SHA256。
2. 再以发布 SDK 模式单独重新构建插件；收集清单、入口、私有依赖、资源与许可。之前的日常源码构建不计作独立交付证据。
3. 复制到该发布副本的 Plugins/PluginId/Version。
4. 重新核对原有宿主文件哈希，确认无需修改/重编宿主。
5. 使用隔离 profile 重启宿主，真实验证导航和设置注册、设置持久化、错误状态及生命周期；编译和文件复制不能替代 DLL 加载证据。

完整、可复用的执行入口是 [Verify-M1.ps1](../scripts/Verify-M1.ps1)。单独构建示例（需把变量指向已发布宿主的绝对路径，从仓库根目录执行）：

```powershell
$publishedHost = 'H:\ProjH\TractorTool\artifacts\manual-publish'
dotnet build .\tests\IndependentPlugin\IndependentPlugin.csproj -t:Rebuild -c Release "-p:HostSdkDir=$publishedHost" -o .\artifacts\manual-plugin
# 将 manual-plugin 完整输出放入该发布副本 Plugins/Test.Independent/1.0.0。
# 用隔离 --profile-root 启动宿主，具体命令见 DEVELOPMENT.md。
```

此代码块是操作示例，不是新增执行证据。首次构建需要正常还原。宿主发布和隔离启动入口见 [开发操作](DEVELOPMENT.md#workflow)。

正式剪贴板插件已有 [独立项目](../Plugins/TractorTool.Plugins.ClipboardHistory/TractorTool.Plugins.ClipboardHistory.csproj) 和 [打包脚本](../scripts/Build-ClipboardPlugin.ps1)。脚本读取清单版本作为插件产品版本，复制实际还原依赖的许可，输出 ZIP 与 SHA256：

```powershell
./scripts/Build-ClipboardPlugin.ps1 -HostSdkDir H:/ProjH/TractorTool/artifacts/manual-publish -Output H:/ProjH/TractorTool/artifacts/manual-clipboard
```

输出必须是新空目录。将单个本地 ZIP 拖到宿主插件面板，或通过“插件 → 安装 / 更新”选择 ZIP；两者共用可信来源确认及校验流程。确认后显式退出并重新启动，不能只关闭到托盘。日常解决方案可以编译全部工程；发布宿主仍只指定 `TractorTool.csproj`，不会因此静态包含插件，随后独立编译/打包才是插件正式路径。SDK 投影/WinRT 等插件私有库可随插件分发，须携带自己的许可，不误作宿主共享 SDK 删除。

## 7. 插件交付要求

- 提供清单、兼容 SDK/平台、入口、全部私有运行依赖与许可；在已发布宿主中验证，不能只在 IDE 内可用。
- 覆盖缺少依赖、坏 DLL、协议/平台不匹配、禁用、初始化/注册/页面失败等适用分支；错误可定位且不留下部分注册。
- 验证页面与设置注册、深浅色/主题色、重启持久化和相关生命周期；有限任务与常驻服务各自验收。
- 仅使用隔离数据和临时发布副本。静态检查、构建、自动化、集成、真实系统/UI/硬件分别报告。
- 业务特有的用例和实现范围放各插件文档；当前结果和限制写 DEVELOPMENT.md，已完成轮次的执行详情归 PROJECT_HISTORY.md。用户接手运行测试，未经新指示不自动启动验收程序。

视频插件通过 [Build-VideoPlugin.ps1](../scripts/Build-VideoPlugin.ps1) 使用同一 HostSdkDir/空输出目录约定独立打包，源码为 [VideoExport 工程](../Plugins/TractorTool.Plugins.VideoExport/TractorTool.Plugins.VideoExport.csproj)。ZIP 不捆绑 FFmpeg；首次功能使用交给宿主申请。具体版本/可测范围只见开发台账，不能以 JSON 夹具或独立编译代替真实工具执行。
