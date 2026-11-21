# 执行书：fix-skin-form-persistence

目标文件：src/WinQuick.App/Config/ConfigStore.cs、src/WinQuick.App/Radial/SettingsPanel.xaml.cs

## 补丁 1（ConfigStore.cs 模板补 skin/form 渲染）

定位：约 387-397 行 `RenderConfig` 内 appearance 段。先 Read 定向行段核对，old 逐字符匹配：

old:
```
              "appearance": {
                // 全屏遮罩的不透明度，0–0.95。数值越大屏幕压得越暗。
                "dimOpacity": {{Dec(config.Appearance.DimOpacity)}},

                // 悬停高亮色，#RRGGBB。
                "accentColor": {{Str(config.Appearance.AccentColor)}},

                // 调色盘「最近使用」的颜色（新的在前，最多 6 个）。
                "recentAccentColors": [{{string.Join(",",
                    config.Appearance.RecentAccentColors.Select(c => Str(c)))}}]
              },
```

new:
```
              "appearance": {
                // 全屏遮罩的不透明度，0–0.95。数值越大屏幕压得越暗。
                "dimOpacity": {{Dec(config.Appearance.DimOpacity)}},

                // 悬停高亮色，#RRGGBB。
                "accentColor": {{Str(config.Appearance.AccentColor)}},

                // 调色盘「最近使用」的颜色（新的在前，最多 6 个）。
                "recentAccentColors": [{{string.Join(",",
                    config.Appearance.RecentAccentColors.Select(c => Str(c)))}}],

                // 皮肤 id 与盘面形态（null = 默认皮肤/饼形）。换肤面板的选择经
                // OnSkinChanged 落到这里 —— 模板漏了这两个键的话，换肤只活在
                // 内存里，重启就被读回默认。
                "skin": {{Str(config.Appearance.Skin)}},
                "form": {{Str(config.Appearance.Form)}}
              },
```

## 补丁 2（SettingsPanel.xaml.cs BuildConfig 沿用皮肤/形态）

定位：约 1369-1374 行。先 Read 定向行段核对，old 逐字符匹配：

old:
```
            Appearance = new AppearanceConfig
            {
                DimOpacity = ReadDimOpacity(),
                AccentColor = _accent,
                RecentAccentColors = _recent,
            },
```

new:
```
            Appearance = new AppearanceConfig
            {
                DimOpacity = ReadDimOpacity(),
                AccentColor = _accent,
                RecentAccentColors = _recent,
                // 皮肤/形态不在本面板编辑（换装走皮肤面板），但必须沿用 _original：
                // 漏掉会被 new 的默认 null 覆盖 —— 打开一次设置并保存，皮肤就被静默重置。
                Skin = _original.Appearance.Skin,
                Form = _original.Appearance.Form,
            },
```

## 验证步骤

1. `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo`
   预期：0 Error（warning 与基线一致）
2. `git diff src/WinQuick.App/Config/ConfigStore.cs src/WinQuick.App/Radial/SettingsPanel.xaml.cs`
   预期：仅上述两个 hunk，逐字符与执行书 new 一致

不匹配即停：任一 old 无法逐字符匹配时，停止并报告实际文件内容，不得猜测性修改。
