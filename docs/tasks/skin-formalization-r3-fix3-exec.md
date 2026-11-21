# 执行书 R3-fix3：reviewer P2 两项（system 页脚文案 + 强调色二级回退）

## U1 SkinPanel.xaml.cs 页脚文案（约 266-268 行，先 Read）
old：
```
        FooterStatus.Text = $"已切换到「{_catalog.Find(skinId)?.Name ?? SkinCatalog.DefaultId}」" +
```
（该行后续拼接以现场为准整体保留，仅改前半的名称解析。）new：
```
        FooterStatus.Text = $"已切换到「{skinId == SystemSkin.Id ? SystemSkin.Spec().Name : _catalog.Find(skinId)?.Name ?? SkinCatalog.DefaultId}」" +
```

## U2 SystemThemeWatcher.cs AccentColor 二级回退（先 Read 定位 AccentColor 方法）
在 `if (key?.GetValue("AccentColor") is not int argb) return fallback;` 之前不动；将「读不到 AccentColor 即回退」改为先试 ColorizationColor：old：
```
        using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
        if (key?.GetValue("AccentColor") is not int argb) return fallback;
```
new：
```
        using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
        if (key?.GetValue("AccentColor") is not int argb &&
            key?.GetValue("ColorizationColor") is not argb) return fallback;
```

## 验证
1. dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo → 0 error；
2. dotnet test tests/WinQuick.Core.Tests --nologo → 329 全绿。
