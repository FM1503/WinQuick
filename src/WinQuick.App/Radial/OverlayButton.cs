using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinQuick.App.Radial;

/// <summary>
/// 覆盖层里那些"画在窗口内部的小按钮"的共用样式。
///
/// 目前有两个地方用（左上角操作条、扇区编辑面板），它们的观感必须一致 ——
/// 都浮在同一层半透明遮罩上，样式分叉会显得像两个程序。
/// </summary>
internal static class OverlayButton
{
    /// <summary>
    /// 无边框按钮模板：默认透明，悬停时给一层浅色底。
    ///
    /// 用 ControlTemplate 而不是去改系统按钮的样式：系统主题的按钮在这些深色浮层上
    /// 会带出自己的渐变与边框，和遮罩底下的画面打架。
    /// </summary>
    public static ControlTemplate BuildFlatTemplate()
    {
        var template = new ControlTemplate(typeof(Button));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Button.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(56, 255, 255, 255))));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Button.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(96, 255, 255, 255))));
        template.Triggers.Add(pressed);

        return template;
    }
}
