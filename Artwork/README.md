# TractorTool 图标素材

[透明母版](tractortool-master.png) 于 2026-10-03 使用内置 imagegen 按用户的“扳手、瑞士军刀主题”生成。未使用现成品牌标识或参考软件图标；最终选择红色折叠工具柄、银色开口扳手和短螺丝刀组合。

窗口、关于页使用 `TractorTool/Assets/tractortool.png`；程序 EXE 和托盘使用同一来源的 `tractortool.ico`，包含 16/20/24/32/48/64/128/256 像素。母版不嵌入应用。构建不调用图片生成服务；需重新导出时在 Windows PowerShell 执行：

```powershell
./scripts/Generate-AppIcon.ps1
```

脚本只缩放和封装格式，保留透明通道。外形修改应修改母版后再导出，避免各入口分别维护。

## 导航与插件示例图标

主页、日志、插件、设置使用 Microsoft [Fluent Emoji](https://github.com/microsoft/fluentui-emoji/tree/1ffb34c752ecf5d402f04cfb4b392c77f57c54bc) 的 3D PNG，同一提交、同一套风格；独立测试插件使用该套 Puzzle piece 图片。均保留原始文件，无需运行时联网或额外图标包。

| 本地文件 | 上游文件（上述固定提交） |
| --- | --- |
| `TractorTool/Assets/Icons/home.png` | `assets/House/3D/house_3d.png` |
| `TractorTool/Assets/Icons/logs.png` | `assets/Memo/3D/memo_3d.png` |
| `TractorTool/Assets/Icons/tools.png` | `assets/Toolbox/3D/toolbox_3d.png` |
| `TractorTool/Assets/Icons/settings.png` | `assets/Gear/3D/gear_3d.png` |
| `tests/IndependentPlugin/Assets/icon.png` | `assets/Puzzle piece/3D/puzzle_piece_3d.png` |

这些素材使用 MIT 许可，完整声明随宿主 [第三方通知](../THIRD-PARTY-NOTICES.md) 发布；示例插件携带 `Assets/LICENSE-FluentEmoji.txt`。原始下载地址与 SHA256 记录在 `artifacts/reference-emoji/assets.json`。它们不是参考软件的品牌素材，也不替换 TractorTool 应用/托盘图标。常规设置操作继续复用 FluentAvalonia 的单色符号。

系统数据HUD 使用 Microsoft [Fluent System Icons 的 Gauge 彩色仪表盘](https://github.com/microsoft/fluentui-system-icons/blob/883bef1e12d37343d0b2a64a1af43d0209de1128/assets/Gauge/SVG/ic_fluent_gauge_32_color.svg)，不是 Emoji。原始 [SVG](fluent-gauge-32-color.svg) 固定到提交 `883bef1e12d37343d0b2a64a1af43d0209de1128`；用已有 Sharp 将 32 DIP 图形按 8 倍栅格化为透明 256×256 PNG，未修改轮廓或配色。PNG 存放于 `Plugins/TractorTool.Plugins.SystemHud/Assets/icon.png`，MIT 许可随包附于 `Assets/LICENSE-FluentSystemIcons.txt`，见[插件声明](../Plugins/TractorTool.Plugins.SystemHud/NOTICE.md)。SVG SHA256：`9CEAD7CF604F99C61DF754AE9A79BA7BE62CA5ECE728E4BF2B4E7F1F0F3545B5`；PNG SHA256：`C7FC62570664A3A28743BE861A7D6502E55C5B5D5107BBB33EA34D87928CF62B`。不需要运行时联网或额外图标依赖。

## 应用图标生成记录

生成提示词（内置工具，透明背景开启）：

> Use case: logo-brand. Asset type: the single production app icon for TractorTool, a polished Windows desktop toolbox. Create ONE standalone icon on a genuinely transparent background, square canvas, no mockup, no text or lettering. Subject: a compact folded multi-tool with a coral-red Swiss-army-knife-style rounded handle angled from bottom-left toward upper-right, a bold satin silver open-end wrench unfolded from its hinge, and one short secondary screwdriver blade. The wrench must be immediately recognizable and dominant. Original unbranded design; no Swiss cross, no shield, no manufacturer logo. Style: expertly crafted modern desktop utility icon, clean simple geometric silhouette, softly rounded edges, subtle restrained dimensional shading, clear warm coral/red handle and cool silver tools, dark graphite edge accents. The tools form one coherent object, not separate crossed clipart. Composition: centered, fills about 86% of the square, balanced with even transparent margin, two tool ends at most. Designed to read clearly at 24x24 and 32x32: thick silhouettes, broad color blocks, very few details, no fine textures, no tiny screws except a single large hinge rivet, no ground shadow outside the object, no background tile, no text, no watermark. Export with actual transparent alpha.
