# 执行书：skin-color-redesign-wire（compile-change 编译产物）

> 由 scripts/compile-change.mjs 编译 docs 变更单生成；勿手改，改变更单后重新编译。

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

## 补丁脚本

```python
import sys, os, json, base64, re
DATA = json.loads(base64.b64decode("eyJzbHVnIjoic2tpbi1jb2xvci1yZWRlc2lnbi13aXJlIiwicGF0Y2hlcyI6W3siZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjo3MDUsImFuY2hvciI6ImlmIChfaGFsb3MgaXMgbm90IG51bGwpIF9oYWxvc1tpbmRleF0uRGF0YSA9IGdlb21ldHJ5OyIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgICAgIGlmIChfaGFsb3MgaXMgbm90IG51bGwpIF9oYWxvc1tpbmRleF0uRGF0YSA9IGdlb21ldHJ5O1xuICAgICAgICBpZiAoX3NjcmltcyBpcyBub3QgbnVsbCkgX3Njcmltc1tpbmRleF0uRGF0YSA9IGdlb21ldHJ5OyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjo2ODMsImFuY2hvciI6Il9oYWxvc1tpbmRleF0uRGF0YSA9IF9iYXNlR2VvbWV0cnlbaW5kZXhdOyIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgICAgICAgICBpZiAoX2hhbG9zIGlzIG5vdCBudWxsKSBfaGFsb3NbaW5kZXhdLkRhdGEgPSBfYmFzZUdlb21ldHJ5W2luZGV4XTtcbiAgICAgICAgICAgIGlmIChfc2NyaW1zIGlzIG5vdCBudWxsKSBfc2NyaW1zW2luZGV4XS5EYXRhID0gX2Jhc2VHZW9tZXRyeVtpbmRleF07In0seyJmaWxlIjoic3JjL1dpblF1aWNrLkFwcC9SYWRpYWwvUmFkaWFsUmluZ1Zpc3VhbC5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjY0NSwiYW5jaG9yIjoiX2hhbG9zID0gaGFsb3MsIiwib2xkX2NvdW50IjoxLCJuZXciOiJcbiAgICAgICAgICAgIF9oYWxvcyA9IGhhbG9zLFxuICAgICAgICAgICAgX3NjcmltcyA9IHNjcmltcywifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL1JhZGlhbC9SYWRpYWxSaW5nVmlzdWFsLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6NjI1LCJhbmNob3IiOiJ2YXIgZGVhZFpvbmUgPSBBZGRFbGxpcHNlKHJvb3QsIGNlbnRlciwgcmFkaXVzLkRlYWRab25lIC0gNCwiLCJvbGRfY291bnQiOjMsIm5ldyI6IlxuICAgICAgICAvLyBzY3JpbSDnmq7ogqTvvJrmrbvljLrnlLvljYrpgI/kv53lupXnm5jvvIjkuI7miYfljLrlnqvlupXlsYLlkIzoibLlkIzpgI/mmI7luqbvvInigJTigJRcbiAgICAgICAgLy8g5L+u5aSN55So5oi35oiq5Zu+6YeM44CM5Y2K6YCP5omH5Yy65Lit5aSu5LiA5Liq5LiN6YCP5piO5aSn55m954K544CN77yb6Z2eIHNjcmltIOearuiCpOeUqOa0vueUn+WQjOaXj+WunuiJsuOAglxuICAgICAgICB2YXIgZGVhZEZpbGxCcnVzaCA9IHNjcmltQnJ1c2ggPz8gVG9rZW5CcnVzaGVzLkJydXNoKGNvbG9ycy5EZWFkWm9uZUZpbGwpO1xuICAgICAgICB2YXIgZGVhZFpvbmUgPSBBZGRFbGxpcHNlKHJvb3QsIGNlbnRlciwgcmFkaXVzLkRlYWRab25lIC0gNCxcbiAgICAgICAgICAgIGRlYWRGaWxsQnJ1c2gsXG4gICAgICAgICAgICBUb2tlbkJydXNoZXMuQnJ1c2goY29sb3JzLkRlYWRab25lU3Ryb2tlLCBjb2xvcnMuRGVhZFpvbmVTdHJva2VBbHBoYSksIDEpOyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjo1MzAsImFuY2hvciI6ImlmIChoYWxvcyBpcyBub3QgbnVsbCkiLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgICAgICAgICAgaWYgKHNjcmltcyBpcyBub3QgbnVsbClcbiAgICAgICAgICAgIHtcbiAgICAgICAgICAgICAgICB2YXIgc2NyaW0gPSBuZXcgU2hhcGVQYXRoXG4gICAgICAgICAgICAgICAge1xuICAgICAgICAgICAgICAgICAgICBEYXRhID0gZ2VvbWV0cnksXG4gICAgICAgICAgICAgICAgICAgIEZpbGwgPSBzY3JpbUJydXNoLFxuICAgICAgICAgICAgICAgICAgICBJc0hpdFRlc3RWaXNpYmxlID0gZmFsc2UsXG4gICAgICAgICAgICAgICAgfTtcbiAgICAgICAgICAgICAgICBzY3JpbXNbaV0gPSBzY3JpbTtcbiAgICAgICAgICAgICAgICByb290LkNoaWxkcmVuLkFkZChzY3JpbSk7ICAgLy8g5Z6r5bqV5bGC5YWI5YWl5qCR77yM5YWJ5pmV5LiO5omH5Yy655uW5Zyo5LiK6Z2iXG4gICAgICAgICAgICB9XG5cbiAgICAgICAgICAgIGlmIChoYWxvcyBpcyBub3QgbnVsbCkifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL1JhZGlhbC9SYWRpYWxSaW5nVmlzdWFsLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6NTExLCJhbmNob3IiOiJ2YXIgc3Ryb2tlID0gQ3JlYXRlU2VjdG9yU3Ryb2tlKGNvbG9ycywgcmFkaXVzLldpbmRvd1NpemUpOyIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgICAgIHZhciBzdHJva2UgPSBDcmVhdGVTZWN0b3JTdHJva2UoY29sb3JzLCByYWRpdXMuV2luZG93U2l6ZSk7XG4gICAgICAgIC8vIHNjcmltIOS/neW6leWxgu+8iHNwZWMgUjLvvIxBY3J5bGljIOmFjeaWue+8ie+8mueOu+eSg+earuiCpOWcqOavj+S4quaJh+WMuiBmaWxsIOS5i+S4i+Weq+S4gOWxguWNiumAj+m7kSDigJTigJRcbiAgICAgICAgLy8g5a6D5bCx5piv44CM5Lqu5aOB57q45LiK55qE5paH5a2X5L+d5bqV44CN6YKj5LiA5bGC44CC57qv5Yeg5L2V5aSa5LiA5bGCIFBhdGjvvIzml6AgRWZmZWN044CB5peg5L2N5Zu+77yITTAg57qm5p2f77yJ77ybXG4gICAgICAgIC8vIOaVsOe7hOmdniBudWxsIOKfuiBzY3JpbUFscGhhPjAg4oCU4oCUIOS4jiBoYWxvIOWQjOasvuWtmOWcqOaAp+WuiOWNq++8iOacieaXoOWPmOWMluaXtiBUcnlBcHBseVNraW5JblBsYWNlIOW8uuWItumHjeW7uu+8ieOAglxuICAgICAgICB2YXIgc2NyaW1CcnVzaCA9IGNvbG9ycy5TZWN0b3JTY3JpbUFscGhhID4gMC4wMDFcbiAgICAgICAgICAgID8gVG9rZW5CcnVzaGVzLkJydXNoKGNvbG9ycy5TZWN0b3JTY3JpbSwgY29sb3JzLlNlY3RvclNjcmltQWxwaGEpXG4gICAgICAgICAgICA6IG51bGw7XG4gICAgICAgIHZhciBzY3JpbXMgPSBzY3JpbUJydXNoIGlzIG5vdCBudWxsID8gbmV3IFNoYXBlUGF0aFtjb3VudF0gOiBudWxsOyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjozNjgsImFuY2hvciI6ImlmIChfZGVhZFpvbmUgaXMgbm90IG51bGwpIiwib2xkX2NvdW50Ijo1LCJuZXciOiJcbiAgICAgICAgaWYgKF9kZWFkWm9uZSBpcyBub3QgbnVsbClcbiAgICAgICAge1xuICAgICAgICAgICAgLy8g5q275Yy65aGr5YWF5LiOIEJ1aWxkIOWQjOS4gCBzY3JpbSDliIbmlK/vvJrmnInkv53lupXnm5jnlKjkv53lupXnm5jvvIzlkKbliJnnlKjmtL7nlJ/lrp7oibLjgIJcbiAgICAgICAgICAgIF9kZWFkWm9uZS5GaWxsID0gc2NyaW1VcGRhdGUgPz8gVG9rZW5CcnVzaGVzLkJydXNoKGNvbG9ycy5EZWFkWm9uZUZpbGwpO1xuICAgICAgICAgICAgX2RlYWRab25lLlN0cm9rZSA9IFRva2VuQnJ1c2hlcy5CcnVzaChjb2xvcnMuRGVhZFpvbmVTdHJva2UsIGNvbG9ycy5EZWFkWm9uZVN0cm9rZUFscGhhKTtcbiAgICAgICAgfSJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjozNjQsImFuY2hvciI6Il9sYWJlbHNbaV0uU2V0QmFzZUZvcmVncm91bmQoVG9rZW5CcnVzaGVzLkJydXNoKGNvbG9ycy5MYWJlbCkpOyIsIm9sZF9jb3VudCI6MywibmV3IjoiXG4gICAgICAgICAgICBfbGFiZWxzW2ldLlNldEJhc2VGb3JlZ3JvdW5kKFRva2VuQnJ1c2hlcy5CcnVzaChjb2xvcnMuTGFiZWwpKTtcbiAgICAgICAgICAgIF9jaGlsZE1hcmtlcnNbaV0/LkxldChtID0+IG0uU3Ryb2tlID0gYmFzZVN0cm9rZSk7XG4gICAgICAgICAgICBpZiAoX3NjcmltcyBpcyBub3QgbnVsbCkgX3Njcmltc1tpXS5GaWxsID0gc2NyaW1VcGRhdGU7XG4gICAgICAgIH0ifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL1JhZGlhbC9SYWRpYWxSaW5nVmlzdWFsLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MzQ4LCJhbmNob3IiOiJ2YXIgYmFzZUZpbGwgPSBUb2tlbkJydXNoZXMuQnJ1c2goY29sb3JzLlNlY3RvckZpbGwsIGNvbG9ycy5TZWN0b3JGaWxsQWxwaGEpOyIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgICAgIC8vIHNjcmltIOaNouiCpOWQjOatpe+8muWtmOWcqOaAp+W3sueUseS4iumdoueahOWuiOWNq+aLpuS4i++8iOacieKGlOaXoOW/hemHjeW7uu+8ie+8jOi/memHjOWPqueuoeaUueiJsuOAglxuICAgICAgICB2YXIgc2NyaW1VcGRhdGUgPSBjb2xvcnMuU2VjdG9yU2NyaW1BbHBoYSA+IDAuMDAxXG4gICAgICAgICAgICA/IFRva2VuQnJ1c2hlcy5CcnVzaChjb2xvcnMuU2VjdG9yU2NyaW0sIGNvbG9ycy5TZWN0b3JTY3JpbUFscGhhKVxuICAgICAgICAgICAgOiBudWxsO1xuICAgICAgICB2YXIgYmFzZUZpbGwgPSBUb2tlbkJydXNoZXMuQnJ1c2goY29sb3JzLlNlY3RvckZpbGwsIGNvbG9ycy5TZWN0b3JGaWxsQWxwaGEpOyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjozMzgsImFuY2hvciI6ImlmICgoY29sb3JzLkhhbG9QeCA+IDApICE9IChfaGFsb3MgaXMgbm90IG51bGwpKSByZXR1cm4gZmFsc2U7Iiwib2xkX2NvdW50IjoxLCJuZXciOiJcbiAgICAgICAgaWYgKChjb2xvcnMuSGFsb1B4ID4gMCkgIT0gKF9oYWxvcyBpcyBub3QgbnVsbCkpIHJldHVybiBmYWxzZTtcbiAgICAgICAgaWYgKChjb2xvcnMuU2VjdG9yU2NyaW1BbHBoYSA+IDAuMDAxKSAhPSAoX3NjcmltcyBpcyBub3QgbnVsbCkpIHJldHVybiBmYWxzZTsifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL1JhZGlhbC9SYWRpYWxSaW5nVmlzdWFsLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6NjEsImFuY2hvciI6InByaXZhdGUgU2hhcGVQYXRoW10/IF9oYWxvczsiLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgIHByaXZhdGUgU2hhcGVQYXRoW10/IF9oYWxvcztcbiAgICAvLyBzY3JpbSDlnqvlupXlsYLvvIhzcGVjIFIy77yJ77ya5LiOIF9oYWxvcyDlkIzmrL7jgIzmnInliJnmr4/miYfljLrkuIDlsYLjgIHml6DliJkgbnVsbOOAje+8m+WHoOS9leWGmeWFpeeCueS4juWFtuWQjOatpeOAglxuICAgIHByaXZhdGUgU2hhcGVQYXRoW10/IF9zY3JpbXM7In0seyJmaWxlIjoic3JjL1dpblF1aWNrLkFwcC9SYWRpYWwvUmFkaWFsUmluZ1Zpc3VhbC5DaGlsZEFyYy5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjUxOCwiYW5jaG9yIjoiX2NoaWxkU2VjdG9ycy5DbGVhcigpOyIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgICAgIF9jaGlsZFNlY3RvcnMuQ2xlYXIoKTtcbiAgICAgICAgX2NoaWxkU2NyaW1zLkNsZWFyKCk7In0seyJmaWxlIjoic3JjL1dpblF1aWNrLkFwcC9SYWRpYWwvUmFkaWFsUmluZ1Zpc3VhbC5DaGlsZEFyYy5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjUwNSwiYW5jaG9yIjoiZm9yZWFjaCAodmFyIHZpc3VhbCBpbiBfY2hpbGRTZWN0b3JzLkNhc3Q8VUlFbGVtZW50PigpIiwib2xkX2NvdW50Ijo0LCJuZXciOiJcbiAgICAgICAgZm9yZWFjaCAodmFyIHZpc3VhbCBpbiBfY2hpbGRTZWN0b3JzLkNhc3Q8VUlFbGVtZW50PigpXG4gICAgICAgICAgICAgICAgICAgICAuQ29uY2F0KF9jaGlsZFNjcmltcylcbiAgICAgICAgICAgICAgICAgICAgIC5Db25jYXQoX2NoaWxkSWNvbnMpXG4gICAgICAgICAgICAgICAgICAgICAuQ29uY2F0KF9jaGlsZENoaWxkTWFya2Vycy5XaGVyZShtID0+IG0gaXMgbm90IG51bGwpLkNhc3Q8VUlFbGVtZW50PigpKVxuICAgICAgICAgICAgICAgICAgICAgLkNvbmNhdChfY2hpbGRFeHRyYVZpc3VhbHMpKSJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuQ2hpbGRBcmMuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjo0MjksImFuY2hvciI6Il9jaGlsZFNlY3RvcnNbaW5kZXhdLkRhdGEgPSBTZWN0b3JHZW9tZXRyeS5SaW5nU2VjdG9yKCIsIm9sZF9jb3VudCI6NCwibmV3IjoiXG4gICAgICAgIF9jaGlsZFNlY3RvcnNbaW5kZXhdLkRhdGEgPSBTZWN0b3JHZW9tZXRyeS5SaW5nU2VjdG9yKFxuICAgICAgICAgICAgX2NlbnRlciwgYmFuZC5Jbm5lciwgb3V0ZXIsXG4gICAgICAgICAgICBzcGFuLlN0YXJ0RGVnICsgcmFkaXVzLlNlY3RvckdhcERlZ3JlZXMsXG4gICAgICAgICAgICBzcGFuLkVuZERlZyAtIHJhZGl1cy5TZWN0b3JHYXBEZWdyZWVzKTtcbiAgICAgICAgaWYgKGluZGV4IDwgX2NoaWxkU2NyaW1zLkNvdW50KSBfY2hpbGRTY3JpbXNbaW5kZXhdLkRhdGEgPSBfY2hpbGRTZWN0b3JzW2luZGV4XS5EYXRhOyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuQ2hpbGRBcmMuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjoxNDYsImFuY2hvciI6Il9jaGlsZFNlY3RvcnMuQWRkKHNlY3Rvcik7Iiwib2xkX2NvdW50IjoyLCJuZXciOiJcbiAgICAgICAgICAgIGlmIChzY3JpbUJydXNoIGlzIG5vdCBudWxsKVxuICAgICAgICAgICAge1xuICAgICAgICAgICAgICAgIHZhciBzY3JpbSA9IG5ldyBTaGFwZVBhdGhcbiAgICAgICAgICAgICAgICB7XG4gICAgICAgICAgICAgICAgICAgIERhdGEgPSBzZWN0b3IuRGF0YSxcbiAgICAgICAgICAgICAgICAgICAgRmlsbCA9IHNjcmltQnJ1c2gsXG4gICAgICAgICAgICAgICAgICAgIElzSGl0VGVzdFZpc2libGUgPSBmYWxzZSxcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIFJvb3QuQ2hpbGRyZW4uQWRkKHNjcmltKTsgICAvLyDlnqvlupXlsYLlhYjlhaXmoJHvvIzmiYfljLrnm5blnKjkuIrpnaJcbiAgICAgICAgICAgICAgICBfY2hpbGRTY3JpbXMuQWRkKHNjcmltKTtcbiAgICAgICAgICAgIH1cbiAgICAgICAgICAgIF9jaGlsZFNlY3RvcnMuQWRkKHNlY3Rvcik7XG4gICAgICAgICAgICBSb290LkNoaWxkcmVuLkFkZChzZWN0b3IpOyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuQ2hpbGRBcmMuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjoxMTUsImFuY2hvciI6InZhciBzdHJva2UgPSBDcmVhdGVTZWN0b3JTdHJva2UoY29sb3JzLCB0b2tlbnMuUmFkaXVzLldpbmRvd1NpemUpOyIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgICAgIHZhciBzdHJva2UgPSBDcmVhdGVTZWN0b3JTdHJva2UoY29sb3JzLCB0b2tlbnMuUmFkaXVzLldpbmRvd1NpemUpO1xuICAgICAgICAvLyDlrZDlvKflkIzmrL4gc2NyaW0g5Z6r5bqV77yIc3BlYyBSMu+8ie+8muS4juS4gOe6p+eOr+WQjOinhOWImSDigJTigJQg5a2Y5Zyo5oCn55Sx5LiA57qn546v5a6I5Y2r5L+d6K+B5ZCM5om55Ye6546w44CCXG4gICAgICAgIHZhciBzY3JpbUJydXNoID0gY29sb3JzLlNlY3RvclNjcmltQWxwaGEgPiAwLjAwMVxuICAgICAgICAgICAgPyBUb2tlbkJydXNoZXMuQnJ1c2goY29sb3JzLlNlY3RvclNjcmltLCBjb2xvcnMuU2VjdG9yU2NyaW1BbHBoYSkgOiBudWxsOyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuQ2hpbGRBcmMuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjozOCwiYW5jaG9yIjoicHJpdmF0ZSByZWFkb25seSBMaXN0PFVJRWxlbWVudD4gX2NoaWxkRXh0cmFWaXN1YWxzID0gW107Iiwib2xkX2NvdW50IjoxLCJuZXciOiJcbiAgICBwcml2YXRlIHJlYWRvbmx5IExpc3Q8VUlFbGVtZW50PiBfY2hpbGRFeHRyYVZpc3VhbHMgPSBbXTtcbiAgICAvLyDlrZDmiYfljLrnmoQgc2NyaW0g5Z6r5bqV77yI5LiOIF9jaGlsZFNlY3RvcnMg5ZCM55Sf5ZCM54Gt77yb57Si5byV5LiA5LiA5a+55bqUIOKAlOKAlCDopoHkuYjlhajlu7ropoHkuYjkuI3lu7rvvInjgIJcbiAgICBwcml2YXRlIHJlYWRvbmx5IExpc3Q8U2hhcGVQYXRoPiBfY2hpbGRTY3JpbXMgPSBbXTsifV0sImV4cGVjdCI6W3siZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuY3MiLCJjb250YWlucyI6Il9zY3JpbXNbaW5kZXhdLkRhdGEgPSBnZW9tZXRyeTsifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL1JhZGlhbC9SYWRpYWxSaW5nVmlzdWFsLmNzIiwiY29udGFpbnMiOiJ2YXIgZGVhZEZpbGxCcnVzaCA9IHNjcmltQnJ1c2ggPz8gVG9rZW5CcnVzaGVzLkJydXNoKGNvbG9ycy5EZWFkWm9uZUZpbGwpOyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1JhZGlhbFJpbmdWaXN1YWwuQ2hpbGRBcmMuY3MiLCJjb250YWlucyI6Il9jaGlsZFNjcmltcy5BZGQoc2NyaW0pOyJ9XX0=").decode("utf-8"))
SLUG = DATA["slug"]; PATCHES = DATA["patches"]; EXPECT = DATA["expect"]
RB = os.path.join("docs", "changes", SLUG + ".rollback.json")

def fail(file, msg, lines, hit):
    ctx = "\n".join("%4d %s" % (i + 1, l) for i, l in enumerate(lines) if abs(i - hit) <= 3)
    sys.stderr.buffer.write(("[compile-change] FAIL %s: %s\n%s\n" % (file, msg, ctx)).encode("utf-8"))
    sys.exit(1)

# 执行前快照：编辑文件存原始字节，新文件存"已存在"或"created"标记
snap = {}; created = []
for p in PATCHES:
    f = p["file"]
    if p["newfile"]:
        if os.path.exists(f): snap[f] = base64.b64encode(open(f, "rb").read()).decode()
        else: created.append(f)
        continue
    if not os.path.exists(f): fail(f, "文件不存在", [], 0)
    if f not in snap: snap[f] = base64.b64encode(open(f, "rb").read()).decode()
os.makedirs(os.path.dirname(RB), exist_ok=True)
json.dump({"snapshot": snap, "created": created}, open(RB, "w"))

# 逐条应用
for p in PATCHES:
    f = p["file"]
    if p["newfile"]:
        d = os.path.dirname(f)
        if d: os.makedirs(d, exist_ok=True)
        open(f, "wb").write(p["new"].encode("utf-8"))
        continue
    raw = open(f, "rb").read(); text = raw.decode("utf-8")
    # 终修 P2-2：弃用 splitlines(True)（其把 \f \v \x1c-\x1e \x85 \u2028 \u2029 也当行界，
    # 致行号漂移）；改用 re.split 捕获 (\r\n|\r|\n) 重组，仅按这三种分隔符分行且保留原行尾。
    _segs = re.split("(\\r\\n|\\r|\\n)", text)
    ke = []
    for _j in range(0, len(_segs) - 1, 2): ke.append(_segs[_j] + _segs[_j + 1])
    if len(_segs) % 2 == 1 and _segs[-1] != "": ke.append(_segs[-1])  # 末行无行尾（P2-3）
    def _strip(l):
        if l.endswith("\r\n"): return l[:-2]
        if l.endswith("\n") or l.endswith("\r"): return l[:-1]
        return l
    lines = [_strip(l) for l in ke]
    idx = p["lineno"] - 1; oc = p["old_count"]; anchor = p["anchor"]
    if idx < 0 or idx >= len(ke): fail(f, "行号越界 %d" % p["lineno"], lines, max(idx, 0))
    if anchor and anchor not in lines[idx]: fail(f, "锚点不匹配: 期望该行含 %r" % anchor, lines, idx)
    end = idx + oc
    if oc > 0 and end > len(ke): fail(f, "旧行数越界: 行 %d 起 %d 行超出文件" % (p["lineno"], oc), lines, idx)
    _f = ke[idx]
    eol = "\r\n" if _f.endswith("\r\n") else ("\n" if _f.endswith("\n") else ("\r" if _f.endswith("\r") else "\n"))
    if p["new"] == "":
        repl = []  # P1-2：空新内容=删除区间（del lines[idx:end]）
    else:
        repl = [c + eol for c in p["new"].replace("\r\n", "\n").replace("\r", "\n").split("\n")]
        # 终修 P2-3：替换区间含文件末行且原末行无 EOL → 新内容末行同样不补尾 EOL（保持无尾换行）
        if repl and end == len(ke) and not (ke[-1].endswith("\n") or ke[-1].endswith("\r")):
            repl[-1] = _strip(repl[-1])
    ke[idx:end] = repl  # 未触碰行保持原始字节行尾
    open(f, "wb").write("".join(ke).encode("utf-8"))

# expect_after 后置断言
for e in EXPECT:
    f = e["file"]
    if not os.path.exists(f): fail(f, "expect_after 文件不存在", [], 0)
    d = open(f, "rb").read().decode("utf-8")
    if e["contains"] not in d:
        fail(f, "expect_after 缺少子串 %r" % e["contains"], d.replace("\r\n", "\n").split("\n"), 0)

print("[compile-change] OK: %d patches applied" % len(PATCHES))
```

## 执行命令

```bash
node scripts/compile-change.mjs docs/changes/skin-color-redesign-wire.md | python - && ```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-wire.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~SkinContrast" --nologo
```
```

## 回滚说明

执行正向补丁时已快照 docs/changes/skin-color-redesign-wire.rollback.json。回滚（还原编辑、删除新建文件）：

```bash
node scripts/compile-change.mjs docs/changes/skin-color-redesign-wire.md --rollback | python -
```

回滚脚本：

```python
import sys, os, json, base64
SLUG = "skin-color-redesign-wire"
RB = os.path.join("docs", "changes", SLUG + ".rollback.json")
if not os.path.exists(RB):
    sys.stderr.buffer.write(("[compile-change] 回滚失败：缺少 %s\n" % RB).encode("utf-8")); sys.exit(1)
data = json.load(open(RB))
for f, b64 in data.get("snapshot", {}).items():
    d = os.path.dirname(f)
    if d: os.makedirs(d, exist_ok=True)
    open(f, "wb").write(base64.b64decode(b64))
for f in data.get("created", []):
    if os.path.exists(f): os.remove(f)
print("[compile-change] rollback ok: %d restored, %d removed" % (len(data.get("snapshot", {})), len(data.get("created", []))))
```
