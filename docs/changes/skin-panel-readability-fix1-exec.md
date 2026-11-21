# 执行书 fix1：面板可读性审查修复（P1 非确定用例 + P2×5）

## Q1 SkinContrastTests.cs：system-light 固定输入（P1）
先 Read 定位 PanelStates 消费的 switch。将
old：
```
        var color = stateId switch
        {
            "system-light" => TokenStore.PreviewTokens(SystemSkin.Id).Color, // 两态合成用同键集，暗态=基线已由其余用例覆盖
            _ => TokenStore.PreviewTokens(stateId).Color,
        };
```
new：
```
        // system 亮态用固定输入合成（P1：PreviewTokens(system) 读宿主注册表——非确定测试，
        // 本机暗色模式下浅色配对从未被覆盖；固定 true + 官方默认强调色即确定性覆盖）。
        var color = stateId switch
        {
            "system-light" => SkinCatalog.MergeWithOverrides(
                TokenStore.BaseJson, SystemSkin.Compose(true, "#0078D4")).Color,
            _ => TokenStore.PreviewTokens(stateId).Color,
        };
```
（SkinCatalog/TokenStore.BaseJson/SystemSkin 的可访问性不足则按 InternalsVisibleTo 既有接缝最小适配；using WinQuick.Core.Design 若缺按许可补。）

## Q2 同文件：补 Text↔FieldBackground 断言 + 删死变量（P2-2/5）
在面板配对 Theory 的 Accent 断言之后追加：
```csharp
        Assert.True(Math.Abs(Lum(panel.FieldBackground) - Lum(color.Label)) >= 0.15,
            $"{stateId}：输入框底与正文亮度差不足（{panel.FieldBackground} vs {color.Label}）");
```
并删除回归对照用例中未使用的局部 `var translucent = TokenStore.Skins.All.First(s => s.Id == "translucent");` 行。

## Q3 TokenResources.cs：黑底有效的阶梯混色（P2-3/4）
old：
```
        var field = light ? Shift(surface, -0.06) : Shift(surface, +0.06);
```
new：
```
        // 输入框一档阶梯：混色而非乘法 —— 乘法在纯黑底（contrast）恒为 0，层次消失（reviewer P2）。
        // 12% 向浅/向深混合：黑白两端都有可见的一档差。
        var field = light ? Mix(surface, "#3A3A3A", 0.12) : Mix(surface, "#F0F0F0", 0.12);
```
并整体删除 Shift 方法及其 XML 注释（已无调用者）；DerivePanelColors 内如引用 Shift 仅此一处（Q3 已替换）。

## Q4 变更单文档回写（P2-1）
docs/changes/skin-panel-readability.md「## 方案」第 1 条整段替换为：
1. 抽纯函数 `DerivePanelColors(ColorTokens) → PanelPalette`（可单测）：SurfaceSolid/Surface=SectorFill 按 α 合成到 #202020；FieldBackground=合成底 12% 混色一档阶梯（黑底白底均有效）；Accent=HoverFill **原色不透明**（α 是轮盘悬停高亮参数，合成会让低 α 皮肤的面板强调色与底趋同——SkinContrast 抓到后修正）；Outline/HoverSurface 基于**合成底**亮度翻转（修正顺序缺陷）；Danger/Success 按底明暗双套（浅底 #C42B1C/#1B7F3A，暗底原值）。配对阈值：Text↔底 ≥0.3、Accent↔底 ≥0.2、输入框底↔Text ≥0.15。

## 验证
1. dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo → 0 error；
2. dotnet test tests/WinQuick.Core.Tests --filter SkinContrast --nologo → 18/18 全绿（含固定输入的 system-light 与新断言）；
3. dotnet test tests/WinQuick.Core.Tests --nologo → 339 全绿。

old 不匹配即停报。写完输出「已写入 + 行数」。
