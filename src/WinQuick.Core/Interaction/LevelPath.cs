using WinQuick.Core.Config;

namespace WinQuick.Core.Interaction;

/// <summary>
/// 层级路径：从第一层往下钻，记录每一级在它所在层里的下标。
///
/// ## 它解决什么
///
/// 轮盘同时只显示两级：**环 1 = 父层（整圈），环 2 = 当前层（子弧）**。
/// 点环 2 里的分组扇区就再进一层 —— 旧的环 2 整层搬到环 1 的位置，
/// 所以"当前看到的两级"完全由这条路径决定，而不是由"悬停到哪一项"决定。
///
/// 这也是把悬停展开换成点击展开之后必须补上的东西：旧实现里
/// 「环 2 显示谁的子项」只有一个下标（`ChildArcParent`），深度上限天然是 2。
///
/// ## 为什么是纯逻辑
///
/// 推进/回退/换分支这三条算术很容易写错，而错了的表现是"点下去层级跳错"，
/// 靠手点验证既慢又容易漏（尤其是「进入第三层」这条路径要 3 次点击才到）。
/// 抽成纯逻辑之后可以注入固定菜单，把每一层的窗口内容逐项断言。
/// </summary>
public sealed class LevelPath : IEquatable<LevelPath>
{
    /// <summary>在第一层（什么都没展开）。</summary>
    public static readonly LevelPath AtRoot = new([]);

    private readonly int[] _steps;

    private LevelPath(int[] steps) => _steps = steps;

    public static LevelPath Of(IEnumerable<int> steps) => new([.. steps]);

    /// <summary>下钻链：第 i 项是"第 i 层里被展开的那一项"的下标。</summary>
    public IReadOnlyList<int> Steps => _steps;

    public int Depth => _steps.Length;

    public bool IsRoot => _steps.Length == 0;

    /// <summary>
    /// 环 1 显示的层。深度 0 时是一级；更深时是**父层** ——
    /// 「旧的环 2 搬到环 1」这句话在这里落成一个减法。
    /// </summary>
    public int Ring1Level => Math.Max(Depth - 1, 0);

    /// <summary>
    /// 环 1 里"被展开的那一项"（也就是环 2 的父项）在环 1 中的下标；-1 = 环 2 为空。
    /// 子弧的角度锚点、父扇区的高亮、归位旋转都读它。
    /// </summary>
    public int ParentIndexInRing1 => Depth >= 1 ? _steps[^1] : -1;

    /// <summary>点环 2 里的分组扇区：深度 +1（旧的环 2 整层搬到环 1）。</summary>
    public LevelPath Descend(int index) => new([.. _steps, index]);

    /// <summary>
    /// 点环 1 里的分组扇区：**深度不变，只换分支** ——
    /// 环 1 还是那一层（它没变），环 2 换成被点那一项的子项。
    /// 深度 0 时它就是"展开第一层"（结果深度 1）。
    /// </summary>
    public LevelPath SwitchBranch(int index) => new([.. _steps.Take(Math.Max(0, Depth - 1)), index]);

    /// <summary>返回上一层。</summary>
    public LevelPath Back() => Depth == 0 ? this : new(_steps[..^1]);

    /// <summary>环 1 里第 index 项这一项的完整路径（用于执行动作）。</summary>
    public IReadOnlyList<int> StepsToRing1(int index)
        => [.. _steps.Take(Math.Max(0, Depth - 1)), index];

    /// <summary>环 2 里第 index 项这一项的完整路径（用于执行动作）。</summary>
    public IReadOnlyList<int> StepsToRing2(int index) => [.. _steps, index];

    /// <summary>按路径取出某一项（任意深度都能取到 —— 这正是旧实现缺的那条路）。</summary>
    public static MenuItemSpec? Resolve(MenuSpec menu, IReadOnlyList<int> steps)
        => MenuLevels.Resolve(menu, steps);

    /// <summary>环 1 要画的项（父层整层）。</summary>
    public IReadOnlyList<MenuItemSpec> Ring1Items(MenuSpec menu)
        => MenuLevels.ItemsAt(menu, Ring1Level, _steps);

    /// <summary>环 2 要画的项（当前层）。深度 0 时为空 —— 子弧要点一下才出来。</summary>
    public IReadOnlyList<MenuItemSpec> Ring2Items(MenuSpec menu)
        => Depth == 0 ? [] : MenuLevels.ItemsAt(menu, Depth, _steps);

    /// <summary>
    /// 环 2 的父项（子弧属于它）；深度 0 时为 null。
    ///
    /// **父项就是路径最后一段指向的那一项**（环 2 画的是它的子项）——
    /// 写成 `steps[..^1]` 会在深度 1 时解析空路径得到 null，
    /// 表现为"环 2 该有项却根本没展开"（这条是 --level-probe 抓出来的）。
    /// </summary>
    public MenuItemSpec? Ring2Parent(MenuSpec menu)
        => Depth == 0 ? null : MenuLevels.Resolve(menu, _steps);

    public bool Equals(LevelPath? other)
        => other is not null && _steps.AsSpan().SequenceEqual(other._steps);

    public override bool Equals(object? obj) => Equals(obj as LevelPath);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var step in _steps) hash.Add(step);
        return hash.ToHashCode();
    }

    public override string ToString() => _steps.Length == 0 ? "(第一层)" : $"[{string.Join(",", _steps)}]";
}

/// <summary>
/// 菜单树按"层"取项的算术。层号从 0 开始（0 = 一级），与 <see cref="LevelPath.Ring1Level"/> 同一套约定。
/// </summary>
public static class MenuLevels
{
    /// <summary>
    /// 第 <paramref name="level"/> 层的项。
    ///
    /// **路径不够长时返回空** —— 这一层还没被下钻到，它就不存在。
    /// 少了这条判断，"还没展开时的第 1 层"会返回一级自己（写出过一个这样的原型）。
    /// </summary>
    public static IReadOnlyList<MenuItemSpec> ItemsAt(
        MenuSpec menu, int level, IReadOnlyList<int> steps)
    {
        if (level <= 0) return menu.Items;
        if (steps.Count < level) return [];

        var children = menu.Items;
        for (var i = 0; i < level; i++)
        {
            var index = steps[i];
            if (index < 0 || index >= children.Count) return [];
            children = children[index].Children;
        }

        return children;
    }

    /// <summary>按路径取出一项；路径为空或中途越界则返回 null。</summary>
    public static MenuItemSpec? Resolve(MenuSpec menu, IReadOnlyList<int> steps)
    {
        if (steps.Count == 0) return null;

        var children = menu.Items;
        MenuItemSpec? node = null;
        foreach (var index in steps)
        {
            if (index < 0 || index >= children.Count) return null;
            node = children[index];
            children = node.Children;
        }

        return node;
    }
}
