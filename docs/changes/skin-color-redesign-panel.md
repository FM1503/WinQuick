---
slug: skin-color-redesign-panel
status: approved
date: 2026-11-23
tier: L
changed-files: src/WinQuick.App/Radial/SkinPanel.xaml.cs
---

# 皮肤配色重设计 · panel 批：选中行 on-accent 配对字色

- 改什么：皮肤/形态选中行的行内文字从 Color.Text/TextMuted 换成 Color.AccentText；RefreshTextColors 改为按行选中态逐行刷。
- 为什么：spec R1 —— 选中行底色是 Accent，沿用通用文字色就是半透明皮肤白底白字整行消失（用户截图实锤）。
- 影响面：皮肤面板视觉；不动行结构（沿用就地更新、不重建列表的既有约束）。
- 回滚方式：docs/changes/skin-color-redesign-panel.rollback.json。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 309 | private void RefreshTextColors() | 19 | <NL>    private void RefreshTextColors()<NL>    {<NL>        var text = TokenResources.Brush("Color.Text");<NL>        var muted = TokenResources.Brush("Color.TextMuted");<NL>        var onAccent = TokenResources.Brush("Color.AccentText");<NL>        var family = TokenResources.Get<FontFamily>("Font.Family");<NL>        var body = TokenResources.Get<double>("Font.BodySize");<NL>        var small = TokenResources.Get<double>("Font.SmallSize");<NL><NL>        // 选中行底是 Accent → 文字必须换配对色 AccentText（spec R1）。<NL>        // 旧实现按字重统一刷 Text/TextMuted，半透明皮肤就白底白字 —— 用户截图里那行「整块纯白无文字」。<NL>        void RefreshRow(Border row, bool isCurrent)<NL>        {<NL>            foreach (var block in Descendants(row).OfType<TextBlock>())<NL>            {<NL>                block.FontFamily = family;<NL>                var isBody = block.FontWeight == FontWeights.SemiBold;<NL>                block.FontSize = isBody ? body : small;<NL>                block.Foreground = isCurrent ? onAccent : (isBody ? text : muted);<NL>            }<NL>        }<NL><NL>        foreach (var (id, row, _) in _skinRows)<NL>            RefreshRow(row, string.Equals(id, _currentSkin, StringComparison.OrdinalIgnoreCase));<NL>        foreach (var (form, row) in _formRows)<NL>            RefreshRow(row, form == _currentForm);<NL>    } |
| 2 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 212 | Foreground = TokenResources.Brush("Color.TextMuted"), | 1 | <NL>                        Foreground = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.TextMuted"), |
| 3 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 204 | Foreground = TokenResources.Brush("Color.Text"), | 1 | <NL>                        Foreground = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.Text"), |
| 4 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 167 | Foreground = TokenResources.Brush("Color.TextMuted"), | 1 | <NL>            Foreground = noteBrush, |
| 5 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 159 | Foreground = TokenResources.Brush("Color.TextMuted"), | 1 | <NL>            Foreground = noteBrush, |
| 6 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 151 | Foreground = TokenResources.Brush("Color.Text"), | 1 | <NL>            Foreground = nameBrush, |
| 7 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 147 | var text = new StackPanel | 1 | <NL>        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };<NL>        // 选中行底是 Accent → 行内文字换成 on-accent 配对色（spec R1；缺陷：半透明白底白字整行消失）。<NL>        var nameBrush = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.Text");<NL>        var noteBrush = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.TextMuted"); |

## expect_after

| 文件 | 包含子串 |
| --- | --- |
| src/WinQuick.App/Radial/SkinPanel.xaml.cs | var nameBrush = isCurrent ? TokenResources.Brush("Color.AccentText") |
| src/WinQuick.App/Radial/SkinPanel.xaml.cs | RefreshRow(row, form == _currentForm); |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-panel.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo
```