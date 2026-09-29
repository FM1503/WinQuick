using WinQuick.Core.Interaction;

namespace WinQuick.Core.Tests;

/// <summary>
/// 中心（死区）分区：左半 = Esc（关闭），右半 = 返回上一层。
///
/// 角度约定是项目里那一套（0° = 正上、顺时针），左半 = (180°, 360°)。
///
/// ## 为什么全部打「内部点」而不打边界
///
/// 第一版实现把左半写成了上半圆（`angle &gt;= 270 || angle &lt; 90`），
/// 这套测试当时的断言也跟着写错（把"正上 = 左半"当成了预期）；
/// 探针又只点了 3 点钟和 9 点钟 —— 那两个点**恰好在"上半/下半"与"左半/右半"
/// 两种划分下结论一致**，于是实现、单测、探针三方全绿，全是假象。
/// 打内部点（如 10:30 = 左上、4:30 = 右下）才能把"划错方向"这类 bug 真的暴露出来。
/// </summary>
public class CenterZonesTests
{
    [Theory]
    [InlineData(270, true)]     // 9:00 正左
    [InlineData(315, true)]     // 10:30 左上 —— 内部点，专门抓"划成上半"的错
    [InlineData(225, true)]     // 7:30 左下 —— 内部点，专门抓"划成下半"的错
    [InlineData(359.9, true)]
    [InlineData(180.1, true)]
    [InlineData(0, false)]      // 12:00：边界归右半
    [InlineData(90, false)]     // 3:00 正右
    [InlineData(45, false)]     // 1:30 右上 —— 内部点
    [InlineData(135, false)]    // 4:30 右下 —— 内部点
    [InlineData(179.9, false)]  // 6:00 边界内侧：归右半
    [InlineData(180, false)]    // 6:00 边界本点：归右半（边界必须有确定归属）
    public void 左右两半的分界(double angle, bool expectEscHalf) =>
        Assert.Equal(expectEscHalf, CenterZones.IsEscHalf(angle));

    [Fact]
    public void 负角度与超过一圈的角度都按同一套归一化处理()
    {
        // -90 归一化到 270（正左）→ Esc 半
        Assert.Equal(270, CenterZones.Normalize(-90));
        Assert.True(CenterZones.IsEscHalf(-90));

        // 630 = 270 + 360（正左）
        Assert.Equal(270, CenterZones.Normalize(630));
        Assert.True(CenterZones.IsEscHalf(630));

        // -45 归一化到 315（左上）→ Esc 半；+45（右上）→ 不是
        Assert.True(CenterZones.IsEscHalf(-45));
        Assert.False(CenterZones.IsEscHalf(45));
    }

    [Fact]
    public void 返回上一层只在深于第一层时可用()
    {
        Assert.False(CenterZones.IsBackAvailable(0));
        Assert.True(CenterZones.IsBackAvailable(1));
        Assert.True(CenterZones.IsBackAvailable(9));
    }
}
