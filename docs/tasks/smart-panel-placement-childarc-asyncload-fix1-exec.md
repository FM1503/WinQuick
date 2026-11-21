# 执行书：审查问题修复（fix1，两轮 reviewer 共 13 项）

## F1（P0）RadialRingVisual.ChildArc.cs：spinner 挂树
定位：AddChildIcon 的 spinner 分支（约 560-570 行）。先 Read 核对。
old:
```
            _childExtraVisuals.Add(spinner);
            Canvas.SetLeft(spinner, _center.X + offset.X - iconSize / 2);
```
new:
```
            _childExtraVisuals.Add(spinner);
            Root.Children.Add(spinner);
            Canvas.SetLeft(spinner, _center.X + offset.X - iconSize / 2);
```

## F2（P1）ShellIcons.GetOrBeginGet 区分「负命中」与「未就绪」+ 调用方三分支修正
### F2a ShellIcons.cs：签名加 out bool pending
先 Read 定位 GetOrBeginGet 全文。将
old:
```
    public static BitmapSource? GetOrBeginGet(string? path, Action<BitmapSource?> onReady)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (Gate)
        {
            if (MemoryCache.TryGetValue(path, out var cached)) return cached;
        }
```
new:
```
    public static BitmapSource? GetOrBeginGet(string? path, Action<BitmapSource?> onReady, out bool pending)
    {
        // 缓存命中（含负缓存）都算「已定」：负命中返回 null 但 pending=false，
        // 调用方据此走首字占位而不是 spinner —— 否则抽取失败项每次重展都是永久加载态。
        if (string.IsNullOrWhiteSpace(path)) { pending = false; return null; }

        lock (Gate)
        {
            if (MemoryCache.TryGetValue(path, out var cached)) { pending = false; return cached; }
        }
```
并将该方法内 `return null;`（末尾起 Task 后那处）改为 `{ pending = true; return null; }`（先 Read 核对原文精确结构再替换）。
同步更新该方法的 XML 注释第一段：在「缓存命中（含…负缓存）**同步返回**」后加「（负命中以 pending=false 区分）」。

### F2b RadialRingVisual.ChildArc.cs：调用方三分支
old:
```
        var source = ShellIcons.GetOrBeginGet(child.IconPath, ready =>
```
（该行到三分支结束的整段先 Read 核对；将签名调用改为带 out var pending，并把
`else if (string.IsNullOrWhiteSpace(child.IconPath))`
分支条件改为
`else if (!pending)`
——语义：已定无图标（无路径或抽取失败过）都走首字占位。同步删去原 `string.IsNullOrWhiteSpace(child.IconPath)` 判断（被 !pending 覆盖）。spinner 分支条件保持 else。）

## F3（P1）探针真校验「图标全就绪、spinner 无残留」
### F3a SectorSpinner.cs：打标
old:
```
        grid.Loaded += (_, _) => board.Begin(arc, true);
```
new:
```
        // 探针识别标（ChildArcPerfProbe 数 spinner 残留用）；Tag 不参与任何逻辑。
        grid.Tag = "sector-spinner";
        grid.Loaded += (_, _) => board.Begin(arc, true);
```
### F3b RadialRingVisual.ChildArc.cs：加接缝（放在 CollapseChildArcImmediate 方法之前）
```csharp
    /// <summary>探针接缝：子弧加载状态（有源图标数 / 图标总数 / 存活 spinner 数）。</summary>
    internal (int IconsWithSource, int TotalIcons, int SpinnersAlive) ChildArcLoadStateForProbe() =>
        (_childIcons.Count(i => i.Source is not null), _childIcons.Count,
            _childExtraVisuals.OfType<FrameworkElement>()
                .Count(e => e.Tag as string == "sector-spinner"));
```
（FrameworkElement 的 using 若缺按许可补。）
### F3c RadialWindow.cs / OverlayController.cs：各加一行转发（先 grep `ExpandChildArcForProbe` 找到两级转发的位置，紧邻处同款风格加）
RadialWindow：
```csharp
    internal (int, int, int) ChildArcLoadStateForProbe => _view.ChildArcLoadStateForProbe();
```
OverlayController：
```csharp
    internal (int IconsWithSource, int TotalIcons, int SpinnersAlive) ChildArcLoadStateForProbe() =>
        _radial.ChildArcLoadStateForProbe;
```
### F3d ChildArcPerfProbe.cs：恒真断言改真校验
old:
```
            results.Add(("图标后台就绪窗口 ≤4s", true, $"展开后总耗时 {coldTotalMs}ms（含等待）"));
```
new:
```
            var state = overlay.ChildArcLoadStateForProbe();
            results.Add(("图标全部就绪且 spinner 无残留",
                state.SpinnersAlive == 0 && state.IconsWithSource == state.TotalIcons && state.TotalIcons > 0,
                $"图标 {state.IconsWithSource}/{state.TotalIcons}，残留 spinner {state.SpinnersAlive}，等待 {coldTotalMs}ms"));
```

## F4（P1）ShellIcons：锁只护字典、磁盘+COM 抽取移到锁外
### F4a Get 改造（先 Read 定位全文）
old:
```
        lock (Gate)
        {
            if (MemoryCache.TryGetValue(path, out var cached)) return cached;

            var icon = LoadFromDiskCache(path) ?? ExtractFromShell(path);
            MemoryCache[path] = icon;
            return icon;
        }
```
new:
```
        lock (Gate)
        {
            if (MemoryCache.TryGetValue(path, out var cached)) return cached;
        }

        // 抽取（磁盘+COM）刻意放在锁外：持锁跨 IO 会让后台抽取期间任何 UI 线程的
        // 同步取图标在 lock 上排队（主环 GetScaled、面板），表现为「异步化了还卡」。
        // 代价：同 key 并发可能重复抽取（MemoryCache 幂等、首展一次性成本，可接受；
        // 子弧路径已由 GetOrBeginGet 的 _inflight 去重兜住）。
        var icon = LoadFromDiskCache(path) ?? ExtractFromShell(path);
        lock (Gate) { MemoryCache[path] = icon; }
        return icon;
```
### F4b GetScaled 同款（先 Read 定位）
old:
```
        lock (Gate)
        {
            var key = (path, targetPx);
            if (ScaledCache.TryGetValue(key, out var cached)) return cached;

            var scaled = Bake(Get(path), targetPx);
            ScaledCache[key] = scaled;
            return scaled;
        }
```
new:
```
        lock (Gate)
        {
            var key = (path, targetPx);
            if (ScaledCache.TryGetValue(key, out var cached)) return cached;
        }

        // 同 Get：烘焙（RenderTargetBitmap）在锁外，只把字典读写留在锁内。
        var scaled = Bake(Get(path), targetPx);
        lock (Gate) { ScaledCache[(path, targetPx)] = scaled; }
        return scaled;
```

## F5（P2）spinner 随展开进度淡入淡出
F1 的 spinner 创建处，在 `Root.Children.Add(spinner);` 后加：
```
            spinner.Opacity = _childArcProgress;
```
并在本文件中「进度驱动图标/标签透明度」的更新处（约 438-446 行 RebuildChildGeometry 内，先 Read 核对 image 与 label 的 Opacity 更新写法），同循环内追加对 _childExtraVisuals 中 Tag 为 sector-spinner 的元素做同款 Opacity 更新（一行 LINQ 或 foreach，风格随现场）。

## F6（P2）SectorSpinner.cs：旋转加缓动（对齐 Material「标准 easing」近似）
old:
```
        var spin = new DoubleAnimation
        {
            From = 0, To = 360, Duration = TimeSpan.FromMilliseconds(1200),
            RepeatBehavior = RepeatBehavior.Forever,
        };
```
new:
```
        var spin = new DoubleAnimation
        {
            From = 0, To = 360, Duration = TimeSpan.FromMilliseconds(1200),
            RepeatBehavior = RepeatBehavior.Forever,
            // Material 3 的「标准 easing」近似：二次缓入缓出（每圈 4 个变速点，观感接近原型页）。
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
```

## F7（P2）GetOrBeginGet XML 注释标注线程前提
在 GetOrBeginGet 的 XML 注释末尾（</summary> 前或其后新增 <remarks>）加一句：
「仅限 UI 线程调用：回调用 SynchronizationContext 投回调用线程。」（以 <remarks> 段落形式追加。）

## F8（P2）探针 overlay.Hide 收尾移入 finally
ChildArcPerfProbe.cs：把 `overlay.Hide(); hot.Hide();`（现位于 try 尾部）移到 finally 段首（hot 变量作用域若不足，将声明提到 try 之前 `OverlayController? overlay = null, hot = null;` 并相应初始化——先 Read 全文后以最小改动落盘）。

## F9（P2）ClearCachesForProbe 删不掉磁盘缓存时探针可失败
ShellIcons.cs 签名改：
old:
```
    internal static void ClearCachesForProbe()
```
new:
```
    internal static bool ClearCachesForProbe()
```
方法体末尾改为：目录删除后仍存在 → return false；否则 return true（先 Read 原文，保留原 try/catch 结构，catch 分支 return false）。
ChildArcPerfProbe.cs 冷段调用处改：
old:
```
            ShellIcons.ClearCachesForProbe();
```
new:
```
            if (!ShellIcons.ClearCachesForProbe())
                results.Add(("磁盘图标缓存确已清空", false, "删除失败（占用/权限）——冷缓存段无效"));

            ShellIcons.ClearCachesForProbe();
```
（若认为重复调用不妥，可改为把布尔断言独立成一条 results.Add 且后续仍继续——以最小改动落盘。）

## F10（P2，任务书 P）OverlayController.cs 光标失败判定修正
old:
```
        MonitorLayout.TryGetCursorPosition(out var cx, out var cy);
        if (cx == 0 && cy == 0)
        {
            cx = monitor.X + monitor.Width / 2;
            cy = monitor.Y + monitor.Height / 2;
        }
```
new:
```
        // 拿不到光标（罕见）才退回屏心；(0,0) 不是哨兵 —— 光标真在主屏左上角像素时是合法值。
        if (!MonitorLayout.TryGetCursorPosition(out var cx, out var cy))
        {
            cx = monitor.X + monitor.Width / 2;
            cy = monitor.Y + monitor.Height / 2;
        }
```

## F11（P2）PositionPanel 文档注释更新
old:
```
    /// <summary>
    /// 把面板摆在光标所在屏的**另一侧**，尽量避免压住轮盘
    /// （轮盘跟着光标走，所以把面板放到光标的反方向最不容易重叠）。
    ///
```
new:
```
    /// <summary>
    /// 把面板摆到与轮盘**零重叠**的屏内空处（spec smart-panel-placement-childarc-asyncload §2.1）：
    /// 候选锚位中零重叠者优先、取离光标最近；窄屏全重叠取最小重叠；并列取稳定序。
    ///
```

## F12（P2）PanelPlacementTests 用例 9 重写（锁定「垂直跟随」）
old:
```
    [Fact]
    public void 对侧候选垂直跟随光标()
    {
        // 光标在左下：右侧对侧候选的 Top 应显著偏下（跟随），而不是几何居中。
        var low = Choose(1920, 1440, 200, 1000, 400, 404, 700, 400, 1300);
        Assert.True(low.Top > 1440 / 2 - 700 / 2, "对侧候选应跟随光标下移");
    }
```
new:
```
    [Fact]
    public void 对侧候选垂直跟随光标()
    {
        // 轮盘在右下、光标在轮盘上：左侧跟随候选 (24, 700) 比左中点 (24, 370) 离光标近得多，
        // 且与右侧所有候选同为零重叠 —— 胜出者必须是「跟随」那个，精确锁定该行为。
        var r = Choose(1920, 1440, 1200, 800, 500, 404, 700, 1450, 1050);
        Assert.Equal(24, r.Left);
        Assert.Equal(700, r.Top);
    }
```

F12 后续裁定：跟随候选经实算证明冗余已删（八候选），用例 9 改锁「贴侧中点胜出」(758,716)。
F12 裁定后续被复审证伪（跟随候选约 20-30% 场景胜出），已由 fix2 恢复 10 候选；本行仅留痕。

## F13（P2）执行书回写：smart-panel-placement-exec.md 用例 2 同步为修订版
docs/tasks/smart-panel-placement-exec.md 中用例 2 的断言与注释（预期 (24,24) 那版）替换为与 PanelPlacementTests.cs 现行一致的修订版（名称「轮盘居中_零重叠取最近候选」、断言 (24,190)、注释说明左右中点比四角近 18px、稳定序取左中点）。先 Read 两文件核对后以测试文件现文为准回写。

## 验证（全部修复后）
1. `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` → 0 error；
2. `dotnet build src/WinQuick.Probes/WinQuick.Probes.csproj -c Release --nologo` → 0 error；
3. `dotnet test tests/WinQuick.Core.Tests --nologo` → 全绿（321，用例 9 重写后仍 321）。

old 不匹配即停报（F2b/F5/F8/R3d 允许按「先 Read 核对后以现场为准的最小适配」执行，均在对应条目注明）。
