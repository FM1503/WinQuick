using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace WinQuick.App.Radial;

/// <summary>
/// 把探针定义的"粒子场"挂到轮盘画布上。
///
/// 为什么要这样转一手：粒子场是**探针的实验对象**，属于 M0 目录；
/// 而轮盘视图不该知道探针的存在。这里用反射调它的 Attach/Detach/Step，
/// 只依赖方法名，不产生编译期依赖 —— 生产代码里删掉这个文件也不会影响功能。
/// </summary>
internal static class ParticleFieldHost
{
    public static void Attach(Canvas canvas, Point center, object? field)
    {
        if (field is null) return;

        field.GetType().GetMethod("Attach")?.Invoke(field, [canvas, center, 360.0]);
    }
}
