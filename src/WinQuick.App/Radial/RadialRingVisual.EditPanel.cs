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
/// 扇区上的就地面板 —— 右键扇区出来的「改名 / 移除」，以及点「＋」出来的
/// 「添加程序 / 新建分组」。
///
/// 两块面板合用一个实现，因为形态完全一样（贴着扇区、宽度固定、越界就在窗内钳制），
/// 差别只在行内容。分成两份的话，锚点算法会开始漂移。
///
/// 为什么画在轮盘窗口**内部**，而不是用系统右键菜单或另开一个窗口：
/// 本项目最大的风险面一直是"多个顶层窗口之间争层级与焦点"（遮罩、轮盘、操作条、
/// 设置面板、文件对话框已经五个了）。菜单每多一个窗口，就多一次"看得见却点不动"
/// 的机会。画在窗口内则完全没有这个问题：它在同一张视觉树里，层级由 Canvas 的
/// 子元素顺序决定，点击也不涉及跨窗口的捕获与激活。
///
/// 面板宽度固定，改名/命名时把内容换成输入框，不做尺寸变化 ——
/// 尺寸不变就不必重新定位，也不会在光标附近跳动。
/// </summary>
public sealed partial class RadialRingView
{
    /// <summary>面板宽度（DIP）。够放一行动作加图标，也能容纳输入框里的中文名。</summary>
    private const double PanelWidth = 156;

    private Border? _panel;
    private int _editRootIndex = -1;
    private int _editChildIndex = -1;

    /// <summary>面板各行按钮的文案，供探针核对"该给的选项给全了"。</summary>
    private readonly List<string> _panelRowLabels = [];

    /// <summary>面板各行按钮的动作。探针通过 <see cref="InvokePanelRowForProbe"/> 走这里。</summary>
    private readonly List<Action> _panelRowActions = [];

    /// <summary>当前展开的是哪一块面板。</summary>
    private PanelKind _panelKind = PanelKind.None;

    internal enum PanelKind
    {
        None = 0,
        SectorEdit = 1,
        AddChoice = 2,
        TextInput = 3,
    }

    /// <summary>
    /// 用户在「＋」的选择面板里选了"添加程序…"（或子环的「＋」被直接点击）。
    /// 参数是父项索引，-1 表示加到第一层。
    /// </summary>
    public event EventHandler<int>? AddItemRequested;

    /// <summary>
    /// 用户在扇区编辑面板上选了动作。参数是（第一层索引, 子项索引或 -1, 动作, 新名称）。
    /// 视图只报告"对哪一项做了什么"，不碰菜单数据 —— 它连菜单存在哪都不知道。
    /// </summary>
    public event EventHandler<(int RootIndex, int ChildIndex, SectorEditAction Action, string? Label)>?
        SectorEditRequested;

    /// <summary>
    /// 用户要求新建一个分组（带子菜单的项）。参数是（父项索引, 分组名称）。
    /// 上层还要再问一次"第一项是什么" —— 分组必须有子项，空分组是个点不动的死扇区。
    /// </summary>
    public event EventHandler<(int ParentIndex, string Label)>? AddGroupRequested;

    /// <summary>就地面板是否开着。开着时轮盘的悬停逻辑要让位（见 RadialWindow.EvaluateHover）。</summary>
    public bool IsPanelOpen => _panel is not null;

    /// <summary>诊断用：面板是否真的被加进了视觉树。</summary>
    internal bool PanelAttached => _panel is not null && Root.Children.Contains(_panel);

    /// <summary>诊断用：当前是哪一块面板。</summary>
    internal PanelKind PanelKindForProbe => _panelKind;

    /// <summary>诊断用：面板各行的文案。</summary>
    internal IReadOnlyList<string> PanelRowLabelsForProbe => _panelRowLabels;

    /// <summary>
    /// 诊断用：把面板输入框里的文字改成给定值并点「确定」。
    /// 走的是「确定」按钮同一个提交路径。
    /// </summary>
    internal void ConfirmPanelTextForProbe(string text)
    {
        if (_panel?.Child is not StackPanel stack || stack.Children.Count < 2) return;
        if (stack.Children[1] is not TextBox box) return;

        box.Text = text;
        InvokePanelRowForProbe(0);      // 0 = 确定
    }

    /// <summary>诊断用：面板当前针对的是哪一项（第一层索引, 子项索引或 -1）。</summary>
    internal (int RootIndex, int ChildIndex) EditTargetForProbe => (_editRootIndex, _editChildIndex);

    /// <summary>
    /// 诊断用：点第 <paramref name="index"/> 行。
    /// 走的是按钮同一个委托 —— 不是探针自己重判一遍该做什么。
    /// </summary>
    internal void InvokePanelRowForProbe(int index)
    {
        if (index < 0 || index >= _panelRowActions.Count) return;
        _panelRowActions[index]();
    }

    // ---------------------------------------------------------------- 两块面板

    /// <summary>
    /// 在某个扇区旁边弹出编辑面板。
    /// <paramref name="childIndex"/> 为 -1 表示第一层的项。
    /// </summary>
    public void ShowSectorEdit(int rootIndex, int childIndex)
    {
        var isChild = childIndex >= 0;

        _editRootIndex = rootIndex;
        _editChildIndex = childIndex;

        var rows = new List<(string Glyph, string Text, Action OnClick)>
        {
            ("\uE8AC", "重命名", () => EnterTextMode(
                title: "新名称",
                initial: isChild ? ChildLabelOf(childIndex) : RootItemLabel(rootIndex),
                hint: null,
                onCommit: text => RaiseRename(rootIndex, childIndex, text))),

            ("\uE74D", "移除", () => RaiseRemove(rootIndex, childIndex)),
        };

        ShowPanel(
            kind: PanelKind.SectorEdit,
            anchorAngle: isChild ? _childSpans[childIndex].CenterDeg : _spans[rootIndex].CenterDeg,
            anchorRadius: isChild ? TokenStore.Current.Radius.Ring2Outer + 26
                                  : TokenStore.Current.Radius.Ring1Outer + 26,
            rows);
    }

    /// <summary>
    /// 点「＋」时的选择面板。
    ///
    /// 原来这里直接弹文件浏览器，于是**没有任何途径新建带子项的项** —— 用户报的就是这条：
    /// 想加一个像"开发工具"那样的分组，做不到。
    ///
    /// 分组只在第一层建：子环里再放分组就是第三层，而当前只支持两层
    /// （方案第七节：8 扇区下超过 2 层错误率对专家都不可接受）。
    /// 所以子环的「＋」不弹这块面板，直接进文件浏览器 —— 那也给不出两个选项。
    /// </summary>
    public void ShowAddChoice(int addSectorIndex)
    {
        // 「＋」扇区的索引是**伪索引**：它等于菜单项数，不是任何一项的位置。
        // 报给上层时必须翻译成"第一层"的 -1 —— 直接透传的话，上层会拿到一个
        // 越界的父项索引。（这条是被 --add-sector-probe 抓到的：它报 parent=8。）
        var parentIndex = IsAddSector(addSectorIndex) ? -1 : addSectorIndex;

        _editRootIndex = addSectorIndex;
        _editChildIndex = -1;

        var rows = new List<(string Glyph, string Text, Action OnClick)>
        {
            ("\uE8B7", "添加程序…", () =>
            {
                DismissPanel();
                AddItemRequested?.Invoke(this, parentIndex);
            }),

            ("\uE8F4", "新建分组…", () => EnterTextMode(
                title: "分组名称",
                initial: "新建分组",
                hint: "下一步选一个程序作为它的第一项。之后把鼠标移到这个分组上，"
                    + "在它的子环里点「＋」继续添加。",
                onCommit: text =>
                {
                    DismissPanel();
                    AddGroupRequested?.Invoke(this, (parentIndex, text));
                })),
        };

        ShowPanel(
            kind: PanelKind.AddChoice,
            anchorAngle: _spans[addSectorIndex].CenterDeg,
            anchorRadius: TokenStore.Current.Radius.Ring1Outer + 26,
            rows);
    }

    /// <summary>收起面板（不触发任何动作）。</summary>
    public void DismissPanel()
    {
        if (_panel is null) return;

        Root.Children.Remove(_panel);
        _panel = null;
        _panelKind = PanelKind.None;
        _panelRowLabels.Clear();
        _panelRowActions.Clear();
        _editRootIndex = -1;
        _editChildIndex = -1;
    }

    /// <summary>
    /// 搭出面板并摆到锚点旁。锚点取"扇区外缘再往外一点"：贴着环带，不压住图标与文字。
    /// </summary>
    private void ShowPanel(
        PanelKind kind, double anchorAngle, double anchorRadius,
        IReadOnlyList<(string Glyph, string Text, Action OnClick)> rows)
    {
        DismissPanel();
        _panelKind = kind;

        var content = new StackPanel();
        foreach (var (glyph, text, onClick) in rows)
            content.Children.Add(MakePanelButton(glyph, text, onClick));

        var panel = new Border
        {
            Width = PanelWidth,
            Background = new SolidColorBrush(Color.FromArgb(238, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = content,
        };

        _panel = panel;
        Root.Children.Add(panel);

        PositionPanel(anchorAngle, anchorRadius);
    }

    /// <summary>按当前内容的真实尺寸重新摆一次面板，并钳制在窗口内。</summary>
    private void PositionPanel(double anchorAngle, double anchorRadius)
    {
        if (_panel is not { } panel) return;

        var anchor = Vec2.FromPolar(anchorRadius, anchorAngle);

        // 先量出真实尺寸再摆 —— 面板高度取决于内容行数，估出来的话会偏。
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = panel.DesiredSize;

        var windowSize = TokenStore.Current.Radius.WindowSize;
        const double margin = 8;
        var left = Math.Clamp(_center.X + anchor.X - size.Width / 2, margin, windowSize - size.Width - margin);
        var top = Math.Clamp(_center.Y + anchor.Y - size.Height / 2, margin, windowSize - size.Height - margin);

        Canvas.SetLeft(panel, left);
        Canvas.SetTop(panel, top);
    }

    // ---------------------------------------------------------------- 面板里的行

    private Button MakePanelButton(string glyph, string text, Action onClick)
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

        _panelRowLabels.Add(text);
        _panelRowActions.Add(onClick);

        // 与操作条同源的问题、同源的做法：Button.Click 依赖鼠标捕获，而捕获只有前台窗口
        // 能做。这里虽然通常就是前台窗口，但"通常"不足以支撑一个点不动就没有任何反馈的
        // 交互 —— 改在隧道的 Preview 路径上处理，不依赖捕获。
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return button;
    }

    /// <summary>
    /// 把面板内容换成一块输入框（改名、给分组命名都用它）。
    ///
    /// 文本预填并全选：用户多数时候是微调（比如去掉自动生成的长前缀），
    /// 全选后直接打字即可覆盖，不必先手动删。
    /// </summary>
    private void EnterTextMode(string title, string initial, string? hint, Action<string> onCommit)
    {
        if (_panel is not { } panel) return;

        _panelKind = PanelKind.TextInput;

        // 行文案与动作都换成输入模式自己的 —— 不清掉的话探针读到的还是上一块面板的。
        _panelRowLabels.Clear();
        _panelRowActions.Clear();

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11.5,
            Margin = new Thickness(7, 2, 7, 4),
            Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            FontFamily = new FontFamily(TokenStore.Current.Typography.LabelFontFamily),
        });

        var box = new TextBox
        {
            Text = initial,
            FontSize = 12.5,
            FontFamily = new FontFamily(TokenStore.Current.Typography.LabelFontFamily),
            Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(Color.FromArgb(255, 24, 24, 24)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CaretBrush = Brushes.White,
        };
        stack.Children.Add(box);

        if (hint is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(7, 5, 7, 2),
                Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                FontFamily = new FontFamily(TokenStore.Current.Typography.LabelFontFamily),
            });
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
        };
        actions.Children.Add(MakePanelButton("\uE73E", "确定", Commit));
        actions.Children.Add(MakePanelButton("\uE711", "取消", DismissPanel));
        stack.Children.Add(actions);

        // 输入框自己处理回车与 Esc。
        // Esc 必须在这里吃掉：轮盘窗口的 KeyDown 也监听 Esc（返回上一层 / 关闭覆盖层），
        // 不拦住的话用户按 Esc 想取消输入，结果整个覆盖层被关掉了。
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
                DismissPanel();
            }
        };

        void Commit()
        {
            var text = box.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;   // 空名称没有意义，留在输入框里让用户看见
            onCommit(text);
        }

        panel.Child = stack;

        // 内容换过之后高度会变，按同一锚点重摆一次。
        PositionPanel(
            anchorAngle: _editChildIndex >= 0 && _editChildIndex < _childSpans.Length
                ? _childSpans[_editChildIndex].CenterDeg
                : _editRootIndex >= 0 && _editRootIndex < _spans.Length
                    ? _spans[_editRootIndex].CenterDeg
                    : 0,
            anchorRadius: _editChildIndex >= 0
                ? TokenStore.Current.Radius.Ring2Outer + 26
                : TokenStore.Current.Radius.Ring1Outer + 26);

        // 焦点必须显式给输入框：轮盘窗口是前台窗口，但焦点默认还在窗口自己身上。
        box.Focus();
        box.SelectAll();
    }

    // ---------------------------------------------------------------- 上报

    private void RaiseRename(int rootIndex, int childIndex, string label)
    {
        DismissPanel();
        SectorEditRequested?.Invoke(this, (rootIndex, childIndex, SectorEditAction.Rename, label));
    }

    private void RaiseRemove(int rootIndex, int childIndex)
    {
        // 先收面板再报请求：菜单重建会把整棵视觉树换掉，
        // 留着面板的话它会暂时指向一个已经不存在的项。
        DismissPanel();
        SectorEditRequested?.Invoke(this, (rootIndex, childIndex, SectorEditAction.Remove, null));
    }

    /// <summary>某个子项的完整标签。</summary>
    private string ChildLabelOf(int childIndex) =>
        _editRootIndex >= 0
        && _editRootIndex < _menu.Items.Count
        && childIndex < _menu.Items[_editRootIndex].Children.Count
            ? _menu.Items[_editRootIndex].Children[childIndex].Label
            : "";
}
