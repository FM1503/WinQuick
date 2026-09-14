using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinQuick.App.Design;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;

namespace WinQuick.App.Radial;

/// <summary>对某个扇区要做的编辑动作。</summary>
public enum SectorEditAction
{
    /// <summary>改名。<c>Label</c> 带新名称。</summary>
    Rename,

    /// <summary>移除该项。</summary>
    Remove,
}

/// <summary>
/// 扇区编辑面板 —— 右键某个扇区时在它旁边弹出的小面板（改名 / 移除）。
///
/// 为什么画在轮盘窗口**内部**，而不是用系统右键菜单或另开一个窗口：
/// 本项目最大的风险面一直是"多个顶层窗口之间争层级与焦点"（遮罩、轮盘、设置面板、
/// 文件对话框已经四个了）。菜单每多一个窗口，就多一次"看得见却点不动"的机会。
/// 画在窗口内则完全没有这个问题：它在同一张视觉树里，层级由 Canvas 的子元素顺序决定，
/// 点击也不涉及跨窗口的捕获与激活。
///
/// 面板宽度是固定的，改名时把内容换成输入框，不做尺寸变化 —— 尺寸不变就不必重新定位，
/// 也不会在光标附近跳动。
/// </summary>
public sealed partial class RadialRingView
{
    /// <summary>面板宽度（DIP）。够放四个汉字加图标，也不至于在小屏上没地方摆。</summary>
    private const double EditPanelWidth = 132;

    private Border? _editPanel;
    private int _editRootIndex = -1;
    private int _editChildIndex = -1;

    /// <summary>
    /// 用户在编辑面板上选了动作。参数是（第一层索引, 子项索引或 -1, 动作, 新名称）。
    /// 视图只报告"对哪一项做了什么"，不碰菜单数据 —— 它连菜单存在哪都不知道。
    /// </summary>
    public event EventHandler<(int RootIndex, int ChildIndex, SectorEditAction Action, string? Label)>?
        SectorEditRequested;

    /// <summary>编辑面板是否开着。开着时轮盘的悬停逻辑要让位（见 RadialWindow.EvaluateHover）。</summary>
    public bool IsEditPanelOpen => _editPanel is not null;

    /// <summary>
    /// 在某个扇区旁边弹出编辑面板。
    /// <paramref name="childIndex"/> 为 -1 表示第一层的项。
    /// </summary>
    public void ShowSectorEdit(int rootIndex, int childIndex)
    {
        DismissSectorEdit();

        var isChild = childIndex >= 0;

        // 摆在扇区外缘再往外一点：贴着环带，不压住图标与文字。
        var tokens = TokenStore.Current;
        var radius = tokens.Radius;
        var anchorRadius = isChild ? radius.Ring2Outer + 26 : radius.Ring1Outer + 26;
        var anchorAngle = isChild ? _childSpans[childIndex].CenterDeg : _spans[rootIndex].CenterDeg;
        var anchor = Vec2.FromPolar(anchorRadius, anchorAngle);

        _editRootIndex = rootIndex;
        _editChildIndex = childIndex;

        var panel = new Border
        {
            Width = EditPanelWidth,
            Background = new SolidColorBrush(Color.FromArgb(238, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = BuildEditButtons(rootIndex, childIndex, isChild),
        };

        _editPanel = panel;
        Root.Children.Add(panel);

        // 先量出真实尺寸再摆 —— 面板高度取决于内容行数，估出来的话会偏。
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = panel.DesiredSize;

        var size0 = tokens.Radius.WindowSize;
        const double margin = 8;
        var left = Math.Clamp(_center.X + anchor.X - size.Width / 2, margin, size0 - size.Width - margin);
        var top = Math.Clamp(_center.Y + anchor.Y - size.Height / 2, margin, size0 - size.Height - margin);

        Canvas.SetLeft(panel, left);
        Canvas.SetTop(panel, top);
    }

    /// <summary>收起编辑面板（不触发任何动作）。</summary>
    public void DismissSectorEdit()
    {
        if (_editPanel is null) return;

        Root.Children.Remove(_editPanel);
        _editPanel = null;
        _editRootIndex = -1;
        _editChildIndex = -1;
    }

    /// <summary>诊断用：编辑面板是否真的被加进了视觉树。</summary>
    internal bool EditPanelAttached => _editPanel is not null && Root.Children.Contains(_editPanel);

    /// <summary>诊断用：面板当前编辑的是哪一项（第一层索引, 子项索引或 -1）。</summary>
    internal (int RootIndex, int ChildIndex) EditTargetForProbe => (_editRootIndex, _editChildIndex);

    private StackPanel BuildEditButtons(int rootIndex, int childIndex, bool isChild)
    {
        var panel = new StackPanel();

        panel.Children.Add(MakeEditButton("\uE8AC", "重命名", () =>
            EnterRenameMode(rootIndex, childIndex, isChild)));

        panel.Children.Add(MakeEditButton("\uE74D", "移除", () =>
        {
            // 先收面板再报请求：菜单重建会把整棵视觉树换掉，
            // 留着面板的话它会暂时指向一个已经不存在的项。
            DismissSectorEdit();
            SectorEditRequested?.Invoke(this, (rootIndex, childIndex, SectorEditAction.Remove, null));
        }));

        return panel;
    }

    /// <summary>
    /// 换成输入框让用户改名。
    ///
    /// 名称会预填成当前名称并全选：用户多数时候是微调（比如去掉自动生成的长前缀），
    /// 全选后直接打字即可覆盖，不必先手动删。
    /// </summary>
    private void EnterRenameMode(int rootIndex, int childIndex, bool isChild)
    {
        if (_editPanel is null) return;

        var current = isChild
            ? ChildLabelOf(childIndex)
            : RootItemLabel(rootIndex);

        var box = new TextBox
        {
            Text = current,
            FontSize = 12.5,
            FontFamily = new FontFamily(TokenStore.Current.Typography.LabelFontFamily),
            Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(Color.FromArgb(255, 24, 24, 24)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CaretBrush = Brushes.White,
        };

        var stack = new StackPanel();
        stack.Children.Add(box);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        Button? confirm = null!;
        confirm = MakeEditButton("\uE73E", "确定", () => Commit());
        actions.Children.Add(confirm);
        actions.Children.Add(MakeEditButton("\uE711", "取消", () =>
        {
            DismissSectorEdit();
        }));
        stack.Children.Add(actions);

        // 输入框自己处理回车与 Esc。
        // Esc 必须在这里吃掉：轮盘窗口的 KeyDown 也监听 Esc（返回上一层 / 关闭覆盖层），
        // 不拦住的话用户按 Esc 想取消改名，结果整个覆盖层被关掉了。
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DismissSectorEdit();
            }
        };

        void Commit()
        {
            var text = box.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;   // 空名称没有意义，留在输入框里让用户看见

            DismissSectorEdit();
            SectorEditRequested?.Invoke(this, (rootIndex, childIndex, SectorEditAction.Rename, text));
        }

        _editPanel.Child = stack;

        // 焦点必须显式给输入框：轮盘窗口是前台窗口，但焦点默认还在窗口自己身上。
        box.Focus();
        box.SelectAll();
    }

    /// <summary>
    /// 面板里的一行按钮。
    ///
    /// 与操作条同源的问题、同源的做法：Button.Click 依赖鼠标捕获，而捕获只有前台窗口能做。
    /// 这里虽然通常是前台窗口，但"通常"不足以支撑一个点不动就没有任何反馈的交互 ——
    /// 改在隧道的 Preview 路径上处理，不依赖捕获。
    /// </summary>
    private static Button MakeEditButton(string glyph, string text, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };

        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = Brushes.White,
        });

        content.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(TokenStore.Current.Typography.LabelFontFamily),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
        });

        var button = new Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(7, 5, 7, 5),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Template = OverlayButton.BuildFlatTemplate(),
        };

        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return button;
    }

    /// <summary>某个子项的完整标签。</summary>
    private string ChildLabelOf(int childIndex) =>
        _editRootIndex >= 0
        && _editRootIndex < _menu.Items.Count
        && childIndex < _menu.Items[_editRootIndex].Children.Count
            ? _menu.Items[_editRootIndex].Children[childIndex].Label
            : "";
}
