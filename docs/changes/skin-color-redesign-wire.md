---
slug: skin-color-redesign-wire
status: approved
date: 2026-11-23
tier: L
changed-files: src/WinQuick.App/Radial/RadialRingVisual.cs, src/WinQuick.App/Radial/RadialRingVisual.ChildArc.cs
---

# 皮肤配色重设计 · wire 批：scrim 垫底与死区保底盘渲染

- 改什么：环1 每扇区 fill 之下加 scrim 垫底层 Path（与 halo 同型存在性守卫与几何同步）；子弧扇区同款垫底；scrim 皮肤死区改画半透保底盘；就地换肤同步垫底与死区。
- 为什么：spec R2（Acrylic 保底层，用户截图半透明白字在亮壁纸不稳 + 纯白死区盘）；用户原型挑定 D 档 α0.65。
- 影响面：仅 scrimAlpha>0 的皮肤（当前只有半透明）多一层几何；其余皮肤 scrimBrush=null 零变化。
- 回滚方式：docs/changes/skin-color-redesign-wire.rollback.json。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.App/Radial/RadialRingVisual.cs | 705 | if (_halos is not null) _halos[index].Data = geometry; | 1 | <NL>        if (_halos is not null) _halos[index].Data = geometry;<NL>        if (_scrims is not null) _scrims[index].Data = geometry; |
| 2 | src/WinQuick.App/Radial/RadialRingVisual.cs | 683 | _halos[index].Data = _baseGeometry[index]; | 1 | <NL>            if (_halos is not null) _halos[index].Data = _baseGeometry[index];<NL>            if (_scrims is not null) _scrims[index].Data = _baseGeometry[index]; |
| 3 | src/WinQuick.App/Radial/RadialRingVisual.cs | 645 | _halos = halos, | 1 | <NL>            _halos = halos,<NL>            _scrims = scrims, |
| 4 | src/WinQuick.App/Radial/RadialRingVisual.cs | 625 | var deadZone = AddEllipse(root, center, radius.DeadZone - 4, | 3 | <NL>        // scrim 皮肤：死区画半透保底盘（与扇区垫底层同色同透明度）——<NL>        // 修复用户截图里「半透扇区中央一个不透明大白点」；非 scrim 皮肤用派生同族实色。<NL>        var deadFillBrush = scrimBrush ?? TokenBrushes.Brush(colors.DeadZoneFill);<NL>        var deadZone = AddEllipse(root, center, radius.DeadZone - 4,<NL>            deadFillBrush,<NL>            TokenBrushes.Brush(colors.DeadZoneStroke, colors.DeadZoneStrokeAlpha), 1); |
| 5 | src/WinQuick.App/Radial/RadialRingVisual.cs | 530 | if (halos is not null) | 1 | <NL>            if (scrims is not null)<NL>            {<NL>                var scrim = new ShapePath<NL>                {<NL>                    Data = geometry,<NL>                    Fill = scrimBrush,<NL>                    IsHitTestVisible = false,<NL>                };<NL>                scrims[i] = scrim;<NL>                root.Children.Add(scrim);   // 垫底层先入树，光晕与扇区盖在上面<NL>            }<NL><NL>            if (halos is not null) |
| 6 | src/WinQuick.App/Radial/RadialRingVisual.cs | 511 | var stroke = CreateSectorStroke(colors, radius.WindowSize); | 1 | <NL>        var stroke = CreateSectorStroke(colors, radius.WindowSize);<NL>        // scrim 保底层（spec R2，Acrylic 配方）：玻璃皮肤在每个扇区 fill 之下垫一层半透黑 ——<NL>        // 它就是「亮壁纸上的文字保底」那一层。纯几何多一层 Path，无 Effect、无位图（M0 约束）；<NL>        // 数组非 null ⟺ scrimAlpha>0 —— 与 halo 同款存在性守卫（有无变化时 TryApplySkinInPlace 强制重建）。<NL>        var scrimBrush = colors.SectorScrimAlpha > 0.001<NL>            ? TokenBrushes.Brush(colors.SectorScrim, colors.SectorScrimAlpha)<NL>            : null;<NL>        var scrims = scrimBrush is not null ? new ShapePath[count] : null; |
| 7 | src/WinQuick.App/Radial/RadialRingVisual.cs | 368 | if (_deadZone is not null) | 5 | <NL>        if (_deadZone is not null)<NL>        {<NL>            // 死区填充与 Build 同一 scrim 分支：有保底盘用保底盘，否则用派生实色。<NL>            _deadZone.Fill = scrimUpdate ?? TokenBrushes.Brush(colors.DeadZoneFill);<NL>            _deadZone.Stroke = TokenBrushes.Brush(colors.DeadZoneStroke, colors.DeadZoneStrokeAlpha);<NL>        } |
| 8 | src/WinQuick.App/Radial/RadialRingVisual.cs | 364 | _labels[i].SetBaseForeground(TokenBrushes.Brush(colors.Label)); | 3 | <NL>            _labels[i].SetBaseForeground(TokenBrushes.Brush(colors.Label));<NL>            _childMarkers[i]?.Let(m => m.Stroke = baseStroke);<NL>            if (_scrims is not null) _scrims[i].Fill = scrimUpdate;<NL>        } |
| 9 | src/WinQuick.App/Radial/RadialRingVisual.cs | 348 | var baseFill = TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha); | 1 | <NL>        // scrim 换肤同步：存在性已由上面的守卫拦下（有↔无必重建），这里只管改色。<NL>        var scrimUpdate = colors.SectorScrimAlpha > 0.001<NL>            ? TokenBrushes.Brush(colors.SectorScrim, colors.SectorScrimAlpha)<NL>            : null;<NL>        var baseFill = TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha); |
| 10 | src/WinQuick.App/Radial/RadialRingVisual.cs | 338 | if ((colors.HaloPx > 0) != (_halos is not null)) return false; | 1 | <NL>        if ((colors.HaloPx > 0) != (_halos is not null)) return false;<NL>        if ((colors.SectorScrimAlpha > 0.001) != (_scrims is not null)) return false; |
| 11 | src/WinQuick.App/Radial/RadialRingVisual.cs | 61 | private ShapePath[]? _halos; | 1 | <NL>    private ShapePath[]? _halos;<NL>    // scrim 垫底层（spec R2）：与 _halos 同款「有则每扇区一层、无则 null」；几何写入点与其同步。<NL>    private ShapePath[]? _scrims; |
| 12 | src/WinQuick.App/Radial/RadialRingVisual.ChildArc.cs | 518 | _childSectors.Clear(); | 1 | <NL>        _childSectors.Clear();<NL>        _childScrims.Clear(); |
| 13 | src/WinQuick.App/Radial/RadialRingVisual.ChildArc.cs | 505 | foreach (var visual in _childSectors.Cast<UIElement>() | 4 | <NL>        foreach (var visual in _childSectors.Cast<UIElement>()<NL>                     .Concat(_childScrims)<NL>                     .Concat(_childIcons)<NL>                     .Concat(_childChildMarkers.Where(m => m is not null).Cast<UIElement>())<NL>                     .Concat(_childExtraVisuals)) |
| 14 | src/WinQuick.App/Radial/RadialRingVisual.ChildArc.cs | 429 | _childSectors[index].Data = SectorGeometry.RingSector( | 4 | <NL>        _childSectors[index].Data = SectorGeometry.RingSector(<NL>            _center, band.Inner, outer,<NL>            span.StartDeg + radius.SectorGapDegrees,<NL>            span.EndDeg - radius.SectorGapDegrees);<NL>        if (index < _childScrims.Count) _childScrims[index].Data = _childSectors[index].Data; |
| 15 | src/WinQuick.App/Radial/RadialRingVisual.ChildArc.cs | 146 | _childSectors.Add(sector); | 2 | <NL>            if (scrimBrush is not null)<NL>            {<NL>                var scrim = new ShapePath<NL>                {<NL>                    Data = sector.Data,<NL>                    Fill = scrimBrush,<NL>                    IsHitTestVisible = false,<NL>                };<NL>                Root.Children.Add(scrim);   // 垫底层先入树，扇区盖在上面<NL>                _childScrims.Add(scrim);<NL>            }<NL>            _childSectors.Add(sector);<NL>            Root.Children.Add(sector); |
| 16 | src/WinQuick.App/Radial/RadialRingVisual.ChildArc.cs | 115 | var stroke = CreateSectorStroke(colors, tokens.Radius.WindowSize); | 1 | <NL>        var stroke = CreateSectorStroke(colors, tokens.Radius.WindowSize);<NL>        // 子弧同款 scrim 垫底（spec R2）：与一级环同规则 —— 存在性由一级环守卫保证同批出现。<NL>        var scrimBrush = colors.SectorScrimAlpha > 0.001<NL>            ? TokenBrushes.Brush(colors.SectorScrim, colors.SectorScrimAlpha) : null; |
| 17 | src/WinQuick.App/Radial/RadialRingVisual.ChildArc.cs | 38 | private readonly List<UIElement> _childExtraVisuals = []; | 1 | <NL>    private readonly List<UIElement> _childExtraVisuals = [];<NL>    // 子扇区的 scrim 垫底（与 _childSectors 同生同灭；索引一一对应 —— 要么全建要么不建）。<NL>    private readonly List<ShapePath> _childScrims = []; |

## expect_after

| 文件 | 包含子串 |
| --- | --- |
| src/WinQuick.App/Radial/RadialRingVisual.cs | _scrims[index].Data = geometry; |
| src/WinQuick.App/Radial/RadialRingVisual.cs | var deadFillBrush = scrimBrush ?? TokenBrushes.Brush(colors.DeadZoneFill); |
| src/WinQuick.App/Radial/RadialRingVisual.ChildArc.cs | _childScrims.Add(scrim); |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-wire.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~SkinContrast" --nologo
```