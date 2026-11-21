# 执行书：fix2（复审 P1：恢复跟随候选；P2：清缓存目录不存在误报）

背景：复审以独立模拟（3500 光标位）证伪「跟随候选从不胜出」——轮盘跟光标走并钳屏的真实耦合下约 20-30% 场景跟随候选胜出且面板离光标更近。恢复 spec §2.1 批准的 10 候选口径。

## G1 PanelPlacement.cs 恢复 10 候选
old（fix1 修订 1 的现状，先 Read 核对）：
```
        var midX = mx + (mw - w) / 2;
        var midY = my + (mh - h) / 2;
        // 八候选：四角 + 四边中点。曾试过「对侧贴边+垂直跟随光标」，经审查与实算证明冗余 ——
        // 贴轮盘那一侧的角/中点候选永远比对侧任何位置近，跟随候选从不胜出，删之。
        var candidates = new (int Left, int Top)[]
        {
            (mx + m, my + m),
            (mx + mw - w - m, my + m),
            (mx + m, my + mh - h - m),
            (mx + mw - w - m, my + mh - h - m),
            (midX, my + m),
            (midX, my + mh - h - m),
            (mx + m, midY),
            (mx + mw - w - m, midY),
        };
```
new:
```
        var midX = mx + (mw - w) / 2;
        var midY = my + (mh - h) / 2;
        var followTop = Clamp(t.CursorY - h / 2, my + m, my + mh - h - m);
        var candidates = new (int Left, int Top)[]
        {
            (mx + m, my + m),
            (mx + mw - w - m, my + m),
            (mx + m, my + mh - h - m),
            (mx + mw - w - m, my + mh - h - m),
            (midX, my + m),
            (midX, my + mh - h - m),
            (mx + m, midY),
            (mx + mw - w - m, midY),
            // 对侧贴边 + 垂直跟随光标：轮盘跟着光标走，光标在屏中上部时轮盘也在中上，
            // 此时对侧跟随位比任何固定候选都离光标近（复审模拟：约 20-30% 场景由它胜出）。
            (mx + m, followTop),
            (mx + mw - w - m, followTop),
        };
```

## G2 PanelPlacementTests.cs 用例 9 恢复强断言（复审分叉例）
old（fix1 修订 2 的现状，先 Read 核对）：
```
    [Fact]
    public void 贴轮盘侧边的中点候选胜出()
    {
        // 轮盘在右半、光标在轮盘上：屏下中点候选 (758, 716) 零重叠且距光标约 490px，
        // 远近于任何对侧候选（>1224px）—— 锁定「贴侧优先」这一真实契约。
        // （曾设「对侧垂直跟随」候选，经审查与实算证明从不胜出，已删，见 PanelPlacement 注释。）
        var r = Choose(1920, 1440, 1200, 800, 500, 404, 700, 1450, 1050);
        Assert.Equal(758, r.Left);
        Assert.Equal(716, r.Top);
    }
```
new:
```
    [Fact]
    public void 对侧跟随候选在轮盘居中光标时胜出()
    {
        // 轮盘跟光标走（真实耦合）：光标 (850,450) 居屏中上，轮盘以它为中心钳屏后 (600,200,500)。
        // 左跟随候选 (24,100) 比所有固定候选都近 —— 锁定「跟随」行为（复审模拟的分叉例）。
        var r = Choose(1920, 1440, 600, 200, 500, 404, 700, 850, 450);
        Assert.Equal(24, r.Left);
        Assert.Equal(100, r.Top);
    }
```

## G3 ShellIcons.cs ClearCachesForProbe 目录不存在不误报
先 Read 定位该方法。在 try 内 Directory.Delete 行之前加一行：
```
            if (!System.IO.Directory.Exists(CacheDirectory)) return true; // 目录不存在=本就空，冷段有效
```
（以现场结构最小适配：若 Delete 在 try 内单行，把该判断插到它上面。）

## G4 fix1 执行书裁定行更正
docs/tasks/smart-panel-placement-childarc-asyncload-fix1-exec.md 末尾 F12 裁定行之后追加：
「F12 裁定后续被复审证伪（跟随候选约 20-30% 场景胜出），已由 fix2 恢复 10 候选；本行仅留痕。」

## 验证
1. dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo → 0 error；
2. dotnet test tests/WinQuick.Core.Tests --nologo → 321 全绿（用例 9 恢复后仍 321）；
3. 其余摆位用例（1/2/3/6 等）在 10 候选下预期不变（若有用例因候选恢复而翻红，停报并贴失败详情——那些用例断言的候选在 10 候选下同样零重叠且更近者不变，理论不翻）。