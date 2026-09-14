using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WinQuick.App.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace WinQuick.App.Interop;

/// <summary>
/// 从 shell 取程序/文件的图标。
///
/// 为什么用 <c>IShellItemImageFactory</c> 而不是 <c>SHGetFileInfo</c> 或
/// <c>Icon.ExtractAssociatedIcon</c>：后两者只能给固定的小尺寸（典型 32×32）。
/// 本机是 175% 缩放，34 DIP 的图标需要约 60 物理像素，用 32×32 拉大会明显发糊。
/// IShellItemImageFactory 可以按请求的尺寸取图标，而且能拿到 .lnk、UWP 应用、
/// 文件缩略图等 shell 才知道怎么渲染的东西。
///
/// 缓存到磁盘：每次启动都向 shell 提取几十个图标是有感知的延迟，
/// 而图标基本不变，所以按「路径 + 修改时间」做键缓存成 PNG。
/// </summary>
public static class ShellIcons
{
    /// <summary>请求的图标像素尺寸。取 2 的幂且比实际显示大，缩放时更清晰。</summary>
    private const int RequestedSize = 96;

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinQuick", "iconcache");

    private static readonly Dictionary<string, BitmapSource?> MemoryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// 取图标。<paramref name="path"/> 可以是文件路径，也可以是 shell 命名空间路径
    /// （<c>shell:AppsFolder\&lt;AUMID&gt;</c>）—— UWP 应用只能用后者。
    /// 取不到时返回 null，由调用方画占位图；不该因为一个图标失败就整项不显示。
    /// </summary>
    public static BitmapSource? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (Gate)
        {
            if (MemoryCache.TryGetValue(path, out var cached)) return cached;

            var icon = LoadFromDiskCache(path) ?? ExtractFromShell(path);
            MemoryCache[path] = icon;
            return icon;
        }
    }

    private static string CacheKey(string path)
    {
        var stamp = 0L;
        try
        {
            if (File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            // 取不到时间戳时退化为 0，仍然能命中缓存，只是程序更新后不会自动失效。
        }

        // 路径可能含非法文件名字符，用哈希做键名。
        var hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(path + "|" + stamp));
        return Convert.ToHexString(hash)[..24] + ".png";
    }

    private static string CachePath(string path) => Path.Combine(CacheDirectory, CacheKey(path));

    private static BitmapSource? LoadFromDiskCache(string path)
    {
        try
        {
            var file = CachePath(path);
            if (!File.Exists(file)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // 缓存损坏就当没缓存，重新提取。
            return null;
        }
    }

    private static void SaveToDiskCache(string path, BitmapSource icon)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(icon));
            using var stream = File.Create(CachePath(path));
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            // 缓存写不进去（磁盘只读、权限受限）不该影响显示，只是下次还要重新提取。
            CrashLog.Write("写图标缓存失败", ex);
        }
    }

    private static BitmapSource? ExtractFromShell(string path)
    {
        // shell 命名空间路径（shell:AppsFolder\<AUMID>）不是文件系统路径，
        // 直接交给 shell 去解析 —— UWP 应用的图标只能这么取。
        // 传统文件路径则先做存在性检查，避免把"写错了路径"和"shell 解析失败"混为一谈。
        var isShellPath = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

        if (!isShellPath && !File.Exists(path) && !Directory.Exists(path)) return null;

        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            if (hr != 0 || factory is null) return null;

            try
            {
                var size = new SIZE { cx = RequestedSize, cy = RequestedSize };
                // SIIGBF_ICONONLY：只要图标，不要缩略图。
                // 缩略图对图片文件很好看，但对一个快捷启动器来说，图标更稳定也更易辨认。
                const int SIIGBF_ICONONLY = 0x4;
                // SIIGBF_BIGGERSIZEOK：允许返回比请求更大的位图（用于 DPI 缩放）。
                const int SIIGBF_BIGGERSIZEOK = 0x1;

                var hrImage = factory.GetImage(size, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hBitmap);
                if (hrImage != 0 || hBitmap == IntPtr.Zero) return null;

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();

                SaveToDiskCache(path, source);
                return source;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write($"提取图标失败: {path}", ex);
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                PInvoke.DeleteObject((Windows.Win32.Graphics.Gdi.HGDIOBJ)hBitmap);
        }
    }

    /// <summary>缓存的图标数量与目录大小，供诊断用。</summary>
    public static (int Count, long Bytes) CacheStats()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory)) return (0, 0);
            var files = Directory.GetFiles(CacheDirectory, "*.png");
            return (files.Length, files.Sum(f => new FileInfo(f).Length));
        }
        catch
        {
            return (0, 0);
        }
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }
}
