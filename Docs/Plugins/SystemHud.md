# 桌面状态 HUD 插件

参考 zmd-charge-plus 的使用场景，在本项目 .NET 10 / Avalonia 12.1.2 环境下独立实现。入口为 [HudPlugin](../../Plugins/TractorTool.Plugins.SystemHud/HudPlugin.cs)，当前进度及证据只见 [开发台账](../DEVELOPMENT.md#current-status)。

## 范围与接入

按用户确认分三步推进：

1. 基础 HUD：电量/插拔电源提醒、时钟、CPU/内存/网速，动画、屏幕位置与缩放、点击穿透、托盘操作。
2. 多方案、轮播、变量模板、颜色规则、编辑和预览。
3. GPU/温度/硬件传感器、网络探测、HTTP/JSON、DeepSeek 和更多变量。

通过现有页面注册接口提供“桌面状态 HUD”主页面，以及“设置 → 插件设置 → 桌面状态 HUD”；不新增导航 API。打开软件仍复用现有托盘“显示 TractorTool”。两个页面使用宿主共享样式；透明 HUD 使用插件内的深色/黄色卡片，不覆盖其他页面样式。没有复制参考仓库的代码、美术、字体或动画资源。

| 入口 | 职责 |
| --- | --- |
| 插件主页面 | 运行/失败状态、显示或隐藏、暂停或恢复、6 秒预览、最近显示内容；指标、显示器、锚点、缩放及偏移的布局编辑 |
| 插件设置 | 启用、默认常驻、电源变化提醒、点击穿透、显示动画五项持久偏好 |
| 托盘子菜单 | 复用主页面的显示/隐藏、暂停/恢复、预览操作，以及临时切换指标；启动失败或关闭 HUD 时禁用 |

仅依赖 Contracts 1.7、UI 1.5、Avalonia 和 MVVM；宿主不静态引用插件，不新增 Core 服务、独立进程、驱动或外部安装包。GPU/传感器有真实消费需求时再评估 LibreHardwareMonitor 等库。访问被拒绝时明确显示“需要管理员权限，请退出后以管理员身份运行 TractorTool”，不自动提权或安装服务。

## 交互

- 默认启用、非常驻；插入/拔出电源时显示电量 6 秒，电量变化更新已显示内容，无电池/未知状态显示明确说明。
- 常驻显示选定的单项指标；托盘可临时切换常驻/隐藏、暂停更新、预览 6 秒、切换下一指标。托盘临时操作不自动写入偏好；重启以保存的设置为准。
- 显示器、九宫格锚点、0.6–2 倍缩放及水平/垂直偏移。位置按显示器工作区和 DPI 计算，偏移不能把窗口完全移出屏幕。保存的显示器编号不在当前列表时回到主显示器；枚举顺序改变时需重新选择。极小工作区/超大缩放只能钉在工作区原点，不能保证整个卡片放得下。
- 默认点击穿透、不抢焦点、不在任务栏增加入口、置顶。关闭穿透时单击 HUD 隐藏；不是可交互工具面板，也不是嵌入 Explorer 桌面层。
- 出现时淡入；插件动画开关与宿主“界面动画”设置共同生效。关闭动画不影响数据显示。
- 主页面“保存布局”只保存显示内容和位置；设置页“保存偏好”只保存五项开关，不提交或清空另一页未保存的修改。预览使用已保存布局（托盘临时指标切换除外）。保存失败不切换到未持久化的配置，保留错误并可重试。
- 主页面只展示 HUD 最近读数，不另启一份性能采样。启动失败显示原因并禁用控制，保留配置编辑供修复后重启；详细异常由宿主统一记录。

## 采集与生命周期

电源采用 `RegisterPowerSettingNotification`，监听电源来源和电量百分比；初始化及通知时用 `GetSystemPowerStatus` 读取，无 150ms 电源轮询。注册失败明确提示自动提醒不可用，不悄悄启用后台轮询。

CPU 采用 `GetSystemTimes` 时间差，内核时间包括空闲时间；内存采用 `GlobalMemoryStatusEx`；网速采用 .NET `NetworkInterface.GetIPStatistics` 的收发计数差，使用 Stopwatch 实际间隔。与参考项目的计数器差分思路一致，不移植其 WMI/驱动整套采集代码。首次采样/计数器重置显示未知，不能冒充 0；CPU 大于 64 逻辑处理器时仅覆盖调用线程所属处理器组，界面注明范围。网速为活动非 loopback/tunnel 接口合计，含虚拟网卡时可能重复计数，不宣称是纯互联网流量。

精确的 CPU 使用率和网速需要区间，网络连接变化事件不能替代字节速率采样；内存阈值通知也不是实时占用率。仅当前性能指标可见且未暂停时，每秒在后台读取一次；不重叠采样，不做 WMI 查询/ETW 全量追踪。时钟在可见时每秒更新；电量页等待事件。实际第一次启动性能采样写一条开发优化 Warning，此后不逐秒写日志。

`IPluginBackgroundService` 拥有窗口与通知，切页或隐藏宿主不取消。隐藏 HUD/暂停/换指标使旧采样失效；下次采样重新建立基线。显式退出先停止计时器、注销电源通知、解绑屏幕/窗口事件并关闭 HUD，再等待在途采样结束；不以抛正常取消异常来实现停止。

配置由宿主 `IPluginSettingsStore` 保存到该插件的数据目录 `hud.json`，不访问宿主配置或其他插件数据。动态数值只在内存中，重启不恢复旧采样。

## 验收方法

独立插件打包使用已有 [Build-Plugin](../../scripts/Build-Plugin.ps1)，不要复制宿主/Avalonia/.NET 框架 DLL。HUD 需要包含 E66 Win32 框架程序集加载修复的宿主；旧宿主只更新插件仍会失败。HUD 是新增独立插件，首版整包脚本仍只组装原有两个正式插件；完整发行前再决定默认附带范围。

- 使用修正后的宿主安装 ZIP，显式退出重启，在插件主页面控制/预览，在插件设置中保存偏好；确认两处未保存修改互不影响，托盘临时切换不被“保存偏好”写入布局。
- 核对启动失败状态、控制禁用及日志原因；正常启动后确认关闭主窗口、切页继续运行，禁用插件并重启后菜单和 HUD 均消失。
- 插拔电源、休眠恢复、电量改变、无电池桌面机；确认没有伪造百分比或重复弹窗。
- 五项指标逐一观察，确认暂停/隐藏没有持续采样或日志刷屏；网卡断开/重连不出现负数或尖峰，失败显示未知及原因。
- 高低 DPI、负坐标副屏、拔掉目标显示器、极限偏移；核对真实位置和可恢复性。
- 点击穿透、无焦点抢占、隐藏/预览到期、动画开/关、托盘快捷项、重复应用、退出恰逢采样。
- [HudChecks](../../tests/TractorTool.HudChecks/Program.cs) 是纯逻辑控制台检查，覆盖 CPU 差分、网速重置/实际时间、非法配置和屏幕边界。命令 `dotnet run --project tests/TractorTool.HudChecks -c Release` 供获准时执行；编译不等于检查运行通过。

API 依据：[Windows 电源通知](https://learn.microsoft.com/en-us/windows/win32/power/registering-for-power-events)、[电源 GUID](https://learn.microsoft.com/en-us/windows/win32/power/power-setting-guids)、[GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)、[GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex)、[NetworkChange](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkchange)。上游调研和固定提交记录见 [E65](../PROJECT_HISTORY.md#evidence-system-hud)。
