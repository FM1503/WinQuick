namespace WinQuick.Core.Geometry;

/// <summary>平面点。不依赖 WPF，Core 要保持纯粹可单测。</summary>
public readonly record struct Vec2(double X, double Y)
{
    public double Length => Math.Sqrt(X * X + Y * Y);

    public double AngleDeg
    {
        get
        {
            // 角度约定：0° = 正上方，顺时针为正。与 Kando 一致，也符合钟面直觉。
            var deg = Math.Atan2(X, -Y) * 180.0 / Math.PI;
            return deg < 0 ? deg + 360.0 : deg;
        }
    }

    public static Vec2 FromPolar(double radius, double angleDeg)
    {
        var rad = angleDeg * Math.PI / 180.0;
        return new Vec2(radius * Math.Sin(rad), -radius * Math.Cos(rad));
    }
}

/// <summary>一个扇区占据的角度区间，半开区间 [Start, End)。</summary>
public readonly record struct SectorSpan(double StartDeg, double EndDeg)
{
    public double SweepDeg => EndDeg - StartDeg;
    public double CenterDeg => StartDeg + SweepDeg / 2.0;

    public bool Contains(double angleDeg)
    {
        var a = Normalize(angleDeg);
        var s = Normalize(StartDeg);
        var e = Normalize(EndDeg);
        return s < e
            ? a >= s && a < e
            : a >= s || a < e;    // 跨越 0° 的区间
    }

    public static double Normalize(double deg)
    {
        var d = deg % 360.0;
        return d < 0 ? d + 360.0 : d;
    }
}

/// <summary>命中测试的落点描述。</summary>
public enum HitZone
{
    /// <summary>中心死区 —— 定义为"取消整棵菜单"，不会选中任何项。</summary>
    DeadZone,
    Sector,
    /// <summary>在环外 —— 不选中任何项。</summary>
    Outside,
}

public readonly record struct HitResult(HitZone Zone, int Index, double Distance, double AngleDeg);
