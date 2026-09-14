using WinQuick.Core.Design;
using WinQuick.Core.Geometry;

namespace WinQuick.Core.Interaction;

/// <summary>指针落点相对于当前状态的意义。</summary>
public readonly record struct HoverProbe(
    HitZone Zone,
    int Index,
    bool ItemHasChildren,
    long NowMs);

/// <summary>状态机给出的下一步动作。由调用方（视图层）去真正操作计时器与视觉。</summary>
public enum HoverAction
{
    None,

    /// <summary>开始等待展开延迟。</summary>
    ScheduleOpen,

    /// <summary>立即展开（延迟已过，或用户点击要求跳过延迟）。</summary>
    OpenNow,

    /// <summary>开始等待收起延迟。</summary>
    ScheduleClose,

    /// <summary>立即收起。</summary>
    CloseNow,
}

/// <summary>
/// 子菜单的悬停状态机 —— 纯逻辑，时钟由外部注入。
///
/// 为什么单独抽出来：这套规则涉及两个延迟、斜向缓冲、以及"指针移进子弧后不该收起"
/// 这类状态依赖，靠人工反复点击去验证几乎不可行。抽成纯逻辑之后可以注入假时钟，
/// 把「等 199ms 不展开、201ms 展开」这类边界一次性测清。
///
/// 规则与依据：
/// - 展开延迟 200ms：Cockburn IJHCS 2024 实测，200ms 太快会误展开（70% 受试者选更慢的一侧），
///   400ms 区间内用户明显偏好更慢，250ms 时偏好五五开。
/// - 收起延迟 350ms：明显长于展开延迟。若两者接近，指针掠过扇区边缘时子弧会闪。
/// - 斜向缓冲：指针从父扇区斜着移向子弧的途中会越过缝隙与相邻扇区，
///   这段过程不能触发收起，否则子弧会在指针到达前消失。
/// - 指针进入已展开的子弧后，始终取消收起计时 —— 子弧接管指针。
/// </summary>
public sealed class SubmenuHoverMachine
{
    private readonly InteractionTokens _tokens;

    private int _openParent = -1;
    private int _pendingParent = -1;
    private long _pendingSinceMs;

    private bool _closing;
    private long _closingSinceMs;

    public SubmenuHoverMachine(InteractionTokens tokens)
    {
        _tokens = tokens;
    }

    /// <summary>当前已展开子弧的父扇区索引，-1 表示没有。</summary>
    public int OpenParent => _openParent;

    /// <summary>正在等待展开的父扇区索引，-1 表示没有。</summary>
    public int PendingParent => _pendingParent;

    /// <summary>是否正在等待收起。</summary>
    public bool IsClosing => _closing;

    /// <summary>
    /// 处理一次指针落点，返回应当执行的动作。
    /// 调用方应当**按固定频率**反复调用（即使指针没动），因为"等待延迟到期"这件事
    /// 需要靠时间推进来触发 —— 只在下一次移动时才检查的话，指针停住不动就永远不会展开。
    /// </summary>
    public HoverAction OnPointer(in HoverProbe probe)
    {
        var now = probe.NowMs;

        // 指针落在已展开的子弧范围内（含缝隙）—— 子弧接管指针，取消收起。
        if (probe.Zone is HitZone.ChildSector or HitZone.ChildArcGap)
        {
            if (_closing) CancelClosing();
            _pendingParent = -1;
            return HoverAction.None;
        }

        if (probe.Zone == HitZone.Sector)
        {
            var index = probe.Index;

            // 先处理"已经在等这一项"的情况，而且必须放在最前面：
            // 否则在等待期间反复轮询会不断重置计时起点，指针停住不动就永远等不到展开。
            // （这个 bug 就是被单元测试抓到的 —— 手点几乎不可能定位。）
            if (_pendingParent == index)
            {
                if (now - _pendingSinceMs >= _tokens.HoverOpenDelayMs)
                {
                    _pendingParent = -1;
                    _openParent = index;
                    _closing = false;
                    return HoverAction.OpenNow;
                }
                return HoverAction.None;
            }

            // 指针落在已展开子弧的父扇区上 —— 保持展开，取消可能的收起。
            if (index == _openParent)
            {
                _pendingParent = -1;
                _closing = false;
                return HoverAction.None;
            }

            // 叶子扇区：不展开，但要收起已展开的子弧。
            if (!probe.ItemHasChildren)
            {
                _pendingParent = -1;
                return RequestClose(now);
            }

            // 另一个有子项的扇区。同时只能显示一个子弧，所以旧的**立即**收起
            // 而不是走收起延迟 —— 让旧的滞留 350ms 而新的 200ms 后就出现，
            // 两个子弧会重叠成一团。
            _pendingParent = index;
            _pendingSinceMs = now;

            if (_openParent >= 0)
            {
                _openParent = -1;
                _closing = false;
                return HoverAction.CloseNow;
            }

            return HoverAction.ScheduleOpen;
        }

        // 死区或环外：收起子弧，但不关闭整棵菜单（整棵菜单由点击/ Esc 处理）。
        _pendingParent = -1;
        return RequestClose(now);
    }

    /// <summary>用户点击了有子项的扇区 —— 跳过延迟立即展开。</summary>
    public HoverAction OpenImmediately(int parentIndex)
    {
        if (_openParent == parentIndex) return HoverAction.None;

        _pendingParent = -1;
        _closing = false;
        _openParent = parentIndex;
        return HoverAction.OpenNow;
    }

    /// <summary>收起延迟是否已到期。调用方在计时到期时调它确认一次。</summary>
    public HoverAction OnCloseTimerElapsed(long nowMs)
    {
        if (!_closing) return HoverAction.None;
        if (nowMs - _closingSinceMs < _tokens.HoverCloseDelayMs) return HoverAction.None;

        _openParent = -1;
        _closing = false;
        return HoverAction.CloseNow;
    }

    /// <summary>收起延迟是否已经到期（供调用方决定还要不要继续轮询）。</summary>
    public bool CloseDelayElapsed(long nowMs) =>
        _closing && nowMs - _closingSinceMs >= _tokens.HoverCloseDelayMs;

    /// <summary>整棵菜单关闭时复位。</summary>
    public void Reset()
    {
        _openParent = -1;
        _pendingParent = -1;
        _closing = false;
    }

    private HoverAction RequestClose(long now)
    {
        if (_openParent < 0) return HoverAction.None;
        if (_closing) return HoverAction.None;

        _closing = true;
        _closingSinceMs = now;
        return HoverAction.ScheduleClose;
    }

    private void CancelClosing()
    {
        _closing = false;
    }
}
