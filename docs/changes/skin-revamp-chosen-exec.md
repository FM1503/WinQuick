# 执行书：skin-revamp-chosen

## M1 design/skins.json 三皮肤 overrides.color 整段替换
先 Read 定位三个条目。以原型 pal 为真源：
1. light 条目 overrides.color 替换为：
```json
"color": {
  "dimAlpha": 0.28,
  "sectorFill": "#FAF6EF",
  "sectorFillAlpha": 0.97,
  "sectorStroke": "#6B5D4A",
  "sectorStrokeAlpha": 0.22,
  "hoverFill": "#B45309",
  "hoverFillAlpha": 1.0,
  "hoverStroke": "#FFFFFF",
  "hoverStrokeAlpha": 0.9,
  "hoverLabel": "#FFFFFF",
  "deadZoneFill": "#E7E0D2",
  "deadZoneStrokeAlpha": 0.16,
  "label": "#3F3A31",
  "hint": "#3F3A31",
  "hintAlpha": 0.6,
  "badgeFill": "#EFE8DA",
  "badgeFillAlpha": 0.45
}
```
2. translucent 条目 overrides.color 替换为（新增 strokeGradient 键）：
```json
"color": {
  "dimAlpha": 0.2,
  "sectorFill": "#FFFFFF",
  "sectorFillAlpha": 0.07,
  "sectorStroke": "#FFFFFF",
  "sectorStrokeAlpha": 0.5,
  "strokeWidth": 1.4,
  "strokeGradient": true,
  "hoverFill": "#FFFFFF",
  "hoverFillAlpha": 0.16,
  "hoverStroke": "#FFFFFF",
  "hoverStrokeAlpha": 0.85,
  "hoverLabel": "#FFFFFF",
  "deadZoneFill": "#FFFFFF",
  "deadZoneStrokeAlpha": 0.3,
  "label": "#FFFFFF",
  "hintAlpha": 0.7
}
```
3. retro 条目 overrides.color 替换为：
```json
"color": {
  "dimAlpha": 0.5,
  "sectorFill": "#0D140D",
  "sectorFillAlpha": 1.0,
  "sectorStroke": "#33FF66",
  "sectorStrokeAlpha": 0.55,
  "strokeWidth": 1.5,
  "hoverFill": "#33FF66",
  "hoverFillAlpha": 0.22,
  "hoverStroke": "#33FF66",
  "hoverStrokeAlpha": 1.0,
  "hoverLabel": "#33FF66",
  "deadZoneFill": "#050805",
  "deadZoneStrokeAlpha": 0.55,
  "label": "#5CFF8E",
  "hintAlpha": 0.8,
  "badgeFill": "#33FF66",
  "badgeFillAlpha": 0.6
}
```
（retro 的 typography（mono 字体）与 motion（steps 等）保持不动；translucent/light 的 motion/typography 同样不动。）

## M2 src/WinQuick.Core/Design/DesignTokens.cs：ColorTokens 加属性
先 Read 定位 ColorTokens 的 BadgeFill 属性附近，追加：
```csharp
    /// <summary>
    /// 玻璃拟态描边（skin-revamp T1）：整窗垂直渐变描边替代平色边。
    /// SectorStroke/SectorStrokeAlpha 此时作为渐变的两端基色（上端亮、下端暗）。
    /// </summary>
    public bool StrokeGradient { get; init; }
```

## M3 src/WinQuick.App/Radial/RadialRingVisual.cs：渐变描边分支
old（约 349 行，先 Read 核对）：
```
        var baseStroke = TokenBrushes.Brush(colors.SectorStroke, colors.SectorStrokeAlpha);
```
new：
```
        var baseStroke = TokenBrushes.Brush(colors.SectorStroke, colors.SectorStrokeAlpha);
        if (colors.StrokeGradient)
        {
            // 玻璃拟态（skin-revamp T1，用户挑定）：整窗垂直渐变描边——上亮下暗的高光边。
            // MappingMode 必须 Absolute：相对 boundingBox 会让顶部/底部扇区各自截到渐变的不同段，
            // 「一圈边框光照一致」就不成立了。窗口高固定 740（tokens.radius.windowSize）。
            var gradient = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 740),
            };
            gradient.GradientStops.Add(new GradientStop(
                TokenBrushes.Parse(colors.SectorStroke, colors.SectorStrokeAlpha), 0));
            gradient.GradientStops.Add(new GradientStop(
                TokenBrushes.Parse(colors.SectorStroke, colors.SectorStrokeAlpha * 0.24), 1));
            gradient.Freeze();
            baseStroke = gradient;
        }
```
（TokenBrushes.Parse(hex, alpha) 签名先 grep 核实，不同则适配；LinearGradientBrush/GradientStop/Point 的 using 若缺按许可补 System.Windows.Media。）

## M4 SectorGeometry.cs + RingForm.cs：W3 大圆角
1. SectorGeometry.cs 的 RoundedRingSector 内（先 Read 核对 104-112 行）：
old：
```
        var corner = Math.Max(0.5, Math.Min(cornerRadius, sweep * 0.2));
```
new：
```
        var corner = Math.Max(0.5, Math.Min(cornerRadius, sweep * 0.45));
```
old：
```
        var maxDeg = sweep / 4;
```
new：
```
        // W3 大圆角（skin-revamp 用户挑定）：角度封顶从 sweep/4 放宽到 0.45 倍 ——
        // 实测瓶颈在角度封顶而非传入的半径值（14px 早已被 7.5° 封死）。
        var maxDeg = sweep * 0.45;
```
2. RingForm.cs Metrics：
old：
```
        RingForm.Rounded => (deadZone, ring1Outer, 14, baseGap),
```
new：
```
        RingForm.Rounded => (deadZone, ring1Outer, 22, baseGap),
```

## M5 SectorGeometry.cs：P3 花瓣柔化
old：
```
        var bow = half * 1.18;
```
new：
```
        // P3 柔化（skin-revamp 用户挑定）：外凸从 1.18 收到 1.10 —— 叶感保留、臃肿感去掉。
        var bow = half * 1.10;
```

## 验证
1. dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo → 0 error；
2. dotnet test tests/WinQuick.Core.Tests --nologo → 全绿（含 SkinContrast 三套新配色）；
3. JSON 两文件可解析（python json.load）。

old 不匹配即停报。写完输出「已写入 + 各行数」。
