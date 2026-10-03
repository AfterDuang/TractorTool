# 剪贴板历史插件

## 1. 范围与依据

本文集中维护首版剪贴板插件的业务需求、平台设计和验收方法，对应 [产品需求](../TractorTool.md) R08、R09。公共接入规则见 [插件开发规范](../PLUGIN_DEVELOPMENT.md)，宿主机制见 [软件架构](../ARCHITECTURE.md)。实施阶段为 M2；实际完成情况及 A08 验收结果只记录在 [开发与验收](../DEVELOPMENT.md#acceptance)。

## 2. 已确认行为

首版读取 Windows 自带的剪贴板历史，进入页面时加载，支持手动刷新和基本列表展示。系统历史关闭、访问被拒绝、内容为空、项目已失效时分别给出明确状态。

内容类型按系统 API 实际返回情况处理，无法展示的项目提供占位说明，不能导致整个页面失败。具体支持的展示类型在实现与验收时记录。

该插件用于验证 Windows 专属能力、按需页面、插件导航与错误状态。系统 API 调用留在 Windows 插件内部，不能让宿主核心依赖 Windows 剪贴板 API。

- 只查看系统已有历史，不建立自己的历史库，不增加常驻监听来代替系统历史。
- 默认先完成文本展示，再根据系统实际返回能力增加图片或其他类型；最终支持范围以实际结果记录。
- 页面采用宿主共享主题与控件，导航由插件注册，不能把剪贴板业务硬编码到宿主。

## 3. 平台与内部设计

剪贴板插件使用 Windows 历史 API，区分成功、访问被拒绝和系统历史未启用。切换页面后可以释放显示资源；刷新取消令牌只作用于该次加载，不影响视频任务。[Windows API 状态](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.datatransfer.clipboardhistoryitemsresultstatus?view=winrt-26100)

插件采用 `net10.0-windows10.0.19041.0`，最低支持版本声明为 Windows 10 build 19041，固定 Windows SDK 投影包 `10.0.19041.57`。API 自 build 17763 引入，但本插件采用更高的编译/支持基线；这不是最低系统实机测试结论。清单排除非 Windows，入口再次检查版本。[系统 API](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.datatransfer.clipboard.gethistoryitemsasync?view=winrt-26100)、[桌面应用使用 WinRT](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps)

当前源码位于 [TractorTool.Plugins.ClipboardHistory](../../Plugins/TractorTool.Plugins.ClipboardHistory)，只正式引用已发布的 Contracts/UI，宿主没有对此插件的静态项目引用。职责如下：

| 部分 | 责任 |
| --- | --- |
| [ClipboardPlugin](../../Plugins/TractorTool.Plugins.ClipboardHistory/ClipboardPlugin.cs) | 注册导航及页面工厂；每次创建页面使用新的 VM |
| [WindowsClipboardHistory](../../Plugins/TractorTool.Plugins.ClipboardHistory/WindowsClipboardHistory.cs) | 调用系统 API，保留系统状态和单项读取错误；不使用 Task.Run 将访问转移到任意后台线程 |
| [ClipboardHistoryViewModel](../../Plugins/TractorTool.Plugins.ClipboardHistory/ClipboardHistoryViewModel.cs) | 页面首次进入时加载、手动刷新、维护加载/空/失败状态；取消和迟到返回保护 |
| [ClipboardHistoryView](../../Plugins/TractorTool.Plugins.ClipboardHistory/ClipboardHistoryView.axaml) | 共享主题下的列表、可选中文本、状态说明和刷新入口 |

系统历史由 Windows 拥有，宿主只提供页面、设置、目录和日志等公共能力。当前仅展示文本，每项显示最多 20,000 字符；不支持的图片/文件等内容显示占位，失效或读取失败的单项不影响其他项。页面加载和手动刷新由 Avalonia UI 线程发起，保留 WinRT 剪贴板访问所需线程/前台上下文；实际权限、窗口焦点和系统策略差异需在 Windows 运行验证。

插件没有私有设置页或后台监听，无需为演示而增加开关或历史数据库。独立版本 `1.0.0` 在清单中维护，打包脚本将其作为程序集产品版本。私有的 MVVM、Windows SDK 投影及 WinRT 运行库随包提供；许可来源与文件见 [NOTICE.md](../../Plugins/TractorTool.Plugins.ClipboardHistory/NOTICE.md)。

## 4. 生命周期、错误与数据

- 进入页面时加载，离页可以取消本次读取、释放显示资源和订阅；取消令牌不得影响其他插件、任务或插件宿主生命周期。
- 区分系统历史关闭、访问拒绝、没有内容、单项已失效和类型无法展示。单项失败保留其占位说明，不让整个列表失败。
- Windows 专属代码留在插件内，非支持平台在加载前被清单过滤；不模拟其他系统的 Windows 历史接口。
- 不把历史正文复制到应用数据库或运行日志；若以后确需展示偏好，使用宿主提供的插件设置存储，不以此保存历史内容。
- 调试和验收不擅自清空剪贴板、修改系统历史设置或记录真实敏感正文。

## 5. 验收方法

以下是待执行方法，不是通过记录。统一结果位置为 [开发验收 A08](../DEVELOPMENT.md#acceptance)，同时关联 A02/A03 的独立加载和兼容性要求。

| 场景 | 判定与证据 |
| --- | --- |
| 真实历史读取 | 独立构建的插件在已发布宿主中加载；进入页面显示系统实际返回的内容 |
| 手动刷新 | 更新的是系统当前历史；切页再返回仍可加载，未启动自建历史监听 |
| 系统状态 | 历史关闭、访问拒绝、空列表分别有明确说明，不能全部归为“没有内容” |
| 单项异常 | 已失效或不支持的内容有占位，其他项目继续显示 |
| 生命周期 | 离页/刷新取消只影响该次读取；不停止其他任务或常驻服务 |
| 平台与 UI | 在目标 Windows/SDK 环境真实调用；记录内容类型清单、主题和长内容展示结果 |

另有 [ClipboardChecks](../../tests/TractorTool.ClipboardChecks/Program.cs) 使用可控读取器覆盖状态、初始化一次/手动刷新、离页迟到结果、异常脱敏和应用取消。它不能证明 Windows API 已调用；本轮仅编译，未执行。真实 Windows 结果与这些模型检查及构建结果分开记录。
