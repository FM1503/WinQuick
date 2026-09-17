using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinQuick.App.Radial;

/// <summary>
/// 扇区标签。处理「标签放不进扇区」这件事。
///
/// 三级策略，从最可读到最不得已：
/// 1. **折行显示** —— 折成最多两行。宽度直接减半，这是同时解决「长文本显示不全」
///    与「3 点/9 点方向文字压到图标」两个问题的关键。
/// 2. **自动缩小字号** —— 缩到下限 11px 为止。缩到 11px 仍比别的办法可读。
/// 3. **省略号截断** —— 缩到下限仍放不下（不可断行的长词，例如超长英文名）时截断，
///    完整文字**在中心显示**（见 RadialRingView 的中心读出）。
///
/// **这里曾经是"悬停时来回滚动"，已经去掉。** 两个问题都是它带来的：
/// - 悬停时文字会持续左右移动。菜单的用法是「看一眼就要点」，动的字反而读不到；
/// - 滚动要挂一条 Forever 的动画时钟，悬停期间每帧都要推进，而它换来的可读性为零。
/// 换成"截断 + 中心显示完整文字"之后，静止的字一个像素都不动，而完整文字仍然拿得到。
///
/// 另外**悬停不再改字重**。原来悬停会把字重从 Normal 换成 SemiBold，而 SemiBold
/// 更宽 —— 文字是按 Normal 的宽度居中的，换字重后没有重新测量，于是整段字会往右挪
/// 一点点、右边还可能被裁掉。这正是用户报的"鼠标悬停时会发生位移"。
/// 悬停的视觉反馈由扇区高亮与向外鼓出承担，已经足够明显，不需要再动文字。
/// </summary>
internal sealed class SectorLabel
{
    private const double MinimumFontSize = 11.0;

    private readonly Canvas _clipHost;
    private readonly TextBlock _text;
    private readonly double _availableWidth;
    private readonly double _availableHeight;
    private readonly string _fullText;
    private double _overflow;

    public SectorLabel(
        Canvas root, Point center, double labelRadius, double angleDeg,
        string text, double preferredFontSize, double maxWidth, double maxHeight,
        double lineHeightFactor, Brush foreground)
    {
        _availableWidth = maxWidth;
        _availableHeight = maxHeight;
        _fullText = text;

        _text = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            TextAlignment = TextAlignment.Center,
        };

        // 先按「折行」尝试：这是最可读的形态。
        _text.TextWrapping = TextWrapping.Wrap;
        _text.MaxWidth = maxWidth;
        _text.FontSize = preferredFontSize;
        Measure();

        // 折行后放不下（行数超限）或单个不可断行的词超宽，就逐级缩小字号。
        while (!FitsWithinBox() && _text.FontSize > MinimumFontSize)
        {
            _text.FontSize = Math.Max(MinimumFontSize, _text.FontSize - 1);
            Measure();
        }

        // 缩到下限仍然放不下 —— 就在这里截断。
        //
        // **注意判据是 FitsWithinBox()，不是"宽度超了"。** 原来这里只查宽度，
        // 于是"折行后行数超过两行"这种情况完全没被处理：宽度那一项因为 MaxWidth
        // 的夹持永远"刚好合适"，于是代码以为放得下，而实际的第三行往后被下面的
        // 裁剪框直接切掉了 —— 表现就是文字少了半截、还没有任何提示。
        // 这正是用户报的"文本较长时会被裁剪"。
        if (!FitsWithinBox())
        {
            _text.Text = FitWithEllipsis(text, maxWidth, maxHeight, _text.FontSize);
            Measure();

            // 溢出量按像素记录（诊断用）：同一段文字在不折行的前提下有多宽。
            var unconstrained = new TextBlock
            {
                Text = text,
                FontSize = _text.FontSize,
                FontFamily = _text.FontFamily,
                TextWrapping = TextWrapping.NoWrap,
            };
            unconstrained.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _overflow = Math.Max(0, unconstrained.DesiredSize.Width - maxWidth);
        }

        var boxWidth = _overflow > 0 ? maxWidth : Math.Min(maxWidth, _text.DesiredSize.Width);
        var boxHeight = Math.Min(maxHeight, _text.DesiredSize.Height);

        // 裁剪容器：没有它，折行高度超限时文字会画到相邻扇区上。
        _clipHost = new Canvas
        {
            Width = boxWidth,
            Height = boxHeight,
            Clip = new RectangleGeometry(new Rect(0, 0, boxWidth, boxHeight)),
        };
        _clipHost.Children.Add(_text);
        Canvas.SetLeft(_text, _overflow > 0 ? 0 : (boxWidth - _text.DesiredSize.Width) / 2);
        Canvas.SetTop(_text, 0);

        Position(root, center, labelRadius, angleDeg, boxWidth, boxHeight);
        _ = lineHeightFactor;
    }

    /// <summary>
    /// 是否被省略号截断（完整文字需要去中心看）。
    ///
    /// 判据是"显示的文本与原始文本不同"，不是"溢出像素 > 0" ——
    /// 后者只在"单个长词宽过扇区"时成立，而真正常见的是
    /// "折行后行数超限"，那种情况溢出像素可以是 0。
    /// 判据写错会让中心读出永远不触发，而这恰好是"文字被裁剪"的补救途径。
    /// </summary>
    public bool IsElided => !string.Equals(_text.Text, _fullText, StringComparison.Ordinal);

    /// <summary>被省略掉的字符数。供验证用。</summary>
    public int ElidedChars => Math.Max(0, _fullText.Length - _text.Text.Length);

    /// <summary>
    /// 最终内容是否真的装进了框里（宽和高都算）。
    ///
    /// 这是"没有被悄悄裁掉"的判据：任何标签都必须满足 `FitsBox || IsElided` ——
    /// 要么装得下，要么明确标出了截断。两者都不满足就是"少了一截还没有提示"，
    /// 而那正是最初的缺陷（三行文字被裁剪框切掉、宽度那一项看起来还完全正常）。
    /// </summary>
    public bool FitsBox =>
        _text.DesiredSize.Width <= _availableWidth + 0.5 &&
        _text.DesiredSize.Height <= _availableHeight + 0.5;

    /// <summary>达到最小字号后仍然横向溢出的像素数。</summary>
    public double OverflowPixels => _overflow;

    /// <summary>当前字号，供验证「自动缩小」是否生效。</summary>
    public double FontSize => _text.FontSize;

    /// <summary>是否发生了折行。</summary>
    public bool IsWrapped => _text.TextWrapping == TextWrapping.Wrap;

    /// <summary>文字实际占的宽度，供验证。</summary>
    public double TextWidth => _text.DesiredSize.Width;

    /// <summary>当前**显示**的文字（可能已截断）。</summary>
    public string Text => _text.Text;

    /// <summary>
    /// 原始完整文字。中心读出必须用这个而不是 <see cref="Text"/> ——
    /// 用后者的话，中心显示的是"被截断的那一段"，等于把同一个缺陷画了两遍。
    /// （这个错第一版就犯了：中心读出显示 "VeryLongApplicationName…"。）
    /// </summary>
    public string FullText => _fullText;

    /// <summary>
    /// 影响排版的属性快照。悬停前后比一遍：只要有一个变了，文字就可能移动 ——
    /// 这是"悬停不位移"这条能在程序里核对的前提。
    /// </summary>
    public (double Left, double FontSize, string Weight, bool Wrapping, double Width) LayoutSnapshot =>
        (Canvas.GetLeft(_clipHost), _text.FontSize, _text.FontWeight.ToString(),
         _text.TextWrapping == TextWrapping.Wrap, _text.DesiredSize.Width);

    /// <summary>标签上是否挂着动画时钟（不该有：滚动已经去掉）。</summary>
    public bool HasActiveAnimation => _text.HasAnimatedProperties;

    /// <summary>整体透明度。子弧展开时图标与文字要跟着扇区厚度一起淡入。</summary>
    public void SetOpacity(double opacity) => _clipHost.Opacity = opacity;

    /// <summary>当前透明度。入场要"记录 → 归零 → 原样恢复"，所以得读得到。</summary>
    public double Opacity => _clipHost.Opacity;

    /// <summary>
    /// 入场位移：从 (dx, dy) 滑到原位（子环"径向滑出"用）。
    ///
    /// 用 <c>RenderTransform</c> 而不是改 <c>Canvas.Left/Top</c> —— 后者每帧都会走一次布局，
    /// 而这只该是一次渲染变换。结束后变换停在 0（与"没有位移"等价）。
    /// </summary>
    public void SlideIn(double dx, double dy, TimeSpan duration, System.Windows.Media.Animation.KeySpline spline)
    {
        var transform = new TranslateTransform(dx, dy);
        _clipHost.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty,
            Design.MotionSplines.FromTo(dx, 0, duration, spline));
        transform.BeginAnimation(TranslateTransform.YProperty,
            Design.MotionSplines.FromTo(dy, 0, duration, spline));
    }

    /// <summary>清掉入场位移（离屏渲染与"立即展开"要的是终态）。</summary>
    public void ClearSlide()
    {
        if (_clipHost.RenderTransform is not TranslateTransform transform) return;

        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        _clipHost.RenderTransform = null;
    }

    /// <summary>从视觉树上摘掉自己。子弧收起时必须调用，否则不可见的标签会参与渲染遍历。</summary>
    public void Detach()
    {
        _clipHost.Children.Clear();

        if (_clipHost.Parent is Panel panel)
            panel.Children.Remove(_clipHost);
    }

    /// <summary>
    /// 折行装不下时，找出"能装进这个框的最长前缀 + 省略号"。
    ///
    /// 用二分而不是逐字减：构建期最多 6 次文字测量，而逐字减在长文本上是几十次。
    /// 只保留两行（不是退化成一行）—— 两行能容下的字比一行多一倍，
    /// 而完整文字在中心还能读全，所以这里的目标是"尽量多露一点"。
    /// </summary>
    private string FitWithEllipsis(string full, double maxWidth, double maxHeight, double fontSize)
    {
        var probe = new TextBlock
        {
            FontSize = fontSize,
            FontFamily = _text.FontFamily,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = maxWidth,
        };

        var low = 1;
        var high = full.Length;
        var best = 0;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            probe.Text = string.Concat(full.AsSpan(0, mid), "…");
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            if (probe.DesiredSize.Height <= maxHeight + 0.5 && probe.DesiredSize.Width <= maxWidth + 0.5)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best <= 0 ? "…" : string.Concat(full.AsSpan(0, best), "…");
    }

    private bool FitsWithinBox() =>
        _text.DesiredSize.Width <= _availableWidth + 0.5 &&
        _text.DesiredSize.Height <= _availableHeight + 0.5;

    private void Measure() =>
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

    private void Position(
        Canvas root, Point center, double labelRadius, double angleDeg, double boxWidth, double boxHeight)
    {
        var offset = Core.Geometry.Vec2.FromPolar(labelRadius, angleDeg);

        Canvas.SetLeft(_clipHost, center.X + offset.X - boxWidth / 2);
        Canvas.SetTop(_clipHost, center.Y + offset.Y - boxHeight / 2);

        root.Children.Add(_clipHost);
    }

    /// <summary>
    /// 把标签挪到新的角度（拖拽预览用）。半径与盒子尺寸都不变 ——
    /// 拖拽只在角向移动，标签的可用宽度、折行结果都不该变。
    /// </summary>
    public void MoveTo(Point center, double labelRadius, double angleDeg)
    {
        var offset = Core.Geometry.Vec2.FromPolar(labelRadius, angleDeg);

        Canvas.SetLeft(_clipHost, center.X + offset.X - _clipHost.Width / 2);
        Canvas.SetTop(_clipHost, center.Y + offset.Y - _clipHost.Height / 2);
    }

    /// <summary>
    /// 悬停状态变化。
    ///
    /// **只动透明度，不动字号与字重。** 动字重会触发一次文字重排（WPF 要按新字重
    /// 重新测量并重新居中），既让文字在悬停时挪位置，又给每次悬停加一笔不小的开销 ——
    /// 而悬停是轮盘上最频繁的动作。
    /// </summary>
    public void SetHover(bool hovered) => _text.Opacity = hovered ? 1.0 : 0.92;
}
