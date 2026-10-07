# SystemHud 源码导航

从根目录的 [HudPlugin.cs](HudPlugin.cs) 开始：它连接宿主生命周期、页面和托盘入口。

```text
TractorTool.Plugins.SystemHud/
├─ HudPlugin.cs          插件入口与宿主接入
├─ Configuration/       方案、设置、凭据保存格式与校验
│  ├─ HudProfile.cs     方案及轮播配置
│  ├─ HudSettings.cs    共用设置、读数模型与计数计算
│  └─ HudSecrets.cs     Windows 凭据加解密
├─ Sampling/            单次读取与数据来源
│  ├─ HudSampler.cs     采集协调与请求缓存
│  ├─ WindowsMetrics.cs  基础 Windows 指标
│  ├─ HardwareMetrics.cs CPU / GPU 传感器
│  └─ NetworkMetrics.cs  ICMP / TCP / HTTP / JSON / 余额
├─ ViewModels/          运行状态、命令和编辑交互
│  ├─ HudModel.cs       显示、暂停、采样与停止
│  ├─ HudModel.Profiles.cs  方案编辑、轮播与预览
│  └─ HudModel.Sources.cs   传感器选择与凭据操作
├─ Views/               界面与显示转换
│  ├─ HudView.axaml            主页面
│  ├─ HudSettingsView.axaml    插件设置页
│  ├─ HudWindow.axaml          桌面悬浮窗口
│  ├─ HudCard.axaml            共用三态布局、手势与弹性形变
│  ├─ HudSlotView.axaml        共用文字、图标与环形区域
│  ├─ HudLayout.cs             三态、内部具名区域与切换规则
│  └─ HudPresentation.cs       模板、颜色与显示模型
├─ Licenses/            第三方许可原文
├─ NOTICE.md            依赖及源码来源
├─ plugin.json          插件清单
└─ TractorTool.Plugins.SystemHud.csproj
```

每份 AXAML 与同名 `.axaml.cs` 放在同一目录。`bin/`、`obj/` 是生成目录，不属于源码分层。

`HudLayout` 定义内部显示数据，不属于用户配置：默认态 `Compact.Left/Center/Right`，长条 `Strip.Left/Primary/Secondary/Right`，详细态 `Expanded` 复用四槽类型填入图标、主值、名称与辅助内容，并增加正文 `Detail`；`HudDisplay.Updated` 独立呈现读取时间。`HudPresentation` 按指标填入这些具名区域，`HudSlotView` 渲染文字、图标或真实进度环。新增指标复用默认映射，或在 Presentation 调整对应区域，不增加用户区域编辑器。网速的上传值由 `HudReading.SecondaryValue` 提供，`Context` 提供接口范围、可用内存等补充信息；都不持久化，不从本地化说明中拆字符串。详情不再拼接一遍标题、主值和说明。

详细态所有指标统一为左侧图标与名称横排并居中，右上辅助、右下主读数；下方细线与整行说明/时间构成信息区。内置 Context 的换行由 Presentation 合并，用户模板不改写。HudCard 的局部 headerSlot 样式对文字作 1 DIP 光学居中补偿，不调整图标或正文。

`HudCard` 管理三态布局、单/双击与拖动识别、有限形变；保留固定行列定义，仅调整尺寸和控件位置。长短条松手即切换，没有单击等待计时器；中央双击沿系统 ClickCount 接续，收窄后消失的两端不保留隐形命中区。显式状态变化统一取消候选点击，键盘路径先于 Button 默认动作清理鼠标状态。按下反馈绑定到整个 Island，顶部模板保持透明。`HudSlotView.Item` 接收区域内容，内部 Grid 绑定 Item；控件自身仍继承父级 HudDisplay，避免编译绑定在两种类型间误转换。`HudWindow` 执行原生移动、DPI/边缘定位与窗口区域裁剪，位置换算集中在 `HudSettings`。左侧唯一的编辑预览和桌面共用卡片，编辑预览用草稿与示例数据，不启动采样；采集器不参与布局或动画，不为每个指标复制视图。

修改布局先看 `Views`，修改操作流程先看 `ViewModels`，修改采集或接口先看 `Sampling`，修改持久化数据先看 `Configuration`。这些目录用于源码导航；类型命名空间保持 `TractorTool.Plugins.SystemHud`，插件入口和配置身份不随目录变化。

完整职责、数据流和行为约束见 [插件设计文档](../../Docs/Plugins/SystemHud.md)，当前实现与验收状态见 [开发台账](../../Docs/DEVELOPMENT.md#current-status)。
