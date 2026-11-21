# 执行书：fix-autostart-rollback-tray-settings

目标文件：src/WinQuick.App/Hosting/ResidentHost.cs（仅此一个）

## 补丁 1（缺陷二：注入托盘设置回调）

定位：约第 70-73 行。先 Read 定向行段核对，old 必须逐字符匹配（含缩进与空行）：

old:
```
        _session = new SessionWatcher();
        _tray = new TrayHost(_overlay);

        _hotkeys.Pressed += (_, _) => _overlay.Toggle();
```

new:
```
        _session = new SessionWatcher();
        _tray = new TrayHost(_overlay);

        // 托盘「设置…」的回调在这条装配线上注入：托盘自己不认识设置窗口，
        // 而漏掉这行时菜单项是静默空操作（OpenSettings?.Invoke() 对 null），
        // 既不弹窗也不进崩溃日志 —— 用户看到的就是「点了没反应」。
        _tray.OpenSettings = OpenSettings;

        _hotkeys.Pressed += (_, _) => _overlay.Toggle();
```

## 补丁 2（缺陷一：PersistSettings 不再按配置回写注册表）

定位：约第 772-788 行（`private void PersistSettings()` 方法体）。先 Read 定向行段核对，old 必须逐字符匹配：

old:
```
    private void PersistSettings()
    {
        if (_pendingSettings is not { } config) return;
        _pendingSettings = null;

        var autoStartOk = AutoStart.SetEnabled(config.Startup.AutoStart);

        if (!_config.SaveConfig(config, out var error))
        {
            _tray.ShowBalloon("WinQuick 无法保存设置", error ?? "未知原因");
            return;
        }

        if (!autoStartOk)
            _tray.ShowBalloon("WinQuick", "开机自启写入注册表失败（可能被组策略或安全软件限制）。");
    }
```

new:
```
    private void PersistSettings()
    {
        if (_pendingSettings is not { } config) return;
        _pendingSettings = null;

        // 这里刻意**不**写开机自启：注册表是事实来源（托盘菜单与设置面板的开关
        // 都直写注册表），配置里的 autoStart 只是首启种子。若在这里按配置回写，
        // 换肤（OnSkinChanged 以旧配置为基底构造快照）会把用户在托盘勾的自启
        // 静默删掉，而托盘菜单的勾选状态只在构造时读一次，界面上仍显示已勾选。
        if (!_config.SaveConfig(config, out var error))
        {
            _tray.ShowBalloon("WinQuick 无法保存设置", error ?? "未知原因");
        }
    }
```

## 验证步骤

1. `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo`
   预期：Build succeeded / 0 Error（warning 与改动前一致即可）
2. `git diff --stat`
   预期：仅 src/WinQuick.App/Hosting/ResidentHost.cs 与两个新文档变更

不匹配即停：任一 old 无法逐字符匹配时，停止并报告实际文件内容，不得猜测性修改。
