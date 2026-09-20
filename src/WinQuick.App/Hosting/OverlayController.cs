using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using WinQuick.App.Radial;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Hotkey;
using WinQuick.Core.Motion;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Hosting;

/// <summary>
/// 覆盖层的编排：遮罩层 + 轮盘层的显示/隐藏、焦点保存还原、置顶维持、显示器变更重建。
///
/// 窗口在启动时就全部创建好并保持隐藏（<see cref="Prewarm"/>）。这是刻意的：
/// Kando 在 1.8.0 引入懒初始化后用户抱怨"首次打开明显变慢"，宁可退回旧版本。
/// 预热让热键回调只需要 Show + 启动动画，这是达成「按下到可见 &lt;100ms」的前提。
/// </summary>
public sealed class OverlayController : IDisposable
{
    /// <summary>
    /// 置顶重申间隔。WS_EX_TOPMOST 只保证压住非 topmost 窗口，
    /// 不高于其他 topmost（任务管理器、其他常驻置顶工具都在同一个 band 里）。
    /// 没有文档化的更高层级，所以只能周期性重申 —— 这是行业惯例做法。
    /// </summary>
    private static readonly TimeSpan TopmostReassertInterval = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// 系统对话框弹出期间用的重申周期。
    /// 对话框的 topmost 只能靠事后补样式得到，越早补上越好。
    /// </summary>
    private static readonly TimeSpan FastTopmostReassertInterval = TimeSpan.FromMilliseconds(100);

    private void SetTopmostInterval(TimeSpan interval)
    {
        _topmostTimer.Interval = interval;

        // 时钟在跑时改 Interval 不会立刻重新计时，重启一次让新周期立即生效。
        if (_topmostTimer.IsEnabled)
        {
            _topmostTimer.Stop();
            _topmostTimer.Start();
        }

        // 立刻重申一次，不必等新周期。
        ReassertTopmost();
    }

    private readonly List<RawDimWindow> _dims = [];
    private RadialWindow _radial;   // 菜单热重载时会整体替换，所以不是 readonly
    private readonly OverlayActionBar _actionBar = new();

    // 设置面板按需创建：设置是低频操作，为它常驻一个窗口不值得。
    // 首次打开后保留，避免反复创建。
    private SettingsPanel? _settingsPanel;

    /// <summary>皮肤选择面板。与设置面板同一档（窗口属性、拖动、层级、入场动画全部一致）。</summary>
    private SkinPanel? _skinPanel;

    /// <summary>Hide/Show 往返之前设置面板是否打开着，用于恢复。</summary>
    private bool _settingsWasOpen;
    private bool _skinWasOpen;

    /// <summary>
    /// 本次收起**不要**归还焦点。只有一个场景会置位：检测到用户换了虚拟桌面而主动收起
    /// （原窗口在别的桌面上，SetForegroundWindow 过去会把用户拽回那个桌面）。
    /// </summary>
    private bool _suppressFocusRestore;

    /// <summary>本次 Show 是否要保留轮盘原有位置（菜单重建时为真）。</summary>
    private (int X, int Y, int Width, int Height)? _preserveRadialPosition;

    /// <summary>
    /// 我们自己的窗口层级栈。定义一次，之后所有置顶重申都走它。
    ///
    /// 之前是每个窗口各自调一个 ReassertTopmost，而那些实现都是空操作
    /// （传了 SWP_NOZORDER，让插入位置被忽略）。集中定义还有一个好处：
    /// 层级关系变成一处可读、可验证的声明，而不是散在四个文件里的隐式约定。
    ///
    /// 从上到下声明；重申时自动反序执行，保证栈顶最后插入、确实在最前。
    /// </summary>
    private readonly ZOrderManager _zOrder = new();
    private readonly ForegroundFocus _focus = new();
    private readonly DispatcherTimer _topmostTimer;

    /// <summary>
    /// 休眠后的空闲内存清理（延迟触发）。用户要求："关闭轮盘到后台常驻时自动清理自身占用"。
    /// 为什么延迟：工作集被换出后下次唤出要重新缺页，延迟覆盖"关掉马上又打开"的常见情形。
    /// </summary>
    private readonly DispatcherTimer _idleTrimTimer;

    /// <summary>退场动画播完后才真正隐藏窗口。动画不影响动作执行，只影响"什么时候看不见"。</summary>
    private readonly DispatcherTimer _exitTimer;
    private HwndSource? _radialSource;

    private bool _disposed;

    public OverlayController(MenuSpec menu)
    {
        _radial = CreateRadialWindow(menu);

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TopmostReassertInterval,
        };
        // 定时器先做廉价检查，只在真的需要时才重申 —— 无条件重申会让分层窗口
        // 持续重新合成，表现为闪烁（用户报的现象）。
        _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(
                Math.Max(1000, TokenStore.Current.Interaction.IdleTrimDelayMs)),
        };
        _idleTrimTimer.Tick += (_, _) =>
        {
            _idleTrimTimer.Stop();

            // 期间又被唤出了就不清理（那会把刚用上的页换出去）。
            if (IsVisible) return;
            if (!_settings.Behavior.TrimMemoryWhenIdle) return;

            Diagnostics.MemoryTrim.Run();
        };

        _topmostTimer.Tick += (_, _) =>
        {
            // 用户把桌面切走了就收起覆盖层 —— 否则那些窗口留在原来那个桌面上，
            // 既看不见也关不掉（Esc 收不到），却仍然占着"已显示"的状态。
            HideIfDesktopChanged();

            if (_zOrder.NeedsReassert()) ReassertTopmost();
        };

        // 系统对话框必须在最顶层参与重申。
        //
        // 为什么不是"给对话框加个 topmost 就完事"：我们的 800ms 置顶重申会把
        // 自己的窗口逐个插到 topmost 层最前面，那样反而会把对话框挤到我们后面。
        // 让它成为层级栈的顶层，重申结束时它才确实在最前面。
        _zOrder.AddLayer("系统对话框", () => FindOwnedSystemDialog() is { } dialog ? [dialog] : []);

        // 层级从上到下声明。注意这里**必须**与视觉预期一致：
        // 设置面板盖住操作条，操作条盖住轮盘，轮盘盖住遮罩。
        //
        // 皮肤面板紧跟设置面板：它们是同一类东西（轮盘旁边的一块面板，同一套窗口属性、
        // 同一份拖动实现），层级也同一档。实际不会同时在场（打开皮肤会收起设置），
        // 两层都留着只是为了"谁在哪一档"这句话在代码里是显式写明的。
        _zOrder.AddLayer("设置面板", () => _settingsPanel is { IsVisible: true } ? [_settingsPanel.HandleValue] : []);
        _zOrder.AddLayer("皮肤面板", () => _skinPanel is { IsVisible: true } ? [_skinPanel.HandleValue] : []);
        _zOrder.AddLayer("操作条", () => _actionBar.IsVisible ? [_actionBar.HandleValue] : []);
        // 看覆盖层自己的逻辑状态，**不看 _radial.IsVisible** ——
        // 休眠（Park）期间窗口仍是"可见"的（只是停在屏幕外），按可见性判断会把它
        // 一直算进 z 序栈里。
        _zOrder.AddLayer("轮盘", () => IsVisible ? [_radial.HandleValue] : []);
        _zOrder.AddLayer("遮罩", () => _dims.Where(d => d.IsValid).Select(d => d.HandleValue).ToList());

        // 设置改为开在覆盖层会话里（面板与轮盘同时在场），所以这里不再收掉覆盖层 ——
        // 收掉的话"改动立刻看到效果"就无从谈起了。
        _actionBar.SettingsRequested += (_, _) =>
        {
            if (IsSettingsOpen) CloseSettings();
            else OpenSettings();
        };

        _actionBar.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _exitTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _exitTimer.Tick += (_, _) =>
        {
            _exitTimer.Stop();
            FinishHide();
        };
    }

    private RadialWindow CreateRadialWindow(MenuSpec menu)
    {
        var window = new RadialWindow(menu);
        window.DismissRequested += (_, _) => Hide();
        window.ItemActivated += (_, selection) => ItemActivated?.Invoke(this, selection);
        window.AddItemRequested += (_, parentIndex) => AddItemRequested?.Invoke(this, parentIndex);
        window.SectorEditRequested += (_, request) => SectorEditRequested?.Invoke(this, request);
        window.AddGroupRequested += (_, request) => AddGroupRequested?.Invoke(this, request);
        window.ReorderRequested += (_, request) => ReorderRequested?.Invoke(this, request);
        window.FocusLost += (_, _) => OnRadialFocusLost();
        return window;
    }

    /// <summary>
    /// 换用另一份菜单。
    ///
    /// **就地替换视觉树，不重建窗口。** 早先这里是"销毁窗口 + 新建 + Show"，
    /// 效果等于把整个覆盖层重开一次：轮盘消失、再播一遍入场动画长出来，
    /// 用户每改一次快捷选项都要看一次这个。
    ///
    /// 就地替换省掉的不只是动画：窗口句柄不变 → 不重新插入 topmost band、
    /// 不重新抢焦点、不必重新定位，也就没有"重建过程里顺序错了导致点不到"的机会。
    /// 位置保留不再需要显式处理 —— 窗口压根没动过。
    /// </summary>
    public void RebuildForMenu(MenuSpec menu)
    {
        if (_disposed) return;

        // **不传 editMode**：编辑态是独立状态，不能在每次菜单重建时按"面板是否开着"重算 ——
        // 长按进入编辑态时面板并没开，重算会把编辑态算没（而重排后正好会重建一次菜单）。
        _radial.ApplyMenu(menu);

        // 菜单重建会把视觉树换成"静止的完整圆盘"。若此刻覆盖层正处在休眠（停放）状态，
        // 那份完整圆盘就留在窗口里了 —— 下次唤出的第一帧会先把它露出来
        //（与"首次启动后先渲染终态"是同一个根因：分层窗口显示的是上一次提交的位图）。
        // 用户从资源管理器右键「加一项」正是在休眠中触发重建的，这条路径必须堵上。
        // 约定：**停放中的窗口里必须是空的环。**
        if (!IsVisible) _radial.ResetForDormancy();
    }

    /// <summary>
    /// 弹「选一个程序加进菜单」的文件对话框；用户取消时返回 null。
    ///
    /// 对话框期间必须同时做两件事，缺任何一件都会出错：
    /// 提高置顶重申频率（对话框不会自动变成 topmost，不重申就会被遮罩盖住一闪），
    /// 以及屏蔽"失去焦点就关闭"（对话框夺走前台焦点，否则会顺手把覆盖层关掉）。
    /// </summary>
    public string? PickExecutableForMenu()
    {
        if (_disposed) return null;

        _modalDialogDepth++;
        SetTopmostInterval(FastTopmostReassertInterval);
        try
        {
            return ProgramPicker.Pick(_radial);
        }
        finally
        {
            _modalDialogDepth--;
            SetTopmostInterval(TopmostReassertInterval);
        }
    }

    /// <summary>等第一帧合成过去之后再放入场动画。</summary>
    private void StartEntranceAfterFirstFrame()
    {
        // 回调**必须挂在一个字段上**：CompositionTarget.Rendering 是静态事件，
        // 用局部闭包挂上去、又没有渲染发生的话，它会一直留在那儿（这个坑本项目踩过）。
        if (_pendingEntranceStart is null)
        {
            _pendingEntranceStart = (_, _) =>
            {
                if (_pendingEntranceStart is null) return;
                CompositionTarget.Rendering -= _pendingEntranceStart;
                _pendingEntranceStart = null;

                if (!IsVisible) return;    // 期间被关掉了就别再放了

                // 排到下一批：这样动画的第一帧落在**下一个**渲染帧上，
                // 而不是与刚过去的那一帧（映射那一帧）挤在一起。
                _radial.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded, new Action(() => _radial.PlayEntrance()));
            };
        }

        CompositionTarget.Rendering += _pendingEntranceStart;
    }

    private EventHandler? _pendingEntranceStart;

    /// <summary>同时打开的系统对话框层数（正常只可能是 0 或 1）。</summary>
    private int _modalDialogDepth;

    /// <summary>遮罩不透明度的用户覆盖值（0–1）。null 表示沿用设计基线。</summary>
    public double? DimOpacityOverride { get; private set; }

    /// <summary>
    /// 更新遮罩不透明度。已建好的遮罩窗口用的是旧的 alpha，必须重建才会生效。
    /// 只重建遮罩窗口，不动轮盘 —— 后者重建的代价大得多（要重画视觉树）。
    /// </summary>
    public void ApplyDimOpacity(double? opacity)
    {
        if (_disposed) return;
        if (DimOpacityOverride == opacity) return;

        DimOpacityOverride = opacity;

        // **就地更新 alpha，不重建窗口。**
        //
        // 之前这里是"销毁整屏遮罩窗口 + 重建"，每次滑动滑块都会让整个屏幕闪一下 ——
        // 用户报的"设置界面一直在闪烁"里就有这一份。
        // 改分层窗口的 alpha 只是个属性更新，系统重新合成一次即可，观感是连续变化的。
        var alpha = ToAlpha(opacity);
        foreach (var dim in _dims) dim.SetAlpha(alpha);
    }

    private static byte ToAlpha(double? opacity) =>
        opacity is { } value
            ? (byte)Math.Clamp(Math.Round(value * 255), 0, 255)
            : RawDimWindow.DefaultAlpha;

    public bool IsVisible { get; private set; }

    /// <summary>诊断用：遮罩窗口句柄。供 --overlay-probe 核对窗口真实状态，而不是相信自己的布尔量。</summary>
    internal IReadOnlyList<nint> DimHandles => _dims.Select(d => d.HandleValue).ToList();

    /// <summary>诊断用：轮盘窗口句柄。</summary>
    internal nint RadialHandle => _radial.HandleValue;

    /// <summary>诊断用：子弧是否展开。</summary>
    internal bool IsChildArcExpanded => _radial.IsChildArcVisible;

    /// <summary>诊断用：子弧展开进度。</summary>
    internal double ChildArcProgress => _radial.ChildArcProgress;

    /// <summary>诊断用：渲染出来的子项数量。</summary>
    internal int ChildItemCount => _radial.ChildItemCount;

    /// <summary>诊断用：视觉树上的直接子元素数量。</summary>
    internal int VisualChildCount => _radial.VisualChildCount;

    /// <summary>诊断用：跳过悬停延迟直接展开子弧。</summary>
    internal void ExpandChildArcForProbe(int parentIndex) => _radial.ExpandChildArc(parentIndex);

    /// <summary>诊断用：根环「＋」的索引。</summary>
    internal int AddSectorIndexForProbe => _radial.AddSectorIndex;

    /// <summary>诊断用：子弧「＋」的索引。</summary>
    internal int ChildAddSectorIndexForProbe => _radial.ChildAddSectorIndex;

    /// <summary>诊断用：按真实命中结果走一遍右键分派。</summary>
    internal WinQuick.Core.Geometry.HitResult RightClickForProbe(double x, double y) =>
        _radial.RightClickForProbe(new System.Windows.Point(x, y));

    /// <summary>诊断用：编辑面板是否开着且真的在视觉树上。</summary>
    internal bool IsPanelOpen => _radial.IsPanelOpen;

    /// <summary>诊断用：直接要求编辑某一项，跳过命中测试。</summary>
    internal void ShowSectorEditForProbe(int rootIndex, int childIndex) =>
        _radial.ShowSectorEditForProbe(rootIndex, childIndex);

    /// <summary>诊断用：收起面板。</summary>
    internal void DismissPanelForProbe() => _radial.DismissPanelForProbe();

    /// <summary>诊断用：某个标签的排版快照。</summary>
    internal (double Left, double FontSize, string Weight, bool Wrapping, double Width)
        LabelLayoutSnapshotForProbe(int index) => _radial.LabelLayoutSnapshot(index);

    /// <summary>诊断用：某个标签是否挂着动画时钟。</summary>
    internal bool LabelHasAnimationForProbe(int index) => _radial.LabelHasAnimation(index);

    /// <summary>诊断用：某个标签的完整文字。</summary>
    internal string LabelTextForProbe(int index) => _radial.LabelText(index);

    /// <summary>诊断用：某个标签是否被截断。</summary>
    internal bool LabelIsElidedForProbe(int index) => _radial.LabelIsElided(index);

    /// <summary>诊断用：被悄悄裁掉（既没装下、又没标出截断）的标签。</summary>
    internal IReadOnlyList<string> SilentlyClippedLabelsForProbe => _radial.SilentlyClippedLabels;

    /// <summary>诊断用：某个标签的最终字号。</summary>
    internal double LabelFontSizeForProbe(int index) => _radial.LabelFontSize(index);

    /// <summary>诊断用：某个标签的文字占宽。</summary>
    internal double LabelTextWidthForProbe(int index) => _radial.LabelTextWidth(index);

    /// <summary>诊断用：中心的 "Esc" 提示是否可见。</summary>
    internal bool EscHintVisibleForProbe => _radial.EscHintVisible;

    /// <summary>诊断用：被截断的标签数。</summary>
    internal int ElidedLabelCountForProbe => _radial.ElidedLabelCount;

    /// <summary>诊断用：最大标签溢出像素。</summary>
    internal double MaxLabelOverflowForProbe => _radial.MaxLabelOverflow;

    /// <summary>诊断用：把某个扇区立即置为悬停（不播放动画）。</summary>
    internal void SetHoverImmediateForProbe(int index) => _radial.SetHoverImmediateForProbe(index);

    /// <summary>诊断用：被悬停子项当前的鼓出像素数。</summary>
    internal double CurrentChildBulgeForProbe => _radial.CurrentChildBulgeForProbe;

    /// <summary>诊断用：子环形变动画是否还在跑。</summary>
    internal bool IsChildBulgeAnimatingForProbe => _radial.IsChildBulgeAnimatingForProbe;

    /// <summary>诊断用：立即把子环悬停形变落定（不播放动画）。</summary>
    internal void SetChildHoverImmediateForProbe(int index) =>
        _radial.SetChildHoverImmediateForProbe(index);

    /// <summary>诊断用：设置子项悬停（播放动画）。</summary>
    internal void SetChildHoverForProbe(int index) => _radial.SetChildHoverForProbe(index);

    /// <summary>诊断用：面板是否真的挂在视觉树上。</summary>
    internal bool PanelAttached => _radial.PanelAttached;

    /// <summary>诊断用：当前是哪一块面板。</summary>
    internal string PanelKindForProbe => _radial.PanelKindForProbe;

    /// <summary>诊断用：面板各行的文案。</summary>
    internal IReadOnlyList<string> PanelRowLabelsForProbe => _radial.PanelRowLabelsForProbe;

    /// <summary>诊断用：点面板的第 index 行（走按钮同一个委托）。</summary>
    internal void InvokePanelRowForProbe(int index) => _radial.InvokePanelRowForProbe(index);

    /// <summary>诊断用：改输入框文字并确认。</summary>
    internal void ConfirmPanelTextForProbe(string text) => _radial.ConfirmPanelTextForProbe(text);

    /// <summary>诊断用：编辑态、当前菜单项数、以及「＋」扇区索引。</summary>
    internal (bool EditMode, int ItemCount, int AddSector) RadialStateForProbe =>
        _radial.RadialStateForProbe;

    /// <summary>诊断用：面板在窗口内的矩形。</summary>
    internal System.Windows.Rect? PanelBoundsForProbe => _radial.PanelBoundsForProbe;

    /// <summary>诊断用：直接切换编辑态（正式路径由打开/关闭设置面板驱动）。</summary>
    internal void SetEditModeForProbe(bool editMode) => _radial.SetEditMode(editMode);

    /// <summary>诊断用：把某个扇区置为悬停，制造一次进行中的形变动画。</summary>
    internal void HoverForProbe(int index) => _radial.HoverForProbe(index);

    /// <summary>诊断用：面板当前编辑的是哪一项。</summary>
    internal (int RootIndex, int ChildIndex) EditTargetForProbe => _radial.EditTargetForProbe;

    /// <summary>诊断用：走一遍 Esc 的处理路径（收面板 → 收子弧 → 关覆盖层）。</summary>
    internal void EscapeForProbe() => _radial.EscapeForProbe();

    /// <summary>诊断用：按真实命中结果走一遍左键分派（含"面板开着时先收面板"）。</summary>
    internal WinQuick.Core.Geometry.HitResult LeftClickForProbe(double x, double y) =>
        _radial.LeftClickForProbe(new System.Windows.Point(x, y));

    /// <summary>诊断用：只按下（不抬起）。</summary>
    internal void PressForProbe(double x, double y) =>
        _radial.PressForProbe(new System.Windows.Point(x, y));

    /// <summary>诊断用：只抬起（配合上一次按下）。</summary>
    internal void ReleaseForProbe(double x, double y) =>
        _radial.ReleaseForProbe(new System.Windows.Point(x, y));

    /// <summary>诊断用：模拟指针移动。</summary>
    internal void MovePointerForProbe(double x, double y) =>
        _radial.MovePointerForProbe(new System.Windows.Point(x, y));

    /// <summary>诊断用：长按阈值（毫秒）。探针要按它等待，不能写死 2000。</summary>
    internal int LongPressMsForProbe => Design.TokenStore.Current.Interaction.LongPressMs;

    /// <summary>诊断用：是否正在拖拽。</summary>
    internal bool IsDraggingForProbe => _radial.IsDraggingForProbe;

    /// <summary>诊断用：被拖的是子环里的项还是第一层的。</summary>
    internal bool DragIsChildForProbe => _radial.DragIsChildForProbe;

    /// <summary>
    /// 诊断用：把一块"粒子场"挂到轮盘画布上或摘下来。
    /// 粒子探针要量的是"在真实的分层窗口上逐帧动一堆图形"的代价，
    /// 所以必须挂在真窗口的真画布上，而不是另开一个干净窗口量。
    /// </summary>
    internal void AttachParticleFieldForProbe(object? field) =>
        _radial.AttachParticleFieldForProbe(field);

    /// <summary>诊断用：子环的编辑态虚线是否画出来了。</summary>
    internal bool ChildEditOutlineAttached => _radial.ChildEditOutlineAttached;

    /// <summary>诊断用：按下时判定的扇区索引（-1 = 不在真实项上）。</summary>
    internal int PressedIndexForProbe => _radial.PressedIndexForProbe;

    /// <summary>诊断用：按下时判定为子环里的项。</summary>
    internal bool PressedIsChildForProbe => _radial.PressedIsChildForProbe;

    /// <summary>诊断用：当前落点格子。</summary>
    internal int DragTargetSlotForProbe => _radial.DragTargetSlotForProbe;

    /// <summary>诊断用：目标格子标记是否真的画出来了。</summary>
    internal bool DragMarkerAttachedForProbe => _radial.DragMarkerAttachedForProbe;

    /// <summary>诊断用：把窗口内坐标交给轮盘做命中测试（走的是点击同一套数学）。</summary>
    internal WinQuick.Core.Geometry.HitResult HitTestForProbe(double x, double y) =>
        _radial.HitTestForProbe(new System.Windows.Point(x, y));

    /// <summary>诊断用：按真实命中结果走一遍点击分派，事件照常发出。</summary>
    internal WinQuick.Core.Geometry.HitResult ClickForProbe(double x, double y) =>
        _radial.ClickForProbe(new System.Windows.Point(x, y));

    /// <summary>重建菜单后把某个父项的子弧重新展开，让用户看见刚加进去的项。</summary>
    public void ReopenChildArc(int parentIndex) => _radial.ReopenChildArc(parentIndex);

    internal void CollapseChildArcForProbe() => _radial.CollapseChildArc();

    /// <summary>
    /// 某个叶子项被点击。<c>ChildIndex</c> 为 -1 表示第一层的项，否则是子弧里的子项。
    /// </summary>
    public event EventHandler<(int ParentIndex, int ChildIndex)>? ItemActivated;

    /// <summary>
    /// 用户点了环里的「＋」，要求往那一级加一项。
    /// 参数是父项索引；-1 表示第一层。
    /// </summary>
    public event EventHandler<int>? AddItemRequested;

    /// <summary>
    /// 用户拖拽重排了某一层。参数是（父项索引，从哪，到哪）；父项索引为 -1 表示第一层。
    /// </summary>
    public event EventHandler<(int ParentIndex, int From, int To)>? ReorderRequested;

    /// <summary>用户要求新建分组（带子菜单的项）。</summary>
    public event EventHandler<(int ParentIndex, string Label)>? AddGroupRequested;

    /// <summary>用户在扇区的编辑面板上选了改名或移除。</summary>
    public event EventHandler<(int RootIndex, int ChildIndex, Radial.SectorEditAction Action, string? Label)>?
        SectorEditRequested;

    /// <summary>设置面板正在显示。</summary>
    public bool IsSettingsOpen => _settingsPanel is { IsVisible: true };

    /// <summary>设置面板里的改动需要被应用与持久化。参数是组装好的新配置。</summary>
    public event EventHandler<AppConfig>? SettingsChanged;

    /// <summary>
    /// 用户在皮肤面板里选了一套皮肤/形态。由常驻方负责落盘并让轮盘重建。
    /// 控制器只管"面板说了什么"，不管"怎么存" —— 那是常驻方的职责（它持配置与菜单文件）。
    /// </summary>
    public event EventHandler<(string SkinId, Radial.RingForm Form)>? SkinChanged;

    /// <summary>皮肤面板关闭（探针与焦点归还要用）。</summary>
    public event EventHandler? SkinClosed;

    /// <summary>设置面板已关闭（用户点 ✕ 或按 Esc）。</summary>
    public event EventHandler? SettingsClosed;

    /// <summary>设置面板里改了菜单（增删项、改名称）。需要写回 menus.json 并重建轮盘。</summary>

    /// <summary>用户在覆盖层左上角点了「退出」。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// 预先创建所有窗口并隐藏。必须在启动时调用一次，把渲染栈、字体缓存、
    /// D3D 表面都热起来 —— 否则第一次唤出会明显卡顿。
    /// </summary>
    public void Prewarm()
    {
        RebuildDimWindows();

        // 让轮盘窗口真正渲染一次再隐藏：只创建不显示的话，字体与渲染目标都还没分配，
        // 第一次唤出仍然会卡。
        //
        // ShowActivated 必须先关掉 —— 否则这个预热用的 Show 会在开机时把焦点从
        // 用户正在用的窗口抢走。位置也放到屏幕外，避免开机闪一下。
        _radial.ShowActivated = false;
        _radial.Left = -32000;
        _radial.Top = -32000;
        _radial.Show();
        _radial.Park();          // 首次映射的成本留在启动时，别留给第一次唤出

        // **停放之后必须把环清空。**
        // 分层窗口显示的是"上一次提交的那张位图"，而预热时环是完整的 ——
        // 不清的话，第一次唤出把窗口挪回屏上的一瞬就会先露出完整圆盘、动画才开始
        // （用户报的"首次启动后仍先渲染终态"）。
        // 约定：**窗口处于停放状态时，它里面装的必须是空的环。**
        _radial.ResetForDormancy();
        _radial.ShowActivated = true;

        // 操作条同样先渲染一次：它的首次映射（创建渲染目标、量文字）否则会落在
        // 显示那一刻 —— 而那正是全流程最拥挤的一帧（见 Show 里的说明）。
        _actionBar.ShowActivated = false;
        _actionBar.Left = -32000;
        _actionBar.Top = -32000;
        _actionBar.Show();
        _actionBar.Park();          // 同样以休眠收尾：首次映射留在启动时
        _actionBar.ShowActivated = false;   // 操作条本来就永不激活

        // 窗口句柄此时才存在，所以消息钩子只能在这里挂。
        // 显示器拓扑变化后遮罩窗口的尺寸全都会失效，必须重建窗口池。
        _radialSource = (HwndSource?)PresentationSource.FromVisual(_radial);
        _radialSource?.AddHook(OnRadialMessage);
    }

    /// <summary>
    /// 显示覆盖层。
    /// **必须同步完成** —— 前台权限的豁免只在同一次输入事件的调用栈内有效，
    /// 一旦 await、切线程或 PostMessage 之后再设置前台窗口，权限就丢了。
    /// </summary>
    public void Show() => Show(null);

    /// <param name="preserveRadialPosition">
    /// 非 null 时把轮盘摆在给定的物理像素矩形，而不是光标处。
    /// 供"菜单变更后重建"使用 —— 那时用户的操作焦点在设置面板上，
    /// 把轮盘挪到光标处会正好压住面板。
    /// </param>
    private void Show((int X, int Y, int Width, int Height)? preserveRadialPosition)
    {
        if (_disposed || IsVisible) return;

        // 唤醒就取消待办的内存清理：刚换出的页马上要用，清理会白做一次还拖慢唤出。
        _idleTrimTimer.Stop();

        _preserveRadialPosition = preserveRadialPosition;

        // 停掉可能还在跑的退场计时器 —— 否则它会在 167ms 后把刚显示出来的轮盘又藏回去。
        // （ApplyDimOpacity 与菜单重建都会在 Hide 之后紧接着 Show，这条路径很常见。）
        _exitTimer.Stop();

        // 显示器数量变了就重建遮罩窗口池，否则会有屏没被遮住。
        if (_dims.Count != MonitorLayout.All().Count)
            RebuildDimWindows();

        // **虚拟桌面：唤出前先把我们的窗口搬到用户此刻所在的桌面。**
        //
        // 窗口属于"创建时所在的那个桌面"，而 Windows 不会因为 Show() 就搬它。
        // 不搬的后果是用户实测报的那两种：只出遮罩（轮盘留在原桌面，点不到也关不掉），
        // 或者一激活就被拽回工具所在的桌面。详见 Interop/VirtualDesktop.cs。
        MoveWindowsToCurrentDesktop();

        _focus.Save();

        foreach (var dim in _dims) dim.Show();

        // **唤出的顺序是规格。**
        //
        // 顺序被抽到 Core 的 `SummonSequence` 里，并有单测锁住 —— 其中一条是
        // "落定显示之前的状态必须早于把窗口挪到屏上"：窗口从"停放"恢复时，
        // 屏幕上显示的是上一次提交的位图，顺序反了就会先闪一帧完整圆盘。
        // 这里只负责把每个步骤映射到具体动作，不再自己排列。
        foreach (var step in SummonSequence.Steps(AnimationsEnabled()))
        {
            switch (step)
            {
                case SummonStep.LeaveDormancy:
                    // 今天不需要额外动作：停放态的不透明度已经是 0，而下一步的落定会把
                    // 窗口层缩放/角度一并写死（见 RadialWindow.PrepareEntrance）。
                    break;

                case SummonStep.SettlePreShowState:
                    _radial.PrepareEntrance();
                    break;

                case SummonStep.ShowAtCursor:
                    if (_preserveRadialPosition is { } bounds)
                        _radial.PlaceAt(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    else
                        _radial.PlaceAtCursor();

                    _radial.Show();
                    break;

                case SummonStep.ReassertTopmost:
                    // **一次整栈批量提交**，而不是每个窗口各发一串 SetWindowPos：
                    // 实测显示那一刻最长的帧（50ms 上下）就落在这一段，
                    // 而逐窗重申会在中间态被合成器取走一帧（那正是"整屏压暗"的来源）。
                    ReassertTopmost();
                    break;

                case SummonStep.PlayEntrance:
                    // 排到下一批再起跑，不在这里同步调：同步调的话时钟立刻起跑，
                    // 而 UI 线程随后还要忙一阵（操作条、遮罩、焦点、托盘），
                    // 等第一帧真正呈现时动画已走过大半 —— 实测不透明度首帧就到 0.71，
                    // 一次 167ms 的缩放淡入被压成"啪地出现"。
                    _radial.Dispatcher.BeginInvoke(
                        DispatcherPriority.Loaded, new Action(() => _radial.PlayEntrance()));
                    break;
            }
        }

        // 操作条晚一步再显示：它是角落里的次要控件，而显示这一帧已经背着
        // 窗口映射、首次呈现、遮罩与 z 序的活。挪到下一批（Loaded 优先级）
        // 不影响可用性 —— 用户此刻在看圆盘 —— 却把它从那最拥挤的一帧里拿掉。
        _radial.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _actionBar.PlaceAtMonitorCorner();
            _actionBar.Show();
            ReassertTopmost();          // 操作条进来之后整栈再定一次序
        }));

        _radial.StartInteraction();

        // 拿键盘焦点，否则 Esc 收不到。这里同步调用是刻意的（见方法注释）。
        // 设置面板开着时不能夺焦点 —— 用户可能正在里面打字（热键录制、改名、填色值），
        // 抢走焦点会打断输入。
        if (!IsSettingsOpen) ForegroundFocus.AttachTo(_radial);

        // 恢复显隐往返之前打开着的面板（设置或皮肤 —— 两者不会同时在场）。
        if (_settingsWasOpen && _settingsPanel is not null)
        {
            _settingsPanel.PlaceAtMonitorCornerIfNeeded();
            PositionSettingsPanel();
            _settingsPanel.Show();
            _settingsWasOpen = false;
            ForegroundFocus.AttachTo(_settingsPanel);
        }
        else if (_skinWasOpen && _skinPanel is not null)
        {
            PositionSkinPanel();
            _skinPanel.Show();
            _skinWasOpen = false;
            ForegroundFocus.AttachTo(_skinPanel);
        }


        IsVisible = true;
        _topmostTimer.Start();
    }

    /// <summary>
    /// 隐藏覆盖层并把前台焦点还给用户原来的窗口。
    ///
    /// 遮罩层立刻消失（它是用户的视觉反馈），轮盘窗口等退场动画播完再隐藏。
    /// 焦点**立刻**归还 —— 用户按下扇区之后应马上回到原来的窗口，
    /// 不能等动画放完才还（Kando #650 的教训）。
    /// </summary>
    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;

        // 退场的顺序同样是规格，见 Core 的 `DismissSequence`：
        // 焦点归还与遮罩消失必须排在"等退场动画"之前 —— 退场动画**不得阻塞动作执行**
        //（Kando #650：退场淡出之后才启动应用，用户投诉延迟；本项目也把它写进了令牌注释）。
        // 这里没有改成按步骤表执行：现有控制流（动画 + 退场计时器）已经被探针与人眼验证过，
        // 为了对齐一张清单去重排它，风险大于收益。顺序由那边的手册 + 这行注释共同约束。

        _topmostTimer.Stop();

        if (_pendingEntranceStart is not null)
        {
            CompositionTarget.Rendering -= _pendingEntranceStart;
            _pendingEntranceStart = null;
        }

        // 关闭覆盖层同时退出编辑态：编辑态是"当前这次唤出"里的状态，
        // 下次按热键应当是干净的日常盘面（否则一按热键就看到「＋」和拖拽态）。
        _radial.SetEditMode(false);

        // 指针轮询必须停 —— 它每 25ms 唤醒一次 UI 线程，窗口不可见时纯属浪费。
        _radial.StopInteraction();
        foreach (var dim in _dims) dim.Hide();
        _actionBar.Park();
        // 记住面板状态：显隐往返（改设置、重建菜单、显示器变化）之后要恢复它，
        // 否则用户正在填的表单会毫无预兆地消失。
        _settingsWasOpen = _settingsPanel is { IsVisible: true };
        _skinWasOpen = _skinPanel is { IsVisible: true };
        _settingsPanel?.Hide();
        _skinPanel?.Hide();

        // 退场期间不再接受输入，否则用户在收缩过程中还能点到扇区。
        _radial.IsHitTestVisible = false;

        var exitMs = TokenStore.Current.Motion.ExitDurationMs;
        if (AnimationsEnabled() && exitMs > 0)
        {
            _radial.PlayExit();
            _exitTimer.Interval = TimeSpan.FromMilliseconds(exitMs + 20);
            _exitTimer.Start();
        }
        else
        {
            FinishHide();
        }

        // 焦点立刻归还，不等动画。
        // 换桌面导致收起时跳过（`_suppressFocusRestore`）—— 原窗口在别的桌面上，
        // 归还焦点等于把用户拽回去，而他刚才是主动切走的。
        if (!_suppressFocusRestore) _focus.Restore();
    }

    /// <summary>
    /// 轮盘失去焦点。
    ///
    /// 只有当焦点落到了**别人的窗口**上才关闭。焦点若落在我们自己的窗口上
    /// （左上角操作条就是这种情况），说明用户仍在操作覆盖层，不该关。
    /// 不判这一下的话，点操作条上的按钮会在 Click 完成前就把覆盖层隐藏掉，
    /// 表现是"按钮点了没反应、只是关闭" —— 这正是用户反馈的现象。
    /// </summary>
    private void OnRadialFocusLost()
    {
        if (!IsVisible) return;

        // 系统对话框正在弹出/收起的这一段里，"前台窗口不是我们"是预期状态而不是用户离开。
        // 少了这一条，用户点「＋」选程序的过程中轮盘就被自己关掉了 ——
        // 选完文件回来发现覆盖层没了，而"再加一项"这件事还没做完。
        if (_modalDialogDepth > 0) return;

        var foreground = PInvoke.GetForegroundWindow();
        if (IsOwnWindow(foreground)) return;

        Hide();
    }

    private bool IsOwnWindow(HWND hwnd)
    {
        if (hwnd == default) return false;
        if (hwnd == (HWND)_radial.HandleValue) return true;
        if (hwnd == (HWND)_actionBar.HandleValue) return true;
        if (_settingsPanel is not null && hwnd == (HWND)_settingsPanel.HandleValue) return true;
        if (_skinPanel is not null && hwnd == (HWND)_skinPanel.HandleValue) return true;
        return _dims.Any(d => hwnd == (HWND)d.HandleValue);
    }

    /// <summary>
    /// 打开设置面板。面板与轮盘同时在场，所以拖滑块、改高亮色都能立刻在轮盘上看到效果。
    /// </summary>
    public void OpenSettings()
    {
        if (_disposed || !IsVisible) return;
        if (IsSettingsOpen) return;

        _settingsPanel ??= CreateSettingsPanel();

        // 面板可能是在另一个桌面上开的（它是常驻复用的窗口，不重建）。
        VirtualDesktop.MoveAllToCurrentDesktop([_settingsPanel.HandleValue]);

        // 两遍定位：先按"最大高度"估个位置，让窗口出现时就大致在正确的地方（避免可见的跳动）；
        // 显示并完成布局后，再按**真实尺寸**重新摆一次并钳制在屏内。
        //
        // 为什么必须两遍：布局完成前 ActualHeight 不可靠，按它算出的 top 会让面板底部溢出屏幕，
        // 而溢出的恰恰是最常用的底部按钮 —— 用户就点不到了。
        // （这个溢出是被探针的「面板完整落在屏内」核对抓到的。）
        // 同上：先落定起始状态，再显示。
        _settingsPanel.PrepareEntrance();

        PositionSettingsPanel();
        _settingsPanel.Show();
        _settingsPanel.UpdateLayout();
        PositionSettingsPanel();

        // 进设置态才把「＋」扇区放出来：日常使用时轮盘里只该有用户自己的项。
        // 面板是开在覆盖层旁边的，所以"打开面板 → 轮盘上出现「＋」"是同一个视野里的变化。
        _radial.SetEditMode(true);

        // 同 _radial：动画排在 Loaded 优先级起跑，否则第一帧就已经走过大半
        // （实测面板首帧不透明度 0.59），入场缩放等于被跳过。
        _settingsPanel.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded, new Action(() => _settingsPanel.PlayEntrance()));

        // 面板需要键盘输入（热键录制、文本框），所以它会夺走轮盘的焦点。
        // 这不影响覆盖层 —— OnRadialFocusLost 会认出"焦点落在自己人身上"。
        ForegroundFocus.AttachTo(_settingsPanel);
    }

    /// <summary>关闭设置面板，把焦点还给轮盘（否则 Esc 就不再关闭覆盖层了）。</summary>
    public void CloseSettings()
    {
        if (_settingsPanel is not { IsVisible: true }) return;

        _settingsPanel.Hide();

        // **不退出编辑态。** 编辑态现在有自己的出口（Esc / 点中心 / 点环外 / 关覆盖层）：
        // 用户可能想关掉面板继续在轮盘上拖拽排版，替他把编辑态收掉反而打断操作。
        ForegroundFocus.AttachTo(_radial);
        SettingsClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 打开皮肤选择面板。
    ///
    /// **它替补设置面板的位置**（两块面板不并存）：都是"轮盘旁边的一块面板"，
    /// 叠在一起只会互相遮挡，而换皮肤要看的恰恰是轮盘本身。
    /// 轮盘保持在场并留在编辑态 —— 换皮肤要的就是"点一下、立刻看到"。
    /// </summary>
    public void OpenSkinPanel()
    {
        if (_disposed || !IsVisible) return;
        if (IsSkinOpen) return;

        CloseSettings();

        _skinPanel ??= CreateSkinPanel();

        // 面板可能是在另一个桌面上开的（它是常驻复用的窗口，不重建）。
        VirtualDesktop.MoveAllToCurrentDesktop([_skinPanel.HandleValue]);

        _skinPanel.PrepareEntrance();
        PositionSkinPanel();
        _skinPanel.Show();
        _skinPanel.UpdateLayout();
        PositionSkinPanel();

        _radial.SetEditMode(true);

        _skinPanel.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded, new Action(() => _skinPanel.PlayEntrance()));

        ForegroundFocus.AttachTo(_skinPanel);
    }

    /// <summary>关闭皮肤面板，把焦点还给轮盘。</summary>
    public void CloseSkinPanel()
    {
        if (_skinPanel is not { IsVisible: true }) return;

        _skinPanel.Hide();
        ForegroundFocus.AttachTo(_radial);
        SkinClosed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsSkinOpen => _skinPanel is { IsVisible: true };

    private SkinPanel CreateSkinPanel()
    {
        var panel = new SkinPanel(
            TokenStore.Skins,
            _settings.Appearance.Skin ?? SkinCatalog.DefaultId,
            Radial.RingForms.Parse(_settings.Appearance.Form),
            apply: (skinId, form) => SkinChanged?.Invoke(this, (skinId, form)));

        panel.CloseRequested += (_, _) => CloseSkinPanel();
        return panel;
    }

    private SettingsPanel CreateSettingsPanel()
    {
        var panel = new SettingsPanel(
            _settings,
            probeHotkey: spec => _settingsPanelProbe(spec),
            applyConfig: config => SettingsChanged?.Invoke(this, config));

        panel.CloseRequested += (_, _) => CloseSettings();
        panel.SkinRequested += (_, _) => OpenSkinPanel();

        return panel;
    }

    /// <summary>由常驻装配注入的热键探测（会临时释放当前热键，见 HotkeyManager.ProbeTemporarily）。</summary>
    /// <summary>
    /// 热键可用性探测。**默认回答"不可用"而不是留 null** ——
    /// 面板可能在被注入之前就被创建（启动顺序），那时调用它会直接空引用崩溃。
    /// </summary>
    private Func<Core.Hotkey.HotkeySpec, (bool Available, int ErrorCode)> _settingsPanelProbe =
        _ => (false, 0);

    /// <summary>设置面板显示时所用的当前配置。</summary>
    private AppConfig _settings = new();

    /// <summary>告诉覆盖层"面板打开时应当以哪份配置为基准"。</summary>
    public void SetSettingsSource(AppConfig config) => _settings = config;

    /// <summary>注入热键探测函数。</summary>
    public void SetHotkeyProbe(Func<Core.Hotkey.HotkeySpec, (bool Available, int ErrorCode)> probe) =>
        _settingsPanelProbe = spec => probe(spec);

    /// <summary>
    /// 把面板摆在光标所在屏的**另一侧**，尽量避免压住轮盘
    /// （轮盘跟着光标走，所以把面板放到光标的反方向最不容易重叠）。
    ///
    /// 设置面板与皮肤面板共用这一份：两块面板不会同时在场（打开皮肤会收起设置），
    /// 所以它们用同一条摆位规则就是对的 —— 各写一份只会让"哪块摆哪儿"变得难以预期。
    /// </summary>
    private void PositionPanel(Window panel, bool placedByUser)
    {
        var scale0 = MonitorLayout.AtCursor().Scale;
        var monitor = MonitorLayout.AtCursor();
        var scale = monitor.Scale;

        const int margin = 24;

        // 先把最大高度限制在屏幕内：面板在小屏幕上可能比屏幕还高，
        // 那时底部的按钮会被截掉、点不到。内容本身可滚动（见 XAML 的 ScrollViewer）。
        var availableDips = (monitor.Height - margin * 2) / scale;
        if (panel.MaxHeight <= 0 || panel.MaxHeight > availableDips)
            panel.MaxHeight = Math.Max(320, availableDips);

        var panelWidth = (int)Math.Round(panel.Width * scale0);

        // 布局完成前 ActualHeight 不可靠，退化为按最大高度估算 ——
        // 第一遍（Show 之前）走的就是这条。
        var measuredDips = panel.ActualHeight;
        var panelHeight = measuredDips > 0
            ? (int)Math.Round(measuredDips * scale0)
            : (int)Math.Round(panel.MaxHeight * scale0);

        var cursorOnLeftHalf = !MonitorLayout.TryGetCursorPosition(out var cx, out _)
                               || cx < monitor.X + monitor.Width / 2;

        var left = cursorOnLeftHalf
            ? monitor.Right - panelWidth - (int)Math.Round(margin * scale0)
            : monitor.X + (int)Math.Round(margin * scale0);

        var top = monitor.Y + (monitor.Height - panelHeight) / 2;

        // 钳制在屏内。竖直方向尤其重要：面板是纵向表单，底部是最常用的按钮，
        // 溢出屏幕就等于点不到 —— 用户已经因为"按钮点不到"报过一次问题了。
        left = Math.Clamp(left, monitor.X, Math.Max(monitor.X, monitor.Right - panelWidth));
        top = Math.Clamp(top, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - panelHeight));

        // 用户手动拖过就不再自动摆位：面板挡住轮盘正是他拖开的动机，
        // 自动摆位会把它拽回原处 —— 等于白拖。（最大高度那段仍然照做，
        // 它不移动窗口，只保证小屏上底部的按钮不会被截掉。）
        if (placedByUser) return;

        SurfaceOps.SetWindowPos("摆位面板");
        var hwnd = (HWND)new WindowInteropHelper(panel).Handle;
        PInvoke.SetWindowPos(hwnd, default, left, top, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }

    private void PositionSettingsPanel()
    {
        if (_settingsPanel is null) return;
        PositionPanel(_settingsPanel, _settingsPanel.PlacedByUser);
    }

    private void PositionSkinPanel()
    {
        if (_skinPanel is null) return;
        PositionPanel(_skinPanel, _skinPanel.PlacedByUser);
    }

    /// <summary>
    /// 把覆盖层的窗口搬到当前虚拟桌面。返回搬动个数（诊断用）。
    /// 面板也算：它们常驻复用，上次可能是在另一个桌面上开的。
    /// </summary>
    private int MoveWindowsToCurrentDesktop()
    {
        var windows = new List<nint> { _radial.HandleValue, _actionBar.HandleValue };
        windows.AddRange(_dims.Where(d => d.IsValid).Select(d => d.HandleValue));
        if (_settingsPanel is not null) windows.Add(_settingsPanel.HandleValue);
        if (_skinPanel is not null) windows.Add(_skinPanel.HandleValue);

        return VirtualDesktop.MoveAllToCurrentDesktop(windows);
    }

    /// <summary>
    /// 覆盖层显示期间，用户把桌面切走了 —— 把它收起来。
    ///
    /// 不做的话这些窗口会留在原来那个桌面上：用户在新的桌面上既看不到它们、
    /// 也没法用 Esc 关掉（轮盘根本不在那儿），而它们却仍然占着"已显示"的状态。
    /// 收起时**不归还焦点**（原窗口在别的桌面上，归还＝把用户拽回去）。
    /// </summary>
    private void HideIfDesktopChanged()
    {
        if (!IsVisible) return;
        if (!VirtualDesktop.IsSupported) return;
        if (VirtualDesktop.IsOnCurrentDesktop(_radial.HandleValue)) return;

        HideWithoutFocusRestore();
    }

    /// <summary>收起覆盖层，但不碰焦点（焦点在别的桌面上，碰了就是把用户拽过去）。</summary>
    private void HideWithoutFocusRestore()
    {
        _suppressFocusRestore = true;
        try
        {
            Hide();
        }
        finally
        {
            _suppressFocusRestore = false;
        }
    }

    /// <summary>
    /// 实时更新随设置变化的外观项。改动立刻生效，不等保存。
    /// </summary>
    public void ApplyRuntimeStyle(AppConfig config)
    {
        ApplyDimOpacity(config.Appearance.DimOpacity);
        _radial.ApplyAccent(config.Appearance.AccentColor);
        _radial.ApplyHoverDelays(config.Behavior.HoverOpenDelayMs, config.Behavior.HoverCloseDelayMs);
        _radial.ApplyAnimations(config.Behavior.AnimationsEnabled);
    }

    /// <summary>
    /// 换皮肤/换形态之后让轮盘重建（令牌与形态都是轮盘在构造时抓的）。
    /// 皮肤面板每点一次都会调到这里，所以它必须走"复用画布"那条不闪的路径。
    /// </summary>
    public void RebuildForAppearance(RingForm form) => _radial.RebuildForSkin(form);

    /// <summary>诊断用：操作条句柄。</summary>

    internal nint ActionBarHandle => _actionBar.HandleValue;

    /// <summary>诊断用：操作条是否可见。</summary>
    internal bool IsActionBarVisible => _actionBar.IsVisible;

    /// <summary>诊断用：设置面板句柄。</summary>
    internal nint SettingsPanelHandle => _settingsPanel?.HandleValue ?? 0;

    /// <summary>
    /// 开始监听前台变更事件（事件驱动地重申置顶）。
    /// 必须在显示覆盖层之前调用一次；与定时器互补 ——
    /// 定时器只兜「置顶位被清 / 内部顺序被重建打乱」，事件兜「别人抢到前台」。
    /// </summary>
    public void StartZOrderWatching() => _zOrder.WatchForeground();

    /// <summary>诊断用：轮盘当前的物理像素矩形。</summary>
    internal (int X, int Y, int Width, int Height)? RadialPhysicalBounds => _radial.CurrentPhysicalBounds;

    /// <summary>诊断用：z 序管理器（探针用它读取重申次数）。</summary>
    internal ZOrderManager ZOrder => _zOrder;

    /// <summary>诊断用：设置面板实例（探针需要它来触发对话框）。</summary>
    internal SettingsPanel? SettingsPanelForProbe => _settingsPanel;

    /// <summary>诊断用：操作条「设置」按钮中心的屏幕物理像素坐标。</summary>
    internal (int X, int Y)? ActionBarSettingsButtonScreenPoint() => ActionBarSettingsButtonCenterInScreen();

    /// <summary>诊断用：「设置」按钮中心在屏幕上的物理像素坐标。</summary>
    internal (int X, int Y)? ActionBarSettingsButtonCenterInScreen()
    {
        if (_actionBar.SettingsButtonCenter is not { } center) return null;

        var handle = (HWND)_actionBar.HandleValue;
        if (!PInvoke.GetWindowRect(handle, out var rect)) return null;

        var scale = MonitorLayout.AtCursor().Scale;
        return (rect.left + (int)Math.Round(center.X * scale),
                rect.top + (int)Math.Round(center.Y * scale));
    }

    /// <summary>真正把轮盘窗口隐藏并重置动画状态。</summary>
    private void FinishHide()
    {
        // 期间若已被重新显示（Hide 之后紧接着 Show），这次的收尾就作废。
        if (IsVisible) return;

        // 真正进入休眠了 → 起一个延迟清理（用户要求：关闭轮盘后自动回收自身占用）。
        // 只在真的睡着时起；唤醒路径上第一件事就是把它停掉。
        if (TokenStore.Current.Interaction.IdleTrimDelayMs > 0)
        {
            _idleTrimTimer.Interval = TimeSpan.FromMilliseconds(
                TokenStore.Current.Interaction.IdleTrimDelayMs);
            _idleTrimTimer.Stop();
            _idleTrimTimer.Start();
        }

        // **休眠而不是卸载**：卸载会让下次唤出付一次 30~60ms 的窗口映射（实测）。
        _radial.Park();
        _radial.ResetAnimationState();
        _radial.ResetInteraction();
        _radial.IsHitTestVisible = true;

        // 最后把环清空：休眠中的窗口里装着"空的环"，这样下次把它挪回屏上时
        // 不可能露出上一轮的完整圆盘（分层窗口显示的是上一次提交的位图）。
        _radial.ResetForDormancy();
    }

    private static bool AnimationsEnabled() => Design.MotionPreference.SystemAllows;

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }

    /// <summary>显示器变更、会话解锁等场景下的强制收尾 —— 宁可多关一次，也不要留下残影。</summary>
    public void ForceHideAndRebuild()
    {
        // 这条路径**不等动画**，直接立刻隐藏：异常场景下确定性比观感重要。
        _exitTimer.Stop();

        if (IsVisible)
        {
            IsVisible = false;
            _topmostTimer.Stop();
            if (!_suppressFocusRestore) _focus.Restore();
        }

        FinishHide();
        foreach (var dim in _dims) dim.Hide();
        RebuildDimWindows();
    }

    private void RebuildDimWindows()
    {
        foreach (var dim in _dims)
        {
            // 事件解绑后再销毁，避免已销毁的窗口回调打到活着的控制器上。
            dim.DismissRequested -= OnDimDismiss;
            dim.Dispose();
        }
        _dims.Clear();

        // 每屏一个遮罩窗口。一个 HWND 只能有一个 DPI，所以不能合并成一个横跨的窗口。
        // 遮罩用统一 alpha（40% 黑），原生窗口实测几乎零内存开销。
        // 透明度可以由用户配置覆盖；默认用设计基线里的值。
        var alpha = ToAlpha(DimOpacityOverride);

        foreach (var monitor in MonitorLayout.All())
        {
            var dim = new RawDimWindow(alpha);
            dim.SetPhysicalBounds(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            dim.DismissRequested += OnDimDismiss;
            _dims.Add(dim);
        }

        // 重建之后必须立刻重申 z 序。
        //
        // 这是本轮那个 bug 的发生点：新建的窗口会被插到 topmost 层**最前面**，
        // 于是重建出来的遮罩压在轮盘与设置面板之上 —— 它们仍然可见（遮罩只有 40% 不透明），
        // 但点击会被遮罩全部吃掉，用户看到的就是「看得见却点不动」。
        // 而这个重建每次改设置都会走（拖遮罩深浅 → 保存 → 热重载 → 重建）。
        ReassertTopmost();
    }

    private void OnDimDismiss()
    {
        // 点在遮罩上 = 点在轮盘之外的空白处。
        Hide();
    }

    /// <summary>
    /// 按层级栈重申整个 z 序，从下往上插入，保证结果确定等于栈的顺序。
    /// </summary>
    private void ReassertTopmost() => _zOrder.Reassert();

    /// <summary>诊断用：环上的扇区数。</summary>
    internal int SectorCountForProbe => _radial.SectorCountForProbe;

    /// <summary>诊断用：几何并发压力。</summary>
    internal void StartGeometryStressForProbe(int sectorCount, int durationMs) =>
        _radial.StartGeometryStressForProbe(sectorCount, durationMs);

    internal void StopGeometryStressForProbe() => _radial.StopGeometryStressForProbe();

    internal bool IsGeometryStressRunningForProbe => _radial.IsGeometryStressRunningForProbe;

    /// <summary>诊断用：残留的逐帧回调数（形变 / 子弧 / 几何压力）。</summary>
    internal static string HookCountsForProbe =>
        $"形变 {Radial.SectorBulgeAnimator.ActiveHookCount}"
        + $" + 子弧 {Radial.ChildArcAnimator.ActiveHookCount}"
        + $" + 几何压力 {Radial.RadialRingView.ActiveStressHookCount}";

    /// <summary>
    /// 诊断用：冻结指针轮询。
    ///
    /// 像素级的"与之前逐像素相同"核对会被**人手的鼠标移动**打破：指针扫过轮盘时
    /// 悬停扇区会被高亮，那一块整片像素都变（实测约 2.5 万像素 ≈ 一个扇区的面积），
    /// 于是核对随机变红、还被误读成产品缺陷。探针期间的悬停不是被测对象，冻结它。
    /// （每次 Show() 都会重新启动轮询，所以 Show 之后要再调一次。）
    /// </summary>
    internal void FreezePointerForProbe() => _radial.StopInteraction();

    /// <summary>诊断用：某扇区图形当前的透明度。</summary>
    internal Func<(double Scale, double Opacity)> RadialEntranceStateForProbe =>
        () => _radial.EntranceStateForProbe;

    /// <summary>诊断用：手动触发一次全栈重申（--flash-probe 拿它量"重申本身会不会闪"）。</summary>
    internal void ReassertTopmostForProbe() => ReassertTopmost();

    /// <summary>诊断用：设置面板的物理像素矩形（屏幕取样要知道往哪儿抓）。</summary>
    internal (int X, int Y, int Width, int Height)? SettingsPanelPhysicalBoundsForProbe
    {
        get
        {
            if (_settingsPanel is not { IsVisible: true } panel) return null;
            if (!PInvoke.GetWindowRect((HWND)panel.HandleValue, out var rect)) return null;
            return (rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        }
    }

    /// <summary>
    /// 找出当前由我们某个窗口拥有的系统对话框（文件选择、消息框等）。
    ///
    /// 判据用**owner 关系**而不是窗口类名：`ShowDialog(owner)` 会把 owner 设成调用方，
    /// 而窗口类名 `#32770` 是通用对话框类，全系统都在用 —— 按类名判会误伤别人的对话框。
    ///
    /// 找到之后要**主动**把它置顶：走的是系统对话框自己的窗口，我们改不了它的创建参数，
    /// 只能事后补样式。这也是为什么必须由重申逻辑兜着 —— 补一次不够，
    /// 我们自己的窗口每次重申都可能把它挤下去。
    /// </summary>
    /// <summary>诊断用：当前由我们某个窗口拥有的系统对话框句柄（0 表示没有）。</summary>
    internal nint OwnedSystemDialogForProbe() => FindOwnedSystemDialog();

    /// <summary>诊断用：某个句柄是不是我们自己的窗口。</summary>
    internal bool IsOwnWindowForProbe(nint hwnd) => IsOwnWindow((HWND)hwnd);

    private nint FindOwnedSystemDialog()
    {
        var foreground = PInvoke.GetForegroundWindow();
        if (foreground == default) return 0;
        if (IsOwnWindow(foreground)) return 0;
        if (!PInvoke.IsWindowVisible(foreground)) return 0;

        if (!IsOwnedByUs(foreground)) return 0;

        // 补上 topmost：对话框创建时并不知道我们的窗口是置顶的，
        // 而官方语义是「一个窗口**变成** topmost 时其被拥有窗口才连带变 topmost」——
        // 我们的面板早就是 topmost 了，这个转变从未发生，所以对话框一直是非置顶的。
        ZOrderManager.BringToTopmostFront(foreground);
        return (IntPtr)foreground;
    }

    /// <summary>这个窗口是不是由我们的某个窗口拥有的。</summary>
    private bool IsOwnedByUs(HWND hwnd)
    {
        var owner = PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER);
        if (owner == default) return false;

        return IsOwnWindow(owner);
    }

    private IntPtr OnRadialMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_* 在 CsWin32 里是 uint，而消息参数是 int，所以这里转成 uint 再比对。
        switch ((uint)msg)
        {
            case PInvoke.WM_DISPLAYCHANGE:
                // 退出全屏游戏、插拔显示器、改分辨率都会走到这里。
                Hide();
                RebuildDimWindows();
                break;

            case PInvoke.WM_KILLFOCUS:
                // 窗口的 Deactivated 事件覆盖不到所有路径，这里作为兜底。
                // 同样要判"是不是自己人抢的焦点"。
                OnRadialFocusLost();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _topmostTimer.Stop();
        _exitTimer.Stop();
        _zOrder.StopWatching();
        _radialSource?.RemoveHook(OnRadialMessage);

        foreach (var dim in _dims)
        {
            dim.DismissRequested -= OnDimDismiss;
            dim.Dispose();
        }
        _dims.Clear();

        _radial.Close();
        _actionBar.Close();
        _settingsPanel?.Close();
    }
}
