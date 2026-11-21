# 执行书 R3-fix1：SystemSkin 暗态补 hoverLabel（校验拦截实测）

背景：skin-probe 第三用例（system 暗态合成）被对比度校验拦截：#4CC2FF 对 #FFFFFF 亮度差 0.32<0.35。Win11 深色模式实际做法是亮蓝底配深字。暗态 overrides 漏了 hoverLabel 覆盖。

## 修改 src/WinQuick.App/Design/SystemSkin.cs（先 Read 核对）
old：
```
            return new System.Text.Json.Nodes.JsonObject
            {
                ["color"] = new System.Text.Json.Nodes.JsonObject { ["hoverFill"] = accentHex },
            };
```
new：
```
            return new System.Text.Json.Nodes.JsonObject
            {
                ["color"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["hoverFill"] = accentHex,
                    // Win11 深色模式的真实做法是亮蓝底配**深字**（不是白字）：
                    // #4CC2FF 对白字亮度差仅 0.32，会被对比度校验拦下（skin-probe 第三用例实测）。
                    ["hoverLabel"] = "#1B1B1B",
                },
            };
```

## 验证
1. dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo → 0 error；
2. dotnet test tests/WinQuick.Core.Tests --nologo → 329 全绿。
