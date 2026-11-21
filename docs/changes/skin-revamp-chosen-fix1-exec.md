# 执行书 fix1：渐变描边统一接线（P1）+ EndPoint 去硬编码（P2）

## N1 RadialRingVisual.cs：抽共享 helper（吞并 M3 的渐变块）
先 Read 定位 M3 加入的渐变分支（约 349-368 行 TryApplySkinInPlace 内）与 `_tokens` 字段名。将 TryApplySkinInPlace 内的
```
        Brush baseStroke = TokenBrushes.Brush(colors.SectorStroke, colors.SectorStrokeAlpha);
        if (colors.StrokeGradient)
        { …整块渐变… }
```
（含执行代理加的显式 Brush 声明行与「为什么」注释，以现场为准）整体移出为类内私有方法（放在 TryApplySkinInPlace 附近）：
```csharp
    /// <summary>
    /// 扇区描边的唯一来源（skin-revamp fix1）：平色或玻璃拟态渐变（strokeGradient）。
    /// 全部赋值点（构造/就地换肤/悬停恢复/子环）都必须走这里 ——
    /// 曾只接一条路径导致重启、重建、悬停过后、子环的渐变静默丢失（reviewer P1）。
    /// </summary>
    private Brush CreateSectorStroke(Core.Design.ColorTokens colors)
    {
        var stroke = TokenBrushes.Brush(colors.SectorStroke, colors.SectorStrokeAlpha);
        if (!colors.StrokeGradient) return stroke;

        // 整窗垂直渐变：上亮下暗。MappingMode 必须 Absolute——相对 boundingBox 会让
        // 顶部/底部扇区各自截到渐变的不同段。窗口高读令牌（预览 sizeOverride 场景才不会错位）。
        var height = _tokens.Radius.WindowSize;
        var gradient = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, height),
        };
        gradient.GradientStops.Add(new GradientStop(
            TokenBrushes.Parse(colors.SectorStroke, colors.SectorStrokeAlpha), 0));
        gradient.GradientStops.Add(new GradientStop(
            TokenBrushes.Parse(colors.SectorStroke, colors.SectorStrokeAlpha * 0.24), 1));
        gradient.Freeze();
        return gradient;
    }
```
（_tokens 字段名/类型以现场为准适配；ColorTokens 全名以现场 using 为准。）TryApplySkinInPlace 原位置改为 `var baseStroke = CreateSectorStroke(colors);`。

## N2 统一其余赋值点（先 Read 每处核对再替换）
1. Build 构造路径（约 503/540 行）：找到创建扇区时赋 Stroke 的平色表达式（形如 `TokenBrushes.Brush(colors.SectorStroke, colors.SectorStrokeAlpha)`），替换为 `CreateSectorStroke(colors)`；
2. 悬停恢复（约 283-285 行 hovered 三目）：非悬停分支的平色表达式替换为 `CreateSectorStroke(colors)`（colors 来源以现场为准——若无 colors 变量则从 _tokens.Current.Color 取，与现场既有取色写法一致）；
3. RadialRingVisual.ChildArc.cs 的子环扇区/标记描边（约 115/143/387-389 行，grep `SectorStroke` 定位全部）：平色表达式替换为 `CreateSectorStroke(_tokens.Current.Color)`（同类 partial，直接可用；取色表达式以现场写法为准）。

## 验证
1. dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo → 0 error；
2. dotnet test tests/WinQuick.Core.Tests --nologo → 329 全绿；
3. grep -n "SectorStroke" src/WinQuick.App/Radial/RadialRingVisual*.cs | grep -v CreateSectorStroke | grep TokenBrushes → 预期仅剩 helper 内 1 处（CreateSectorStroke 内部的两处 Parse 用的是 colors.SectorStroke 字段引用不计；以「类外无平色直建」为准核对输出）。

old 不匹配即停报（N2 各处允许按现场表达式最小适配）。写完输出「已写入 + 行数」。
