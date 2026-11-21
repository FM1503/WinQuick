using System;

namespace WinQuick.App;

/// <summary>
/// 自定义入口。Velopack 的引导必须发生在任何 WPF 初始化之前 ——
/// 更新安装后的首次启动由它处理完安装钩子再放行（spec §3.4）。
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        Velopack.VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
