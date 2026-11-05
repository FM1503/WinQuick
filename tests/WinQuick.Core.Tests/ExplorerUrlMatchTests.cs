using WinQuick.App.Interop;

namespace WinQuick.Core.Tests;

/// <summary>
/// 「新标签导航成功了吗」这一判据的比较口径。
///
/// 为什么值得单测：explorer 的 <c>IWebBrowser2.LocationURL</c> 给的是**带 scheme 的绝对 URI**
/// （<c>file:///C:/Program%20Files</c>），而产品代码手里是裸路径（<c>C:\Program Files</c>）。
/// 早期实现只做百分号还原就 Equals，结果**恒为假** —— 导航明明成功，校验却永远超时，
/// 探针把它误读成「新标签没导航过去」，并因此走进兜底、关掉了本来正确的窗口。
/// 这类「比较口径不一致」的 bug 不会有异常、只会让一切看起来都没成功，只有单测能锁住。
/// </summary>
public class ExplorerUrlMatchTests
{
    [Theory]
    // 真实观测到的形态：带 file:/// 前缀、空格被转义。
    [InlineData("file:///C:/Program%20Files", @"C:\Program Files")]
    [InlineData("file:///D:/Work/WinQuick", @"D:\Work\WinQuick")]
    // 尾斜杠在两边都可能出现；资源管理器给出目录地址时常常带一个。
    [InlineData("file:///C:/Windows/", @"C:\Windows")]
    [InlineData("file:///C:/Windows", @"C:\Windows\")]
    [InlineData("file:///C:/Program%20Files/", @"C:\Program Files")]
    // 大小写不敏感：盘符与路径大小写由文件系统决定，不该影响判定。
    [InlineData("file:///c:/windows", @"C:\Windows")]
    public void 资源管理器地址与本地路径视为同一路径(string url, string path)
    {
        Assert.True(ExplorerTabs.UrlMatches(url, path));
    }

    [Theory]
    [InlineData(null, @"C:\Windows")]
    [InlineData("", @"C:\Windows")]
    [InlineData("   ", @"C:\Windows")]
    // 新标签默认停在「此电脑」，它的 LocationURL 是空串 —— 空串不能匹配任何真实路径，
    // 否则「默认页也算导航成功」会把整条链的校验变成摆设。
    [InlineData("about:blank", @"C:\Windows")]
    [InlineData("file:///C:/Windows", @"C:\Program Files")]
    [InlineData("file:///D:/Work/WinQuic", @"D:\Work\WinQuick")]
    [InlineData(@"D:\Work\WinQuick", @"C:\Work\WinQuick")]
    public void 空值与不同路径不匹配(string? url, string path)
    {
        Assert.False(ExplorerTabs.UrlMatches(url, path));
    }
}
