using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace WinQuick.App.Updates;

/// <summary>
/// Velopack 更新检查。所有网络失败一律静默降级 —— 更新是增强不是主功能，
/// 断网/限流/解析失败不该打扰任何路径（spec §3.4）。
/// 仓库固定为 github.com/FM1503/WinQuick，仅正式版（prerelease: false）。
/// </summary>
public sealed class UpdateChecker
{
    private const string RepoUrl = "https://github.com/FM1503/WinQuick";

    private readonly UpdateManager? _manager;

    public UpdateChecker()
    {
        // 开发目录直接运行（未经安装器）时没有本地版本元数据，UpdateManager
        // 构造/检查会失败 —— 捕获后 IsSupported=false，调用方一律按无更新处理。
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, prerelease: false));
        }
        catch
        {
            _manager = null;
        }
    }

    /// <summary>当前安装方式是否支持在线更新（dotnet run / 旧 publish 目录不支持）。</summary>
    public bool IsSupported => _manager is { IsInstalled: true };

    /// <summary>最近一次检查命中的更新目标；DownloadAsync/ApplyAndRestart 以它为对象。</summary>
    public UpdateInfo? CachedTarget { get; private set; }

    /// <summary>检查新版。无更新/不可用/失败都返回 null（不抛出）。</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!IsSupported || _manager is null) return null;
        try
        {
            CachedTarget = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            return CachedTarget;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>下载到 Velopack 本地缓存（增量优先）。失败返回 false。</summary>
    public async Task<bool> DownloadAsync(UpdateInfo target)
    {
        if (!IsSupported || _manager is null) return false;
        try
        {
            await _manager.DownloadUpdatesAsync(target).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>应用更新并重启。只应被用户显式动作触发（spec：绝不自动重启）。</summary>
    public void ApplyAndRestart(UpdateInfo target)
    {
        if (!IsSupported || _manager is null) return;
        _manager.ApplyUpdatesAndRestart(target);
    }

    /// <summary>当前运行版本（三段）；开发运行解析不出时为 null。</summary>
    public static string? CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
}
