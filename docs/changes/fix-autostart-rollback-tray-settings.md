---
slug: fix-autostart-rollback-tray-settings
tier: S
status: approved
date: 2026-11-21
code-files: src/WinQuick.App/Hosting/ResidentHost.cs
review: quick
---

# 变更单：开机自启被静默回滚 + 托盘「设置…」无反应

## 缺陷一：勾选开机自启后，注册表项被静默删除（开机不自启）

### 根因（实测证据链）

- 勾选（托盘或设置面板）时 `AutoStart.SetEnabled(true)` 直写 HKCU Run，当时成功；
- `ResidentHost.PersistSettings` 每次保存设置都执行 `AutoStart.SetEnabled(config.Startup.AutoStart)`，即**用配置文件里的值反向覆盖注册表**；
- `OnSkinChanged`（换肤/换形态）以 `_config.Config with { Appearance = ... }` 构造保存快照，`Startup` 沿用配置旧值（本机 config.json 中 `startup.autoStart: false`，托盘勾选不写配置）；
- 结果：勾选自启 → 换一次皮肤 → 500ms 去抖后 PersistSettings 用配置里的 false 调 `SetEnabled(false)` → 注册表项被删；托盘菜单勾选态仅在构造时读一次，界面仍显示已勾选，形成「看起来开着、实际已删」。
- 2026-11-21 实测：`reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 无 WinQuick 条目。

### 改动

`ResidentHost.PersistSettings`：删除 `var autoStartOk = AutoStart.SetEnabled(config.Startup.AutoStart);` 与其后的失败气泡分支。注册表此后只由两条路径写入：用户在托盘/设置面板的开关操作（直写）、首启种子（`ResidentHost.Start` 中 `DefaultsWereCreated` 分支，已有）。这正是源码注释声称的「注册表是事实来源」。

## 缺陷二：托盘菜单「设置…」点击无反应

### 根因

`TrayHost.OpenSettings` 属性（TrayHost.cs:25）全仓无任何赋值点：`ResidentHost` 构造处 `new TrayHost(_overlay)` 后未注入。菜单项 `OpenSettings?.Invoke()` 对 null 静默无操作，不弹窗、不进崩溃日志。`git log -S` 确认历史上从未注入（自 M5 引入托盘起缺失，非回归；此前用户能打开设置走的是轮盘操作条入口）。

### 改动

`ResidentHost` 构造处（`_tray = new TrayHost(_overlay);` 之后）补一行 `_tray.OpenSettings = OpenSettings;`。

## 验收标准（用户实测项）

1. 托盘「设置…」能打开设置面板（覆盖层+面板同场）；
2. 托盘勾「开机自启」→ `reg query` 出现 WinQuick 项 → 换一次皮肤 → 项仍在 → 重启机器自启生效。

## 验证

- `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` 0 error；
- reviewer 快审：docs/changes/fix-autostart-rollback-tray-settings.review-report.json。

## 顺带发现（留档不修，待用户定夺）

crash.log 2026-11-05 记录：队列编辑器删除按钮取 `Color.Danger`/`Color.Success` 令牌抛「资源不存在」；`design/tokens.json` 至今无这两个键，即当前删队列项仍会崩。另案处理。
