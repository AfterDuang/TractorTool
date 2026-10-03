# 插件开发规范

本文是所有 TractorTool 插件共同遵守的接入规范；具体业务需求和内部设计写在 [Docs/Plugins](Plugins)，完整进度与验收结果只写 [开发与验收](DEVELOPMENT.md)。装载实现和公共服务的设计依据见 [软件架构](ARCHITECTURE.md)。

## 1. SDK 能力与依赖边界

当前公共代码以 [PluginContracts.cs](../TractorTool.Contracts/PluginContracts.cs) 和 [PluginUi.cs](../TractorTool.UI/PluginUi.cs) 为准：

| 能力 | 当前公开入口 | 边界 |
| --- | --- | --- |
| 插件实例 | `IPlugin`、`PluginContext` | 初始化、停止、释放；上下文提供路径、设置、日志和停止令牌 |
| 页面与设置页 | `IPluginUiModule`、`IPluginPageRegistry` | 工厂注册 Avalonia Control，不要求继承宿主 VM |
| 常驻活动 | `IPluginBackgroundService` | 按插件生命周期运行，与页面显示独立 |
| 页面离开 | `IPluginPageLifetime` | 只清理页面订阅和显示资源 |
| 路径、设置、日志 | `IAppPaths`、`IPluginSettingsStore`、`IVersionedSettings`、`IAppLog` | 按稳定 PluginId 使用宿主提供的服务 |
| 有限任务、外部包、进程服务 | 后续阶段设计，当前尚无公开接口 | 不伪造已存在的 API，不引用 Core 内部类绕过缺失能力 |

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
| `apiVersion` | 1.0 |
| `uiApiVersion` | 当前为 1.2；宿主仍接受旧 1.0、1.1 页面注册 |
| `avaloniaVersion` | 精确匹配 12.1.2 |
| `supportedPlatforms` | 非空 OS/RID 列表，与宿主 OS 或 RID 匹配后才加载 |
| `dependencies` | 外部包需求预留，M3 前必须为空或省略；非空会被明确判为不兼容 |
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
- **外部可执行工具**：由宿主包服务提供版本化路径和占用保护，不混入插件代码目录。当前未提供包服务，相关业务待 M3。

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

## 5. 统一 UI 与设置

- 页面采用 Avalonia/MVVM，复用宿主 UI SDK 的 `SettingRow`、`ToolIcon` 和 [共享样式](../TractorTool.UI/Styles/SharedStyles.axaml)；业务布局放插件自己的 View。
- 颜色使用主题动态资源，支持深色、浅色、跟随系统和主题色，不再安装第二套全局主题。工具身份可用彩色图片并保留原色；设置和工具栏操作使用共享单色符号，随主题调整前景色。
- 切页由宿主 PageHost 统一呈现和过渡，页面不得绕过导航所有权控制宿主窗口。
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

使用 VS Code 和现有 Avalonia/.NET 工作流。参考 [IndependentPlugin.csproj](../tests/IndependentPlugin/IndependentPlugin.csproj)：正式构建通过 `HostSdkDir` 引用**已发布的** Contracts/UI DLL，并禁止复制它们；Avalonia 是相同版本的仅编译引用。示例的 DesignTimeBuild 条件项目引用只用于 IntelliSense，不是正式构建的宿主依赖。

验证顺序：

1. 先发布宿主到新的空目录并记录原有文件 SHA256。
2. 再单独构建插件；收集清单、入口、私有依赖、资源与许可。
3. 复制到该发布副本的 Plugins/PluginId/Version。
4. 重新核对原有宿主文件哈希，确认无需修改/重编宿主。
5. 使用隔离 profile 重启宿主，真实验证导航和设置注册、设置持久化、错误状态及生命周期；编译和文件复制不能替代 DLL 加载证据。

完整、可复用的执行入口是 [Verify-M1.ps1](../scripts/Verify-M1.ps1)。单独构建示例（需把变量指向已发布宿主的绝对路径，从仓库根目录执行）：

```powershell
$publishedHost = 'H:\ProjH\TractorTool\artifacts\manual-publish'
dotnet build .\tests\IndependentPlugin\IndependentPlugin.csproj -c Release "-p:HostSdkDir=$publishedHost" -o .\artifacts\manual-plugin
# 将 manual-plugin 完整输出放入该发布副本 Plugins/Test.Independent/1.0.0。
# 用隔离 --profile-root 启动宿主，具体命令见 DEVELOPMENT.md。
```

此代码块是操作示例，不是新增执行证据。首次构建需要正常还原。宿主发布和隔离启动入口见 [开发操作](DEVELOPMENT.md#workflow)。

正式剪贴板插件已有 [独立项目](../Plugins/TractorTool.Plugins.ClipboardHistory/TractorTool.Plugins.ClipboardHistory.csproj) 和 [打包脚本](../scripts/Build-ClipboardPlugin.ps1)。脚本读取清单版本作为插件产品版本，复制实际还原依赖的许可，输出 ZIP 与 SHA256：

```powershell
./scripts/Build-ClipboardPlugin.ps1 -HostSdkDir H:/ProjH/TractorTool/artifacts/manual-publish -Output H:/ProjH/TractorTool/artifacts/manual-clipboard
```

输出必须是新空目录。将单个本地 ZIP 拖到宿主插件面板，或通过“插件 → 安装 / 更新”选择 ZIP；两者共用可信来源确认及校验流程。确认后显式退出并重新启动，不能只关闭到托盘。插件及其检查工程不加入宿主日常构建链，VS Code 设计时引用仅用于补全；发布后独立构建才是正式路径。SDK 投影/WinRT 等插件私有库可随插件分发，须携带自己的许可，不误作宿主共享 SDK 删除。

## 7. 插件交付要求

- 提供清单、兼容 SDK/平台、入口、全部私有运行依赖与许可；在已发布宿主中验证，不能只在 IDE 内可用。
- 覆盖缺少依赖、坏 DLL、协议/平台不匹配、禁用、初始化/注册/页面失败等适用分支；错误可定位且不留下部分注册。
- 验证页面与设置注册、深浅色/主题色、重启持久化和相关生命周期；有限任务与常驻服务各自验收。
- 仅使用隔离数据和临时发布副本。静态检查、构建、自动化、集成、真实系统/UI/硬件分别报告。
- 业务特有的用例和实现范围放各插件文档；结果和限制只写 DEVELOPMENT.md。当前用户接手运行测试，未经新指示不自动启动验收程序。
