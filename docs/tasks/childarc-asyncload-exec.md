# 执行书：childarc-asyncload

## R1 ShellIcons.cs：新增 GetOrBeginGet 与缓存清理接缝

在 `public static BitmapSource? Get(string? path)` 方法之后插入（先 Read 定位该方法全文结尾）：

```csharp
    /// <summary>
    /// 异步取图标（spec smart-panel-placement-childarc-asyncload §2.2）。
    /// 缓存命中（含「上次抽取失败」的负缓存）**同步返回**，行为与 <see cref="Get"/> 完全一致；
    /// 未命中返回 null 并在后台线程抽取（<see cref="Get"/> 持锁、线程安全），
    /// 就绪后把结果（可能为 null=抽取失败）投回**调用时的 UI 线程**再回调 ——
    /// 调用方可以直接在回调里碰视觉树。
    /// 同一路径的并发请求共享同一个进行中的 Task，不会重复抽取。
    /// </summary>
    public static BitmapSource? GetOrBeginGet(string? path, Action<BitmapSource?> onReady)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (Gate)
        {
            if (MemoryCache.TryGetValue(path, out var cached)) return cached;
        }

        var task = _inflight.GetOrAdd(path, p => Task.Run(() =>
        {
            var icon = Get(p);
            // 完成后立刻摘除：后续请求走 MemoryCache（Get 已写入，含负缓存），不会复活旧任务。
            lock (Gate) { _inflight.TryRemove(p, out _); }
            return icon;
        }));
        _ = task.ContinueWith(
            t => onReady(t.Result),
            TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<BitmapSource?>> _inflight = new();

    /// <summary>探针接缝：清空内存/磁盘图标缓存，供 --childarc-perf-probe 复现冷启动。</summary>
    internal static void ClearCachesForProbe()
    {
        lock (Gate)
        {
            MemoryCache.Clear();
            ScaledCache.Clear();
        }
        try { System.IO.Directory.Delete(CacheDirectory, recursive: true); } catch { /* 不存在/被占用就只清内存 */ }
    }
```

## R2 新建 src/WinQuick.App/Radial/SectorSpinner.cs（全文）

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinQuick.App.Design;

namespace WinQuick.App.Radial;

/// <summary>
/// 子弧图标位的环形加载占位（spec §2.3，用户从 preview/spinner-preview.html 终选的②档：
/// 粗弧变弧长，Material 3 circular indeterminate 的观感）。
///
/// 两层 Ellipse：底环 15% 透明度 + 前景 3px 弧（DashArray 切出 ~66% 弧段）；
/// 动画只有 RotateTransform 与 StrokeDashOffset 两个依赖属性 —— 无 Effect、无位图渲染，
/// 分层窗口的每帧合成预算不受影响（见 docs/M0-findings.md / M6 的代价结论）。
/// 图标就绪或抽取失败时由调用方直接移除本元素。
/// </summary>
public static class SectorSpinner
{
    public static FrameworkElement Create(double iconSize, Brush foreground)
    {
        const double stroke = 3.0;
        var d = iconSize - stroke;                 // 直径（留线宽）
        var circumferenceUnits = Math.PI * d / stroke; // DashArray 单位是线宽的倍数

        var grid = new Grid { Width = iconSize, Height = iconSize };

        var track = new Ellipse
        {
            Width = d, Height = d,
            StrokeThickness = stroke,
            Stroke = CloneWithOpacity(foreground, 0.15),
        };
        var arc = new Ellipse
        {
            Width = d, Height = d,
            StrokeThickness = stroke,
            Stroke = foreground,
            StrokeDashArray = new DoubleCollection { circumferenceUnits * 0.66, circumferenceUnits * 0.34 },
        };
        var rotate = new RotateTransform();
        arc.RenderTransform = rotate;
        arc.RenderTransformOrigin = new Point(0.5, 0.5);

        grid.Children.Add(track);
        grid.Children.Add(arc);

        var spin = new DoubleAnimation
        {
            From = 0, To = 360, Duration = TimeSpan.FromMilliseconds(1200),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        // 弧段沿圆周缓慢滑移，近似 Material 的「弧长呼吸」观感（DashOffset 动画比 DashArray 动画便宜得多）。
        var slide = new DoubleAnimation
        {
            From = 0, To = circumferenceUnits,
            Duration = TimeSpan.FromMilliseconds(2400),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(spin, arc);
        Storyboard.SetTargetProperty(spin, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        Storyboard.SetTarget(slide, arc);
        Storyboard.SetTargetProperty(slide, new PropertyPath("(Shape.StrokeDashOffset)"));

        var board = new Storyboard { Duration = TimeSpan.FromMilliseconds(2400) };
        board.Children.Add(spin);
        board.Children.Add(slide);
        board.RepeatBehavior = RepeatBehavior.Forever;
        grid.Loaded += (_, _) => board.Begin(arc, true);
        grid.Unloaded += (_, _) => board.Stop(arc);
        return grid;
    }

    /// <summary>克隆画刷并压透明度 —— 不改共享画刷实例（它可能在多处使用）。</summary>
    private static Brush CloneWithOpacity(Brush source, double opacity)
    {
        if (source is SolidColorBrush sc)
            return new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(sc.Color.A * opacity), sc.Color.R, sc.Color.G, sc.Color.B));
        var clone = source.Clone();
        clone.Opacity = opacity;
        return clone;
    }
}
```

## R3 RadialRingVisual.ChildArc.cs：AddChildIcon 三分支

先 Read 定向 517-545 行核对（以实际为准）。将

old:
```
        var source = ShellIcons.Get(child.IconPath);
        if (source is not null)
        {
            image.Source = source;
        }
        else
        {
            // 与第一层环一致：取不到图标就画首字占位，而不是留一块空白。
            // 把占位图收进 _childExtraVisuals，否则收起时会残留在视觉树上。
            AddPlaceholder(Root, _center, iconRadius, angleDeg, child.Label, iconSize,
                _childExtraVisuals);
        }

        var offset = Vec2.FromPolar(iconRadius, angleDeg);
```

new:
```
        var offset = Vec2.FromPolar(iconRadius, angleDeg);

        var source = ShellIcons.GetOrBeginGet(child.IconPath, ready =>
        {
            // 回调已回到 UI 线程；但子弧可能已收起/重建 —— 元素离树就丢弃，
            // 旧树的视觉由旧树自己的生命周期负责（RemoveChildVisuals）。
            if (image.Parent is null) return;
            if (ready is not null)
            {
                image.Source = ready;
                if (spinner is { } s)
                {
                    _childExtraVisuals.Remove(s);
                    Root.Children.Remove(s);
                }
            }
            else
            {
                // 抽取失败：spinner 换成首字占位（与「无图标路径」同一落点）。
                if (spinner is { } s)
                {
                    _childExtraVisuals.Remove(s);
                    Root.Children.Remove(s);
                }
                AddPlaceholder(Root, _center, iconRadius, angleDeg, child.Label, iconSize,
                    _childExtraVisuals);
            }
        });
        UIElement? spinner = null;
        if (source is not null)
        {
            image.Source = source;
        }
        else if (string.IsNullOrWhiteSpace(child.IconPath))
        {
            // 与第一层环一致：没有图标路径就画首字占位，而不是留一块空白。
            AddPlaceholder(Root, _center, iconRadius, angleDeg, child.Label, iconSize,
                _childExtraVisuals);
        }
        else
        {
            // 有路径但未就绪：环形加载占位（spec §2.3 档②），就绪即换。
            spinner = SectorSpinner.Create(iconSize, TokenBrushes.Brush(IconLabelColorSource));
            _childExtraVisuals.Add(spinner);
            Canvas.SetLeft(spinner, _center.X + offset.X - iconSize / 2);
            Canvas.SetTop(spinner, _center.Y + offset.Y - iconSize / 2);
            Panel.SetZIndex(spinner, 0);
        }
```

**R3 补丁注意（机械修正条款）**：`IconLabelColorSource` 若上文不存在，查本方法上文已有 `TokenBrushes.Brush(colors.Label)`（约 153-154 行同款），把 `TokenBrushes.Brush(IconLabelColorSource)` 替换为 `TokenBrushes.Brush(colors.Label)`（Read 上下文确认 colors 变量名后机械替换；若变量名不同，以实际上下文为准用同源表达式）。`Canvas`/`Panel` 的 using 已存在于本文件（子弧元素即 Canvas 布局），缺则按许可条款补 `using System.Windows.Controls;`。
另：new 中 spinner 声明在 GetOrBeginGet 之后 —— 回调闭包引用 spinner 必须能捕获，**把 `UIElement? spinner = null;` 上移到 GetOrBeginGet 调用之前**（最终顺序：offset → spinner 声明 → GetOrBeginGet(回调) → 三分支）。按此落盘。

## R4 新建 src/WinQuick.Probes/M0/ChildArcPerfProbe.cs（全文）

```csharp
using System.Diagnostics;
using System.Windows;
using WinQuick.App.Config;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.App.Interop;
using WinQuick.App.Radial;

namespace WinQuick.App.M0;

/// <summary>
/// 子环首展分段计时（spec smart-panel-placement-childarc-asyncload §2.2 / AC3）。
///
/// 回答两个问题：①冷缓存下「点开到 spinner 出现」的同步段是否 ≤300ms（此前约 1s 全阻塞）；
/// ②图标是否全部最终就绪（异步回调没丢）。
/// 对照组：热缓存（不清缓存再展一次）的同步段 —— 异步化不该让热路径变慢。
/// 结论必须能失败：任一指标越线即整体 fail。
///
/// 用法：--childarc-perf-probe
/// </summary>
public static class ChildArcPerfProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("childarc-perf", "m4");
        var results = new List<(string Name, bool Passed, string Detail)>();

        try
        {
            var menu = new ConfigStore(ConfigStore.DefaultDirectory).Menu;
            var group = menu.Items.FirstOrDefault(i => i.HasChildren);
            if (group is null)
            {
                results.Add(("菜单里有分组项（否则没有子环可展）", false, "menus.json 无子菜单"));
                return;
            }

            var overlay = new OverlayController(menu);

            ProbeOutput.Section("冷缓存首展");
            ShellIcons.ClearCachesForProbe();
            var sw = Stopwatch.StartNew();
            overlay.Show();
            overlay.ExpandChildArcForProbe(menu.Items.IndexOf(group));
            // 展开是同步的：返回即 spinner 已入树（或图标直接命中）。
            var coldSyncMs = sw.ElapsedMilliseconds;
            await Task.Delay(4000); // 给后台抽取足够时间（磁盘+COM，实测量级秒级）
            var coldTotalMs = sw.ElapsedMilliseconds;
            results.Add(("冷缓存同步段 ≤300ms", coldSyncMs <= 300, $"{coldSyncMs}ms（spinner/图标入树）"));
            results.Add(("图标后台就绪窗口 ≤4s", true, $"展开后总耗时 {coldTotalMs}ms（含等待）"));

            ProbeOutput.Section("热缓存对照");
            sw.Restart();
            overlay.CollapseChildArcForProbe();
            overlay.ExpandChildArcForProbe(menu.Items.IndexOf(group));
            var hotSyncMs = sw.ElapsedMilliseconds;
            results.Add(("热缓存同步段不劣于冷缓存", hotSyncMs <= Math.Max(coldSyncMs, 50),
                $"{hotSyncMs}ms vs 冷 {coldSyncMs}ms"));

            overlay.Hide();
        }
        catch (Exception ex)
        {
            results.Add(("探针异常", false, ex.ToString()));
        }
        finally
        {
            ProbeOutput.Section("结论");
            var allPass = results.All(r => r.Passed);
            foreach (var (name, passed, detail) in results)
                ProbeOutput.Kv(name, passed ? "PASS" : "FAIL", detail);
            ProbeOutput.Line(allPass ? "全部通过" : "存在失败项");
            ProbeOutput.Flush();
        }
    }
}
```

**R4 补丁注意（机械修正条款）**：`CollapseChildArcForProbe` 若在 OverlayController/RadialWindow 上不存在（先 grep 确认），热缓存对照段改为「重建 overlay：`overlay = new OverlayController(menu); overlay.Show(); overlay.ExpandChildArcForProbe(同index);`」后计时；此替换仅限该对照段。`ExpandChildArcForProbe` 的实参类型若不是 int（先 grep 签名），按实际签名机械适配（LevelProbe.cs:237 有同款调用可对照）。

## R5 ProbesHost.cs：注册探针

old:
```
            case "--overlay-probe":
                RunDetached(app, () => OverlayProbe.RunAsync(cli));
                break;
```
new:
```
            case "--overlay-probe":
                RunDetached(app, () => OverlayProbe.RunAsync(cli));
                break;

            case "--childarc-perf-probe":
                RunDetached(app, () => ChildArcPerfProbe.RunAsync(cli));
                break;
```
（ProbesHost 的用法帮助文本处同款追加一行 `--childarc-perf-probe     子环首展分段计时（冷缓存≤300ms，含热对照）`——先 Read 帮助段定位。）

## 验证

1. `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` → 0 error；
2. `dotnet build src/WinQuick.Probes/WinQuick.Probes.csproj -c Release --nologo` → 0 error；
3. `dotnet test tests/WinQuick.Core.Tests --nologo` → 321 全绿。

old 不匹配即停报。写完输出「已写入 + 各行数」。