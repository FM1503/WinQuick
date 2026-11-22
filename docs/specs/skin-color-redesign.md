---
status: approved
wireframe: approved
slug: skin-color-redesign
date: 2026-11-23
review: required
ui-files:
  - preview/skin-color-preview.html
code-files:
  - design/tokens.json
  - design/skins.json
  - src/WinQuick.Core/Design/DesignTokens.cs
  - src/WinQuick.App/Design/TokenResources.cs
  - src/WinQuick.App/Radial/SkinPanel.xaml.cs
  - src/WinQuick.App/Radial/RadialRingVisual.cs
  - src/WinQuick.App/Radial/SettingsPanel.xaml
  - tests/WinQuick.Core.Tests/SkinContrastTests.cs
---

# 皮肤配色全量重设计（轮盘 + 面板配对法补全）

## 0. 背景与缺陷实锤（用户截图，v1.1.5，2026-11-23）

用户报告「轮盘和设置面板还是存在内容配色问题，导致内容和背景颜色重叠」，附截图两证：

1. **皮肤面板中「半透明」的选中行整块纯白、无文字。** 根因：SkinPanel 选中行背景 = `Color.Accent`，
   文字 = `Color.Text`；而 v1.1.5 的 `DerivePanelColors` 为修复霓虹绿被压暗，把 Accent 定为
   `HoverFill` **原色** —— 半透明皮肤的 HoverFill 与 Label 同为 `#FFFFFF`，白底白字。
   逐套核算选中行配对（Accent 底 × Text 字）：

   | 皮肤 | Accent（选中行底） | Text（行内文字） | 判定 |
   |---|---|---|---|
   | 半透明 | `#FFFFFF` | `#FFFFFF` | 完全不可见（截图实锤） |
   | 高对比 | `#FFFF00` | `#FFFFFF` | 白压黄，不可读 |
   | 霓虹 | `#00E5FF` | `#E0F8FF` | 亮青压浅青，不可读 |
   | Material | `#D0BCFF` | `#E6E1E5` | 浅紫压浅灰，不可读 |
   | 复古像素 | `#33FF66` | `#5CFF8E` | 亮绿压亮绿，不可读 |
   | 浅色 | `#B45309` | `#3F3A31` | 深压深，勉强 |
   | 默认深色 | `#0078D4` | `#FFFFFF` | 碰巧合格 |
   | 跟随系统 | 强调色原色 | 不配对的 Label | 看运气 |

   **结构性根因：上一轮只立了「Text↔Surface / Accent↔Surface / Field↔Text」三条配对法，
   漏了「文字画在 Accent 底上」这条。** 凡拿 Accent 当背景画的元素全部暴露在此漏洞下。

2. **轮盘侧（半透明皮肤在用）**：死区是**不透明纯白圆盘**（`deadZoneFill: #FFFFFF` 无 α 覆盖），
   在半透扇区中央打出一个与材质脱节的焊死大白点；角标/子项占位字/spinner 的底色在各皮肤上
   要么没覆盖、要么手拍，与文字没有保证关系。这类次要面此前**逐套手拍、无派生规则**，
   本次按用户拍板改为**统一派生**。

用户指令：**全量排查相关问题，重新设计所有皮肤的配色方案**。
澄清已定（AskUserQuestion 2026-11-23）：保身份、重配色；半透明皮肤加暗色 scrim 保底；
次要表面统一派生规则；HTML 原型先行、用户挑定后实装。

## 1. 需求规格

R1 修复 text-on-accent 配对缺失：所有以 Accent 为底的区域（选中行、高亮态），文字取
   **配对派生色 AccentText**，不再复用 Color.Text。逐套必须可读（矩阵锁定）。
R2 半透明皮肤加 **scrim 保底层**（Fluent Acrylic 的真实构成），白字在浅色/纯白壁纸上达标；
   **定稿（2026-11-23 用户原型挑定）**：五档 A–E（0.45/0.55 提亮、0.65 提亮、0.65 压暗、0.72 压暗）中取 D 档 —— scrim α0.65 + 悬停压暗（hoverFill #000000 α0.18）。
R3 轮盘次要表面（死区底/描边、角标底/字、子项占位字、spinner）**改为从主色派生**，
   skins.json 只保留身份色覆盖；死区不再是独立手拍值。
R4 八套皮肤全部配色按上述规则重推一遍：每套保持身份色相（默认 Fluent 蓝 / 浅色纸感米白+琥珀 /
   霓虹青 / 玻璃白 tint / Material 紫 / 高对比黑黄白 / 磷光绿 / 系统强调色），
   扇区底、悬停、描边、文字、hint、角标、死区、面板全套键重算。
R5 面板标题类「强调色当文字」处（Section 用 Color.Accent）：给出两档让用户挑
   （达标保证的强调色标题 vs Text 色 + SemiBold），依据是微软明文不建议在 acrylic 面上放强调色文字。
   **定稿（2026-11-23 用户原型确认，默认档）**：B 档 —— Section 标题用 Text 色 + SemiBold（SkinPanel.xaml），Accent 只保留「当线/当点缀」语义。
R6 配对法规则全部纯函数化并单测锁死，覆盖 **8 套皮肤 × 全配对矩阵 × 明暗两类极端底**；
   新增皮肤/改配色必须过矩阵才算合法（Validate 层拦不住的部分由测试拦）。

非目标：不改形态几何、不改动效参数（除配色重推所需的最小值）、不做系统高对比模式联动、
不做壁纸亮度自适应（用户已选 scrim 方案而非自适应）。

## 2. 调研结论与来源

| # | 结论 | 出处 | 获取状态 |
|---|---|---|---|
| S1 | Acrylic 材质配方 = 背景 + 模糊 + **exclusion blend 保底层** + 色彩 tint + 噪点；保底层的职责就是「确保其上 UI 的对比度与可读性」；自定义 tint/透明度会伤可读性；**明确禁止在 acrylic 面上放强调色文字**；透明效果关闭/高对比/省电/低端硬件时一律**回退实色** | Microsoft Learn《Acrylic material》(learn.microsoft.com/windows/apps/design/style/acrylic，2026-10-02 修订) | ✅ 全文抓到 |
| S2 | 分层窗口拿不到系统模糊，本项目以「α 合成到暗底」近似 exclusion/tint 保底（v1.1.5 面板已如此，本轮把同一语义扩到轮盘面与 scrim） | 本项目 docs/M0-findings.md 既有实测 | ✅ |
| S3 | 正文对比度 ≥4.5:1，大字（18pt 常规 / 14pt 粗体）≥3:1；相对亮度 Rec.709 伽马校正公式 | W3C WCAG 2.2 SC 1.4.3 | ✅ 全文抓到 |
| S4 | **非文本 UI 边界、状态指示、焦点环 ≥3:1**；比值不得四舍五入（2.999 即不合格） | W3C WCAG 2.2 SC 1.4.11 | ✅ 全文抓到 |
| S5 | Material 每个色彩角色 X 配套专用文字角色 **on-X**，不复用通用文字色；动态色按「对比度要求」自动调整 | Material color-utilities README（m3 规范页为 SPA 抓不到正文，如实标注）；on-X 概念另见 docs/M8-skins.md 既有调研 | ◐ 部分（README 已抓，规范正文未达） |
| S6 | 玻璃拟态社区做法：暗 tint/scrim 兜底 + 高对比描边勾边界 + 文字投影；同工具先例 Kando Neon Lights（霓虹身份沿用自 M8 调研） | 社区经验值，按 AGENTS.md 规则如实标注 | ◐ 标注为社区 |

阈值分层（配对矩阵用）：正文 4.5 / 大字与次要文字 3.0 / 非文本边界（死区描边、色板边界、
输入框边框、选中行与面板底的边界）3.0。纯亮度差判据（Field↔Text ≥0.15 等 v1.1.5 内部判据）
保留并标注其与 WCAG 的映射。

## 3. 技术方案

### 3.1 配对法补全（R1）

- `PanelPalette` 增键 **`AccentText`**；派生规则：`对比度(Accent, 候选深色) ≥ 对比度(Accent, 候选浅色)`
  取优者 —— 具体为从 SystemSkin.AccentLabelText 抽出共享纯函数（按 WCAG 相对亮度而不是 0.5 硬阈值，
  两处的旧「亮度>0.5」实现一并换成「取对比度更大的一侧」，行为兼容）。候选深色 = `#1B1B1B`，
  候选浅色 = `#FFFFFF`；高饱和中灰强调色（如 #7C4DFF）自动得到白字。
- 新资源键 `Wq.Color.AccentText`；`SkinPanel.BuildSkinRow/UpdateSelectionVisuals`、
  `SettingsPanel` 所有以 Accent 为底的元素（皮肤选中行、形态选中行）Foreground 改用 AccentText。
- 色板 swatch 仍画皮肤原色（它的语义是「展示材质」，有 Color.Outline 边界即合规，S4 判边界不判内容）。

### 3.2 次要面统一派生（R3）

新增纯函数（与 DerivePanelColors 同侧、可单测）`DeriveWheelSecondary(ColorTokens c)`：
- `SectorComposite` = sectorFill 按 α 合成到暗基线 `#202020`（与面板同源语义）。
- **死区底** = 明皮肤：`mix(SectorComposite, #FFFFFF, 0.10)`；暗皮肤：`mix(SectorComposite, #000000, 0.35)`；
  即死区永远比扇区「更暗一档/更亮一档」同族色，半透明皮肤不再是纯白盘。
- **死区描边** = 反向对比 ≥3:1（SC 1.4.11）：按死区底亮度取 `#FFFFFF`α0.35 或 `#000000`α0.5。
- **角标底** = 死区同派生链再暗一档；角标字/spinner 色 = `Label`（配对矩阵校验 角标字↔角标底 ≥4.5）。
- **hint** = Label 按合成底方向 α 衰减（现有 hintAlpha 保留，矩阵补 提示字↔扇区合成底 ≥3.0）。
- skins.json 中 **删除各皮肤手拍的 deadZoneFill/badgeFill 覆盖**（霓虹、浅色、material、高对比、
  复古、半透明全删），改走派生；皮肤仍可按身份覆盖，但矩阵对「覆盖后」的值判定。

### 3.3 scrim 保底层（R2）

- tokens.json 新增 `color.sectorScrim`（默认 `#000000`）+ `color.sectorScrimAlpha`（默认 `0.0`），
进 DesignTokens.Validate 域（0..0.9 —— 实现时把上限从 1 收紧到 0.9，超过就不是玻璃是黑板；reviewer P2 勘误）。默认皮肤行为不变。
- 轮盘扇区渲染路径：scrimAlpha>0 时**先铺一层底层扇区 Path**（实色 scrim @ scrimAlpha），
  再叠原 sectorFill —— 两层纯几何，无 Effect、无位图（S2 约束）。只加一层 Path，成本与扇区同级。
- 半透明皮肤 scrim 档位 0.45 / 0.55 / 0.65 进原型让用户挑；
  达标验证按**最坏白壁纸**（纯白 #FFFFFF 为底）算 白 label ↔ 合成结果 ≥4.5（SC 1.4.3），
  单测以「白色壁纸极端值」为输入锁定（真实壁纸任意，规则保证最坏情形）。
- 半透明皮肤死区改同材质派生后，截图中的「大白圆点」自然消除。

### 3.4 八套皮肤重配色（R4）

身份锚点不变，全色域按 §2 阈值重推；具体候选值在原型呈现（左列 8 套 + 现状对照），
挑定后写回 skins.json。重推的成套清单：默认·深色、浅色、霓虹、半透明、Material 弹性、
高对比、复古像素、跟随系统（Compose() 的明暗两套同步更新）。
「高对比」皮肤特殊处理：选中行黄底黑字（AccentText 派生自动满足），纯黑实心不变。

### 3.5 面板强调色标题（R5）

Section 标题两档进原型：A=保留 Accent 作标题色 + 矩阵强制 标题↔Surface ≥4.5（不达标皮肤自动调 accent）；
B=标题改 Text + SemiBold，Accent 只用于行内小强调。

### 3.6 原型（preview/skin-color-preview.html）

- 复刻真实几何：740 窗口比例、ring1Outer 200 / deadZone 56 / ring2 218–350、12 父项示例标签
  （用 menus.json 真实标签，AGENTS.md 规矩）、描边粗细/渐变/光晕逐套真实值。
- 右侧同屏两块：**轮盘 SVG** + **面板行真实样式 mock**（普通行/选中行/hint/输入框/标题/滚动区）。
- 背景三档壁纸可切：深色花纹、中等蓝灰、**纯白**（验证 scrim 的极端情形）。
- 左列：8 套皮肤重配色方案，每套附「现状对照」背靠背；scrim 三档、标题 A/B 两档独立控件。

- 页内 selfCheck()（onerror + 关键配对计算复核），`node preview/_verify.mjs` 可跑纯逻辑自检。
  注（reviewer P2-3 勘误）：原型 selfCheck 的 W4/W5 按面板合成底口径，比单测口径乐观；单测按实际渲染分支（scrim 保底盘死区、角标叠扇区底）判定 —— 终判以单测与实机为准，两者当时皆过。

## 4. 测试计划（TDD）

- T1 `AccentText` 派生纯函数单测：8 强调色候选 + 中灰/黄/白极端 → 黑白择一对比度最大。
- T2 `DeriveWheelSecondary` 单测：8 皮肤死区/角标派生确定值；半透明死区不再是 #FFFFFF。
- T3 SkinContrast 全矩阵：现有 18 条扩为「9 皮肤状态（含系统明暗）× 全配对行」，
  新增行：AccentText↔Accent ≥4.5、label↔最坏白壁纸 scrim 合成 ≥4.5、
  角标字↔角标底 ≥4.5、提示字↔扇区合成底 ≥3.0、死区描边↔相邻扇区 ≥3.0。
- T4 DesignTokens.Validate：sectorScrimAlpha 范围 + scrim>0 皮肤的 label↔白壁纸 判定入 Validate。
- T5 编译 + `skin-probe` 资源键存在性扩展（AccentText 键装机检查）。
- T6 原型逻辑自检 `node preview/_verify.mjs file:///…/skin-color-preview.html`。

五列验收测试计划表在任务书 `docs/tasks/skin-color-redesign.md` 中固化。

## 5. 验收 checklist（观感归用户，AGENTS.md）

- [ ] 用户逐套切换 8 皮肤：选中行、标题、输入框、hint、色板均可读（含把壁纸调成纯白再验半透明皮肤）。
- [ ] 轮盘：半透明皮肤死区不再是焊死大白点，scrim 档挑定值生效；角标/占位字/spinner 与底有层次。
- [ ] 现状对照列由用户背靠背确认「重配色版不劣于现状、可读性全面达标」。
- [ ] 全矩阵单测 pass、编译 0 error、crash.log 无新增。

## 6. 线框（原型页 ASCII）

```
┌────────────────────────────┬──────────────────────────────────────────┐
│ 皮肤（8 套 × 重配色候选）    │  壁纸: [深花纹][中蓝灰][纯白]   scrim:[45|55|65]│
│ ▸ 默认·深色   [现状对照]     │  ┌───────────────┐  ┌────────────────────┐ │
│ ▸ 浅色        [现状对照]     │  │ 轮盘 SVG       │  │ 面板 mock           │ │
│ ▸ 霓虹        [现状对照]     │  │ (真实几何/     │  │ ▭ 普通行            │ │
│ ▸ 半透明      [现状对照]     │  │  12 真实标签,  │  │ ▨ 选中行(AccentText)│ │
│ ▸ Material    [现状对照]     │  │  死区/角标/    │  │ ▭ hint 行           │ │
│ ▸ 高对比      [现状对照]     │  │  悬停全量)     │  │ ▭ 输入框            │ │
│ ▸ 复古像素    [现状对照]     │  └───────────────┘  │ 标题: [A 强调色|B文本]│ │
│ ▸ 跟随系统(明暗×强调色样例)   │                     └────────────────────┘ │
└────────────────────────────┴──────────────────────────────────────────┘
```

wireframe: approved —— 2026-11-23 AskUserQuestion 用户选定布局 A（左列 8 套 + 右列轮盘 SVG 与面板 mock 双联），spec 同轮批准，UI 文件已解锁。