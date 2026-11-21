# 执行书：skin-panel-readability

## P1 TokenResources.cs：抽纯函数 DerivePanelColors
先 Read 定位 Install 的颜色段与 R1 加的派生键块。在 TokenResources 类内（Install 之前）加：

```csharp
    /// <summary>面板派生键的结果（纯数据，供 Install 与单测共用）。</summary>
    public sealed record PanelPalette(
        string SurfaceSolid, string Surface, string FieldBackground,
        string Outline, string HoverSurface, string Accent, string Danger, string Success);

    /// <summary>
    /// 面板配色的全部派生逻辑（skin-panel-readability，2026-11-21 审计后重建）。
    ///
    /// 语义：轮盘扇区是「原料叠桌面」（半透明原料在桌面上是暗底白字），
    /// 面板是「自绘实色表面」—— 直接用原料会把 α0.07 的白实色化成纯白底配白字。
    /// 所以面板键一律**先按 α 合成到暗基线**再派生；Outline/HoverSurface 基于合成后的底
    /// 判明暗（修正此前按原料判定的顺序缺陷）。
    /// 纯函数无 WPF 依赖 —— 可读性配对由此可被单测直接锁住。
    /// </summary>
    public static PanelPalette DerivePanelColors(Core.Design.ColorTokens c)
    {
        const string DarkBase = "#202020";
        var surface = Composite(c.SectorFill, c.SectorFillAlpha, DarkBase);
        var light = Luminance(surface) > 0.5;
        var field = light ? Shift(surface, -0.06) : Shift(surface, +0.06);
        return new PanelPalette(
            SurfaceSolid: surface,
            Surface: surface,
            FieldBackground: field,
            Outline: light ? "#000000" : "#FFFFFF",
            HoverSurface: light ? "#000000" : "#FFFFFF",
            Accent: Composite(c.HoverFill, c.HoverFillAlpha, DarkBase),
            Danger: light ? "#C42B1C" : "#F08A8A",
            Success: light ? "#1B7F3A" : "#8CC88C");
    }

    /// <summary>按 α 把颜色合成到基色上（面板实色表面的语义核心）。</summary>
    private static string Composite(string hex, double alpha, string overHex)
    {
        var a = Math.Clamp(alpha, 0, 1);
        return Mix(hex, overHex, a);
    }

    private static string Mix(string aHex, string bHex, double t)
    {
        var (ar, ag, ab) = Rgb(aHex);
        var (br, bg, bb) = Rgb(bHex);
        var r = (int)Math.Round(ar * t + br * (1 - t));
        var g = (int)Math.Round(ag * t + bg * (1 - t));
        var b = (int)Math.Round(ab * t + bb * (1 - t));
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>亮度的相对偏移（±0.06 ≈ 一档容器阶梯，M3 海拔规律）。</summary>
    private static string Shift(string hex, double delta)
    {
        var (r, g, b) = Rgb(hex);
        var l = Luminance(hex);
        var k = l + delta > 0.5 ? 1.12 : 0.88; // 亮底变深、暗底变亮（用乘法避免 255 溢出/截断）
        // 简化：方向由 delta 正负直接定，乘法缩放整通道。
        k = delta > 0 ? 1.12 : 0.88;
        return $"#{Math.Min(255,(int)Math.Round(r*k)):X2}{Math.Min(255,(int)Math.Round(g*k)):X2}{Math.Min(255,(int)Math.Round(b*k)):X2}";
    }

    private static (int, int, int) Rgb(string hex)
    {
        var body = hex.TrimStart('#');
        return (Convert.ToInt32(body[..2], 16), Convert.ToInt32(body.Substring(2, 2), 16), Convert.ToInt32(body.Substring(4, 2), 16));
    }
```
（Shift 内首行的三元判定行是冗余草稿——落盘时删除该行只保留 `k = delta > 0 ? 1.12 : 0.88;` 一行与注释；Luminance 已在 R1 加入类内，直接复用。）

## P2 Install 颜色段改消费派生
先 Read 定位。将现有：
```
        Set("Color.Surface", TokenBrushes.Brush(color.SectorFill, color.SectorFillAlpha));
        Set("Color.SurfaceSolid", TokenBrushes.Brush(color.SectorFill));
```
替换为：
```
        var panel = DerivePanelColors(color);
        Set("Color.Surface", TokenBrushes.Brush(panel.Surface));
        Set("Color.SurfaceSolid", TokenBrushes.Brush(panel.SurfaceSolid));
```
并将 FieldBackground 行替换：
old：
```
        Set("Color.FieldBackground", TokenBrushes.Brush(color.DeadZoneFill));
```
new：
```
        Set("Color.FieldBackground", TokenBrushes.Brush(panel.FieldBackground));
```
Accent 行替换：
old：
```
        Set("Color.Accent", TokenBrushes.Brush(color.HoverFill));
```
new：
```
        Set("Color.Accent", TokenBrushes.Brush(panel.Accent));
```
语义色两行替换：
old：
```
        Set("Color.Success", new SolidColorBrush(Color.FromRgb(0x8C, 0xC8, 0x8C)));
        Set("Color.Danger", new SolidColorBrush(Color.FromRgb(0xF0, 0x8A, 0x8A)));
```
new：
```
        Set("Color.Success", TokenBrushes.Brush(panel.Success));
        Set("Color.Danger", TokenBrushes.Brush(panel.Danger));
```
R1 的派生键块（Outline/HoverSurface 与其注释）整体替换为：
```
        // 派生键来自 DerivePanelColors（基于合成底的明暗翻转，含半透明皮肤的正确方向）。
        Set("Color.Outline", TokenBrushes.Brush(panel.Outline, 0.16));
        Set("Color.HoverSurface", TokenBrushes.Brush(panel.HoverSurface, 0.05));
```
（DeadZone/DeadZoneStroke 两键保持原料（轮盘语义），不动。）

## P3 SkinContrastTests.cs 追加面板配对用例（类内追加）
```csharp
    /// <summary>面板配对（skin-panel-readability）：面板是实色表面，派生键必须对每套皮肤可读。</summary>
    public static IEnumerable<object[]> PanelStates()
    {
        foreach (var skin in TokenStore.Skins.All)
            yield return new object[] { skin.Id };
        yield return new object[] { "system-light" };
        yield return new object[] { "system-dark" };
    }

    private static double Lum(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
        var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
        var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    [Theory]
    [MemberData(nameof(PanelStates))]
    public void 面板派生键_每套皮肤下正文与强调色都可读(string stateId)
    {
        var color = stateId switch
        {
            "system-light" => TokenStore.PreviewTokens(SystemSkin.Id).Color, // 两态合成用同键集，暗态=基线已由其余用例覆盖
            _ => TokenStore.PreviewTokens(stateId).Color,
        };
        var panel = TokenResources.DerivePanelColors(color);
        var surface = Lum(panel.SurfaceSolid);
        Assert.True(Math.Abs(surface - Lum(color.Label)) >= 0.3,
            $"{stateId}：正文与面板底亮度差不足（{panel.SurfaceSolid} vs {color.Label}）");
        Assert.True(Math.Abs(surface - Lum(panel.Accent)) >= 0.2,
            $"{stateId}：强调色与面板底亮度差不足（{panel.SurfaceSolid} vs {panel.Accent}）");
    }

    [Fact]
    public void 半透明皮肤的面板底是暗色_回归对照()
    {
        // 修复前：α0.07 白实色化=纯白底配白字。这条锁死合成语义不再回退。
        var translucent = TokenStore.Skins.All.First(s => s.Id == "translucent");
        var merged = TokenStore.PreviewTokens("translucent");
        var panel = TokenResources.DerivePanelColors(merged.Color);
        Assert.True(Lum(panel.SurfaceSolid) < 0.3, $"应为暗面板，实为 {panel.SurfaceSolid}");
    }
```
（TokenResources/SystemSkin 命名空间 using 若缺按许可补 WinQuick.App.Design；system-dark 态由 default 用例覆盖，此处以 system-light 单列。）

## 验证
1. dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo → 0 error；
2. dotnet test tests/WinQuick.Core.Tests --filter SkinContrast --nologo → 全绿（8 原+10 新=18）；
3. dotnet test tests/WinQuick.Core.Tests --nologo → 全量（329+10=339）。
