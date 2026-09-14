using System.IO;
using System.Text;
using WinQuick.App.Diagnostics;

namespace WinQuick.App;

/// <summary>
/// 崩溃日志。常驻托盘程序的失败形态很隐蔽 —— 热键不响应、托盘图标消失，
/// 用户看到的现象和真正的原因之间没有线索，所以未处理异常必须落盘。
/// 日志放 %LOCALAPPDATA%\WinQuick\logs，与配置和图标缓存分开。
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinQuick", "logs");

    public static void Write(string source, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, "crash.log");

                var sb = new StringBuilder();
                sb.AppendLine(new string('-', 78));
                sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {source}");
                sb.AppendLine($"PID={Environment.ProcessId}  版本={typeof(CrashLog).Assembly.GetName().Version}");
                sb.AppendLine(ex?.ToString() ?? "(无异常对象)");

                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                Console.WriteLine($">>> 崩溃日志: {path}");
            }
        }
        catch
        {
            // 记日志本身失败时不能再次抛异常 —— 那会把崩溃原因替换掉。
        }
    }

    public static string? ReadTail(int maxChars = 4000)
    {
        try
        {
            var path = Path.Combine(LogDirectory, "crash.log");
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch
        {
            return null;
        }
    }
}
