---
slug: fix-badgefill-token-and-about-theme
tier: M
status: approved
date: 2026-11-21
code-files: src/WinQuick.App/Design/TokenResources.cs, src/WinQuick.App/Radial/SettingsPanel.xaml, src/WinQuick.App/WinQuick.App.csproj
review: quick
---

# 变更单：队列添加无反应（缺 Color.BadgeFill 键）+「关于」分区开关文字未适配主题

## 缺陷四：启动队列「添加…」点击无反应

根因（安装版 crash.log 2026-11-21 11:10 三条实录）：`SystemLocationPicker.BuildPlaceholder` 取 `TokenResources.Brush("Color.BadgeFill")` 抛「资源不存在」→ 选择器构造失败 → `PickMenuItemForAdd` 的 catch 兜底写日志，用户看到点击无反应。`Color.BadgeFill` 键从未在 `TokenResources.Install` 注册（tokens.json 的 badgeFill 是轮盘令牌通道；面板键需显式 Set）。上轮队列模式新代码引用了未注册键。

改动：Install 颜色区补注册 `Color.BadgeFill`，值取 `TokenBrushes.Brush(color.BadgeFill, color.BadgeFillAlpha)`（与轮盘角标同源，换肤跟随，皮肤可覆盖为 #FFFFFF/0.45）。

## 缺陷一：「启动时自动检查更新」文字颜色未适配主题

根因：PageAbout 的 AutoUpdateBox 用原生 CheckBox + Content，文字走系统前景色，深色面板下不随主题。修法：补 `Foreground="{DynamicResource Wq.Color.Text}"`（Install 换肤时重写资源即跟随，见 TokenResources.cs:30 注释）。

## 附带：版本 1.1.0 → 1.1.1（发版伴随，走 CI + 在线更新链路交付）

## 验收标准（用户实测）

1. 启动队列分区点「添加…」弹出选择面板（含「所有文件」第一档与轮盘菜单来源清单）；
2. 「关于」分区「启动时自动检查更新」文字颜色与面板主题一致；
3. v1.1.1 发布后，用户在「关于」点「检查更新」→ 下载就绪 → 「立即更新并重启」→ 重启后版本显示 1.1.1（同时完成 AC6 闭环）。

## 验证

- `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` 0 error；
- reviewer 快审：docs/changes/fix-badgefill-token-and-about-theme.review-report.json。

## 第 2 步：机械应用三处补丁

### P1 src/WinQuick.App/Design/TokenResources.cs
old:
```
        Set("Color.DeadZoneStroke", TokenBrushes.Brush(color.DeadZoneStroke, color.DeadZoneStrokeAlpha));

        // 状态色：成功/警告。不放进 tokens —— 它们是语义色，与设计基线无关。
```
new:
```
        Set("Color.DeadZoneStroke", TokenBrushes.Brush(color.DeadZoneStroke, color.DeadZoneStrokeAlpha));
        // 角标底色：SystemLocationPicker 无图标项的占位块用，与轮盘角标同源、换肤跟随。
        Set("Color.BadgeFill", TokenBrushes.Brush(color.BadgeFill, color.BadgeFillAlpha));

        // 状态色：成功/警告。不放进 tokens —— 它们是语义色，与设计基线无关。
```

### P2 src/WinQuick.App/Radial/SettingsPanel.xaml
old:
```
                                    <CheckBox x:Name="AutoUpdateBox" Margin="0,12,0,0"
                                              Content="启动时自动检查更新" Click="OnAutoUpdateChanged" />
```
new:
```
                                    <CheckBox x:Name="AutoUpdateBox" Margin="0,12,0,0"
                                              Foreground="{DynamicResource Wq.Color.Text}"
                                              Content="启动时自动检查更新" Click="OnAutoUpdateChanged" />
```

### P3 src/WinQuick.App/WinQuick.App.csproj
old:
```
    <Version>1.1.0</Version>
```
new:
```
    <Version>1.1.1</Version>
```

## 第 3 步：验证并汇报

1. `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` → 预期 0 error 0 warning；
2. `dotnet test tests/WinQuick.Core.Tests --nologo` → 预期 312 全绿（基线不回退）；
3. `git diff --stat` → 仅上述 3 文件。

汇报：每补丁 完成/停报 + 三条验证关键输出。

## M 级补充（reviewer 快审后升级）

改动 3 文件且含 csproj 版本号（配置变更），超出 S 判据，按 M 级补齐：受影响测试报告见 docs/changes/fix-badgefill-token-and-about-theme.test-report.json；reviewer 快审已通过（pass-with-issues，报告同目录 review-report.json）。

## 根因补充（reviewer P2）

Color.BadgeFill 的消费点共两处：SystemLocationPicker.BuildPlaceholder（队列添加，crash.log 实锤）与 SettingsPanel.xaml.cs:998（修复前同炸，本次注册一并覆盖，无需第二处改动）。
