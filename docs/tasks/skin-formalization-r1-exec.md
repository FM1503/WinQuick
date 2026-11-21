# 执行书 R1：材质色令牌化 + 面板跟随（D8+D2）

## S1 TokenResources.cs：Install 增派两个明暗自翻转键
先 Read 定位「颜色 ----」段（Set("Color.DeadZoneStroke",...) 行后）。追加：
```csharp
        // 材质派生键（spec skin-formalization §1）：面板的白系描边/悬停底在浅色皮肤下必须翻成黑系，
        // 否则就是「深色块贴在浅色面板上」。按 SectorFill 明暗自动派生，皮肤不必逐套声明。
        var fillIsLight = Luminance(color.SectorFill) > 0.5;
        var materialBase = fillIsLight ? "#000000" : "#FFFFFF";
        Set("Color.Outline", TokenBrushes.Brush(materialBase, 0.16));
        Set("Color.HoverSurface", TokenBrushes.Brush(materialBase, 0.05));
```
并在 TokenResources 类内加私有辅助（放类尾）：
```csharp
    /// <summary>Rec.709 相对亮度（与 DesignTokens.Validate 的判据同源，仅此处为派生键服务）。</summary>
    private static double Luminance(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
        var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
        var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }
```

## S2 两面板 XAML：StaticResource → DynamicResource（换肤即时跟随）
对 src/WinQuick.App/Radial/SettingsPanel.xaml 与 SkinPanel.xaml 各执行一次**全量替换**（replace_all）：`StaticResource` → `DynamicResource`。
（依据：Install(force) 换肤时重写 Application.Resources，DynamicResource 引用即时取新值；StaticResource 在首次解析后定死——这正是「面板不跟肤」的病灶。78+处引用全量替换，性能影响可忽略——面板一次性解析。）

## S3 硬编码材质色替换（10 处）
先 Read 各处核对，逐处替换：
1. SkinPanel.xaml.cs:234 `BorderBrush = TokenBrushes.Brush("#FFFFFF", 0.18),` → `BorderBrush = Design.TokenResources.Brush("Color.Outline"),`
2. SettingsPanel.xaml.cs:524 `BorderBrush = TokenBrushes.Brush("#FFFFFF", 0.15),` → `BorderBrush = Design.TokenResources.Brush("Color.Outline"),`
3. SettingsPanel.xaml.cs:541 `Background = TokenBrushes.Brush("#2A2F3A"),` → `Background = Design.TokenResources.Brush("Color.SurfaceSolid"),`
4. SettingsPanel.xaml.cs:542 `BorderBrush = TokenBrushes.Brush("#FFFFFF", 0.15),` → `BorderBrush = Design.TokenResources.Brush("Color.Outline"),`
5. SettingsPanel.xaml.cs:551 `Fill = TokenBrushes.Brush("#E08A7A"),` → `Fill = Design.TokenResources.Brush("Color.Danger"),`
6. SettingsPanel.xaml.cs:968 `Background = TokenBrushes.Brush("#FFFFFF", 0.045),` → `Background = Design.TokenResources.Brush("Color.HoverSurface"),`
7. SettingsPanel.xaml.cs:1073 `Background = TokenBrushes.Brush("#FFFFFF", 0.05),` → `Background = Design.TokenResources.Brush("Color.HoverSurface"),`
8. SettingsPanel.xaml:128 `<Setter TargetName="Bd" Property="Background" Value="#08FFFFFF" />` → `<Setter TargetName="Bd" Property="Background" Value="{DynamicResource Wq.Color.HoverSurface}" />`
9. SettingsPanel.xaml:132 同上 `#10FFFFFF` 行 → 同款 HoverSurface
（行号以 Read 实际为准；cs 文件的命名空间前缀按现场 using 情况取 `TokenResources.Brush` 或 `Design.TokenResources.Brush`。）

## S4 功能色注释声明
1. SettingsPanel.xaml 的 HSV 色相环区域（SvBase/渐变/SvDot，约 210-256 行）段首加注释：
`<!-- 功能色（HSV 拾色器的色相环/饱和度轨/游标描边）：表达的是色彩空间本身，不随皮肤明暗翻转 —— 刻意不令牌化（spec skin-formalization 边界）。 -->`
2. retro 动效风格声明：design/skins.json 的 retro 条目 src 字段末尾追加一句：「；steps(4) 阶梯为复古像素风格化动效，属 M3 规矩的风格例外（spec §3）」。

## S5 design/tokens.json：perspective 死键标注
perspective 段的 _comment 数组末尾追加一行字符串：
`"预留未采用：M9 评审用户判定不采用伪 3D（docs/M9-pseudo3d.md），此段令牌暂无代码读取 —— 保留待未来启用（spec skin-formalization §4）。"`

## 验证
1. `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` → 0 error；
2. `dotnet test tests/WinQuick.Core.Tests --nologo` → 321 全绿；
3. `grep -rn 'Brush("#' src/WinQuick.App/Radial --include="*.cs" | grep -v obj | wc -l` → 预期 0（材质色清零，功能色在 xaml）。

old 不匹配即停报。写完输出「已写入 + 行数」。
