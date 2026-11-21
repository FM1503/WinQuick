using WinQuick.App.Config;
using WinQuick.Core.Config;

namespace WinQuick.Core.Tests;

/// <summary>
/// updates 配置段的三条底线：旧配置缺段=默认开（升级不丢静默检查）、显式关闭能往返、
/// 渲染模板必须含该段（漏段=保存即重置 —— 皮肤字段踩过的坑，见 docs/changes/fix-skin-form-persistence.md）。
/// </summary>
public class UpdateConfigTests
{
    [Fact]
    public void 旧配置缺段_默认开启静默检查()
    {
        var parsed = AppConfig.Parse("""{ "version": 1 }""");
        Assert.True(parsed.Updates.AutoCheck);
    }

    [Fact]
    public void 显式关闭_渲染解析往返保持()
    {
        var config = new AppConfig { Updates = new UpdateConfig { AutoCheck = false } };
        var round = AppConfig.Parse(ConfigStore.RenderConfig(config));
        Assert.False(round.Updates.AutoCheck);
    }

    [Fact]
    public void 渲染模板含_updates段_且默认渲染往返为开()
    {
        var round = AppConfig.Parse(ConfigStore.RenderConfig(new AppConfig()));
        Assert.True(round.Updates.AutoCheck);
    }
}
