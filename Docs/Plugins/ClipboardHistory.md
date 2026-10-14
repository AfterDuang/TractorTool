# 剪贴板历史插件

## 1. 范围与依据

本文维护首版剪贴板插件的需求、Windows 适配、交互和验收方法，对应 [产品需求](../TractorTool.md) R08、R09。公共接入规则见 [插件开发规范](../PLUGIN_DEVELOPMENT.md)，宿主机制见 [软件架构](../ARCHITECTURE.md)。实施阶段为 M2；实际验收结果只记录在 [开发与验收](../DEVELOPMENT.md#acceptance)。

## 2. 已确认行为

用户的主要流程是：找到曾复制的内容 → 选择并预览 → 再次复制 → 粘贴到目标应用。只展示历史文本和占位卡片不能满足此流程。

- **左侧历史、右侧预览**：左侧紧凑列表显示类型、文本摘要和时间；单击选择，右侧显示可选文本或图片。两侧分别滚动，滚动条在各自面板边缘，不给每条记录再套滚动卡片。页面底部保留 24 个逻辑像素，与其他页面的正文留白一致；卡片使用剩余高度，不贴住窗口底框。
- **再次复制**：右侧“复制”按钮、历史行双击、列表聚焦时 Enter / Ctrl+C，均复制选中的系统原历史项。右侧文本选区的 Ctrl+C 继续复制所选文字。复制不自动粘贴或切换外部应用。
- **系统事件刷新**：打开页面加载；页面存续期间订阅系统历史变化/历史开关事件，合并刷新请求；窗口重新获得焦点时补刷。保留手动刷新，支持重试暂时失败的预览。
- **保留上下文**：刷新成功时按系统 Id 更新列表，尽量保留当前选择和已生成的预览；所选项被系统移除才选择其他项。访问暂时被拒绝不立即清空已有列表。
- **原内容独立于预览**：预览被截断、图片太大或类型无法展示时，仍可请求 Windows 复制完整原内容；系统拒绝或该项已删除要明确反馈，不能显示虚假成功。
- **系统拥有历史**：不监听当前剪贴板来建立自己的数据库，不保存历史正文或图片到应用数据/日志。离页解绑系统事件并释放预览，不增加常驻服务。

交互参考 [Apple 分栏视图](https://developer.apple.com/design/human-interface-guidelines/split-views) 与 [滚动视图](https://developer.apple.com/design/human-interface-guidelines/scroll-views) 的相邻列表/详情、区域独立滚动原则。保留 Windows 和 Avalonia 的控件与输入行为，不照搬 macOS 外观。具体视觉和缩放结果需在真实窗口验收。

## 3. 平台与内部设计

插件采用 `net10.0-windows10.0.19041.0`，最低支持 Windows 10 build 19041，固定 Windows SDK 投影包 `10.0.19041.57`。历史 API 自 build 17763 引入，插件使用更高编译基线；这不是最低系统实机测试结论。清单排除非 Windows，入口再次检查系统版本。[历史读取 API](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.datatransfer.clipboard.gethistoryitemsasync?view=winrt-26100)、[桌面应用使用 WinRT](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps)

| 部分 | 责任 |
| --- | --- |
| [ClipboardPlugin](../../Plugins/TractorTool.Plugins.ClipboardHistory/ClipboardPlugin.cs) | 页面工厂和导航注册，传入宿主停止令牌及 UI 派发函数 |
| [WindowsClipboardHistory](../../Plugins/TractorTool.Plugins.ClipboardHistory/WindowsClipboardHistory.cs) | 读取系统快照、保留原生历史项、事件订阅、按需预览、复制原项；不使用 Task.Run 转移 WinRT 剪贴板访问 |
| [ClipboardHistoryViewModel](../../Plugins/TractorTool.Plugins.ClipboardHistory/ClipboardHistoryViewModel.cs) | 事件合并、Id/选择保留、状态、预览取消和迟到结果保护、复制反馈及离页清理 |
| [ClipboardHistoryView](../../Plugins/TractorTool.Plugins.ClipboardHistory/ClipboardHistoryView.axaml) | 分栏与共享主题、各自滚动、双击/键盘交互和窗口激活补刷 |

`HistoryChanged` / `HistoryEnabledChanged` 回调只发出刷新通知，页面将通知派发到 Avalonia UI 线程再调用 `GetHistoryItemsAsync`。读取进行中收到多个事件时，合并为随后一次读取；本次异常也继续消费已排队的通知。没有定时器或持续轮询；首次进入、窗口激活和手动刷新是另外三个读取入口。每次触发读取系统快照与文本摘要，不为所有条目解码图片；实际 CPU/内存未作性能测量。项目按原始 `Timestamp` 倒序排列，不按本地时间字符串排序。[系统历史事件](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.datatransfer.clipboard.historychanged?view=winrt-26100)

复制调用 `Clipboard.SetHistoryItemAsContent`，传入保留的原 `ClipboardHistoryItem`；结果区分 Success、AccessDenied、ItemDeleted 和异常。未知预览格式不会被转换成文本再复制。系统焦点/策略或内容已失效时仍可能失败；Windows 并不保证任意文件或自定义格式都会进入系统历史。[复制原历史项](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.datatransfer.clipboard.sethistoryitemascontent?view=winrt-26100)

当前预览范围（代码能力，实际系统格式待验）：

| 系统提供的内容 | 预览方式与界限 |
| --- | --- |
| 文本 | 左侧摘要最多 180 字符；右侧最多 20,000 字符并提示截断；复制使用原项 |
| Bitmap | 只为当前选中项异步读取/解码；输入流最多 32 MiB、源图最多 1 亿像素、预览长边最多 1600 像素；超限提示并保留复制入口 |
| HTML / RTF（没有可用普通文本时） | 以惰性纯文本展示源码；不执行 HTML、不获取远程资源，复制保留原格式 |
| StorageItems（系统确实返回时） | 展示文件名，不打开或读取文件内容 |
| 其他格式 | 显示类型说明；允许请求复制原项，不能保证目标应用支持粘贴 |

图像通过 Windows `BitmapDecoder` 缩放解码为 BGRA 预乘像素，再创建 Avalonia 位图；替换预览前解除旧绑定再释放，离页或迟到结果也释放资源。[缩放像素读取 API](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapdecoder.getpixeldataasync?view=winrt-26100)

正式构建只引用已发布 Contracts/UI，宿主没有插件静态项目引用。独立版本在 [plugin.json](../../Plugins/TractorTool.Plugins.ClipboardHistory/plugin.json) 维护，打包脚本派生程序集产品版本；已有旧版应走更高版本更新，不能覆盖同版本 DLL。私有 MVVM、SDK 投影与 WinRT 库随包提供，许可见 [NOTICE.md](../../Plugins/TractorTool.Plugins.ClipboardHistory/NOTICE.md)。不增加专属设置页或历史数据库。

## 4. 生命周期、错误与数据

- 页面离开先停止接收事件，再取消读取/预览的消费。部分 WinRT 操作不支持原生 Cancel，因此使用无令牌的 AsTask，原生操作完成后检查托管取消，不继续下一步或回填页面。图片流仍等当前原生调用结束后释放，不能用提前结束等待来遗弃迟到流或提前关闭解码输入；原生调用若挂起，其任务与资源也会保留。所有异步操作结束后再释放生命周期取消源。
- 选中项变化取消旧预览；旧结果不能覆盖新项，也不能提前结束新项的加载状态。只保留一个当前预览，不缓存全部图片。
- 临时访问拒绝保留已有列表；成功读取为空或历史已关闭时清空选择/预览。右侧标题绑定非空派生字符串，选择变化同步通知；不依靠父容器隐藏来阻止空对象链求值。系统状态、预览失败与复制结果分别提示。
- 页面操作不影响其他插件的有限任务或常驻服务。Windows 专属实现只留在插件内。
- 不记录真实剪贴板正文，不擅自清空历史、改动系统历史开关或删除用户内容；运行验收由用户用临时内容执行。

## 5. 验收方法

下表是待执行方法，不是通过记录；完整结果集中在 [开发验收 A08](../DEVELOPMENT.md#acceptance) / [E18](../PROJECT_HISTORY.md#evidence-ui-fixes)。

| 场景 | 判定与证据 |
| --- | --- |
| 独立更新 | 将更高版本 ZIP 拖入宿主插件页，确认后显式退出重启；版本更新且宿主文件未替换 |
| 分栏与选择 | 窄/宽窗口、浅/深色、长文本、空列表；左右独立滚动，滚动到底能看到最后一项，卡片底部始终有留白；选中行无多层卡片高亮，文本选区仅在预览区域 |
| 再次复制 | 临时文本、截图、超长文本；复制后粘贴到适合的目标应用，核对原内容；双击空白/滚动条不复制 |
| 自动刷新 | 页面打开时在其他应用复制临时内容，返回后刷新；快速连续复制不并发读写，也不不断重置选择 |
| 预览竞争 | 快速切换图片/文本、刷新时继续切换；最后选中的内容正确，旧请求不遮盖当前预览 |
| 系统状态 | 历史关闭、空、拒绝、系统移除选中项、暂时无法预览时分别有反馈；空选择/离页无标题绑定错误，手动刷新可重试预览 |
| 生命周期 | 历史读取/图片预览尚未结束时切页、快速改选、多次返回，确保不报原生 Cancel/页面清理错误且旧结果不回填；隐藏/恢复后刷新正常，离页解除事件订阅，退出无资源异常 |
| 格式界限 | 记录 Windows 实际返回的格式和目标应用粘贴结果；不能将模型替身或文件存在当作 WinRT 成功 |

[ClipboardChecks](../../tests/TractorTool.ClipboardChecks/Program.cs) 使用明确替身覆盖上述状态机、事件合并、选择保留、原项 Id 复制、迟到预览/离页及错误脱敏，未调用 Windows API。构建、模型运行和真实 UI 三类结果分开记账；用户接手运行验收后，代理未自行执行这些检查。
