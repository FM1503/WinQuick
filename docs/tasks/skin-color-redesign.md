---
slug: skin-color-redesign
spec: docs/specs/skin-color-redesign.md
date: 2026-11-23
---

# 任务书：皮肤配色全量重设计（spec R1–R6）

## 目标文件

design/tokens.json、design/skins.json、src/WinQuick.Core/Design/DesignTokens.cs、
src/WinQuick.App/Design/TokenResources.cs、src/WinQuick.App/Design/SystemSkin.cs、
src/WinQuick.App/Radial/SkinPanel.xaml.cs、src/WinQuick.App/Radial/RadialRingVisual.cs、
src/WinQuick.App/Radial/SettingsPanel.xaml、src/WinQuick.Probes（skin-probe 扩展）、
tests/WinQuick.Core.Tests/SkinContrastTests.cs、preview/skin-color-preview.html（新，先行）。

## 期望行为

1. R1：PanelPalette 增 AccentText（对比度取优的 #1B1B1B/#FFFFFF 择一，共享纯函数，
   SystemSkin.AccentLabelText 同规则改造）；新资源键 Wq.Color.AccentText；
   所有以 Accent 为底的区域（皮肤/形态选中行、高亮态）Foreground 改 AccentText。
2. R2：tokens 增 color.sectorScrim(#000000)/sectorScrimAlpha(0.0)；轮盘 scrimAlpha>0 时
   扇区先铺底层 Path 再叠 fill（纯几何两层，无 Effect）；档位与半透明皮肤取值等用户原型挑定后回填。
3. R3：新增 DeriveWheelSecondary 纯函数（死区底/死区描边/角标底/角标字/spinner/hint 从主色派生，
   规则见 spec §3.2）；skins.json 删除手拍 deadZoneFill/badgeFill 覆盖；轮盘渲染改读派生值。
4. R4：八套皮肤重配色（身份色相保留）；候选值以原型用户挑定为准。
5. R5：面板 Section 标题 A/B 两档进原型，取值以挑定为准。
6. R6：SkinContrastTests 扩全矩阵（9 皮肤状态 × 全配对行 + 最坏白壁纸 scrim 判定 +
   DesignTokens.Validate scrim 域 + AccentText/派生单测）。

## 验收标准与测试计划（本表之外的测试禁止执行）

| 验收标准 | 层级 | 命令 | 预期输出 | 预计耗时 |
|---|---|---|---|---|
| AC1 编译 0 error | 静态 | dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo | 0 错误 | 90s |
| AC2 全矩阵与派生单测全绿 | 单元 | dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~SkinContrast" | 失败 0 | 60s |
| AC3 令牌校验单测全绿 | 单元 | dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~DesignTokens" | 失败 0 | 60s |
| AC4 原型逻辑自检 | 静态 | node preview/_verify.mjs "file:///D:/Work/WinQuick/preview/skin-color-preview.html" | selfCheck 全 PASS | 15s |
| AC5 装机 skin-probe（publish 后） | 集成 | publish\\probes\\WinQuick.Probes.exe --skin-probe | 全 PASS | 20s |
| AC6 交付前全量单测 | 单元 | dotnet test tests/WinQuick.Core.Tests -c Release --nologo | 失败 0 | 120s |
| AC7 观感实机 | 用户 | 逐套切 8 皮肤 + 纯白壁纸场景验半透明 | 用户确认 | 用户侧 |

## 参考

docs/specs/skin-color-redesign.md（§1 R1–R6、§3 技术方案、§4 测试计划）；
AGENTS.md §3.5 观感原型流程；docs/M8-skins.md（皮肤调研与 Neon 出处）。