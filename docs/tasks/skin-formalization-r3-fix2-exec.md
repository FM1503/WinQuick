# 执行书 R3-fix2：hoverLabel 按强调色亮度自动取黑/白（Win11 官方机制）

背景：fix1 的固定深字在「暗态+深强调色」（如 #0078D4）下亮度差 0.29 仍被校验拦。Win11 官方：强调色「generated automatically and optimized for contrast in both light and dark modes」（learn.microsoft.com/windows/apps/design/signature-experiences/color）——字色随强调色亮度自动取。

## 修改 src/WinQuick.App/Design/SystemSkin.cs（先 Read 全文核对）
1. 暗态分支（fix1 加的 hoverLabel 固定值）与亮态分支的 `["hoverLabel"] = "#FFFFFF",` 一并改为 `["hoverLabel"] = LabelFor(accent 相关)，`——具体：
   - 暗态：`["hoverLabel"] = AccentLabelText(accentHex),`（删原 "#1B1B1B" 行与注释，注释并入函数）；
   - 亮态：`["hoverLabel"] = AccentLabelText(accent),`（替换原 "#FFFFFF" 行）。
2. 类内加私有辅助（放 Compose 之后）：
```csharp
    /// <summary>
    /// 悬停字色按强调色亮度自动取黑/白 —— Win11 官方机制（强调色「自动为明暗两模式
    /// 优化对比度」）。单一强调色在明暗切换时可能配错固定字色（暗态+#0078D4+深字差仅
    /// 0.29 被校验拦下，skin-probe 实测），自动取色对任意强调色恒达标。
    /// </summary>
    private static string AccentLabelText(string accentHex)
    {
        var r = Convert.ToInt32(accentHex.Substring(1, 2), 16) / 255.0;
        var g = Convert.ToInt32(accentHex.Substring(3, 2), 16) / 255.0;
        var b = Convert.ToInt32(accentHex.Substring(5, 2), 16) / 255.0;
        return (0.2126 * r + 0.7152 * g + 0.0722 * b) > 0.5 ? "#1B1B1B" : "#FFFFFF";
    }
```
（亮态分支中 accent 变量名以现场为准——Compose(light, accentHex) 的参数名。）

## 验证
1. dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo → 0 error；
2. dotnet test tests/WinQuick.Core.Tests --nologo → 329 全绿。
