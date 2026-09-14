using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinQuick.App.Diagnostics;

/// <summary>
/// M0 探针的输出出口。
/// WinExe 默认没有附加控制台，只能靠 Console.WriteLine 会丢输出，
/// 所以统一写文件；同时在能拿到控制台时顺带回显一份。
/// </summary>
public static class ProbeOutput
{
    private static readonly StringBuilder Buffer = new();
    private static string? _filePath;

    /// <param name="name">输出文件名（不含扩展名）。</param>
    /// <param name="stage">里程碑子目录，默认 m0。M2 之后的探针产物应与 M0 的实测数据分开存放。</param>
    public static void Initialize(string name, string stage = "m0")
    {
        var dir = Path.Combine(Paths.RepoRoot, "artifacts", stage);
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, name + ".txt");

        Buffer.Clear();
        Line($"# {name}");
        Line($"# 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"# 进程: {Environment.ProcessPath}");
        Line();
    }

    public static void Line(string text = "")
    {
        Buffer.AppendLine(text);
        Console.WriteLine(text);
    }

    public static void Section(string title)
    {
        Line();
        Line("=== " + title + " ===");
    }

    public static void Kv(string key, object? value)
    {
        Line($"  {key,-34} {value}");
    }

    /// <summary>把缓冲区落盘。即使探针中途崩溃也尽量写出来。</summary>
    public static void Flush()
    {
        if (_filePath is null) return;
        try
        {
            File.WriteAllText(_filePath, Buffer.ToString(), new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine(">>> 已写入 " + _filePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine(">>> 写文件失败: " + ex.Message);
        }
    }
}

public static class Paths
{
    /// <summary>仓库根目录（从可执行文件位置往上找到含解决方案文件的那层）。</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        // 必须同时认 .sln 和 .slnx —— .NET 10 的 `dotnet new sln` 生成的是
        // 新的 XML 格式 WinQuick.slnx。只认 .sln 会找不到根目录，
        // 悄悄退化到当前工作目录（从资源管理器启动时就是 bin 目录），
        // 结果探针产物写到了错误的位置。
        string[] markers = ["WinQuick.slnx", "WinQuick.sln"];

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var marker in markers)
                if (File.Exists(Path.Combine(dir.FullName, marker)))
                    return dir.FullName;
            dir = dir.Parent;
        }

        // 找不到根目录时不返回工作目录（那会把产物散落到不可预测的位置），
        // 而是落到一个明确的兜底位置，便于发现异常。
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinQuick", "probe-output");
    }

    public static long WorkingSet => Environment.WorkingSet;

    public static long PrivateBytes
    {
        get
        {
            using var p = Process.GetCurrentProcess();
            return p.PrivateMemorySize64;
        }
    }
}
