using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace WinQuick.App.Diagnostics;

/// <summary>
/// 屏幕取样器 —— 按固定间隔抓一小块**屏幕**，用相邻帧的像素差找「闪」。
///
/// 为什么要抓屏幕而不是抓我们的视觉树：闪不是「内容画错了」，而是
/// **合成器在两帧之间取走了一块还没准备好的表面** —— 视觉树里一切正常，只有屏幕像素
/// 能看见它。<c>RenderTargetBitmap</c> 那类离屏渲染在这里是无效的：它渲染的是
/// 「我们希望画成什么」，而不是「屏幕上真的出现了什么」。
///
/// 判据用**相邻帧差**而不是「与某个参考帧比」：不必假设背景是什么颜色，被测场景只要
/// 保持静止，任何与前后帧的差异都只能来自呈现层。这个判据能不能失败也是可验证的 ——
/// <c>--flash-probe</c> 里有一条「藏掉覆盖层必须被检出」的对照检查。
/// </summary>
internal sealed class ScreenSampler : IDisposable
{
    /// <summary>比较时的抽样步长（隔几个像素比一个）。整屏逐像素比没必要，慢且更容易受噪点影响。</summary>
    private const int Step = 2;

    private readonly int _x, _y, _w, _h;
    private readonly HDC _screenDc;
    private readonly HDC _memDc;
    private readonly HBITMAP _bitmap;
    private readonly HGDIOBJ _oldBitmap;
    private readonly byte[] _current;
    private readonly byte[] _previous;
    private readonly BITMAPINFO _info;
    private bool _disposed;

    public ScreenSampler(int x, int y, int width, int height)
    {
        _x = x;
        _y = y;
        _w = width;
        _h = height;

        _screenDc = PInvoke.GetDC(default);
        _memDc = PInvoke.CreateCompatibleDC(_screenDc);
        _bitmap = PInvoke.CreateCompatibleBitmap(_screenDc, width, height);
        _oldBitmap = PInvoke.SelectObject(_memDc, _bitmap);

        _current = new byte[width * height * 4];
        _previous = new byte[width * height * 4];

        // 32 位、自上而下（高度为负）—— 自下而上会让"行"与直觉相反，
        // 后面若要看具体位置会白白绕一圈。
        _info = new BITMAPINFO();
        _info.bmiHeader.biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>();
        _info.bmiHeader.biWidth = width;
        _info.bmiHeader.biHeight = -height;
        _info.bmiHeader.biPlanes = 1;
        _info.bmiHeader.biBitCount = 32;
    }

    /// <summary>抓一帧，返回与上一帧的差异（像素差总和，以及"明显变了"的像素数）。</summary>
    /// <summary>最近一次采样中"变化像素"的包围盒（行/列范围），没变化时是 null。</summary>
    public (int MinRow, int MaxRow, int MinCol, int MaxCol)? LastChangedBounds { get; private set; }

    public unsafe (long Diff, int Changed) Sample()
    {
        if (_disposed) return (0, 0);

        if (!PInvoke.BitBlt(_memDc, 0, 0, _w, _h, _screenDc, _x, _y, ROP_CODE.SRCCOPY))
            return (0, 0);

        fixed (byte* p = _current)
        {
            var info = _info;
            if (PInvoke.GetDIBits(_memDc, _bitmap, 0, (uint)_h, p, &info, DIB_USAGE.DIB_RGB_COLORS) == 0)
                return (0, 0);
        }

        long diff = 0;
        var changed = 0;
        var minRow = int.MaxValue;
        var maxRow = int.MinValue;
        var minCol = int.MaxValue;
        var maxCol = int.MinValue;

        for (var row = 0; row < _h; row += Step)
        {
            var offset = row * _w * 4;
            for (var col = 0; col < _w; col += Step)
            {
                var i = offset + col * 4;
                var db = Math.Abs(_current[i] - _previous[i]);
                var dg = Math.Abs(_current[i + 1] - _previous[i + 1]);
                var dr = Math.Abs(_current[i + 2] - _previous[i + 2]);

                var pixel = db + dg + dr;
                diff += pixel;
                if (pixel > 24)
                {
                    changed++;
                    if (row < minRow) minRow = row;
                    if (row > maxRow) maxRow = row;
                    if (col < minCol) minCol = col;
                    if (col > maxCol) maxCol = col;
                }
            }
        }

        LastChangedBounds = changed == 0 ? null : (minRow, maxRow, minCol, maxCol);

        Buffer.BlockCopy(_current, 0, _previous, 0, _current.Length);
        return (diff, changed);
    }

    /// <summary>取当前这一帧的副本（用于"动作前后是否逐像素相同"的比对）。</summary>
    public byte[] CaptureFrame()
    {
        Sample();                       // 先抓一帧进 _current
        return (byte[])_current.Clone();
    }

    /// <summary>两帧之间有多少差别（与相邻帧差同一个度量，可直接和噪声底比）。</summary>
    public static (long Diff, int Changed) Compare(byte[] a, byte[] b, int width, int height)
    {
        long diff = 0;
        var changed = 0;

        for (var row = 0; row < height; row += Step)
        {
            var offset = row * width * 4;
            for (var col = 0; col < width; col += Step)
            {
                var i = offset + col * 4;
                var pixel = Math.Abs(a[i] - b[i])
                          + Math.Abs(a[i + 1] - b[i + 1])
                          + Math.Abs(a[i + 2] - b[i + 2]);

                diff += pixel;
                if (pixel > 24) changed++;
            }
        }

        return (diff, changed);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        PInvoke.SelectObject(_memDc, _oldBitmap);
        PInvoke.DeleteObject(_bitmap);
        PInvoke.DeleteDC(_memDc);
        PInvoke.ReleaseDC(default, _screenDc);
    }
}
