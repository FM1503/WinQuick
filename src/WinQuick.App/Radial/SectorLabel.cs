using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinQuick.App.Radial;

/// <summary>
/// 扇区标签。处理「标签放不进扇区」这件事。
///
/// 四级策略，从最可读到最不得已：
/// 1. **折行显示** —— 折成最多两行。宽度直接减半，这是同时解决「长文本显示不全」
///    与「3 点/9 点方向文字压到图标」两个问题的关键。
/// 2. **自动缩小字号** —— 缩到下限 11px 为止。缩到 11px 仍比滚动可读，
///    因为滚动文字一眼看不完，而菜单是「看一眼就要点」的东西。
/// 3. **悬停时滚动** —— 只有不可断行的长单词（例如超长英文名）才会走到这一级。
/// 中文可以任意位置断行，所以中英混排的菜单基本用不到。
///
/// 为什么不做成无条件滚动：饼菜单的节奏是「看准了立刻点」，滚动文字在那 200ms 里读不完。
/// 而且静止状态下所有标签一起滚会很吵。滚动用**往返**而不是单向循环 ——
/// 单向循环会让文字跳回起点，观感像闪烁。
/// </summary>
internal sealed class SectorLabel
{
    private const double MinimumFontSize = 11.0;

    /// <summary>滚动速度（像素/秒）。太快读不清，太慢要等。</summary>
    private const double ScrollPixelsPerSecond = 28.0;

    private readonly Canvas _clipHost;
    private readonly TextBlock _text;
    private readonly double _availableWidth;
    private readonly double _availableHeight;
    private double _overflow;

    public SectorLabel(
        Canvas root, Point center, double labelRadius, double angleDeg,
        string text, double preferredFontSize, double maxWidth, double maxHeight,
        double lineHeightFactor, Brush foreground)
    {
        _availableWidth = maxWidth;
        _availableHeight = maxHeight;

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

        // 缩到下限仍然横向放不下 —— 说明遇到了不可断行的长词（中文不会走到这里，
        // 因为 WPF 可以在任意汉字间断行）。
        if (_text.DesiredSize.Width > maxWidth + 0.5)
        {
            _text.TextWrapping = TextWrapping.NoWrap;
            _text.MaxWidth = double.PositiveInfinity;
            Measure();
            _overflow = Math.Max(0, _text.DesiredSize.Width - maxWidth);
        }

        var boxWidth = _overflow > 0 ? maxWidth : Math.Min(maxWidth, _text.DesiredSize.Width);
        var boxHeight = Math.Min(maxHeight, _text.DesiredSize.Height);

        // 裁剪容器：没有它，折行高度超限或滚动时文字会画到相邻扇区上。
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

    /// <summary>是否需要滚动才能看全。供验证用。</summary>
    public bool NeedsScroll => _overflow > 0.5;

    /// <summary>达到最小字号后仍然横向溢出的像素数。</summary>
    public double OverflowPixels => _overflow;

    /// <summary>当前字号，供验证「自动缩小」是否生效。</summary>
    public double FontSize => _text.FontSize;

    /// <summary>是否发生了折行。</summary>
    public bool IsWrapped => _text.TextWrapping == TextWrapping.Wrap;

    /// <summary>文字实际占的宽度，供验证。</summary>
    public double TextWidth => _text.DesiredSize.Width;

    /// <summary>原始完整文字（未截断）。</summary>
    public string Text => _text.Text;

    /// <summary>整体透明度。子弧展开时图标与文字要跟着扇区厚度一起淡入。</summary>
    public void SetOpacity(double opacity) => _clipHost.Opacity = opacity;

    /// <summary>从视觉树上摘掉自己。子弧收起时必须调用，否则不可见的标签会参与渲染遍历。</summary>
    public void Detach()
    {
        StopScroll();
        _clipHost.Children.Clear();

        if (_clipHost.Parent is System.Windows.Controls.Panel panel)
            panel.Children.Remove(_clipHost);
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
    /// 悬停状态变化。只有真的放不下时才启动滚动，且只在悬停时滚 ——
    /// 静止状态下所有标签一起滚动会很吵，也破坏了「扫一眼就能读」这个前提。
    /// </summary>
    public void SetHover(bool hovered)
    {
        _text.FontWeight = hovered ? FontWeights.SemiBold : FontWeights.Normal;
        _text.Opacity = hovered ? 1.0 : 0.92;

        if (!NeedsScroll) return;

        if (hovered) StartScroll();
        else StopScroll();
    }

    private void StartScroll()
    {
        var scrollMs = _overflow / ScrollPixelsPerSecond * 1000.0;

        // 往返滚动而不是单向循环：短溢出时往返更好读，
        // 单向循环会让文字"跳"回起点，观感像闪烁。
        var animation = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };

        void At(double ms, double value) =>
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(
                value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));

        const double leadInMs = 500;
        const double holdMs = 700;

        At(0, 0);
        At(leadInMs, 0);                          // 先停一下，让用户看清开头
        At(leadInMs + scrollMs, -_overflow);      // 滚到露出结尾
        At(leadInMs + scrollMs + holdMs, -_overflow);
        At(leadInMs + scrollMs * 2 + holdMs, 0);  // 滚回开头

        _text.BeginAnimation(Canvas.LeftProperty, animation);
    }

    private void StopScroll()
    {
        _text.BeginAnimation(Canvas.LeftProperty, null);
        Canvas.SetLeft(_text, 0);
    }
}
